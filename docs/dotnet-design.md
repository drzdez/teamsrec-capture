# .NET design of the recording app (teamsrec-capture)

This document explains how the .NET version of the recording app in [`dotnet/`](../dotnet) is built and why. It is a
port of the Python prototype [`legacy/teamsrec.py`](../legacy/teamsrec.py): behaviour, thresholds and messages are
taken over, and the data output follows the contract [recording-format.md](recording-format.md), so teamsrec-transcribe
processes recordings from the .NET version unchanged.

## Why .NET

- **One executable** without Python, a virtual environment and the `pythonw` trampoline (the prototype showed two
  processes in the process list, and once we killed the real one by mistake).
- **Windows APIs directly**: WASAPI and Core Audio through NAudio, the registry, windows and COM (Outlook) without
  bridges such as pywin32/pycaw.
- **Stability**: the UI (WinForms, tray icon) without Tcl/Tk; window capture runs in a separate process as in the
  prototype, so its crash cannot bring the audio recording down.
- Installation later as `winget install` (roadmap).

## Overview

```
dotnet/
  TeamsRec.Capture.slnx
  src/TeamsRec.Capture/            the app (net10.0-windows, WinForms, NAudio, Tomlyn)
    Core/          shared types and interfaces (IClock, INotifier, IAudioSource, TrackInfo, CallApp, Log, ...)
    Config/        configuration from teamsrec.toml, writing with comments kept, the settings page model, file watcher
    Audio/         devices, recorder (system loopback + microphone), microphone test, the call's microphone
    Detection/     who holds the microphone (registry), Teams windows, the join screen, calls in other apps
    Calendar/      Outlook through COM, picking the meeting by title and time
    Screen/        capturing Teams windows to MP4 (PrintWindow -> ffmpeg), in a child process
    Recording/     audio watchdog, mix, finishing a recording (sidecar), recovery after a crash
    Contract/      naming (stem, folders), sidecar JSON by the contract
    Settings/      fallback local settings page (HttpListener + settings.html)
    App/           tray icon, main loop, dialogs, log, status file for the review page
    Program.cs     single instance (mutex), log, recovery, start
  tests/TeamsRec.Capture.Tests/    xUnit - logic without hardware
```

The modules match parts of the prototype one to one, so it is easy to trace where a behaviour comes from (table in
[`dotnet/PARITY.md`](../dotnet/PARITY.md)).

## The flow of one recording

```
 MonitorLoop (every 3 s)
   │  MicUsers.TeamsInUse() / MicUsers.Current()  ← ConsentStore registry: who holds the microphone right now
   │  WindowTitles + CallDetector                 ← join screen? a meeting in a browser?
   ▼
 start ── title: Teams window → Outlook.Meeting() (by title, then time) → the app's name
   │      microphone: Devices.CallInputDevice()    ← Core Audio: which microphone the call app records from
   │      Recorder.Start()                         ← WASAPI loopback (_sys.wav) + microphone (_mic.wav)
   │      ScreenCaptureProcess (Teams and playback) ← _screen<N>.mp4, 2 fps, in a child process
   ▼
 running ─ Watchdog.Tick()        ← no data → reopen with a growing pause; dead microphone → yellow icon
   │       Watchdog.FollowCallMic()← the call app switched headsets → switch the microphone track
   │       end: the app releases the microphone (10 s), Stop & keep, 4 h safety stop, silence in playback
   ▼
 stop ─── RecordingInfo taken at the moment of the stop (title, calendar, windows seen, continues, call_app)
   │      not a single WAV → the folder goes, "nevznikla" (not made) message
   ▼
 Finalizer (background) ── 16 kHz mix (ffmpeg) → calendar re-matched by the window → folder renamed
                           → sidecar .json (written last = the recording is complete)
```

## Key decisions

