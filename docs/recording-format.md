# teamsrec recording format — v1

The contract between `teamsrec-capture` (producer) and `teamsrec-transcribe` (consumer).
Capture owns the format. Every incompatible change raises `format` and gets a new section in this document.
Machine-readable form of the sidecar: [`recording.schema.json`](recording.schema.json).

## Folder and naming

```
<OUT_DIR>/
  <YYYY>/
    <MM>/
      <stem>/                                one folder per recording, stem = <YYYY-MM-DD>_<HHMM>_<slug>
        <stem>.json                          sidecar (the recording's metadata)    capture
        <stem>_sys.wav                       system sound (loopback)               capture
        <stem>_mic.wav                       microphone (source=live, onsite)      capture
        <stem>_mix.wav                       mono 16 kHz mix for ASR               capture
        <stem>_screen<N>.mp4                 Teams window videos (live)            capture
        <stem>.transcript.json               normalised transcript                 transcribe
        <stem>.speakers.json                 speaker → name mapping                transcribe (by hand / page)
        <stem>.speakers_video.json           speaker timeline from the video       transcribe (video / window videos)
        <stem>.txt                           readable transcript                   transcribe
        <stem>.srt                           subtitles                             transcribe
        <stem>.summary.md                    minutes, summary, action items        transcribe
        <stem>.summary.<model>.md            minutes from another model            transcribe
        <stem>.timings.json                  how long each part of the last jobs took  transcribe (derived)
```

- Recordings are split into folders `<YYYY>/<MM>/<stem>/` by the local start time of the recording. Each recording
  has its own folder named by its stem; all its files lie in it and carry the stem in their name, so they stay
  unambiguous when copied or sent elsewhere. Deleting or moving a recording is an operation on one folder.
- `stem` = `<YYYY-MM-DD>_<HHMM>_<slug>`; the time is the local start time of the recording.
- `slug`: the meeting title after NFKD normalisation, without diacritics, `[^A-Za-z0-9]+` → `-`, lower case, at most 60
  characters, fallback `teams-call`.
- A recording is **complete** only once the sidecar `.json` exists. Until then nobody else touches the folder. Capture
  writes the sidecar as its last step.
- Recordings shorter than the limit (default 120 s) and aborted recordings are deleted by capture without a sidecar.

### Renaming

A meeting's title can be changed afterwards (`teamsrec-transcribe rename`, the review page), also before the
transcript exists. The stem is recomputed from the same date and time and the new slug; the folder and all `<stem>.*`
files are renamed, and the sidecar (`title`, `slug`, names of the tracks and the mix), the minutes' headings and the
stem references in `_speakers/voiceprints.json` are updated. The same slug from a differently written title does not
change the folder. A clash with an existing folder is refused.

## Retention

Audio is temporary, the transcript permanent. The WAV files (`_sys`, `_mic`, `_mix`) and the window videos can be
deleted after a successful transcription (by hand or with `purge-audio` in teamsrec-transcribe). The sidecar `.json`,
`.transcript.json`, `.speakers.json`, `.txt`, `.srt` and `.summary*.md` stay. Consumers must therefore expect that the
files listed in `tracks` and `mix` may no longer exist; the sidecar remains the record that the recording took place.

## Sidecar `<stem>.json`

```json
{
  "format": 1,
  "app": "teamsrec-capture",
  "app_version": "1.0.5",
  "title": "Týdenní sync",
  "slug": "tydenni-sync",
  "source": "live",
  "start": "2026-09-03T14:00:12",
  "end": "2026-09-03T14:47:50",
  "duration_s": 2858,
  "stop_reason": "call_ended",
  "language": "cs",
  "tracks": {
    "sys": { "file": "2026-09-03_1400_tydenni-sync_sys.wav", "sample_rate": 48000, "channels": 2 },
    "mic": { "file": "2026-09-03_1400_tydenni-sync_mic.wav", "sample_rate": 48000, "channels": 1 }
  },
  "mix": { "file": "2026-09-03_1400_tydenni-sync_mix.wav", "sample_rate": 16000, "channels": 1 },
  "participants": [
    { "name": "Jana Nováková", "email": "jana@example.com", "role": "organizer" }
  ],
  "teams_windows_seen": ["Týdenní sync | Microsoft Teams"]
}
```

