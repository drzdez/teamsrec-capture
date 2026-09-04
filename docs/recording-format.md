# teamsrec recording format — v1

Kontrakt mezi `teamsrec-capture` (producent) a `teamsrec-transcribe` (konzument).
Formát vlastní capture. Každá nekompatibilní změna zvýší `format` a dostane novou sekci v tomto dokumentu.
Strojově čitelná podoba sidecaru: [`recording.schema.json`](recording.schema.json).

## Adresář a pojmenování

```
<OUT_DIR>/
  <YYYY>/
    <MM>/
      <YYYY-MM-DD>_<HHMM>_<slug>.json        sidecar (metadata nahrávky)      capture
      <YYYY-MM-DD>_<HHMM>_<slug>_sys.wav     systémový zvuk (loopback)         capture
      <YYYY-MM-DD>_<HHMM>_<slug>_mic.wav     mikrofon (jen source=live)        capture
      <YYYY-MM-DD>_<HHMM>_<slug>_mix.wav     mono 16 kHz mix pro ASR           capture
      <YYYY-MM-DD>_<HHMM>_<slug>.transcript.json   normalizovaný přepis        transcribe
      <YYYY-MM-DD>_<HHMM>_<slug>.speakers.json     mapování mluvčí → jméno     transcribe (ručně)
      <YYYY-MM-DD>_<HHMM>_<slug>.speakers_video.json   časová osa mluvčích z videa   transcribe (import s videem)
      <YYYY-MM-DD>_<HHMM>_<slug>.txt               přepis pro čtení            transcribe
      <YYYY-MM-DD>_<HHMM>_<slug>.srt               titulky                     transcribe
      <YYYY-MM-DD>_<HHMM>_<slug>.summary.md        zápis, shrnutí, úkoly       transcribe
```

- Nahrávky jsou členěny do adresářů `<YYYY>/<MM>/` podle lokálního času začátku nahrávky. Všechny soubory jedné nahrávky leží v jednom adresáři vedle sebe.
- `stem` = `<YYYY-MM-DD>_<HHMM>_<slug>`; čas je lokální čas začátku nahrávky.
- `slug`: název schůzky po NFKD normalizaci, bez diakritiky, `[^A-Za-z0-9]+` → `-`, lowercase, max 60 znaků, fallback `teams-call`.
- Nahrávka je **hotová**, až když existuje sidecar `.json`. Do té doby do adresáře nikdo jiný nesahá. Capture zapisuje sidecar jako poslední krok.
- Nahrávky kratší než limit (výchozí 120 s) a zrušené nahrávky capture smaže bez sidecaru.

## Retence

Audio je dočasné, přepis trvalý. WAV soubory (`_sys`, `_mic`, `_mix`) lze po úspěšné transkripci smazat
(ručně nebo příkazem `purge-audio` v teamsrec-transcribe). Sidecar `.json`, `.transcript.json`,
`.speakers.json`, `.txt`, `.srt` a `.summary.md` zůstávají. Konzumenti proto musí počítat s tím, že soubory
uvedené v `tracks` a `mix` už nemusí existovat; sidecar zůstává záznamem o tom, že nahrávka proběhla.

## Sidecar `<stem>.json`

