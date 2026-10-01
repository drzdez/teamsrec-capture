# teamsrec na Linuxu, Macu a tabletech – rozbor a návrh

Stav k 1. 10. 2026. Dokument je podklad pro rozhodnutí, nic z toho zatím není implementované. Navazuje na
[dotnet-design.md](dotnet-design.md) (současná Windows aplikace pro nahrávání) a [recording-format.md](recording-format.md)
(kontrakt mezi nahráváním a přepisem).

## 1. Cíl

- teamsrec má fungovat i na **Linuxu** a **Macu**, později i na **tabletech** (Android, iOS/iPadOS).
- Co nejvíc z toho, co už běží na Windows, se má **přepoužít**, ne psát znovu.
- „Jádro“ (logika nahrávání a vše kolem nahrávky) má běžet na čemkoli; platformově specifické mají být jen tenké
  vrstvy kolem: zachycení zvuku, poznání hovoru, snímání oken, kalendář, ikona v liště.
- Přepis zůstává v Pythonu (WhisperX, pyannote, OCR, Ollama/Claude) – tam je ekosystém a jinde se to dohnat nedá.

## 2. Z čeho se dnes teamsrec skládá

```
 teamsrec-capture (C#, .NET 10, Windows)          teamsrec-transcribe (Python 3.12)
 ─────────────────────────────────────           ──────────────────────────────────────────
 tray, detekce hovoru, nahrávání zvuku,           přepis (WhisperX + pyannote, OCR jmen z videa),
 snímání oken Teams, Outlook, finalizace   ──►    zápisy (Ollama / Claude), server stránky (REST + SSE)
            │   nahrávka podle kontraktu                       ▲
            │   (sidecar JSON + WAV + MP4)                     │ HTTP 127.0.0.1
            ▼                                       web stránka (index.html, čistý JS) – v prohlížeči
      <out_dir>/YYYY/MM/<stem>/                     nebo v okně aplikace (Tauri, Rust, ~300 řádků)
```

Tři rozhraní už dnes nezávisí na platformě a jsou tím nejcennějším, co se přenese:

1. **Kontrakt nahrávky** (`recording-format.md`): složka se sidecarem a stopami. Kdo ho zapíše, s tím přepis umí pracovat.
2. **REST + SSE API serveru stránky** (popsané v OpenAPI 3.1, `/api/openapi.json`).
3. **Webová stránka**: jedna stránka pro kontrolu přepisů, zápisy i nastavení obou aplikací; běží v jakémkoli prohlížeči
   nebo WebView.

## 3. Co je vázané na Windows (změřeno v kódu)

### Nahrávací aplikace (5 400 řádků C#)

| oblast | řádků | Windows? | čím |
|---|---|---|---|
| Core (typy, rozhraní) | 106 | ne | – |
| Config (TOML, nastavení, sledování souboru) | 532 | ne | – |
| Contract (pojmenování, sidecar) | 437 | ne | – |
| Recording (finalizace, mix přes ffmpeg, obnova po pádu, watchdog) | 770 | ne | – |
| Settings (záložní stránka nastavení) | 279 | ne | HttpListener je .NET |
| App – logika (`AppLogic`, `MonitorLoop`) | ~850 | částečně | volá Windows části níže |
| **Audio** (WASAPI loopback + mikrofon, test mikrofonu) | 924 | **ano** | NAudio, Core Audio |
| **Detection** (kdo používá mikrofon, okna Teams) | 420 | **ano** | registr ConsentStore, Core Audio sessions, EnumWindows |
| **Screen** (video oken) | 455 | **ano** | PrintWindow (GDI) + ffmpeg |
| **Calendar** | 306 | **ano** | Outlook COM (logika párování schůzek je neutrální) |
| **Tray, dialogy** | ~350 | **ano** | WinForms |

**Zhruba polovina (2 500 řádků) je platformově neutrální** a pokrytá velkou částí ze 163 testů.

### Přepis a server (6 000 řádků Pythonu, 2 200 řádků stránky)

Python sám běží na Linuxu i Macu. Na Windows jsou vázané jen drobnosti:

