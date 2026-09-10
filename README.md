# teamsrec-capture

Windows tray app that detects a Microsoft Teams call, asks whether to record it, and records
system audio (the other participants) and the microphone (you) as separate WAV tracks.
Also records playback of stored Teams recordings on demand.

Output is a folder of recordings described by a JSON sidecar — see
[docs/recording-format.md](docs/recording-format.md). Transcription and summaries are done by
the companion project [teamsrec-transcribe](https://github.com/drzdez/teamsrec-transcribe).

## Status

**No .NET code yet.** What exists:

- `docs/recording-format.md` + `docs/recording.schema.json` — the contract both projects build on.
- `legacy/teamsrec.py` — the working Python prototype this app is a port of. It is usable today,
  see [Using the prototype today](#using-the-prototype-today).
- `docs/HANDOFF_claudeApp.md` — original design notes.

## How it will be used

Start it once (or let it autostart at login) and forget about it. It sits in the tray:

1. When Teams starts using the microphone, a small always-on-top prompt appears in the top-right corner:
   **Record "<meeting title>"? [● Record] [Skip]**. It auto-skips after 45 s. Enter = record, Esc = skip.
2. While recording, the tray icon is red and shows the elapsed time. Tray menu: **Stop & keep**, **Abort & delete**.
3. Recording stops by itself ~10 s after the call ends (hard cap 4 h). Recordings shorter than a minimum length are discarded (5 s in the prototype, `MIN_DURATION_S`; 2 min planned as the default).
4. Files land in one folder per recording, `<OUT_DIR>/YYYY/MM/<stem>/`, as `<stem>_sys.wav`, `_mic.wav`,
   `_mix.wav` + `<stem>.json` sidecar (stem = `<date>_<time>_<title-slug>`).

Manual modes in the tray menu:

- **Record now** — record without detection (any call, any app).
- **Record playback** — for stored Teams recordings you cannot download: play them at normal speed, only the
  system track is recorded, recording stops after 30 s of silence or on Stop.

Nothing is transcribed here; run `teamsrec-transcribe process` (or let it watch the folder).

## Configuration

Shared TOML with teamsrec-transcribe: `%APPDATA%\teamsrec\teamsrec.toml`. Settings dialog in the tray menu
writes the same file.

```toml
[recordings]
out_dir = "D:/meetings"

[capture]
prompt_timeout_s = 45        # auto-skip the prompt after this
min_duration_s = 120         # shorter recordings are deleted
max_duration_s = 14400       # 4 h safety net
call_end_grace_s = 10        # call must look ended this long before we stop
silence_stop_s = 30          # playback mode: stop after this much silence
language = "cs"              # expected meeting language, written to the sidecar (auto-detect happens later)
devices = "default"          # or explicit WASAPI device names for loopback / mic
autostart = true             # register in the Startup folder

[user]
name = "Zdeněk"      # read by teamsrec-transcribe: the mic track of a live recording is this person
```

## Using the prototype today

`legacy/teamsrec.py` implements the flow above: detection, prompt, dual-track recording, *Record now*,
*Record playback* (system track only, stops after 30 s of silence), mix, and a sidecar in the contract's format v1
(`source` = live / manual / playback). It reads `out_dir` from the shared `%APPDATA%\teamsrec\teamsrec.toml`;
the other settings are constants at the top of the file. Recordings it makes are processed by
`teamsrec-transcribe process` like any other.

Setup (once):

```
cd legacy
uv venv --python 3.12 .venv
uv pip install --python .venv/Scripts/python.exe pyaudiowpatch pystray pillow pywin32 psutil
winget install Gyan.FFmpeg        # enables the _mix.wav (found automatically in the WinGet folder)
```

Run: double-click `legacy/run_teamsrec.cmd` (no console) or `run_teamsrec_console.cmd` (shows errors).
Log: `<OUT_DIR>/teamsrec.log`.

Autostart at login: put a shortcut to `legacy/run_teamsrec.cmd` into the Startup folder (Win+R -> `shell:startup`),
window style *Minimized*. Or from PowerShell:

```
$s = (New-Object -ComObject WScript.Shell).CreateShortcut("$([Environment]::GetFolderPath('Startup'))\teamsrec - capture.lnk")
$s.TargetPath = "D:\projects\teamsrec-capture\legacy\run_teamsrec.cmd"; $s.WorkingDirectory = "D:\projects\teamsrec-capture\legacy"
$s.WindowStyle = 7; $s.Save()
```

A second start (desktop shortcut while the autostarted one is running) exits immediately: the app holds a
named mutex, so only one instance ever records.

Testing without a real meeting: in Teams use *Calendar → Meet now* and join alone, or *Settings → Devices →
Make a test call* — both make Teams grab the microphone, so the prompt appears within 3 s. Leaving the call stops
the recording after ~10 s. Recordings shorter than `MIN_DURATION_S` (5 s) are deleted.

The prototype uses the Windows default devices; restart it after switching headsets.

## Planned stack

- .NET 8, WPF (prompt window + tray icon), NAudio (`WasapiLoopbackCapture`, `WasapiCapture`), mixing in-process
  (no ffmpeg dependency)
- Call detection via `HKCU\...\CapabilityAccessManager\ConsentStore\microphone`
- Meeting title from the Teams window title; classic Outlook calendar (COM) as a later, better source of title
  and participants
- Windows only; single-file publish, winget/MSIX later
- Planned (2026-09-10): capture the Teams window with the participant gallery during a live call as a low-fps video
  (`<stem>_screen.mp4`), so the active-speaker names come from the same video analysis used for imported Teams
  recordings. When someone shares their screen, Teams shows the gallery in its pop-out window: the capture follows
  whichever Teams window shows the name labels. A minimized window cannot be captured; diarization and voice prints
  cover those stretches.

## Consent

Local capture is invisible to other participants. Tell them you are recording.