**Shared interfaces in `Core/Types.cs`.** Modules know no more of each other than needed: the watchdog works with
`IAudioSource`, not with a concrete recorder; time comes through `IClock`, notifications through `INotifier`. So the
watchdog, the choice of microphone, call detection or meeting matching can be tested without a sound card, Teams or
Outlook - the same scenarios as `legacy/smoke_test.py`, only in xUnit.

**A recording's metadata is taken at the moment of the stop (`RecordingInfo`).** When an on-site meeting turns into a
Teams call, the next recording starts at once while the previous one is still being finished. If finishing read the
app's current state, the first recording would get the second one's title (a bug we fixed in the prototype).

**The microphone by the call app, not by Windows.** Teams, Zoom and the browser choose their microphone themselves;
Windows' default input can be a completely different device (on 29 Sep the BT-W5 dongle was recorded while Teams used
the Sony headset, and the user was missing from the recording entirely). `Devices.CallInputDevice()` reads the Core
Audio sessions and picks the microphone of the call's process (Teams → known call apps → anything but us).

**A call = who holds the microphone (ConsentStore registry).** Reliable even when muted in the app; but the Teams join
screen holds the microphone too, so recording starts only after joining (the meeting window). A browser counts only
with a meeting open in the window title, otherwise dictation or voice search would be recorded. The app's own entry
(`teamsrec-capture.exe`) never counts as Teams.

**A watchdog with a growing pause.** When the audio stops coming (a Bluetooth headset gone to sleep), the reopened
streams are judged after 12 s and the next attempt waits 0/30/60/180/300 s (at most 8×). Without it the prototype once
raised 13 device-change messages in 6 minutes. A microphone without any room noise for 2 minutes means an unused device
is being recorded - the icon turns yellow and the message repeats after 5 minutes.

**Window capture in a separate process.** As in the prototype: the app starts itself as
`teamsrec-capture.exe --screen-capture <stem> <start>`. A hung `PrintWindow` or a crash in GDI or the encoder stays in
that process and cannot bring the audio recording down. The process captures until the app closes its stdin (or the app
ends) and then writes `<stem>_screens.json`. If it does not end within 45 s, the app stops it; the videos on disk are
used even without its report (`recovered`). Each Teams window has its own ffmpeg process that receives 1600×900 frames
(aspect ratio kept, black borders).

**Stopping outside the lock.** The recording's state is taken under the lock; closing the streams and videos (even tens
of seconds) happens outside it, so the tray icon never waits. Likewise the Outlook query at the start runs only after
recording has started, outside the lock.

**One settings page for both apps.** The tray's Settings… opens the review page's Nastavení (teamsrec-transcribe,
through `teamsrec-review.exe --settings`); the app's own page (`HttpListener` + `settings.html`, 127.0.0.1 only) is only
the fallback without it. Configuration is written to the shared `teamsrec.toml` line by line, so comments and
teamsrec-transcribe's keys stay; `ConfigWatcher` picks up changes made elsewhere without a restart.

**The review page knows what is being recorded.** On every change the app writes `%TEMP%\teamsrec-capture.json`
(recording or not, title, start, pid); the review server shows a red dot and can stop its processing while a recording
runs. A click on a balloon opens the review page (on the saved recording).

**One instance, the same mutex as the prototype.** `Local\teamsrec-capture` - the Python and .NET versions never record
at the same time. Before starting the .NET version the prototype must be quit (Quit in the tray) and the watchdog task
pointed at the new exe.

## What stays in Python

Transcription, speaker names, minutes and the review page stay in teamsrec-transcribe (Python, WhisperX on the GPU). The
.NET recorder talks to them only through the recordings folder and the sidecar by the contract, plus the status file -
which is why the contract is the most important shared part, and both projects must follow it to the letter.

## Tests

`dotnet test` runs the logic tests: detection of the join screen and of calls in other apps, the call's microphone, the
watchdog (pauses, dead microphone, headset switch), meeting matching from the calendar, TOML writing with comments kept,
the sidecar by the contract, recovering a WAV header after a crash, the review-page launch and status file. Real
recording is checked on a call.
