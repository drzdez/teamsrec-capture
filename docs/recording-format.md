# teamsrec recording format — v1

Kontrakt mezi `teamsrec-capture` (producent) a `teamsrec-transcribe` (konzument).
Formát vlastní capture. Každá nekompatibilní změna zvýší `format` a dostane novou sekci v tomto dokumentu.
Strojově čitelná podoba sidecaru: [`recording.schema.json`](recording.schema.json).

## Adresář a pojmenování

```
<OUT_DIR>/
  <YYYY>/
    <MM>/
      <stem>/                                jedna složka na nahrávku, stem = <YYYY-MM-DD>_<HHMM>_<slug>
        <stem>.json                          sidecar (metadata nahrávky)      capture
        <stem>_sys.wav                       systémový zvuk (loopback)         capture
        <stem>_mic.wav                       mikrofon (jen source=live)        capture
        <stem>_mix.wav                       mono 16 kHz mix pro ASR           capture
        <stem>.transcript.json               normalizovaný přepis              transcribe
        <stem>.speakers.json                 mapování mluvčí → jméno           transcribe (ručně)
        <stem>.speakers_video.json           časová osa mluvčích z videa       transcribe (import s videem)
        <stem>.txt                           přepis pro čtení                  transcribe
        <stem>.srt                           titulky                           transcribe
        <stem>.summary.md                    zápis, shrnutí, úkoly             transcribe
```

- Nahrávky jsou členěny do adresářů `<YYYY>/<MM>/<stem>/` podle lokálního času začátku nahrávky. Každá nahrávka má
  vlastní složku pojmenovanou stemem; všechny její soubory leží v ní a nesou stem v názvu, takže zůstávají
  jednoznačné i po zkopírování nebo odeslání jinam. Smazání či přesun nahrávky je operace s jednou složkou.
- `stem` = `<YYYY-MM-DD>_<HHMM>_<slug>`; čas je lokální čas začátku nahrávky.
- `slug`: název schůzky po NFKD normalizaci, bez diakritiky, `[^A-Za-z0-9]+` → `-`, lowercase, max 60 znaků, fallback `teams-call`.
- Nahrávka je **hotová**, až když existuje sidecar `.json`. Do té doby do adresáře nikdo jiný nesahá. Capture zapisuje sidecar jako poslední krok.
- Nahrávky kratší než limit (výchozí 120 s) a zrušené nahrávky capture smaže bez sidecaru.

### Přejmenování

Název schůzky lze změnit dodatečně (`teamsrec-transcribe rename`, kontrolní stránka). Stem se přepočítá ze
stejného data a času a nového slugu; složka i všechny soubory `<stem>.*` se přejmenují, sidecar (`title`, `slug`,
názvy stop a mixu), nadpisy zápisů a odkazy na stem v `_speakers/voiceprints.json` se opraví. Stejný slug
z jinak zapsaného názvu složku nemění. Kolize s existující složkou se odmítne.

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
| `source` | enum | ano | `live` = hovor v Teams, `playback` = přehrávání uloženého záznamu, `manual` = ruční nahrávání bez detekce, `import` = externí soubor (např. záznam schůzky stažený z Teams), `onsite` = schůzka na místě z jednoho mikrofonu (jen stopa `mic`, na ní jsou všichni, uživatel se z ní nepojmenovává; jména z otisků a diarizace) |
| `start`, `end` | ISO 8601 lokální čas bez zóny | ano | začátek a konec nahrávání |
| `duration_s` | int | ano | délka v sekundách |
| `stop_reason` | enum | ano | `call_ended`, `max_duration`, `user_stop`, `silence`, `app_quit`, `onsite_upgraded` (schůzka na místě přešla do Teams), `n/a` (u `import`) |
| `continues` | string | ne | stem předchozí části: živá nahrávka navazuje na nahrávku na místě, která přešla do hovoru v Teams |
| `language` | BCP‑47 | ne | očekávaný jazyk schůzky, výchozí `cs` |
| `tracks.sys` | track | ne* | loopback; u `playback` a `manual` jediná stopa; u `import` a `onsite` chybí (*povinné pro vše kromě `import` a `onsite`) |
| `tracks.mic` | track | ne | mikrofon = uživatel; chybí u `playback` |
| `mix` | track | ne | mono 16 kHz součet všech stop, vstup pro cloudové ASR; chybí, když mix selhal; u `import` jediná a povinná stopa |
| `origin_file` | string | ne | jen `import`: původní název souboru, ze kterého nahrávka vznikla |
| `origin_path` | string | ne | jen `import`: absolutní cesta k původnímu souboru v době importu |
| `metadata_source` | enum | ne | jen `import`: odkud jsou `title` a `start`: `teams-name`, `container`, `file`, `user` |
| `participants[]` | objekt | ne | z kalendáře, pokud dostupné; `role` ∈ `organizer`, `required`, `optional`, `self` |
| `audio_silent` | bool | ne | `true` = ve všech stopách bylo jen digitální ticho (zařízení nedodalo data); nahrávka se nepřepisuje |
| `audio_reopens` | int | ne | kolikrát musel hlídač znovu otevřít zvukové streamy |
| `teams_windows_seen[]` | string | ne | ladicí informace |

