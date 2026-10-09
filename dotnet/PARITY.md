# Parity: .NET port vs. the Python prototype

Reference: `legacy/teamsrec.py` (0.9.0). Port: `dotnet/src/TeamsRec.Capture` (1.0.5).
Review date: 2026-09-29. Checked by reading both code bases function by function. The build has
0 errors and 0 warnings, and `dotnet test` passes 154 of 154 tests. Nothing has run on real hardware
yet: the prototype holds the shared mutex, so a live run would record the same call twice.

Status legend: **done** = same behaviour as the prototype, **partial** = ported with a
behavioural difference or an open risk, **missing** = not ported, **bug** = ported but does
the wrong thing on a real call.

## Feature table

| Feature | Prototype function | .NET file / class | Status | Notes |
|---|---|---|---|---|
| Teams call detection (mic in use) | `teams_mic_in_use` | `Detection/MicUsers.cs` `MicUsers.TeamsInUse` | done | **Fixed 2026-09-29**: matches the app identity (package or exe name), skips `teamsrec*`, our own exe and `NotACall`; test `The_recorder_itself_is_not_a_Teams_call`. Was: tests for "teams" anywhere in the ConsentStore key *path*. The .NET exe is `teamsrec-capture.exe`, so its own NonPackaged key (`D:#…#teamsrec-capture.exe`) contains "teams". While the app records from a microphone, it reads its own key as "Teams is in a call". The prototype ran as `pythonw.exe` (key under `…#Python312#pythonw.exe`), so it never hit this. See gap 1. |
| Mic holders (other apps) | `mic_users`, `NOT_A_CALL` | `MicUsers.Current` | done | Skips python(w).exe, `teamsrec-capture.exe` and the exe name it runs as. |
| Join screen (pre-join) | `is_prejoin_title`, `is_meeting_window`, `prejoin_only` | `Detection/CallDetector.cs` | done | Same en/cs title sets. Tested. |
| Meeting title from windows | `guess_titles`, `guess_meeting_title`, `is_generic_title` | `CallDetector.GuessTitles/GuessMeetingTitle/IsGenericTitle` | done | Copies of the title sets also live in `Finalizer` and `Outlook` (so modules compile alone). Three lists must be kept in sync. |
| Window enumeration | `teams_windows`, `app_window_titles` | `Detection/WindowTitles.cs` | done | EnumWindows + process snapshot. |
| Calls in other apps | `other_call`, `CALL_APP_IDS`, `BROWSERS`, `WEB_MEETING`, `_clean_web_title` | `CallDetector.OtherCall` | done | Keeps the ordered fragment list and the browser-title rule. The end of the call follows `app.Id in MicUsers.Current()`. |
| Call microphone (which mic the call uses) | `capture_sessions`, `call_input_device`, `_is_teams_process`, `CALL_APPS` | `Audio/Devices.cs` `CaptureSessions`, `CallInputDevice`, `ProcessTree` | done | Core Audio sessions via NAudio, Toolhelp snapshot for parents (4 levels). Skips its own pid and parent pid. Ranking is the same. |
| Input list / unavailable inputs | `audio_inputs`, `unavailable_inputs`, `_state_reason` | `Devices.Inputs`, `Devices.Unavailable`, `StateReason` | partial | Uses MMDevice states instead of the MMDevices registry. NAudio reports "disabled" as state 2, which maps to "zakázané ve Windows" (the prototype printed "zakázané" for a bare 2). The hardware name comes from `DeviceFriendlyName`, not the raw registry property. Cosmetic only. |
| find_input / mic test | `find_input`, `test_input` | `Devices.FindInputEndpoint`, `Audio/MicTest.cs` | done | Result shape for `/api/test` is the same (`device, peak, seconds, silence_level`). |
| Recorder: loopback + mic to WAV | `Recorder._open/_callback/start/stop` | `Audio/Recorder.cs` | partial | Same thresholds (SILENCE 300, ALIVE 8) and the same watchdog fields. WAV is 16-bit at the device mix rate, max 2 channels. Two differences: (a) the RIFF header is flushed every 5 s (improvement); (b) **new behaviour:** a WasapiOut plays silence on the default output so the loopback keeps sending buffers. PortAudio did not need this. It has not been tested live. The WAV header written by NAudio is 46 bytes, not 44, which matters for orphans (gap 2). |
| No device → start fails loudly | `Recorder.start` RuntimeError | `Recorder.Start` InvalidOperationException | done | Czech message is the same. The list of available devices comes from `Devices.Inputs()`. |
| Reopen after a stall (pad the gap with silence) | `Recorder.reopen` | `Recorder.Reopen` | done | Re-resolves devices and keeps a track only if its format is unchanged. The gap is padded in 1 s chunks. One difference: when there is no default output, the prototype raised (the caller then backs off 300 s). .NET logs it and returns false, so the next tick retries, capped at 8 reopens. |
| Reopen backoff / verdict | `_reopen_result`, `_try_reopen`, `AUDIO_BACKOFF_S`, `REOPEN_CHECK_S` | `Recording/Watchdog.cs` | done | Same constants (0/30/60/180/300, max 8, check after 12 s, recovered within 4 s). Tested with a fake clock. |
| Audio watchdog (stall / silent / no mic / dead mic, re-warn 300 s) | `_audio_watchdog` | `Watchdog.Tick` | done | Same order of problems, same Czech texts, same all-clear message. `Reset()` runs on every start (the prototype reset `audio_warned` in `start`). |
| Dead room mic (on-site) | `_audio_watchdog` `mic_only` branch | `Watchdog.TickOnsite` | done | |
| Follow the call's mic (headset switch) | `_follow_teams_mic` | `Watchdog.FollowCallMic` | done | Same 30 s rhythm. The "warned" mic is kept across recordings, as in the prototype. Inherited weakness: when the new mic has a different format, the mic stream is closed and not reopened, so the mic track stays silent for the rest of the call (the prototype does the same). |
| "No audio after 10 s" warning | `loop` | `MonitorLoop.TickRecording` | done | |
| Monitor loop (3 s) | `App.loop` | `App/MonitorLoop.cs` `Tick/TickIdle/TickRecording` | done* | The logic matches branch by branch (join screen, Teams start, other app, calendar offer, reset of `declined`, call-end grace of 10 s, playback silence stop after 30 s once past 15 s, 4 h cap). (The `TeamsInUse` self-detection that broke call end and on-site upgrade is fixed, see gap 1.) |
| Start (stem, folder, mic choice, calendar, title source, screen) | `App.start` | `MonitorLoop.StartRecording`, `AppLogic.MicFor/ResolveTitle/RecordsWindows` | done | Same stem/folder layout and title-source rules. **Fixed 2026-09-29**: Teams windows are captured for playback too (`AppLogic.RecordsWindows` = `not onsite and not call_app`, as the prototype); the Outlook lookup runs after the recorder started, outside `_lock`. |
| Stop (delete no-audio / aborted / < 5 s, hand off to finalizer) | `App.stop` | `MonitorLoop.Stop`, `AppLogic.Discard`, `Finalizer.NoAudioFile` | done | Same rules and messages. **Fixed 2026-09-29**: the state is taken under `_lock`, `rec.Stop()` and the screen capture stop run outside it, so the tray never waits for them. |
| Discard prompt (`prompt_default` ask/skip/record) | `ask_discard`, `_ask_discard` | `App/Dialogs.cs` `AskDiscard`, `MonitorLoop.AskDiscardInBackground` | done | MessageBoxTimeoutW, 45 s, same flags and Czech text. "Only a clear Ano discards." |
| Yes/No box | `ask_yes_no` | `Dialogs.YesNo` | done | |
| Tk "record?" popup | `prompt` | — | missing (intended) | Dead code in the prototype: nothing calls it. |
| On-site + device policy | `_onsite_device`, `start_onsite`, `_start_onsite` | `MonitorLoop.OnsiteDevice`, `AppLogic.ChooseOnsiteDevice`, `StartOnsite` | done* | `device_missing` ask/fail/fallback and all messages are the same, and it is tested. *It becomes unusable on a real run: see gap 1 (with the default `onsite_upgrade = true`, a live recording replaces it after about 3 s). |
| Calendar offer (on-site) | `_calendar_offer`, `OFFER_WINDOW_S` | `MonitorLoop.CalendarOffer`, `AppLogic.ShouldOffer` | done | 30 s Outlook throttle, 240 s window, offered once per `start|subject` key, `calendar` mode skips Teams meetings. |
| On-site → live upgrade (`continues`) | `_upgrade_to_live` | `MonitorLoop.UpgradeToLive` | done* | The logic is the same. *It triggers falsely because of gap 1. |
| Playback recording | `start_playback` | `TrayApp.StartPlayback`, `Dialogs.InputBox` | done | Title dialog defaults to the foreground window's title. Teams windows are captured (see Start). |
| Manual recording | tray "Record now" | `MonitorLoop.StartManual` | done | |
| Outlook items (COM) | `outlook_items` | `Calendar/Outlook.cs` `Items` | done | Late-bound COM, IncludeRecurrences + Sort + Restrict, GetFirst/GetNext, times truncated to minutes. |
| Meeting pick (title / time, difflib 0.8) | `pick_meeting`, `candidates_at`, `_title_norm` | `Outlook.Pick`, `CandidatesAt`, `TitleNorm`, `CloseMatch/Ratio` | done | difflib ratio is re-implemented (no autojunk) and tested. |
| outlook_meeting (+ candidates) | `outlook_meeting` | `Outlook.Meeting` (`CalendarItem.Candidates`) | done | **Fixed 2026-09-29**: the candidates come with the match, at the start (as `outlook_meeting`). |
| Title fix-up at the end | `_finalize` (generic → window title) | `Finalizer.Finalize` | done | |
| Calendar re-match by window title | `_rematch` | `Finalizer.Rematch` | done | Tested. |
| Rename folder + files | `_rename_files` | `Contract/Naming.cs` `RenameFiles`, `Finalizer` | done | The prototype's `stem.name[17:]` off-by-one is noted. The result is the same, because a same-name rename is a no-op. |
| Mix (ffmpeg, mono 16 kHz, amix normalize=0) | `mix_audio`, `_ffmpeg` | `Recording/Mixer.cs` | done | Same ffmpeg lookup order (PATH, `TEAMSREC_FFMPEG_DIR`, winget Gyan.FFmpeg). |
| Sidecar fields | `_finalize` meta | `Contract/Sidecar.cs`, `Finalizer` | done | format, app, app_version, title, slug, source, start/end (ISO seconds), duration_s (banker's rounding like Python), stop_reason, title_source, audio_reopens, tracks (with `device`), teams_windows_seen, continues, call_app, mix, participants, calendar {source, subject, organizer, start/end to the minute, match, status, candidates ≤5}, audio_silent, screens. Differences: `app` = `teamsrec-capture` / `1.0.5` (matches the contract example), LF endings, written via `.json.tmp` + move. Unknown keys are kept on read. |
| Stop reasons | `STOP_REASONS` | `StopReasons.ToContract`, `AppLogic.StopReasonCode` | done | `app_crash` (orphans) is not in the contract's `stop_reason` enum. The prototype writes it too, so this is contract drift, not a port gap. |
| Post-recording hook | `POST_HOOK` | — | missing | It is `None` in the prototype, so no behaviour is lost. |
| Orphan recovery | `recover_orphans`, `_repair_wav`, `screens_on_disk` | `Recording/Orphans.cs` | done | **Fixed 2026-09-29**: `RepairWav`/`ReadHeader` walk the RIFF chunks (any `fmt ` size, LIST chunks); test `A_crashed_NAudio_wav_is_repaired_and_read`. Was: the logic is ported (glob, < 5 s delete, stem parse, mix, `app_crash`, `recovered`, recovered screens). But `RepairWav`/`ReadHeader` accept only the canonical 44-byte header (`data` at offset 36). NAudio's `WaveFileWriter` writes an 18-byte `fmt ` chunk, so `data` is at offset 38 (header 46 bytes; verified with NAudio 2.2.1). As a result, **no WAV written by the .NET recorder is ever recovered**: after a crash, kill or shutdown it gets no sidecar, so transcribe never sees it. The prototype's own `_repair_wav` would reject these files too. See gap 2. |
| Screen capture (per Teams window, 2 fps, 1600×900, x264 crf 26, fragmented mp4) | `ScreenCapture`, `grab_window`, `fit_canvas` | `Screen/ScreenCapture.cs`, `Screen/ScreenCaptureProcess.cs` | done | Same canvas, size filter (500×350), last-frame repeat, per-window files, titles, offsets. It pipes BGR24 instead of RGB24 (equivalent). **Fixed 2026-09-29**: runs in a child process like `ScreenCaptureProc` (`teamsrec-capture.exe --screen-capture <stem> <started>`, stops when its stdin closes, reports in `<stem>_screens.json`); after 45 s the child is killed (its encoders still finish their files) and the videos on disk are kept as `recovered`. |
| DPI awareness for capture | `screen_capture_child` → `SetProcessDpiAwarenessContext(-4)` | `Program.cs` / csproj | done | **Fixed 2026-09-29**: `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` (applied by `ApplicationConfiguration.Initialize()`). Was: no `ApplicationHighDpiMode` in the csproj and no `SetHighDpiMode` call, so WinForms runs as SystemAware, not PerMonitorV2. On a monitor whose DPI differs from the primary, window rectangles are virtualized, so frames come out scaled or cropped. |
| Settings server / API | `SettingsHandler`, `settings_state`, `save_settings`, `config_set`, `SETTINGS_MAP` | `Settings/SettingsServer.cs`, `Config/SettingsModel.cs`, `Config/TomlEditor.cs` | done | HttpListener on 127.0.0.1 (falls back to `localhost` when http.sys refuses). The same routes: `GET /`, `GET /api/settings`, `POST /api/settings`, `/api/test`, `/api/sound-panel`. `settings.html` is byte-identical and embedded as a resource. Additions: an Origin check on POST (CSRF) and `Cache-Control: no-store`. The TOML is edited in place, keeping comments and order. |
| Config load | `_config`, `CONFIG_PATH`, globals | `Config/AppConfig.cs` | done | `TEAMSREC_CONFIG`, else `%APPDATA%\teamsrec\teamsrec.toml`. Same keys and defaults. Python-like `str()`/`bool()` coercion. |
| Tray icon + menu | `App.__init__` menu, `status`, `_refresh`, `icon_image` | `App/TrayApp.cs`, `AppLogic.StatusText/Tooltip` | done | Same items, enable rules and status texts. Grey, red and yellow icons. The tooltip is truncated to 127 characters. Balloon tips with the title "teamsrec". |
| Test microphone (tray) | `test_microphone` | `MonitorLoop.TestMicrophone` | done | |
| Open folder / Quit | menu | `TrayApp.OpenFolder/Quit` | done | Quit stops with `app_quit` and waits up to 2 min for sidecars. The prototype's daemon finalize thread could be cut off. |
| Single instance (shared mutex) | `_single_instance` `Local\teamsrec-capture` | `Program.cs` | done | Same name, so the prototype and the port exclude each other. The smoke run logged "already running". |
| Logging | `logging.basicConfig` → `<out_dir>/teamsrec.log` | `App/FileLog.cs`, `Core.Log` | done | Same line format (`YYYY-MM-DD HH:MM:SS,mmm LEVEL msg`), shared-write open. |
| Windows session end (logoff/shutdown) | — (none) | `TrayApp.OnSessionEnding`, `MonitorLoop.EndSession` | done (1.1.1) | The prototype relied on orphan recovery. .NET closes the WAV files and writes the sidecar at once (`stop_reason` `session_end`, no mix, no new calendar lookup) and asks Windows for the time (ShutdownBlockReasonCreate). |

Tests: `legacy/smoke_test.py` has 18 tests. The .NET suite has 154 (App 24, Audio 10, Calendar 17,
Config 11, Contract 15, Detection 11, Finalizer 7, Watchdog 6). None of the tests touches real
devices, Outlook, ffmpeg capture, the tray or the HTTP listener.

## Gaps and risks, by priority (what breaks on a real call)

1. ~~Self-detection as a Teams call (blocker).~~ **Fixed 2026-09-29**. `MicUsers.TeamsInUse` matches "teams" anywhere in the
   ConsentStore key path, and `teamsrec-capture.exe` contains "teams". While the port records a microphone,
   Windows lists it as a mic user, so the port counts its own capture as a Teams call:
   - a live Teams call **never stops** at call end. It runs until you stop it from the tray or the 4 h cap;
   - an on-site recording is "upgraded" to live on the next tick with the default
     `onsite_upgrade = true`. The on-site part is under 5 s and gets deleted, and the live part never ends;
   - manual and playback recordings are not affected (manual ignores `in_call`, playback opens no mic).
   Fix: in `TeamsInUse`, match on the ident (the packaged name or the exe file name, not the full path) and
   skip `NotACall` plus our own exe, as `Current()` already does. Add a test with a
   `…#teamsrec-capture.exe` entry.
2. ~~Orphan recovery cannot read the port's own WAVs (high).~~ **Fixed 2026-09-29**. NAudio writes a 46-byte header, and
   `RepairWav`/`ReadHeader` need `data` at byte 36. A crash, kill, power loss or Windows shutdown during a
   recording leaves playable WAVs (they are flushed every 5 s) but **no sidecar**, so teamsrec-transcribe
   never processes the recording, and the prototype's recovery cannot help either. Fix: walk the RIFF chunks
   (`fmt ` of any size, then `data`) in both `RepairWav` and `ReadHeader`. Add a test that uses a file written
   by `WaveFileWriter`.
3. ~~DPI awareness missing for screen capture (medium).~~ **Fixed 2026-09-29**. Set PerMonitorV2
   (`<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` or
   `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` before `ApplicationConfiguration.Initialize()`).
   Without it, Teams windows on a second monitor with a different scale are captured scaled or cropped,
   which damages the name-label analysis.
4. ~~Screen capture is not isolated (medium).~~ **Fixed 2026-09-29**: child process, see the Screen capture row. It is a thread, not a child process. A hung `PrintWindow`
   can stall it for the rest of the call, leave ffmpeg processes open until the app exits, and drop those
   videos from `screens`. A native crash in GDI or the encoder pipe takes the audio recording down with it.
   Minimum fix: after a failed join, fill `screens` from the files on disk (as `screens_on_disk` did) and kill
   the leftover encoders. Better: run it as a child process (`teamsrec-capture.exe --screen-capture …`).
5. ~~Tray freezes while a recording stops (medium-low).~~ **Fixed 2026-09-29**: slow work outside `_lock`. `Stop()` holds `_lock` while streams close (up
   to 10 s writer join) and the screen capture shuts down (up to 30 s join plus 20 s per encoder).
   `TrayApp.UpdateMenu`/`RefreshIcon` take the same lock on the UI thread. Snapshot the state under the lock
   and do the slow work outside it.
6. ~~Loopback keep-alive is new and untested (medium-low).~~ **Fixed 2026-09-29**: removed, as in the prototype (the only
   output here is a Bluetooth dongle; a call keeps the audio engine running anyway). Playing silence on the default output keeps
   the sys track continuous. That is likely good, but it has never run with a Bluetooth headset (it keeps
   the link awake; check that it does not force HFP or change the mix). The prototype had no such stream.
7. ~~Playback no longer records Teams windows (low).~~ **Fixed 2026-09-29**: recorded again. It is a divergence from the prototype. Decide
   whether it was intended. Name labels in a played-back Teams recording were a source of speaker names.
8. ~~Calendar candidates are looked up at finalize, not at start (low).~~ **Fixed 2026-09-29**: with the match, at the start. An Outlook closed after the call
   leaves `calendar.candidates` empty. The Outlook COM call also now runs on a thread-pool (MTA) thread,
   which should work for an out-of-process server but has not been tried.
9. **Inherited from the prototype, unchanged (note only):**
   - the sys track is the loopback of the *Windows default* output, so a Teams output set to another
     device records silence (the watchdog warns after 90 s);
   - a mic switch to a different format leaves the mic track silent for the rest of the call;
   - no handling of session end;
   - `app_crash` is not in the contract enum;
   - the Outlook Object Model Guard can prompt when reading recipients.
10. **Untested live:** recording, reopen, screen capture (child process), Outlook, tray balloons, the
    settings page in a browser, and the input box. Do the first live run only after gaps 1 and 2 are
    fixed, with the prototype quit (shared mutex).
