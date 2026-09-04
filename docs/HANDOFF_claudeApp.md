# teamsrec — meeting capture & transcription (handoff from claude.ai discussion, 2026-08-31)

## Goal
One self-contained app (single machine) that:
1. Detects that a Teams call started, shows a confirm prompt ("Record <title>? [Record]/[Skip]", auto-skip ~45 s).
2. Records mic + system loopback audio locally (separate tracks: `_mic.wav` = me, `_sys.wav` = others).
3. Auto-stops when the call ends (grace ~10 s), hard cap 4 h, discards recordings < 2 min, tray icon shows state, "Abort & delete" available.
4. Names outputs `OUT_DIR/YYYY/YYYY-MM-DD_HHMM_<slug>` + JSON sidecar (title, start, end, duration_s, stop_reason, files).
5. Transcribes locally (WhisperX, language `cs`, diarization via pyannote; GPU = RTX 5090 laptop / CUDA; on Apple Silicon use mlx-whisper, diarization on CPU) and sends transcript to the user's Claude gateway for summary/action items.

Must deploy as ONE system on Windows, Linux, or macOS — no split across machines. Language: Python (transcription stack is Python; don't add a second runtime).

## Architecture decision
Package with three platform adapter interfaces, selected by `sys.platform`; everything else is shared code:
- `AudioCapture` — mic is cross-platform via PortAudio/`sounddevice`; loopback is per-OS:
  - Windows: WASAPI loopback via `pyaudiowpatch` (stock PortAudio doesn't expose it)
  - Linux: PipeWire monitor source (appears as a normal input device to PortAudio)
  - macOS: BlackHole virtual device first (easy), ScreenCaptureKit later (native, macOS 13+)
- `CallDetector` — preferred per-OS: Windows registry `HKCU\...\CapabilityAccessManager\ConsentStore\microphone` (`LastUsedTimeStop == 0` for a *teams* entry); Linux: PipeWire graph (who holds a mic stream); macOS: CoreAudio `DeviceIsRunningSomewhere`. Cross-platform fallbacks: calendar events (Outlook/Graph) and/or audio-activity heuristic (bidirectional sound > X s).
- `MeetingTitle` — primary source should be calendar (better than window titles); fallback per-OS window title (Windows `EnumWindows` → "<subject> | Microsoft Teams", skip nav titles; Wayland gives nothing).
Minimal-platform-code strategy agreed: calendar for detection+title, leaving loopback as the only truly OS-specific piece.

## Existing code
`teamsrec.py` (in this repo) — working Windows-only prototype: registry call detection, Tk prompt, pystray tray, pyaudiowpatch dual-track recording, ffmpeg mix to mono 16 kHz `_mix.wav`, JSON sidecar, POST_HOOK template. Refactor it into the package structure; keep behavior and naming convention identical.

## Roadmap
1. Restructure into package (`core/` state machine + `platform/{win,linux,mac}` adapters), Windows + Linux backends first, mac stub.
2. Transcription worker: watch OUT_DIR for finished `_mix.wav` (or run POST_HOOK), run WhisperX (`--model large-v3 --language cs --diarize`), write `.txt`/`.srt` next to audio.
3. Summarize step: POST transcript to Claude gateway (OpenAI-compatible endpoint), save summary + action items markdown next to transcript.
4. Later: calendar-based detector/title (Graph API), macOS ScreenCaptureKit capture, packaging (pipx / PyInstaller per OS, autostart).

## Constraints & notes
- Consent: local capture shows participants nothing — the prompt is only the user's own consent; keep the red tray state and abort path prominent.
- Czech language support is mandatory → Whisper large-v3; NVIDIA Parakeet is English-only, don't use.
- Separate mic/sys tracks are kept deliberately (free "me vs. them" attribution even without diarization).
- Meetily (Rust/Tauri) was evaluated as an off-the-shelf alternative; rejected for now (no diarization, no confirm-prompt auto-detection, foreign naming), but check its releases before building the mac capture.