Objekt `track`: `file` (jen jméno souboru, ne cesta), `sample_rate` (Hz), `channels` (1 nebo 2). WAV je vždy PCM 16‑bit.

## Importované nahrávky (`source: import`)

Záznam schůzky pořízený samotným Teams (soubor `<Název>-YYYYMMDD_HHMMSS-Meeting Recording.mp4`) nebo jiný
audio/video soubor se do adresáře nahrávek dostane příkazem `teamsrec-transcribe import <soubor>`:

- `title` a `start` se vezmou z názvu souboru podle vzoru Teams; když vzor nesedí, `title` = název souboru bez přípony a `start` = čas změny souboru. Obojí lze přepsat parametry.
- Zvuk se převede na `<stem>_mix.wav` (mono 16 kHz PCM); `tracks` je prázdný objekt, `stop_reason` = `n/a`, `origin_file` = původní název.
- Diarizace pracuje jen nad `mix`, stopa `mic` neexistuje, takže mluvčí `me` se neurčuje automaticky.
- Původní soubor se nekopíruje ani nemaže.

### Ad-hoc soubory bez sidecaru

Pravidlo: **sidecar vytvoří ten nástroj, který se nahrávky dotkne jako první.** Nahrávka bez sidecaru není chyba,
ale vstup pro import. Platí pro libovolné audio nebo video (mp4, m4a, mp3, wav, webm, mkv…), nejen pro záznamy Teams.

- `teamsrec-transcribe transcribe <soubor>` nad souborem bez sidecaru provede import implicitně a pokračuje přepisem.
  Není potřeba volat `import` zvlášť.
- Metadata se odvozují v tomto pořadí a lze je přepsat parametry `--title`, `--start`, `--language`, `--participants`:
  1. vzor názvu záznamu Teams (`<Název>-YYYYMMDD_HHMMSS-Meeting Recording`),
  2. `creation_time` z metadat kontejneru (ffprobe),
  3. název souboru bez přípony jako `title` a čas změny souboru jako `start`.
- Sidecar dostane navíc `origin_path` (absolutní cesta k původnímu souboru) a `metadata_source` (`teams-name`,
  `container`, `file`, `user`), aby bylo vidět, jak spolehlivé `title` a `start` jsou.
- Nahrávka se vždy normalizuje do `<OUT_DIR>/YYYY/MM/<stem>/` s `_mix.wav`; jiné rozložení neexistuje.
  Původní soubor zůstává na místě.
- **Schránka `<OUT_DIR>/_inbox/`:** cokoli sem uživatel přetáhne, se importuje při dalším běhu
  `teamsrec-transcribe process` (nebo watcherem), soubor se po úspěšném importu přesune do `_inbox/done/`.
- Analýza mluvčích z videa se pokusí jen o rozložení Teams. Když ve videu nenajde zvýrazněné jmenovky
  (Zoom, Meet, jiný layout), tiše skončí a mluvčí dá diarizace.

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

### Kalendář (od 2026-09-14, volitelné)