| Field | Type | Required | Meaning |
|---|---|---|---|
| `format` | int | yes | version of this format, now `1` |
| `app`, `app_version` | string | yes | who made the recording |
| `title` | string | yes | meeting title (from the Teams window, the calendar, or typed by hand) |
| `slug` | string | yes | the slug used in the file names |
| `source` | enum | yes | `live` = a Teams call, `playback` = playing back a stored recording, `manual` = manual recording without detection, `import` = an external file (e.g. a meeting recording downloaded from Teams), `onsite` = an on-site meeting from one microphone (only the `mic` track, everybody is on it, the user is not named from it; names from voice prints and diarization) |
| `start`, `end` | ISO 8601 local time without zone | yes | start and end of recording |
| `duration_s` | int | yes | length in seconds |
| `stop_reason` | enum | yes | `call_ended`, `max_duration`, `user_stop`, `silence`, `app_quit`, `onsite_upgraded` (the on-site meeting moved to Teams), `session_end` (Windows logged off, restarted or shut down during the recording: the files were closed properly, the mix is left to teamsrec-transcribe), `app_crash` (recovered at the next start after the app died), `n/a` (for `import`) |
| `continues` | string | no | stem of the previous part: a live recording continuing an on-site recording that turned into a Teams call |
| `language` | BCP‑47 | no | expected language of the meeting, default `cs` |
| `tracks.sys` | track | no* | loopback; the only track for `playback` and `manual`; missing for `import` and `onsite` (*required for all but `import` and `onsite`; also missing when Windows had no output device during the whole call – the microphone is recorded alone, and a loopback that joins later starts with silence from the start of the recording) |
| `tracks.mic` | track | no | microphone = the user; missing for `playback` |
| `mix` | track | no | mono 16 kHz sum of all tracks, input for ASR; missing when the mix failed; for `import` the only and required track |
| `origin_file` | string | no | only `import`: the original name of the file the recording came from |
| `origin_path` | string | no | only `import`: absolute path of the original file at import time |
| `metadata_source` | enum | no | only `import`: where `title` and `start` come from: `teams-name`, `container`, `file`, `user` |
| `participants[]` | object | no | from the calendar, if available; `role` ∈ `organizer`, `required`, `optional`, `self` |
| `call_app` | string | no | a call outside Teams: the app (`Zoom`, `Webex`, `Slack`, `WhatsApp`, `Chrome (Meet – …)` …); missing for Teams |
| `audio_purged` | date | no | audio and window videos deleted (`purge-audio`); transcript, minutes and names stay |
| `audio_silent` | bool | no | `true` = every track held only digital silence (the device delivered no data); the recording is not transcribed |
| `audio_reopens` | int | no | how many times the watchdog had to reopen the audio streams |
| `teams_windows_seen[]` | string | no | debugging information |

