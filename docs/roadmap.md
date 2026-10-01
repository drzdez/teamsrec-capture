# teamsrec roadmap

The shared plan of both repositories (teamsrec-capture, teamsrec-transcribe). Decisions about the data format are in
`recording-format.md`; here is only the order and state of the work. State: ☐ not started, ◐ in progress, ☑ done.

## Done

- ☑ Recording contract v1 (a folder per recording, sidecar, transcript, speakers). 2026-09-04
- ☑ Python capture prototype: live call, manual recording, playback, autostart, protection against running twice.
- ☑ WhisperX transcription on the GPU, names from the Teams video (OCR of name labels), txt/srt export.
- ☑ Minutes locally through Ollama, Claude as a comparison; a Speakers section in the minutes. 2026-09-10
- ☑ The user's own voice from the microphone track (`[user] name`). 2026-09-10
- ☑ Settings through a local page (prototype 0.8.0). 2026-09-24: *Settings…* and *Test microphone* in the icon's menu;
  choosing the microphone from a list of real devices (disabled ones shown with the reason), the `device_missing`
  policy, offering a recording from the calendar (`onsite_offer`) and an on-site meeting turning into a live call
  (`onsite_upgrade`, the `continues` field in the sidecar). Settings are written to the shared TOML, comments kept.
- ☑ Recording resilience (prototype 0.7.0 and 0.7.1). 2026-09-24: a recording that opens no audio device does not
  start at all (4 on-site meetings on 22–23 Sep "recorded" into nothing); a yellow icon and a repeated alarm when the
  audio does not come. 2026-09-21: the Teams pre-join screen ("Připojení ke schůzce") is not recorded – it holds the
  microphone only for the device preview; the audio watchdog reopens the streams with a growing pause
  (0/30/60/180/300 s, at most 8) instead of every 20 s; a recording on which nothing was ever heard gets
  `audio_silent` and is not transcribed.

## Next steps (in this order, decided 2026-09-10/11)

1. ☑ **Voice prints** (transcribe), 2026-09-11. No audio samples stored: the diarization already returns an
   embedding, which is stored directly (`_speakers/voiceprints.json`). Whoever is named once is recognised by voice in
   later recordings; the recognition is written as an assignment and the page shows it for confirmation. Threshold and
   margin calibrated on the first recordings, see `lab/FINDINGS.md`. For the user to decide: on by default, automatic
   assignment (not just a suggestion), telling the team about the biometrics.
2. ☑ **Review page** (transcribe, the `review` command). Done 2026-09-11: playing samples, names, merging, saving,
   regenerating, choosing a recording, a desktop shortcut, the People tab (first name, last name, nickname, what to
   write in the minutes). Voice samples are added to the tab with the prints. A local page in the browser: play samples
   of each label, assign a name with suggestions, merge labels, save and regenerate; the People tab for managing
   prints. One HTML file + JavaScript with `@ts-check`/JSDoc, no framework and no build step, a JSON API from the
   Python server on 127.0.0.1 only. Part of the teamsrec-transcribe package, its own `web/` folder, the API documented
   so that it can later be split off or replaced by a native window (since 2026-09-30 also the Tauri desktop window).
   The "process latest" shortcut opens it when someone stays unnamed.
3. ☑ **Capturing Teams windows during a live recording**. 2026-09-14 in the Python prototype (0.4.0): every Teams
   window as `<stem>_screen<N>.mp4` (2 fps, x264, a separate process), sidecar `screens`. 2026-09-16: the live window
   highlights the speaking tile with an outline, not the name label; outline detection + OCR of the label in the
   tile's corner (`analyze_screen`) verified on a stand-up. Ported to .NET together with capture.
4. ☑ **.NET port of capture** (WinForms + NAudio), see [dotnet-design.md](dotnet-design.md). Live since 2026-09-29;
   the Python prototype stays in `legacy/`.

## To confirm (recorded only)

- ☐ **Full-text search in transcripts** across recordings. No database: a pass over the `.txt` files, possibly a small
  index in a `_index` folder as a deletable cache. The solution and scope (search only, or also browsing recordings on
  the review page) are still to be confirmed. Recorded 2026-09-11.
- ☐ **Installing on a new machine**: `install.ps1` in this repo, run with one command, idempotent (running again =
  update): check the GPU and disk space, winget (git, uv, ffmpeg, Ollama), clone both repositories, `uv sync`,
  `config --init`, choose the Ollama model by VRAM, `hf auth login` + the pyannote consent (the only manual step),
  shortcuts. No classic installer (MSI): the bulk is models and CUDA packages, which are downloaded on the machine.
  Windows only, like capture; transcribe stays portable in code, but without official support for other OSes. Later
  `winget install` for the .NET capture and `uv tool install` for transcribe. Recorded 2026-09-11.
- ☐ **Recording outside Teams** (capture): today recording is offered only when Teams takes the microphone (the
  ConsentStore registry + ms-teams.exe processes). Extend to Zoom, Google Meet and Webex in the browser and as apps,
  and in general to anything that starts using the microphone: detection through ConsentStore (a key per app, per
  site for a browser) and a list of known apps with how to get the meeting's name from them (window title, tab). For
  an unknown app just a question "Record the call in <app>?" with a manual name. The sources of speaker names stay the
  microphone, voice prints and diarization; reading name labels from the window (step 3) is Teams-specific, the others
  later. Recorded 2026-09-14.
- ☑ **Language per speaker**: mixed cs/sk meetings; a reply gets its own `language` when its speaker clearly speaks
  another configured language (`[transcribe] per_speaker_language`).
- ☑ **Deleting audio after a period** (`purge-audio`, `[retention] audio_days`): transcripts and minutes stay.
- ◐ **More transcription backends**: cloud OpenAI and ElevenLabs Scribe done (`[transcribe] provider`); CPU fallback
  and Azure AI Speech not yet.
- ☑ **Outlook calendar** as the source of the meeting's name and participants: 2026-09-14, classic Outlook through COM,
  optional (`[calendar] outlook`, asked at `config --init`). No Graph API yet (an app registration in the tenant).

- ☐ **Linux, Mac, tablets**: analysis and design in [cross-platform-design.md](cross-platform-design.md) – the core as
  a .NET library extracted from today's app, ports for audio / detection / windows / calendar, Python keeps its role;
  Linux first, then Mac, a tablet first as a transcript review. Recorded 2026-10-01.

## Principles that apply to everything

- The source of data is only the recordings folder and the files in it; no database, no hidden index. Derived files
  (transcript, exports, minutes, prints, any index) can be deleted and computed again at any time.
- Nothing leaves the PC without an explicit setting (the Claude API only with `provider = "anthropic"`, cloud
  transcription only with `[transcribe] provider` set to a cloud service).
- Secrets only in environment variables or the Windows Credential Manager, never in the configuration or the repo.