S `[calendar] outlook = true` ve sdílené konfiguraci si capture při startu hovoru a transcribe při importu
vezmou z klasického Outlooku na tomto počítači (COM, lokálně, bez sítě) schůzku běžící v čase začátku
(začátek −10 min … konec +5 min; přednost má ta, která čas skutečně obsahuje, pak schůzky Teams). Sidecar pak
má `participants` (jména účastníků) a

```json
"calendar": {"source": "outlook", "subject": "WFMS sync", "organizer": "Jana Nováková",
             "start": "2026-09-14T08:30", "end": "2026-09-14T09:15"}
```

Spárování: nejdřív podle názvu (titulek okna Teams u živého hovoru, název souboru u importu, shoda s předmětem
schůzky i přibližná) – `match: "title"`; teprve bez shody podle času – `match: "time"`, což je jen odhad (ad-hoc
hovor během naplánované schůzky, dvě paralelní schůzky). `status` je `auto` | `confirmed`; kontrolní stránka
ukazuje panel Schůzka se zdroji (`title_source`: `calendar` | `window` | `manual` | `file`, u účastníků `source`:
`calendar` | `manual`) a umí spojení potvrdit, odpojit (odebere účastníky z kalendáře, název zůstává) nebo spojit
s jinou schůzkou (`candidates` v sidecaru, jinak živý dotaz do Outlooku; název, účastníci i složka se přizpůsobí).
Do zápisu jdou z kalendáře organizátor a plánovaný čas. Bez Outlooku (nový Outlook bez COM, jiný stroj) se nic nemění.

### Snímky oken Teams `<stem>_screen<N>.mp4` (živé nahrávky, od 2026-09-14)

Capture během živého hovoru ukládá každé okno Teams jako video se 2 snímky za sekundu (x264, plátno
1600×900 se zachováním poměru stran, černé okraje). Teams má oken víc – hovor, vyskakovací galerie, sdílený
obsah – a ta vznikají a zanikají během hovoru, proto má každé okno vlastní soubor a v sidecaru položku:

```json
"screens": [
  {"file": "<stem>_screen1.mp4", "fps": 2, "width": 1600, "height": 900, "start_offset_s": 0.0,
   "end_offset_s": 1748.0, "frames": 3496, "titles": ["Připojení ke schůzce | Microsoft Teams", "WFMS sync | Microsoft Teams"]}
]
```

`start_offset_s` je posun začátku videa vůči začátku nahrávky. Minimalizované okno nejde sejmout, opakuje se
poslední snímek, aby časová osa seděla se zvukem. Transcribe každé video okna schůzky (okna s navigací Teams, např. Kalendář, se přeskakují) projede analýzou
aktivního řečníka: v živém okně dostane mluvící dlaždice tenký rámeček v barvě Teams (na rozdíl od staženého
záznamu, kde se barví jmenovka), jmenovka se čte OCR z levého dolního rohu dlaždice (kandidáti jmen = registr osob
+ účastníci). Vlastní dlaždice uživatele rámeček nedostává, toho pojmenuje mikrofonní stopa. Osy se posunou a sloučí
do `speakers_video.json` se `source: "teams-screen"`. Ověřeno 2026-09-16 na živém standupu.

### Zdroje mluvčích a jejich priorita

Přepis zvuku je vždy stejný. Liší se jen, odkud se bere „kdo mluví“, a to podle toho, co nahrávka má, ne podle přípony:

1. `speakers_video.json` (záznam Teams s videem nebo snímaná okna) – segment, který zvýraznění přímo pokrývá
   (≥ 30 % délky), dostane to jméno. Celé diarizační označení se přejmenuje až ve druhém kole, po mikrofonu, a jen
   když video jednomu jménu připisuje aspoň 10 s, většinu pokrytého času a aspoň čtvrtinu všeho, co označení řeklo
   (2026-09-16: krátké zvýraznění jinak „vlastnilo“ 47 minut cizí řeči).
2. Stopa `mic` (živá nahrávka) – diarizační označení, které se kryje s aktivitou mikrofonu, dostane jméno
   uživatele z konfigurace (`[user] name`). Ověřeno 2026-09-10: 89 % aktivity u uživatele, 13–21 % u ostatních.