The `track` object: `file` (file name only, no path), `sample_rate` (Hz), `channels` (1 or 2); optionally `device` (the device it was recorded from, the last one) and `devices` (`[{"device", "from_s"}]`, only when the device changed during the recording: a headset switched on, another output taken by the call – a later device with another rate is converted to the file's). WAV is always 16-bit PCM.

## Imported recordings (`source: import`)

A meeting recording made by Teams itself (file `<Title>-YYYYMMDD_HHMMSS-Meeting Recording.mp4`) or another audio/video
file gets into the recordings folder with `teamsrec-transcribe import <file>`:

- `title` and `start` are taken from the file name by the Teams pattern; when the pattern does not match, `title` =
  the file name without extension and `start` = the file's modification time. Both can be overridden by parameters.
- The audio is converted to `<stem>_mix.wav` (mono 16 kHz PCM); `tracks` is an empty object, `stop_reason` = `n/a`,
  `origin_file` = the original name.
- Diarization works only on `mix`; there is no `mic` track, so the user is not named automatically.
- The original file is neither copied nor deleted.

### Ad-hoc files without a sidecar

The rule: **the sidecar is created by the tool that touches the recording first.** A recording without a sidecar is not
an error but input for an import. This holds for any audio or video (mp4, m4a, mp3, wav, webm, mkv…), not only Teams
recordings.

- `teamsrec-transcribe transcribe <file>` on a file without a sidecar imports it implicitly and goes on with the
  transcription. There is no need to call `import` separately.
- The metadata are derived in this order and can be overridden by `--title`, `--start`, `--language`, `--participants`:
  1. the Teams recording name pattern (`<Title>-YYYYMMDD_HHMMSS-Meeting Recording`),
  2. `creation_time` from the container's metadata (ffprobe),
  3. the file name without extension as `title` and the file's modification time as `start`.
- The sidecar also gets `origin_path` (absolute path of the original file) and `metadata_source` (`teams-name`,
  `container`, `file`, `user`), so it shows how reliable `title` and `start` are.
- The recording is always normalised into `<OUT_DIR>/YYYY/MM/<stem>/` with `_mix.wav`; no other layout exists. The
  original file stays where it is.
- **Inbox `<OUT_DIR>/_inbox/`:** whatever the user drops here is imported at the next run of
  `teamsrec-transcribe process` (or by a watcher); after a successful import the file moves to `_inbox/done/`.
- Speaker analysis from the video only tries the Teams layout. When it finds no highlighted name labels in the video
  (Zoom, Meet, another layout), it quietly ends and the diarization gives the speakers.

## Transcript `<stem>.transcript.json`

The normalised output of every transcription provider. Everything behind the provider (naming speakers, export,
summary) works only with this file.

```json
{
  "format": 1,
  "provider": "whisperx",
  "provider_version": "3.3.1",
  "model": "large-v3",
  "language": "cs",
  "created": "2026-09-03T16:10:00",
  "speakers": ["SPEAKER_00", "SPEAKER_01", "me"],
  "segments": [
    { "start": 0.42, "end": 3.10, "speaker": "SPEAKER_00", "track": "sys", "text": "Tak začneme." },
    { "start": 3.30, "end": 5.80, "speaker": "me", "track": "mic", "text": "Dobře, první bod." }
  ]
}
```

- `speaker`: the provider's identifier; `me` is reserved for the user (derived from the `mic` track when it exists).
  A segment the diarization gave nobody has no speaker (`null`); it is listed as `UNKNOWN` in `speakers`, exported as
  `?`, and can be given a speaker later (then `"assigned": "manual"`).
- `track`: `sys`, `mic` or `mix`, by the track the segment came from.
- `words[]` in a segment is optional: `{ "start", "end", "word", "score" }`.
- Times are seconds from the start of the recording, shared by all tracks.
- `language` in a segment is optional (BCP‑47). It is present when the transcript was made **per speaker**: each
  speaker's language is determined from their longest stretches, the transcription runs once per language present and
  the segments are put together by speaker. The root `language` is then the majority language. (Decision 2026-09-04,
  see teamsrec-transcribe `lab/FINDINGS.md`.)

### Calendar (since 2026-09-14, optional)

With `[calendar] outlook = true` in the shared configuration, capture at the start of a call and transcribe at import
take from classic Outlook on this PC (COM, locally, no network) the meeting running at the start time (start −10 min …
end +5 min; the one that really contains the time wins, then Teams meetings). The sidecar then has `participants`
(participants' names) and

```json
"calendar": {"source": "outlook", "subject": "WFMS sync", "organizer": "Jana Nováková",
             "start": "2026-09-14T08:30", "end": "2026-09-14T09:15"}
```

Matching: first by title (the Teams window title of a live call, the file name of an import; equal or approximately
equal to the meeting's subject) – `match: "title"`; only without a match by time – `match: "time"`, which is just a
guess (an ad-hoc call during a scheduled meeting, two parallel meetings). `status` is `auto` | `confirmed`; the review
page shows a Schůzka panel with the sources (`title_source`: `calendar` | `window` | `manual` | `file`, for participants
`source`: `calendar` | `manual`) and can confirm the link, detach it (removes the calendar participants, the title
stays) or link another meeting (`candidates` in the sidecar, otherwise a live query to Outlook; title, participants and
folder follow). The organiser and the planned time from the calendar go into the minutes. Without Outlook (the new
Outlook without COM, another machine) nothing changes.

### Teams window videos `<stem>_screen<N>.mp4` (live recordings, since 2026-09-14)

During a live call (and while playing back a recording) capture saves each Teams window as a video at 2 frames per
second (x264, a 1600×900 canvas keeping the aspect ratio, black borders). Teams has several windows – the call, a
pop-out gallery, shared content – and they come and go during a call, so each window has its own file and an entry in
the sidecar:

```json
"screens": [
  {"file": "<stem>_screen1.mp4", "fps": 2, "width": 1600, "height": 900, "start_offset_s": 0.0,
   "end_offset_s": 1748.0, "frames": 3496, "titles": ["Připojení ke schůzce | Microsoft Teams", "WFMS sync | Microsoft Teams"]}
]
```

`start_offset_s` is the video's offset from the start of the recording. A minimised window cannot be captured; the last
frame is repeated so that the timeline matches the audio. A video found on disk after the capture process crashed has
`"recovered": true`, `frames: -1` and no `end_offset_s`. Transcribe runs every meeting-window video (windows with Teams
navigation, e.g. Calendar, are skipped) through the active-speaker analysis: in a live window the speaking tile gets a
thin outline in the Teams accent colour (unlike a downloaded recording, where the name label is coloured); the name
label is read by OCR from the tile's bottom-left corner (name candidates = the people registry + participants). The
user's own tile never gets the outline; the microphone track names the user. The timelines are shifted and merged into
`speakers_video.json` with `source: "teams-screen"`. Verified 2026-09-16 on a live stand-up.

### Speaker sources and their priority

The audio transcription is always the same. Only where "who speaks" comes from differs, by what the recording has, not
by its extension. The groups are the diarization's voice groups; the sources below only name them (the microphone also
single replies), so a group never mixes voices – better more groups that the user merges than one group of several
people (since 2026-10-05):

1. Track `mic` (live recording) – the diarization label that coincides with microphone activity gets the user's name
   from the configuration (`[user] name`); also a single reply (≥ 1.5 s) with the microphone active ≥ 80 %, including one
   the diarization gave nobody. "Active" = at least 10 dB above the track's floor **and** at least −55 dBFS (a headset
   with a noise gate sends a −80…−90 dB residue while the others talk). Verified 2026-09-10: 89 % activity for the user,
   13–21 % for the others.
2. **Voice prints** (below) – a group recognised by voice gets the person.
3. `speakers_video.json` (a Teams recording with video, or the window videos) – names **whole groups only**, and only
   groups that neither the microphone nor a voice print named: when the video gives one name at least 10 s, most of
   the covered time and at least a quarter of all the group said (2026-09-16: a short highlight otherwise "owned" 47
   minutes of someone else's speech). A person the voice already found in another group gets no second group. Names
   from a live window count only when they match a participant. Until 2026-10-05 the video also named single replies
   by the highlighted tile; a live window keeps the previous speaker highlighted, so that made mixed groups ("Peter"
   with replies of whoever was highlighted, next to the voice group that really was Peter) – no longer done.
4. Diarization – always runs and makes the groups; the ones nobody named stay `SPEAKER_XX` until the user names them in
   `speakers.json`. Two groups of one person are merged on the page by giving them the same name.

**Voice prints** (`_speakers/voiceprints.json`, since 2026-09-11, opt-in): the diarization returns an embedding per
   label (pyannote community-1); the transcript stores it in `speaker_embeddings` (key = the resulting name/label, a unit
   vector). When a label gets a person **confirmed** (the page, `label-speakers`, the user's microphone track), the embedding is stored under the
   person (at most 10 per person, with the stem and label of origin; near duplicates ≥ 0.95 are dropped). In a new
   recording unknown labels are compared by cosine similarity; a match ≥ `threshold` with a lead ≥ `margin` over the
   second best person writes the person into `speakers.json` (like a manual assignment, can be corrected) and into the
   transcript's `voice_matches` `{label: {person, score}}`. Source `voiceprint` in `speaker_sources`.

The transcript may have `removed_speakers`: a list of labels whose replies the user deleted as noise
(`{"label", "segments", "at"}`); their segments are not in the transcript. A new transcript (`--force`) brings them back.
`merged_speakers` records merges of labels of one person; each moved segment keeps `merged_from`, so a merge can be
undone.

## Speakers from video `<stem>.speakers_video.json`

Made at `import` of a Teams recording with video (and from the window videos of live recordings). Teams highlights the
active speaker's name label; the frame analysis gives intervals per name when that person spoke. Segments of the
transcript get the name with the largest overlap; names are from OCR, refined by the `participants` list.

```json
{ "format": 1, "source": "teams-video", "fps": 2,
  "speakers": { "Jana Nováková": [[12.0, 15.5], [40.0, 61.5]], "Petr Svoboda": [[15.5, 40.0]] } }
```

## People `_speakers/people.json`

A registry of people outside the recordings, one file for the whole `OUT_DIR`:

```json
{ "format": 1, "people": [
  { "id": "petr-svoboda", "first": "Petr", "last": "Svoboda", "nick": "Péťa", "display": "", "aliases": ["Petr Svoboda (NG)"] }
] }
```

`display` = `first` | `full` | `nick` | empty (default from the configuration `[people] display`, default `nick`). Mode
`nick` without a nickname means the first name. The transcript and `speakers.json` keep the person's identifier (`id`)
or the literal name from the video or the microphone; the displayed form is decided only at export and summary time.
Unregistered names are printed literally. A person may have `"voiceprint": false` (opted out of voice recognition).

File `_speakers/voiceprints.json`: `{"format": 1, "model": "<diarization model>", "people": {"<id>": [{"v": [...],
"stem": "...", "label": "...", "added": "..."}]}}`. Derived, deletable; one person's prints are deleted by
`people forget-voice`.

## Speakers `<stem>.speakers.json`

Manual mapping after transcription. When it exists, export and summary use names instead of identifiers.

```json
{ "SPEAKER_00": "Jana Nováková", "SPEAKER_01": "Petr Svoboda", "me": "Jan Novák" }
```

## Language per speaker (transcript)

`<stem>.transcript.json` has `language` at the top (the meeting's language) and `languages` (all languages in the
transcript). In a mixed meeting a reply can have its own `segments[].language` when its speaker clearly speaks another
of the configured languages (`[transcribe] per_speaker_language`); a missing `language` on a reply = the meeting's
language.

## Capture status `%TEMP%\teamsrec-capture.json` (not part of a recording)

Written by teamsrec-capture on every change, read by the review server: `{"app", "version", "pid", "running",
"recording", "title", "stem", "source", "started", "updated"}`. A file whose `pid` is no longer running counts as
"not running". The review page shows a red dot while `recording` is true, and the server can stop its processing
for the recording (`[transcribe] when_recording`).
