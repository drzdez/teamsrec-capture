# teamsrec-capture

Windows tray app that detects a Microsoft Teams call, asks whether to record it, and records
system audio (the other participants) and the microphone (you) as separate WAV tracks.
Also records playback of stored Teams recordings on demand.

Output is a folder of recordings described by a JSON sidecar — see
[docs/recording-format.md](docs/recording-format.md). Transcription and summaries are done by
the companion project [teamsrec-transcribe](https://github.com/drzdez/teamsrec-transcribe).

## Status

Design phase. The working Python prototype this is ported from is in [`legacy/teamsrec.py`](legacy/teamsrec.py);
the original design notes are in [`docs/HANDOFF_claudeApp.md`](docs/HANDOFF_claudeApp.md).

## Planned stack

- .NET 8, WPF (prompt window + tray icon), NAudio (`WasapiLoopbackCapture`, `WasapiCapture`)
- Call detection via `HKCU\...\CapabilityAccessManager\ConsentStore\microphone`
- Meeting title from the Teams window title; classic Outlook calendar (COM) as a later, better source
- Windows only

## Consent

Local capture is invisible to other participants. Tell them you are recording.
