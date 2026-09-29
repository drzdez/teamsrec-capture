# .NET návrh nahrávací aplikace (teamsrec-capture)

Tento dokument vysvětluje, jak je postavená .NET verze nahrávací aplikace ve složce [`dotnet/`](../dotnet) a proč.
Je to port Python prototypu [`legacy/teamsrec.py`](../legacy/teamsrec.py): chování, prahy a hlášky jsou převzaté,
datový výstup se řídí kontraktem [recording-format.md](recording-format.md), takže nahrávky z .NET verze zpracuje
teamsrec-transcribe beze změny.

## Proč .NET

- **Jeden spustitelný soubor** bez Pythonu, virtuálního prostředí a trampolíny `pythonw` (dnes jsou v seznamu procesů
  dva procesy a jednou jsme omylem zabili ten skutečný).
- **Windows API přímo**: WASAPI a Core Audio přes NAudio, registr, okna a COM (Outlook) bez mostů jako pywin32/pycaw.
- **Stabilita**: UI (WinForms, ikona v liště) bez Tcl/Tk; snímání oken běží stejně jako v prototypu v samostatném
  procesu, takže jeho pád nahrávání zvuku neshodí.
- Instalace do budoucna jako `winget install` (roadmapa).

## Přehled

```
dotnet/
  TeamsRec.Capture.slnx
  src/TeamsRec.Capture/            aplikace (net10.0-windows, WinForms, NAudio, Tomlyn)
    Core/          sdílené typy a rozhraní (IClock, INotifier, IAudioSource, TrackInfo, CallApp, Log, ...)
    Config/        konfigurace z teamsrec.toml, zápis se zachováním komentářů, model stránky nastavení
    Audio/         zařízení, nahrávač (smyčka systému + mikrofon), test mikrofonu, mikrofon hovoru
    Detection/     kdo drží mikrofon (registr), okna Teams, předvstupní obrazovka, hovory v jiných aplikacích
    Calendar/      Outlook přes COM, výběr schůzky podle názvu a času
    Screen/        snímání oken Teams do MP4 (PrintWindow -> ffmpeg)
    Recording/     hlídač zvuku, mix, dokončení nahrávky (sidecar), obnova po pádu
    Contract/      pojmenování (stem, složky), sidecar JSON podle kontraktu
    Settings/      lokální stránka nastavení (HttpListener + settings.html)
    App/           ikona v liště, hlavní smyčka, dialogy, log
    Program.cs     jedna instance (mutex), log, obnova, start
  tests/TeamsRec.Capture.Tests/    xUnit - logika bez hardwaru
```

Moduly odpovídají částem prototypu jedna ku jedné, takže se dá snadno dohledat, odkud které chování pochází
(tabulka v [`dotnet/PARITY.md`](../dotnet/PARITY.md)).

## Tok jedné nahrávky

```
 MonitorLoop (každé 3 s)
   │  MicUsers.TeamsInUse() / MicUsers.Current()  ← registr ConsentStore: kdo právě drží mikrofon
   │  WindowTitles + CallDetector                 ← předvstupní obrazovka? schůzka v prohlížeči?
   ▼
 start ── název: okno Teams → Outlook.Meeting() (podle názvu, pak času) → název aplikace
   │      mikrofon: Devices.CallInputDevice()      ← Core Audio: z kterého mikrofonu nahrává aplikace hovoru
   │      Recorder.Start()                         ← WASAPI loopback (_sys.wav) + mikrofon (_mic.wav)
   │      ScreenCapture (jen Teams)                ← _screen<N>.mp4, 2 fps
   ▼
 běh ──── Watchdog.Tick()        ← data nechodí → reopen s rostoucí pauzou; mrtvý mikrofon → žlutá ikona
   │      Watchdog.FollowCallMic()← aplikace hovoru přepnula sluchátka → přepnout mikrofonní stopu
   │      konec: aplikace pustí mikrofon (10 s), Stop & keep, 4 h pojistka, ticho u přehrávání
   ▼
 stop ─── RecordingInfo zachycené v okamžiku stopu (název, kalendář, viděná okna, continues, call_app)
   │      bez jediného WAV → složka pryč, hlášení „nevznikla“
   ▼
 Finalizer (na pozadí) ── mix 16 kHz (ffmpeg) → přepárování kalendáře podle okna → přejmenování složky
                          → sidecar .json (zapisuje se poslední = nahrávka je hotová)
```

## Klíčová rozhodnutí