3. Diarizace – vždy se spouští, slouží jako záloha pro segmenty bez překryvu a pro účastníky, kteří na videu nejsou.
   Ti zůstávají jako `SPEAKER_XX`, dokud je uživatel nepojmenuje v `speakers.json`.

4. **Hlasové otisky** (`_speakers/voiceprints.json`, od 2026-09-11): diarizace vrací pro každé označení
   embedding (pyannote community-1); přepis ho ukládá v `speaker_embeddings` (klíč = výsledné jméno/označení,
   jednotkový vektor). Když označení dostane osobu (stránka, `label-speakers`, mikrofonní stopa uživatele),
   embedding se uloží pod osobu (nejvýš 10 na osobu, se stemem a označením původu). U nové nahrávky se
   neznámá označení porovnají kosinovou podobností; shoda ≥ `threshold` s odstupem ≥ `margin` od druhé nejlepší
   osoby zapíše osobu do `speakers.json` (jako ruční přiřazení, lze opravit) a do přepisu `voice_matches`
   `{označení: {person, score}}`. Zdroj `voiceprint` v `speaker_sources`.

Plánované zdroje:

5. **Snímání okna Teams při živé nahrávce** (teamsrec-capture, .NET port): během hovoru se ~2× za sekundu snímá
   okno Teams s galerií účastníků a ukládá jako malé video (`<stem>_screen.mp4`, 720p, 2 fps). Po hovoru se
   zpracuje stejnou analýzou jako stažený záznam a vznikne `speakers_video.json`. Při sdílení obrazovky Teams
   zobrazuje galerii v druhém (vyskakovacím) okně – snímá se to okno Teams, ve kterém jsou jmenovky, ne nutně hlavní.
   Minimalizované okno snímat nelze; takové úseky kryje diarizace a hlasové otisky.

Přepis může mít `removed_speakers`: seznam označení, jejichž repliky uživatel smazal jako šum
(`{"label", "segments", "at"}`); jejich segmenty v přepisu nejsou. Nový přepis (`--force`) je obnoví.

## Mluvčí z videa `<stem>.speakers_video.json`

Vzniká při `import` záznamu Teams s videem. Teams zvýrazňuje jmenovku aktivního mluvčího; analýza snímků dá pro každé
jméno intervaly, kdy mluvilo. Je to **přednostní zdroj mluvčích**: segmentům přepisu se přiřadí jméno s největším
překryvem, diarizace slouží jen jako záloha pro segmenty bez překryvu. Jména jsou z OCR, upřesněná seznamem `participants`.

```json
{ "format": 1, "source": "teams-video", "fps": 2,
  "speakers": { "Jana Nováková": [[12.0, 15.5], [40.0, 61.5]], "Petr Svoboda": [[15.5, 40.0]] } }
```

## Lidé `_speakers/people.json`

Registr osob mimo nahrávky, jeden soubor pro celý `OUT_DIR` (vedle něj budou později hlasové vzorky):

```json
{ "format": 1, "people": [
  { "id": "petr-svoboda", "first": "Petr", "last": "Svoboda", "nick": "Péťa", "display": "", "aliases": ["Petr Svoboda (NG)"] }
] }
```

`display` = `first` | `full` | `nick` | prázdné (výchozí z konfigurace `[people] display`, výchozí `nick`).
Režim `nick` bez přezdívky znamená jméno. Přepis i `speakers.json` uchovávají identifikátor osoby (`id`) nebo
doslovné jméno z videa či mikrofonu; zobrazovaná podoba se určuje až při exportu a zápisu. Neregistrovaná jména se
tisknou doslova.

Soubor `_speakers/voiceprints.json`: `{"format": 1, "model": "<diarizační model>", "people": {"<id>": [{"v": [...],
"stem": "...", "label": "...", "added": "..."}]}}`. Odvozený, smazatelný; otisky jedné osoby maže
`people forget-voice`.

## Mluvčí `<stem>.speakers.json`

Ruční mapování po transkripci. Když existuje, export a summary používají jména místo identifikátorů.

```json
{ "SPEAKER_00": "Jana Nováková", "SPEAKER_01": "Petr Svoboda", "me": "Jan Novák" }
```