```json
{
  "format": 1,
  "app": "teamsrec-capture",
  "app_version": "0.1.0",
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

| Pole | Typ | Povinné | Význam |
|---|---|---|---|
| `format` | int | ano | verze tohoto formátu, nyní `1` |
| `app`, `app_version` | string | ano | kdo nahrávku pořídil |
| `title` | string | ano | název schůzky (z okna Teams, kalendáře, nebo zadaný ručně) |
| `slug` | string | ano | slug použitý ve jménech souborů |
| `source` | enum | ano | `live` = hovor v Teams, `playback` = přehrávání uloženého záznamu, `manual` = ruční nahrávání bez detekce, `import` = externí soubor (např. záznam schůzky stažený z Teams) |
| `start`, `end` | ISO 8601 lokální čas bez zóny | ano | začátek a konec nahrávání |
| `duration_s` | int | ano | délka v sekundách |
| `stop_reason` | enum | ano | `call_ended`, `max_duration`, `user_stop`, `silence`, `app_quit`, `n/a` (u `import`) |
| `language` | BCP‑47 | ne | očekávaný jazyk schůzky, výchozí `cs` |
| `tracks.sys` | track | ne* | loopback; u `playback` a `manual` jediná stopa; u `import` chybí (*povinné pro vše kromě `import`) |
| `tracks.mic` | track | ne | mikrofon = uživatel; chybí u `playback` |
| `mix` | track | ne | mono 16 kHz součet všech stop, vstup pro cloudové ASR; chybí, když mix selhal; u `import` jediná a povinná stopa |
| `origin_file` | string | ne | jen `import`: původní název souboru, ze kterého nahrávka vznikla |
| `participants[]` | objekt | ne | z kalendáře, pokud dostupné; `role` ∈ `organizer`, `required`, `optional`, `self` |
| `teams_windows_seen[]` | string | ne | ladicí informace |

Objekt `track`: `file` (jen jméno souboru, ne cesta), `sample_rate` (Hz), `channels` (1 nebo 2). WAV je vždy PCM 16‑bit.

## Importované nahrávky (`source: import`)

Záznam schůzky pořízený samotným Teams (soubor `<Název>-YYYYMMDD_HHMMSS-Meeting Recording.mp4`) nebo jiný
audio/video soubor se do adresáře nahrávek dostane příkazem `teamsrec-transcribe import <soubor>`:

- `title` a `start` se vezmou z názvu souboru podle vzoru Teams; když vzor nesedí, `title` = název souboru bez přípony a `start` = čas změny souboru. Obojí lze přepsat parametry.
- Zvuk se převede na `<stem>_mix.wav` (mono 16 kHz PCM); `tracks` je prázdný objekt, `stop_reason` = `n/a`, `origin_file` = původní název.
- Diarizace pracuje jen nad `mix`, stopa `mic` neexistuje, takže mluvčí `me` se neurčuje automaticky.
- Původní soubor se nekopíruje ani nemaže.

## Přepis `<stem>.transcript.json`

Normalizovaný výstup každého transkripčního providera. Vše za providerem (pojmenování mluvčích, export, summary) pracuje jen s tímto souborem.

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

- `speaker`: identifikátor providera; `me` je vyhrazeno pro uživatele (odvozeno ze stopy `mic`, když existuje).
- `track`: `sys`, `mic`, nebo `mix`, podle toho, ze které stopy segment vznikl.
- `words[]` v segmentu je volitelné: `{ "start", "end", "word", "score" }`.
- Časy jsou sekundy od začátku nahrávky, společné pro všechny stopy.
- `language` v segmentu je volitelné (BCP‑47). Přítomné, když byl přepis dělán **per mluvčí**: jazyk každého mluvčího se
  určí z jeho nejdelších úseků, přepis se spustí jednou pro každý přítomný jazyk a segmenty se poskládají podle mluvčího.
  Kořenové `language` je pak jazyk většinový. (Rozhodnutí 2026-09-04, viz teamsrec-transcribe `lab/FINDINGS.md`.)

### Zdroje mluvčích a jejich priorita

Přepis zvuku je vždy stejný. Liší se jen, odkud se bere „kdo mluví“, a to podle toho, co nahrávka má, ne podle přípony:

1. `speakers_video.json` (import záznamu Teams s videem) – segment dostane jméno s největším překryvem.
2. Stopa `mic` (živá nahrávka) – co je slyšet v mikrofonu, je `me`.
3. Diarizace – vždy se spouští, slouží jako záloha pro segmenty bez překryvu a pro účastníky, kteří na videu nejsou.
   Ti zůstávají jako `SPEAKER_XX`, dokud je uživatel nepojmenuje v `speakers.json`.

## Mluvčí z videa `<stem>.speakers_video.json`

Vzniká při `import` záznamu Teams s videem. Teams zvýrazňuje jmenovku aktivního mluvčího; analýza snímků dá pro každé
jméno intervaly, kdy mluvilo. Je to **přednostní zdroj mluvčích**: segmentům přepisu se přiřadí jméno s největším
překryvem, diarizace slouží jen jako záloha pro segmenty bez překryvu. Jména jsou z OCR, upřesněná seznamem `participants`.

```json
{ "format": 1, "source": "teams-video", "fps": 2,
  "speakers": { "Jana Nováková": [[12.0, 15.5], [40.0, 61.5]], "Petr Svoboda": [[15.5, 40.0]] } }
```

## Mluvčí `<stem>.speakers.json`

Ruční mapování po transkripci. Když existuje, export a summary používají jména místo identifikátorů.

```json
{ "SPEAKER_00": "Jana Nováková", "SPEAKER_01": "Petr Svoboda", "me": "Zdeněk Zdražil" }
```