**Sdílená rozhraní v `Core/Types.cs`.** Moduly se navzájem neznají víc, než je nutné: hlídač pracuje s `IAudioSource`,
ne s konkrétním nahrávačem; čas jde přes `IClock`, oznámení přes `INotifier`. Díky tomu se hlídač, výběr mikrofonu,
detekce hovoru nebo výběr schůzky dají testovat bez zvukové karty, Teams i Outlooku - stejné scénáře jako
`legacy/smoke_test.py`, jen v xUnit.

**Metadata nahrávky se berou v okamžiku stopu (`RecordingInfo`).** Při přechodu schůzky na místě do hovoru v Teams
začíná další nahrávka hned, zatímco se ta předchozí ještě dokončuje. Kdyby dokončení četlo aktuální stav aplikace,
dostala by první nahrávka název té druhé (chyba, kterou jsme v prototypu opravili).

**Mikrofon podle aplikace hovoru, ne podle Windows.** Teams, Zoom i prohlížeč si mikrofon vybírají samy; výchozí vstup
Windows může být úplně jiné zařízení (29. 9. se nahrával dongle BT-W5, zatímco Teams používal sluchátka Sony, a
uživatel v nahrávce chyběl celý). `Devices.CallInputDevice()` čte relace Core Audio a vybírá mikrofon procesu hovoru
(Teams → známé hovorové aplikace → cokoli kromě nás).

**Hovor = kdo drží mikrofon (registr ConsentStore).** Spolehlivé i při ztlumení v aplikaci; předvstupní obrazovka Teams
se ale také drží mikrofonu, proto se nahrává až po připojení (okno schůzky). Prohlížeč se počítá jen s otevřenou
schůzkou v názvu okna, jinak by se nahrával diktát nebo hlasové vyhledávání.

**Hlídač s rostoucí pauzou.** Když zvuk přestane chodit (uspaná Bluetooth sluchátka), znovuotevření streamů se
vyhodnotí po 12 s a další pokus čeká 0/30/60/180/300 s (nejvýš 8×). Bez toho prototyp jednou za 6 minut vyvolal
13 hlášení o změně zařízení. Mikrofon bez jakéhokoli šumu místnosti 2 minuty znamená, že se nahrává z nepoužívaného
zařízení - ikona zežloutne a hlášení se opakuje po 5 minutách.

**Snímání oken v samostatném procesu.** Stejně jako prototyp: aplikace spustí sama sebe jako
`teamsrec-capture.exe --screen-capture <stem> <start>`. Zaseknuté `PrintWindow` nebo pád GDI či enkodéru tak zůstane
v tom procesu a nahrávání zvuku neshodí. Proces snímá, dokud mu aplikace nezavře stdin (nebo dokud aplikace
neskončí), a pak zapíše `<stem>_screens.json`. Když do 45 s neskončí, aplikace ho ukončí; videa na disku se
použijí i bez jeho hlášení (`recovered`). Každé okno Teams má vlastní proces ffmpeg, do kterého jdou snímky
1600×900 (poměr stran zachován, okraje černé).

**Zastavení mimo zámek.** Stav nahrávky se převezme pod zámkem, zavírání streamů a videí (i desítky sekund)
proběhne mimo něj, takže ikona v liště nikdy nečeká. Stejně tak dotaz do Outlooku při startu běží až po spuštění
nahrávání a mimo zámek.

**Stránka nastavení přes `HttpListener`.** Stejný `settings.html` a stejné JSON API jako prototyp (jen 127.0.0.1,
spouští se až kliknutím na Settings…). Konfigurace se zapisuje do sdíleného `teamsrec.toml` po řádcích, takže
komentáře i klíče teamsrec-transcribe zůstanou.

**Jedna instance, stejný mutex jako prototyp.** `Local\teamsrec-capture` - Python a .NET verze tak nikdy nenahrávají
zároveň. Před spuštěním .NET verze je potřeba prototyp ukončit (Quit v liště) a hlídací úlohu přepnout na nový exe.

## Co zůstává z Pythonu

Přepis, jména mluvčích, zápisy a kontrolní stránka zůstávají v teamsrec-transcribe (Python, WhisperX na GPU). .NET
nahrávač s nimi mluví jen přes složku nahrávek a sidecar podle kontraktu - proto je kontrakt nejdůležitější
společná část a oba projekty ho musí dodržet do písmene.

## Testy

`dotnet test` spouští testy logiky: detekce předvstupní obrazovky a hovorů v jiných aplikacích, výběr mikrofonu
hovoru, hlídač (pauzy, mrtvý mikrofon, přepnutí sluchátek), výběr schůzky z kalendáře, zápis TOML se zachováním
komentářů, sidecar podle kontraktu, obnova WAV hlavičky po pádu. Skutečné nahrávání se ověřuje na hovoru.
