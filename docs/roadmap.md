# Plán (roadmap) teamsrec

Společný plán obou repozitářů (teamsrec-capture, teamsrec-transcribe). Rozhodnutí o formátu dat jsou
v `recording-format.md`, tady je jen pořadí a stav práce. Stav: ☐ nezačato, ◐ rozpracováno, ☑ hotovo.

## Hotovo

- ☑ Kontrakt nahrávky v1 (složka na nahrávku, sidecar, přepis, mluvčí). 2026-09-04
- ☑ Python prototyp capture: živý hovor, ruční záznam, přehrávání, autostart, ochrana proti dvojímu spuštění.
- ☑ Přepis WhisperX na GPU, jména z videa Teams (OCR jmenovek), export txt/srt.
- ☑ Zápisy lokálně přes Ollamu, Claude jako srovnání; sekce Mluvčí v zápisu. 2026-09-10
- ☑ Vlastní hlas z mikrofonní stopy (`[user] name`). 2026-09-10

## Další kroky (v tomto pořadí, rozhodnuto 2026-09-10/11)

1. ☑ **Hlasové otisky** (transcribe), 2026-09-11. Bez ukládání zvukových vzorků: diarizace už embedding vrací,
   ukládá se přímo (`_speakers/voiceprints.json`). Kdo je jednou pojmenován, toho další nahrávky poznají po
   hlase; rozpoznání se zapíše jako přiřazení a stránka ho ukáže k potvrzení. Práh a odstup kalibrovány na
   prvních nahrávkách, viz `lab/FINDINGS.md`. K rozhodnutí uživatele: výchozí zapnuto, automatické přiřazení
   (ne jen návrh), informování týmu o biometrii.
2. ☑ **Kontrolní stránka** (transcribe, příkaz `review`). Hotovo 2026-09-11: přehrání ukázek, jména, sloučení,
   uložení, přegenerování, výběr nahrávky, zástupce na ploše, záložka Lidé (jméno, příjmení, přezdívka, co psát
   do zápisu). Hlasové vzorky se do záložky doplní s otisky. Lokální stránka v prohlížeči: přehrát ukázky každého
   označení, přiřadit jméno s našeptávačem, sloučit označení, uložit a přegenerovat; záložka Lidé pro správu
   otisků. Jeden soubor HTML + JavaScript s `@ts-check`/JSDoc, bez frameworku a bez build kroku, JSON API
   z Python serveru jen na 127.0.0.1. Součást balíčku teamsrec-transcribe, vlastní složka `web/`, API popsané
   v dokumentaci, aby šlo později vyčlenit nebo nahradit nativním oknem. Zástupce „zpracovat poslední“ ji
   otevře, když zůstane někdo nepojmenovaný.
3. ☐ **Snímání okna Teams při živé nahrávce** (capture, součást .NET portu). Galerie účastníků ~2 fps jako
   `<stem>_screen.mp4`, po hovoru stejná analýza jako u stažených záznamů. Při sdílení obrazovky sledovat
   vyskakovací okno s jmenovkami; minimalizované okno snímat nelze.
4. ☐ **.NET port capture** (WPF + NAudio), viz README capture. Prototyp v Pythonu do té doby slouží.

## K potvrzení (zatím jen zaznamenáno)

- ☐ **Fulltextové hledání v přepisech** napříč nahrávkami. Bez databáze: průchod `.txt` souborů, případně malý
  index ve složce `_index` jako smazatelná mezipaměť. Rozhodnutí o řešení a rozsahu (jen hledání, nebo i
  prohlížení nahrávek na kontrolní stránce) se ještě potvrdí. Zaznamenáno 2026-09-11.
- ☐ **Instalace na novém stroji**: `install.ps1` v tomto repu, spustitelný jedním příkazem, idempotentní (opakování
  = aktualizace): kontrola GPU a místa, winget (git, uv, ffmpeg, Ollama), klon obou repozitářů, `uv sync`,
  `config --init`, výběr Ollama modelu podle VRAM, `hf auth login` + souhlas pyannote (jediný ruční krok), zástupci.
  Klasický instalátor (MSI) ne: objem tvoří modely a CUDA balíčky, které se stahují až na stroji. Jen Windows,
  stejně jako capture; transcribe zůstává v kódu přenositelné, ale bez oficiální podpory jiných OS. Později
  `winget install` pro .NET capture a `uv tool install` pro transcribe. Zaznamenáno 2026-09-11.
- ☐ **Jazyk per mluvčí** (v2 kontraktu): smíšené cs/sk schůzky dnes dostanou jeden jazyk pro všechny.
- ☐ **Mazání zvuku po lhůtě** (`purge-audio`): přepisy a zápisy zůstávají.
- ☐ **Další přepisové backendy**: CPU fallback, cloud (Azure AI Speech nebo ElevenLabs Scribe).
- ☐ **Kalendář Outlook** jako zdroj názvu a účastníků schůzky.

## Zásady, které platí pro všechno

- Zdroj dat je jen adresář nahrávek a soubory v něm; žádná databáze, žádný skrytý index. Odvozené soubory
  (přepis, exporty, zápis, otisky, případný index) lze kdykoli smazat a spočítat znovu.
- Nic neopouští počítač bez výslovného nastavení (Claude API jen s `provider = "anthropic"`).
- Tajemství pouze v proměnných prostředí, nikdy v konfiguraci ani v repu.
