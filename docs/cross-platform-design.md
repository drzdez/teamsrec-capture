# teamsrec on Linux, Mac and tablets – analysis and design

State as of 1 Oct 2026. This document is a basis for a decision; none of it is implemented yet. It builds on
[dotnet-design.md](dotnet-design.md) (today's Windows recording app) and [recording-format.md](recording-format.md)
(the contract between recording and transcription).

## 1. Goal

- teamsrec should work on **Linux** and **Mac** too, later also on **tablets** (Android, iOS/iPadOS).
- As much as possible of what already runs on Windows should be **reused**, not rewritten.
- The "core" (the recording logic and everything around a recording) should run anywhere; only thin layers around it
  should be platform-specific: audio capture, call detection, window capture, calendar, tray icon.
- Transcription stays in Python (WhisperX, pyannote, OCR, Ollama/Claude) – that is where the ecosystem is, and it
  cannot be matched elsewhere.

## 2. What teamsrec consists of today

```
 teamsrec-capture (C#, .NET 10, Windows)          teamsrec-transcribe (Python 3.12)
 ─────────────────────────────────────           ──────────────────────────────────────────
 tray, call detection, audio recording,           transcription (WhisperX + pyannote, OCR of names from video),
 Teams window capture, Outlook, finalizing  ──►   minutes (Ollama / Claude), page server (REST + SSE)
            │   recording by the contract                      ▲
            │   (sidecar JSON + WAV + MP4)                     │ HTTP 127.0.0.1
            ▼                                       web page (index.html, plain JS) – in a browser
      <out_dir>/YYYY/MM/<stem>/                     or in the app window (Tauri, Rust, ~300 lines)
```

Three interfaces are already platform-independent and are the most valuable things to carry over:

1. **The recording contract** (`recording-format.md`): a folder with a sidecar and tracks. Whoever writes it, the
   transcription can work with it.
2. **The page server's REST + SSE API** (described in OpenAPI 3.1, `/api/openapi.json`).
3. **The web page**: one page for reviewing transcripts, minutes and the settings of both apps; it runs in any
   browser or WebView.

## 3. What is tied to Windows (measured in the code)

### The recording app (5,400 lines of C#)

| area | lines | Windows? | through |
|---|---|---|---|
| Core (types, interfaces) | 106 | no | – |
| Config (TOML, settings, file watching) | 532 | no | – |
| Contract (naming, sidecar) | 437 | no | – |
| Recording (finalizing, mix through ffmpeg, crash recovery, watchdog) | 770 | no | – |
| Settings (fallback settings page) | 279 | no | HttpListener is .NET |
| App – logic (`AppLogic`, `MonitorLoop`) | ~850 | partly | calls the Windows parts below |
| **Audio** (WASAPI loopback + microphone, microphone test) | 924 | **yes** | NAudio, Core Audio |
| **Detection** (who uses the microphone, Teams windows) | 420 | **yes** | ConsentStore registry, Core Audio sessions, EnumWindows |
| **Screen** (window video) | 455 | **yes** | PrintWindow (GDI) + ffmpeg |
| **Calendar** | 306 | **yes** | Outlook COM (the meeting-matching logic is neutral) |
| **Tray, dialogs** | ~350 | **yes** | WinForms |

**Roughly half (2,500 lines) is platform-neutral** and covered by a large part of the 163 tests.

### Transcription and server (6,000 lines of Python, 2,200 lines of page)

Python itself runs on Linux and Mac. Only small things are tied to Windows:

| place | what | replacement |
|---|---|---|
| `settings.input_devices` | list of microphones from the registry | Linux: `pactl list sources`; Mac: Core Audio through a small helper or `system_profiler` |
| `/api/system/sound-settings` | `control mmsys.cpl` | Linux: `pavucontrol` / the desktop's settings; Mac: `open x-apple.systempreferences:…sound` |
| `_kill_tree` | `taskkill /T` | elsewhere a process group (`os.killpg`) |
| `outlook.py` | Outlook COM | see the calendar in chapter 7 |
| `keyring` | Windows Credential Manager | by itself: Secret Service (Linux), Keychain (Mac) |
| WhisperX on CUDA | Linux with NVIDIA the same as Windows | Mac: a different transcription provider (chapter 7) |

The app window (Tauri) builds for Linux (WebKitGTK) and Mac (WKWebView) without code changes.

## 4. Design: layers

```
 ┌───────────────────────────────────────────────────────────────────────────────────────┐
 │  UI: the web page (shared everywhere)  +  a thin shell: tray / menu bar / tablet app   │
 ├───────────────────────────────────────────────────────────────────────────────────────┤
 │  CORE (platform-neutral)                                                              │
 │   • recording state machine (when to start / stop, on-site, playback, other apps)      │
 │   • contract: naming, sidecar, finalizing, mix, crash recovery                         │
 │   • configuration (TOML), status for the page (teamsrec-capture.json), audio watchdog  │
 │   • matching meetings from the calendar (without the source)                           │
 ├───────────────────────────────────────────────────────────────────────────────────────┤
 │  PORTS (interfaces the core calls)                                                    │
 │   IAudioCapture  ICallDetector  IWindowCapture  ICalendarSource  INotifier  ITray     │
 ├──────────────┬──────────────────┬──────────────────┬────────────────┬──────────────────┤
 │  Windows      │  Linux           │  macOS           │  Android        │  iOS / iPadOS     │
 │  WASAPI       │  PipeWire/Pulse  │  ScreenCaptureKit│  AudioRecord    │  AVAudioEngine    │
 │  ConsentStore │  source-outputs  │  Core Audio      │  (mic only)     │  (mic only)       │
 │  EnumWindows  │  X11 / portal    │  SCK windows     │  –              │  –                │
 │  Outlook COM  │  ICS / Graph     │  ICS / Graph     │  Graph / ICS    │  EventKit / Graph │
 └──────────────┴──────────────────┴──────────────────┴────────────────┴──────────────────┘
                 ▼ recording by the contract (+ upload to a server where transcription does not run locally)
 ┌───────────────────────────────────────────────────────────────────────────────────────┐
 │  teamsrec-transcribe (Python) – the same on Windows, Linux, Mac; a tablet calls it     │
 │  over the network                                                                     │
 └───────────────────────────────────────────────────────────────────────────────────────┘
```

Today's .NET code already largely matches this split (`Core/Types.cs` has `IAudioSource`, `INotifier`, `IClock`).
What is missing is separating the ports for call detection, windows and calendar and extracting the core into a
separate library.

## 5. Core language: .NET, Kotlin or Rust?

The core must run on Windows, Linux, Mac, Android and iOS. There are three options:

| criterion | **.NET (C#)** – extract the core from today's app | **Kotlin Multiplatform** | **Rust** (in Tauri) |
|---|---|---|---|
| runs on Win / Linux / Mac | yes (.NET 10) | yes (JVM, or Kotlin/Native) | yes |
| runs on Android / iOS | yes (.NET for Android / iOS, MAUI) | yes, the most mature logic sharing for mobile | yes (Tauri 2 mobile) |
| reuse from Windows | **~2,500 lines + tests unchanged** | rewrite of the core (tests as a template) | rewrite of the core |
| desktop tray UI | Avalonia (Win/Linux/Mac) | Compose Desktop | Tauri (already have it) |
| tablet UI | MAUI + WebView with the page | Compose Multiplatform or WebView | Tauri mobile = **the same web page** |
| native audio | P/Invoke / bindings; ScreenCaptureKit bindings exist for Mac | expect/actual + JNA / ObjC interop | crates (wasapi, pipewire, screencapturekit) |
| languages in the project | 3 (C#, Python, Rust shell) | **4** (+ Kotlin) | 2–3 (Rust, Python, C# until the switch) |
| desktop size / start | small (trimmed .NET) | JVM ~100 MB, slower start (Native more complex) | smallest |

### Recommendation: .NET

- **The largest reuse:** the core of today's app is extracted into a `TeamsRec.Capture.Core` library (`net10.0`,
  without `-windows`) together with its tests. The Windows app just starts using it and nothing changes for it.
- **.NET covers all five platforms** with one runtime, including Android and iOS.
- **Kotlin would mean a fourth language and a third rewrite** of the recording within a month (Python → C# → Kotlin).
  It makes sense only if the centre of gravity moved to native mobile apps (a rich offline UI, background recording as
  the main scenario). As chapter 8 shows, recording on tablets is limited, so I do not expect that.
- **Rust** is tempting because we already have Tauri and the same web page would run on a tablet. But the core would
  be written again. It makes sense only as a future unification of the desktop shell. **The tablet UI through a
  WebView with the page works for .NET just the same.**

## 6. Linux

| port | solution | note |
|---|---|---|
| call audio (loopback) | the PipeWire / PulseAudio **monitor** of the output device: `pw-record --target <sink>.monitor` or libpulse through P/Invoke | reliable; PipeWire is the standard today (Fedora, Ubuntu 22.10+) |
| microphone | the same, the source by name (`pactl list sources`) | choice by name as on Windows |
| **who uses the microphone** | `pactl list source-outputs` / `pw-dump`: each entry has `application.name` and `application.process.binary` | **better than Windows**: directly "chrome", "teams-for-linux", "zoom" |
| Teams on Linux | the official desktop client no longer exists (end of 2022): Teams **in the browser / PWA**, or the unofficial `teams-for-linux` | detection = a browser with a meeting open, the same as today's "other apps" |
| window titles / name-label video | **X11**: list of windows and snapshots (`_NET_CLIENT_LIST`, XGetImage); **Wayland**: only through the ScreenCast portal with a one-time consent (newer portals remember the consent) | on Wayland window capture will be optional; diarization + voice prints work without it |
| calendar | no Outlook COM: the **ICS address** of a published calendar (Outlook on the web supports it), or Microsoft Graph | ICS is the simplest and works everywhere; Graph needs an app registration in Entra |
| tray, notifications | Avalonia `TrayIcon` (StatusNotifierItem); GNOME needs the AppIndicator extension | notifications through `org.freedesktop.Notifications` |
| transcription | **unchanged** (NVIDIA + CUDA runs best on Linux) | a number of Windows troubles go away (ffmpeg in PATH, …) |
| keys | `keyring` → Secret Service (GNOME Keyring / KWallet) | no changes |
| transcript window | Tauri for Linux (WebKitGTK) | `npm run build` on Linux |
| distribution | AppImage or .deb; Flatpak later (the sandbox complicates access to PipeWire and windows) | |

**Effort:** medium. Most of the work is in audio and detection (new ports). Window capture on Wayland is the only
place where Linux can do less than Windows.

## 7. macOS

| port | solution | note |
|---|---|---|
| call audio | **ScreenCaptureKit** (macOS 13+) can capture system audio (`capturesAudio`) | needs the "Screen Recording" permission; before, only a virtual device (BlackHole) |
| microphone | AVAudioEngine / Core Audio | Microphone permission |
| who uses the microphone | Core Audio `kAudioDevicePropertyDeviceIsRunningSomewhere` (that someone uses it) + running processes (Teams, browser) | macOS does not tell "which app" directly; the combination is enough |
| Teams windows | ScreenCaptureKit – snapshots of individual windows | the same permission as audio |
| calendar | ICS / Microsoft Graph; Outlook for Mac has no COM | EventKit when the calendar is synced into the system |
| menu bar | Avalonia `TrayIcon` → `NSStatusItem` | |
| bindings | .NET for macOS has ScreenCaptureKit bindings; otherwise a small Swift helper called as a process | a Swift helper is easier to maintain |
| **transcription** | **no CUDA**: WhisperX (faster-whisper / CTranslate2) runs only on the CPU, slowly | a new provider **mlx-whisper** (Apple Silicon) or **whisper.cpp** (Metal); pyannote on MPS / CPU; minutes through Ollama run well on Metal |
| signing | the app must be signed and notarized (Apple Developer account), otherwise Gatekeeper blocks it | both Tauri and .NET support it |

**Effort:** medium to higher. Recording is clean thanks to ScreenCaptureKit. The main work is a new transcription
provider for Apple Silicon, plus permissions, signing and notarization.

## 8. Tablets: Android and iOS / iPadOS

### What works and what does not

- **Recording Teams call audio is not possible.**
  - Android (10+) allows capturing other apps' audio only through AudioPlaybackCapture, which **by design leaves out
    call audio (VOICE_COMMUNICATION)**.
  - iOS does not let you listen to other apps at all. A ReplayKit broadcast captures app audio, but for a VoIP call
    this is unverified and more likely not.
  - Recording calls therefore stays on the computer.
- **An on-site meeting works well:** a tablet in the middle of the table with a microphone is exactly the "onsite"
  scenario, just without a laptop.
- **Transcription on the tablet locally – rather not.** WhisperX + pyannote need a GPU and Python. Realistic options:
  - send the recording home to the PC (transcription server) – **recommended**;
  - cloud transcription (OpenAI / ElevenLabs, already supported);
  - a small model on the device (whisper.cpp) – only for a quick preview, without diarization.
- **Reviewing transcripts and minutes on a tablet makes a lot of sense:** the same web page, just in a WebView for
  touch.

### Tablet app design

```
 tablet (.NET MAUI, or Tauri mobile)                          PC (Windows / Linux / Mac)
 ┌─────────────────────────────────────┐                    ┌───────────────────────────────┐
 │ record an on-site meeting (mic)      │  upload (HTTPS) ─► │ transcription server (today's  │
 │ core: naming, sidecar,               │                    │   Python)                      │
 │   calendar (EventKit / Graph)        │ ◄── page ───────── │ + import / upload endpoint     │
 │ WebView: transcript review, minutes  │     (REST + SSE)   │ + sign-in (token / pairing)    │
 │                                      │                    │ + reachable in LAN or over VPN │
 └─────────────────────────────────────┘                    └───────────────────────────────┘
```

The server today listens only on 127.0.0.1 and has no sign-in. For a tablet it needs:
- **optional network operation:** LAN, ideally over a VPN or Tailscale, not to the internet;
- **device pairing with a token** (a QR code on the Settings page);
- **TLS;**
- **an endpoint for uploading a recording:** a folder by the contract, like today's `_inbox`.

Security is the main topic here: these are meeting recordings and biometric voice prints.

**Effort:** higher, and it makes sense only after Linux. First the server part (sign-in, upload), then the app itself.

## 9. Python stays – what changes in it

- **Platform-neutral replacements** for the small things from chapter 3: the list of microphones, the sound dialog,
  ending a process.
- **Transcription providers by machine:** `whisperx` (CUDA), `mlx` / `whisper-cpp` (Mac), cloud. The choice is already
  in Settings today; values will be added.
- **Calendar:** an `ics` source (an address), possibly `graph`, next to `outlook` (COM).
- **Server:** optional sign-in and operation outside 127.0.0.1, an upload endpoint.
- **CI on Linux:** run the tests (93) and the page tests on Linux too. Today they run only on Windows.

## 10. Plan

| phase | what | result |
|---|---|---|
| 0 | extract `TeamsRec.Capture.Core` (net10.0) and the ports from today's .NET code, the tests with it | Windows with unchanged behaviour, the core ready for other platforms |
| 1 | Python without the Windows small things, ICS calendar, CI on Linux | transcription and page fully on Linux |
| 2 | **Linux:** PipeWire ports (audio, microphone, detection), Avalonia tray, X11 windows, Tauri build, AppImage | the first non-Windows recording |
| 3 | **Mac:** ScreenCaptureKit (audio + windows), menu bar, mlx / whisper.cpp provider, signing | recording and transcription on Apple Silicon |
| 4 | **server for remote clients:** sign-in, TLS, upload, LAN / VPN | the basis for tablets |
| 5 | **tablet:** on-site recording, transcript review in a WebView | the tablet as a meeting notebook |

Phases 0 and 1 pay off even without other platforms: the code gets clearer and transcription is tested on Linux too.

## 11. Risks

- **Wayland:** window capture only with the portal and consent. On Wayland the name-label video will be optional.
- **Teams on Linux is web-only:** meeting detection depends on the browser, the same as today's "other apps".
- **Mac without CUDA:** the quality and speed of transcription through mlx / whisper.cpp must be measured on real
  recordings, as we measured the cloud providers.
- **Mobile and calls:** there will be no call recording on a tablet. Should that change (e.g. Teams allows capture),
  it is added as a port.
- **Server security on the network:** today 127.0.0.1 without sign-in. Opening it any other way than with a token and
  TLS is not possible.

## 12. To decide

1. **Core language:** I recommend **.NET** (reuse, one runtime everywhere), Kotlin only if the focus moves to mobile
   as the main platform.
2. **Linux first:** yes/no. I recommend yes; it is the closest (Python and CUDA already run there).
3. **Calendar outside Windows:** ICS (simple, read-only) or Microsoft Graph (full access, needs an app registration in
   the company tenant).
4. **Tablet:** transcript review only (quick), or also on-site meeting recording (more work, needs phase 4).
