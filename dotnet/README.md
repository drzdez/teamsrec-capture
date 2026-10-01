# teamsrec-capture (.NET)

This is the .NET 10 port of the teamsrec capture prototype (`legacy/teamsrec.py`). It is a Windows tray app
that:

- notices a Teams call, or a call in Zoom, Webex, Slack, a browser meeting and similar apps;
- records the system sound (WASAPI loopback: the other people) and your microphone as separate 16-bit WAV
  tracks;
- records a low-fps video of each Teams window, used later for name labels;
- writes the sidecar `<stem>.json` defined in [docs/recording-format.md](../docs/recording-format.md).
  [teamsrec-transcribe](https://github.com/drzdez/teamsrec-transcribe) processes the recording from there.

It also records on-site meetings (room microphone only), playback of stored recordings (system sound only)
and manual recordings. It has a local settings page.

Design and reasons: [docs/dotnet-design.md](../docs/dotnet-design.md).
Feature-by-feature comparison with the prototype and the list of open gaps: **[PARITY.md](PARITY.md)**.

## Requirements

- Windows 10/11 and the .NET 10 SDK (`net10.0-windows`, WinForms).
- NuGet: NAudio 2.2.1 (WASAPI), Tomlyn 2.10.1 (TOML). Both are restored automatically.
- ffmpeg is optional but recommended. Without it there is no `_mix.wav` and no window video. It is looked for
  on `PATH`, in `%TEAMSREC_FFMPEG_DIR%`, and in the winget install folder
  (`winget install Gyan.FFmpeg`).
- Classic Outlook is optional, for meeting titles and participants (COM; the new Outlook has no COM).

## Build and test

```powershell
cd D:\projects\teamsrec-capture\dotnet
dotnet build TeamsRec.Capture.slnx
dotnet test  TeamsRec.Capture.slnx
```

The tests (xUnit, 175 of them) cover the pure logic: detection rules, calendar matching, watchdog/backoff,
sidecar format, naming and rename, the TOML editor, and the on-site device policy. They need no audio
device, Outlook, Teams or ffmpeg.

## Installer (MSI) and releases

```powershell
powershell -File installer\build-msi.ps1   # -> installer\bin\x64\Release\teamsrec-capture-<version>-x64.msi
```

The script publishes a self-contained single-file exe (`artifacts\publish`, no .NET runtime needed on the target)
and builds the WiX 6 project in `installer\`: a **per-user** MSI (no admin rights) into
`%LOCALAPPDATA%\Programs\teamsrec-capture`, with a Start menu shortcut and a Startup shortcut (autostart). A newer
MSI upgrades an older one in place.

The installer has one question, the **recordings folder** (`FolderUI.wxs`), prefilled so a user just clicks Next: the
folder the app last used, else `%USERPROFILE%\meetings` (in the profile, writable without admin rights; also the app's
default without any config). The choice goes to `HKCU\Software\teamsrec\capture\OutDir`; the app writes it once into
`[recordings] out_dir` of the shared TOML (`Config/InstallerFolder.cs`), after that the TOML wins (Nastavení). An
upgrade skips the question and keeps the folder. `msiexec /i … /qn OUTDIR=E:\rec` sets it silently. If the folder
cannot be created (a missing drive), the app records into the default and logs why. Before replacing the files it asks a running app to quit by dropping
`quit.request` next to the exe (`App/QuitRequest.cs`) and waits up to 90 s; the app refuses during a recording and the
install stops with a message. After the install it starts the app again (`msiexec /i … LAUNCHAPP=0` skips that).

**Updates** (`App/Updater.cs`): at start (after a minute) and then once a day, unless `[capture] update_check = false`,
the app asks `api.github.com/repos/drzdez/teamsrec-capture/releases/latest` (public, no token). A higher version
with an `-x64.msi` asset shows **Install version X…** in the tray menu and a balloon (not for a declined version, and
not during a recording – then right after it); **Check for updates** asks at once. Installing: a confirmation, the
MSI downloaded to `%TEMP%` from this repository's releases only, its size and SHA-256 checked against the release's
`digest`, then `msiexec /i … /passive` – the MSI quits the app and starts the new version. The last check and a
declined version are kept in `HKCU\Software\teamsrec\capture` (`UpdateLastCheck`, `UpdateDeclined`).

The version is `<Version>` in `TeamsRec.Capture.csproj` (the app reads it from its assembly). GitHub Actions
(`.github/workflows/build.yml`) runs the tests and builds the MSI on every push to `main`; pushing a tag `v<version>`
also publishes a GitHub Release with the MSI:

```powershell
git tag v1.0.6; git push origin v1.0.6
```

## Run

```powershell
.\src\TeamsRec.Capture\bin\Debug\net10.0-windows\teamsrec-capture.exe
```

**The .NET app and the Python prototype use the same single-instance mutex (`Local\teamsrec-capture`)**, so
only one of them records at a time. If the prototype is running (pythonw.exe with the grey/red tray dot),
the .NET app writes `teamsrec is already running, exiting` to the log and quits. **Quit the prototype first**:
tray icon → Quit.

Everything goes to `<out_dir>\teamsrec.log`, in the same line format as the prototype and appended to the
same file. On startup the app also tries to recover recordings that a crash left without a sidecar (see the
PARITY.md; since 2026-09-29 this also works for WAVs the .NET app wrote itself).

> **In use since 2026-09-29, not yet checked on a real call.** Everything the parity review found is fixed
> (PARITY.md gaps 1-8); what is left are notes on behaviour inherited from the prototype (gap 9).

## Configuration

The config file is `%APPDATA%\teamsrec\teamsrec.toml`, or the path in `%TEAMSREC_CONFIG%`. It is shared with
teamsrec-transcribe and with the prototype. A missing or broken file gives the defaults. The settings page
(tray → Settings…) edits it in place and keeps comments and every key it does not manage.

```toml
[recordings]
out_dir = 'D:\meetings'        # recordings go to <out_dir>\YYYY\MM\<stem>\

[calendar]
outlook = false                # classic Outlook (COM): meeting title + participants

[user]
name = ""

[capture]
prompt_default = "record"      # record = just notify | ask = discard box (keeps on timeout) | skip = box, discards on timeout
onsite_mic = ""                # part of the input device name for on-site meetings ("" = Windows default input)
device_missing = "ask"         # ask | fail | fallback: when onsite_mic is not available
onsite_offer = "never"         # never | calendar | always: record a calendar meeting on site when it starts
onsite_upgrade = true          # an on-site meeting that turns into a Teams call switches to a live recording
other_apps = "record"          # record | off: calls outside Teams
```

The fixed constants are the same as in the prototype: poll every 3 s, 10 s call-end grace, 5 s minimum
length, 4 h cap, 30 s playback silence stop, 45 s discard box, 2 fps / 1600×900 window video.

## Output

`<out_dir>\YYYY\MM\<stem>\<stem>_sys.wav`, `_mic.wav`, `_mix.wav` (mono 16 kHz), `_screen<N>.mp4`, and `<stem>.json`.
The stem is `YYYY-MM-DD_HHMM_<slug>`. The sidecar is written last; its existence means the recording is
complete. The full format is in [docs/recording-format.md](../docs/recording-format.md).

## Source layout

```
dotnet/
  TeamsRec.Capture.slnx
  src/TeamsRec.Capture/
    Program.cs            entry: config, log, shared mutex, orphan recovery, tray message loop
    Core/Types.cs         shared types: IClock, INotifier, TrackInfo, CalendarItem, ScreenInfo, IAudioSource, Log ...
    App/                  MonitorLoop (3 s state machine + AppLogic pure rules), TrayApp (icon, menu, notifier),
                          Dialogs (MessageBoxTimeout yes/no + discard box, title input), FileLog
    Detection/            CallDetector (join screen, meeting titles, other apps), MicUsers (ConsentStore),
                          WindowTitles (EnumWindows)
    Audio/                Recorder (loopback + mic → WAV, reopen, watchdog fields), Devices (inputs, unavailable,
                          capture sessions → call microphone), MicTest
    Recording/            Watchdog (stall/silent/dead mic, reopen backoff, follow call mic), Finalizer (mix,
                          title fix-up, calendar re-match, rename, sidecar), Mixer (ffmpeg), Orphans (crash recovery)
    Calendar/             Outlook (COM items, pick by title/time, candidates, difflib ratio)
    Screen/               ScreenCapture (PrintWindow per Teams window → ffmpeg x264)
    Contract/             Sidecar (JSON model + writer), Naming (slug, stem, folders, rename)
    Config/               AppConfig (TOML load), SettingsModel (page fields + validation), TomlEditor (in-place edit)
    Settings/             SettingsServer (127.0.0.1 HttpListener + JSON API), settings.html (the prototype's page)
  tests/TeamsRec.Capture.Tests/
    App, Audio, Calendar, Config, Contract, Detection, Finalizer, Watchdog tests (xUnit)
```

## Current status (2026-09-29)

- It builds with 0 warnings and 0 errors, and all 163 tests pass. Every module of the prototype is ported
  except the post-recording hook (unused in the prototype) and the dead Tk prompt.
- The smoke run with the prototype holding the mutex exited cleanly ("already running").
- **Not yet run on real hardware.** Fixed after the parity review (2026-09-29): the app no longer counts
  its own microphone use as a Teams call, crash recovery reads NAudio's WAV headers, and the process is
  per-monitor-v2 DPI aware. Then, like the prototype: window capture in a child process, the tray never waits
  for a recording to stop, window video for playback too, calendar candidates at the start, and no silence
  stream on the default output (PARITY.md gaps 4-8).
- 1.0.2: edits of teamsrec.toml (for example from the settings page of teamsrec-transcribe) apply without a
  restart (`Config/ConfigWatcher.cs`); only a new recordings folder waits for the next start.
- 1.0.3: a double click on the tray icon (or the bold "Otevřít přepisy" in its menu) opens the review page of
  teamsrec-transcribe, in its desktop window or in the browser as `[capture] tray_open` (`app` | `web`) says, through
  `teamsrec-review.exe` (`[capture] review_app` if it is not in the usual place).
- 1.0.4: "Settings…" in the tray menu opens the same Nastavení as the review page (one settings page for both apps,
  with the Windows sound button); the app's own settings page is only the fallback without teamsrec-review.exe.
- 1.0.5: a click on the "Saved …" balloon opens the review page on that recording (`--open <stem>`); a click on
  any other balloon opens the page. It also writes `%TEMP%	eamsrec-capture.json` (recording or not, title, start, pid) on every
  change, so the review page shows a red dot "Nahrávání probíhá" while it records.