| místo | co | náhrada |
|---|---|---|
| `settings.input_devices` | seznam mikrofonů z registru | Linux: `pactl list sources`; Mac: Core Audio přes malý helper nebo `system_profiler` |
| `/api/system/sound-settings` | `control mmsys.cpl` | Linux: `pavucontrol` / nastavení prostředí; Mac: `open x-apple.systempreferences:…sound` |
| `_kill_tree` | `taskkill /T` | jinde skupina procesů (`os.killpg`) |
| `outlook.py` | Outlook COM | viz kalendář v kapitole 7 |
| `keyring` | Správce přihlašovacích údajů | samo: Secret Service (Linux), Keychain (Mac) |
| WhisperX na CUDA | Linux s NVIDIA stejně jako Windows | Mac: jiný poskytovatel přepisu (kapitola 6) |

Okno aplikace (Tauri) se přeloží pro Linux (WebKitGTK) i Mac (WKWebView) beze změny kódu.

## 4. Návrh: vrstvy

```
 ┌───────────────────────────────────────────────────────────────────────────────────────┐
 │  UI: webová stránka (sdílená všude)  +  tenká skořápka: tray / menu bar / tabletová app │
 ├───────────────────────────────────────────────────────────────────────────────────────┤
 │  JÁDRO (platformově neutrální)                                                          │
 │   • stavový automat nahrávání (kdy začít / skončit, onsite, přehrávání, jiné aplikace)  │
 │   • kontrakt: pojmenování, sidecar, finalizace, mix, obnova po pádu                    │
 │   • konfigurace (TOML), stav pro stránku (teamsrec-capture.json), watchdog zvuku         │
 │   • párování schůzek z kalendáře (bez zdroje)                                          │
 ├───────────────────────────────────────────────────────────────────────────────────────┤
 │  PORTY (rozhraní, která jádro volá)                                                     │
 │   IAudioCapture  ICallDetector  IWindowCapture  ICalendarSource  INotifier  ITray       │
 ├──────────────┬──────────────────┬──────────────────┬────────────────┬──────────────────┤
 │  Windows      │  Linux           │  macOS           │  Android        │  iOS / iPadOS     │
 │  WASAPI       │  PipeWire/Pulse  │  ScreenCaptureKit│  AudioRecord    │  AVAudioEngine    │
 │  ConsentStore │  source-outputs  │  Core Audio      │  (jen mikrofon) │  (jen mikrofon)   │
 │  EnumWindows  │  X11 / portál    │  SCK okna        │  –              │  –                │
 │  Outlook COM  │  ICS / Graph     │  ICS / Graph     │  Graph / ICS    │  EventKit / Graph │
 └──────────────┴──────────────────┴──────────────────┴────────────────┴──────────────────┘
                 ▼ nahrávka podle kontraktu (+ nahrání na server, kde přepis neběží lokálně)
 ┌───────────────────────────────────────────────────────────────────────────────────────┐
 │  teamsrec-transcribe (Python) – stejný na Windows, Linuxu, Macu; tablet ho volá po síti  │
 └───────────────────────────────────────────────────────────────────────────────────────┘
```

Dnešní .NET kód tomuto rozdělení už z velké části odpovídá (`Core/Types.cs` má `IAudioSource`, `INotifier`, `IClock`).
Chybí oddělit porty pro detekci hovoru, okna a kalendář a vytáhnout jádro do samostatné knihovny.

## 5. Jazyk jádra: .NET, Kotlin, nebo Rust?

Jádro musí běžet na Windows, Linuxu, Macu, Androidu i iOS. Ve hře jsou tři cesty:

| kritérium | **.NET (C#)** – vytáhnout jádro z dnešní aplikace | **Kotlin Multiplatform** | **Rust** (v Tauri) |
|---|---|---|---|
| běží na Win / Linux / Mac | ano (.NET 10) | ano (JVM, nebo Kotlin/Native) | ano |
| běží na Android / iOS | ano (.NET for Android / iOS, MAUI) | ano, nejvyzrálejší sdílení logiky pro mobil | ano (Tauri 2 mobile) |
| přepoužití z Windows | **~2 500 řádků + testy beze změny** | přepis jádra (testy jako předloha) | přepis jádra |
| UI tray na desktopu | Avalonia (Win/Linux/Mac) | Compose Desktop | Tauri (už máme) |
| UI na tabletu | MAUI + WebView se stránkou | Compose Multiplatform nebo WebView | Tauri mobile = **stejná webová stránka** |
| nativní audio | P/Invoke / bindingy; pro Mac existují bindingy ScreenCaptureKit | expect/actual + JNA / ObjC interop | crates (wasapi, pipewire, screencapturekit) |
| jazyků v projektu | 3 (C#, Python, Rust skořápka) | **4** (+ Kotlin) | 2–3 (Rust, Python, C# do přechodu) |
| velikost / start desktop | malá (trimovaný .NET) | JVM ~100 MB, pomalejší start (Native složitější) | nejmenší |

### Doporučení: .NET

- **Přepoužití je největší:** jádro z dnešní aplikace se vytáhne do knihovny `TeamsRec.Capture.Core` (`net10.0`, bez
  `-windows`) i s testy. Windows aplikace ji jen začne používat a nic se pro ni nezmění.
- **.NET pokryje všech pět platforem** jedním runtimem, včetně Androidu a iOS.
- **Kotlin by znamenal čtvrtý jazyk a třetí přepis** nahrávání za měsíc (Python → C# → Kotlin). Dává smysl, jen
  kdyby se těžiště přesunulo na nativní mobilní aplikace (bohaté offline UI, nahrávání na pozadí jako hlavní
  scénář). Jak ukazuje kapitola 8, na tabletech je nahrávání omezené, takže to nepředpokládám.
- **Rust** je lákavý tím, že Tauri už máme a na tabletu by běžela stejná webová stránka. Jádro by se ale psalo znovu.
  Dává smysl jen jako budoucí sjednocení desktopové skořápky. **UI na tabletech přes WebView se stránkou platí
  pro .NET stejně.**

## 6. Linux

| port | řešení | poznámka |
|---|---|---|
| zvuk hovoru (loopback) | PipeWire / PulseAudio **monitor** výstupního zařízení: `pw-record --target <sink>.monitor` nebo libpulse přes P/Invoke | spolehlivé; PipeWire je dnes standard (Fedora, Ubuntu 22.10+) |
| mikrofon | stejně, zdroj podle jména (`pactl list sources`) | výběr podle názvu jako na Windows |
| **kdo používá mikrofon** | `pactl list source-outputs` / `pw-dump`: u každého záznamu je `application.name` a `application.process.binary` | **lepší než Windows**: přímo „chrome“, „teams-for-linux“, „zoom“ |
| Teams na Linuxu | oficiální desktopový klient už neexistuje (konec 2022): Teams **v prohlížeči / PWA**, případně neoficiální `teams-for-linux` | detekce = prohlížeč s otevřenou schůzkou, stejně jako dnešní „jiné aplikace“ |
| názvy oken / video jmenovek | **X11**: seznam oken a snímky (`_NET_CLIENT_LIST`, XGetImage); **Wayland**: jen přes portál ScreenCast s jednorázovým souhlasem (novější portály si souhlas pamatují) | na Waylandu bude snímání oken volitelné; diarizace + otisky hlasu fungují i bez něj |
| kalendář | žádný Outlook COM: **ICS adresa** publikovaného kalendáře (Outlook na webu ji umí), nebo Microsoft Graph | ICS je nejjednodušší a funguje všude; Graph vyžaduje registraci aplikace v Entra |
| tray, oznámení | Avalonia `TrayIcon` (StatusNotifierItem); GNOME potřebuje rozšíření AppIndicator | oznámení přes `org.freedesktop.Notifications` |
| přepis | **beze změny** (NVIDIA + CUDA na Linuxu běží nejlépe) | odpadá řada Windows obtíží (ffmpeg v PATH, …) |
| klíče | `keyring` → Secret Service (GNOME Keyring / KWallet) | bez úprav |
| okno přepisů | Tauri pro Linux (WebKitGTK) | build `npm run build` na Linuxu |
| distribuce | AppImage nebo .deb; Flatpak až později (sandbox komplikuje přístup k PipeWire a oknům) | |

**Náročnost:** střední. Nejvíc práce je v audiu a detekci (nové porty). Snímání oken na Waylandu je jediné místo, kde
Linux umí méně než Windows.

## 7. macOS

| port | řešení | poznámka |
|---|---|---|
| zvuk hovoru | **ScreenCaptureKit** (macOS 13+) umí zachytit systémový zvuk (`capturesAudio`) | vyžaduje oprávnění „Nahrávání obrazovky“; dřív jen virtuální zařízení (BlackHole) |
| mikrofon | AVAudioEngine / Core Audio | oprávnění Mikrofon |
| kdo používá mikrofon | Core Audio `kAudioDevicePropertyDeviceIsRunningSomewhere` (že ho někdo používá) + běžící procesy (Teams, prohlížeč) | přímé „která aplikace“ macOS nedává; kombinace stačí |
| okna Teams | ScreenCaptureKit – snímky jednotlivých oken | stejné oprávnění jako zvuk |
| kalendář | ICS / Microsoft Graph; Outlook pro Mac nemá COM | EventKit, když je kalendář synchronizovaný do systému |
| menu bar | Avalonia `TrayIcon` → `NSStatusItem` | |
| bindingy | .NET for macOS má bindingy ScreenCaptureKit; jinak malý pomocník ve Swiftu volaný jako proces | pomocník ve Swiftu je jednodušší na údržbu |
| **přepis** | **CUDA není**: WhisperX (faster-whisper / CTranslate2) poběží jen na CPU, pomalu | nový poskytovatel **mlx-whisper** (Apple Silicon) nebo **whisper.cpp** (Metal); pyannote na MPS / CPU; zápisy přes Ollama běží na Metalu dobře |
| podpis | aplikace musí být podepsaná a notarizovaná (Apple Developer účet), jinak ji Gatekeeper blokuje | Tauri i .NET to umí |

**Náročnost:** střední až vyšší. Nahrávání je díky ScreenCaptureKit čisté. Hlavní práce je nový poskytovatel přepisu
pro Apple Silicon a oprávnění, podpis a notarizace.

## 8. Tablety: Android a iOS / iPadOS

### Co jde a co ne

- **Zvuk hovoru Teams nahrát nejde.**
  - Android (10+) dovoluje zachytit zvuk jiných aplikací jen přes AudioPlaybackCapture, a ten **hovorový zvuk
    (VOICE_COMMUNICATION) z principu vynechává**.
  - iOS ostatní aplikace neposlouchá vůbec. ReplayKit broadcast zvuk aplikací zachytí, ale u VoIP hovoru je to
    neověřené a spíš ne.
  - Nahrávání hovorů tedy zůstává na počítači.
- **Schůzka na místě jde dobře:** tablet uprostřed stolu s mikrofonem je přesně scénář „onsite“, jen bez notebooku.
- **Přepis na tabletu lokálně spíš ne.** WhisperX + pyannote potřebují GPU a Python. Reálné možnosti:
  - poslat nahrávku domů na PC (server přepisu) – **doporučeno**;
  - cloudový přepis (OpenAI / ElevenLabs, už umíme);
  - malý model na zařízení (whisper.cpp) – jen pro rychlý náhled, bez diarizace.
- **Kontrola přepisů a zápisů na tabletu dává velký smysl:** stejná webová stránka, jen ve WebView na dotyk.

### Návrh tabletové aplikace

```
 tablet (.NET MAUI, nebo Tauri mobile)                     PC (Windows / Linux / Mac)
 ┌────────────────────────────────────┐                    ┌──────────────────────────────┐
 │ nahrávat schůzku na místě (mikrofon)│  nahrání (HTTPS) ─►│ server přepisu (dnešní Python)│
 │ jádro: pojmenování, sidecar,        │                    │ + import / upload endpoint    │
 │   kalendář (EventKit / Graph)       │ ◄── stránka ───────│ + přihlášení (token / párování)│
 │ WebView: kontrola přepisů, zápisy   │      (REST + SSE)  │ + dostupný v LAN nebo přes VPN │
 └────────────────────────────────────┘                    └──────────────────────────────┘
```

Server dnes poslouchá jen na 127.0.0.1 a nemá přihlášení. Pro tablet potřebuje:
- **volitelný provoz v síti:** LAN, ideálně přes VPN nebo Tailscale, ne do internetu;
- **párování zařízení tokenem** (QR kód na stránce Nastavení);
- **TLS;**
- **endpoint pro nahrání nahrávky:** složka podle kontraktu, jako dnešní `_inbox`.

Bezpečnost tu je hlavní téma: jde o nahrávky schůzek a biometrické otisky.

**Náročnost:** vyšší a dává smysl až po Linuxu. Nejdřív serverová část (přihlášení, nahrávání), pak samotná aplikace.

## 9. Python zůstává – co se v něm změní

- **Platformově neutrální náhrady** drobností z kapitoly 3: seznam mikrofonů, dialog zvuku, ukončení procesu.
- **Poskytovatelé přepisu podle stroje:** `whisperx` (CUDA), `mlx` / `whisper-cpp` (Mac), cloud. Volba je už dnes
  v Nastavení, přibudou hodnoty.
- **Kalendář:** zdroj `ics` (adresa), případně `graph`, vedle `outlook` (COM).
- **Server:** volitelné přihlášení a provoz mimo 127.0.0.1, endpoint pro nahrání.
- **CI na Linuxu:** testy (93) a testy stránky pustit i na Linuxu. Dnes běží jen na Windows.

## 10. Postup

| fáze | co | výsledek |
|---|---|---|
| 0 | vytáhnout `TeamsRec.Capture.Core` (net10.0) a porty z dnešního .NET kódu, testy s ním | Windows beze změny chování, jádro připravené pro jiné platformy |
| 1 | Python bez Windows drobností, ICS kalendář, CI na Linuxu | přepis a stránka plně na Linuxu |
| 2 | **Linux:** porty PipeWire (zvuk, mikrofon, detekce), Avalonia tray, X11 okna, Tauri build, AppImage | první ne-Windows nahrávání |
| 3 | **Mac:** ScreenCaptureKit (zvuk + okna), menu bar, poskytovatel mlx / whisper.cpp, podpis | nahrávání a přepis na Apple Silicon |
| 4 | **server pro vzdálené klienty:** přihlášení, TLS, nahrávání, LAN / VPN | základ pro tablety |
| 5 | **tablet:** nahrávání na místě, kontrola přepisů ve WebView | tablet jako zápisník schůzek |

Fáze 0 a 1 se vyplatí i bez dalších platforem: zpřehlední kód a přepis bude testovaný i na Linuxu.

## 11. Rizika

- **Wayland:** snímání oken jen s portálem a souhlasem. Na Waylandu bude video jmenovek volitelné.
- **Teams na Linuxu je jen webový:** detekce schůzky závisí na prohlížeči, stejně jako dnešní „jiné aplikace“.
- **Mac bez CUDA:** kvalita a rychlost přepisu přes mlx / whisper.cpp se musí změřit na skutečných nahrávkách,
  stejně jako jsme měřili cloudové poskytovatele.
- **Mobil a hovory:** nahrávání hovorů na tabletu nebude. Kdyby se to změnilo (např. Teams povolí zachycení),
  přidá se to jako port.
- **Bezpečnost serveru v síti:** dnes 127.0.0.1 bez přihlášení. Otevřít ho jinak než s tokenem a TLS nejde.

## 12. K rozhodnutí

1. **Jazyk jádra:** doporučuji **.NET** (přepoužití, jeden runtime všude), Kotlin jen při posunu k mobilu jako
   hlavní platformě.
2. **Linux jako první:** ano/ne. Doporučuji ano; je nejblíž (Python i CUDA tam už běží).
3. **Kalendář mimo Windows:** ICS (jednoduché, jen čtení) nebo Microsoft Graph (plný přístup, potřeba registrace
   aplikace ve firemním tenantu).
4. **Tablet:** jen kontrola přepisů (rychle), nebo i nahrávání schůzek na místě (víc práce, potřebuje fázi 4).
