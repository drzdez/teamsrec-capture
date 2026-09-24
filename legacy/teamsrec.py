r"""
teamsrec.py — prompt-to-record for Microsoft Teams calls (Windows, new Teams).

What it does
  * Sits in the tray. Detects a Teams call by watching whether ms-teams is using
    the microphone (Windows CapabilityAccessManager registry — robust, no window hacks).
  * Pops a small always-on-top prompt: "Record <meeting title>?"  Auto-skips after a timeout.
  * Records system audio (WASAPI loopback = the other people) and your mic into two WAVs,
    stops automatically when the call ends (or on max duration / tray "Stop").
  * Names files  <OUT_DIR>\<YYYY>\<MM>\<stem>\<stem>_sys.wav / _mic.wav  (stem = <YYYY-MM-DD>_<HHMM>_<slug>)
    plus a _mix.wav (mono 16 kHz, WhisperX-ready) if ffmpeg is found, plus a .json sidecar in the
    teamsrec recording format v1 (docs/recording-format.md). OUT_DIR comes from the shared
    %APPDATA%\teamsrec\teamsrec.toml when present.
  * "Record playback": records only the system track (a stored Teams recording being played back),
    stops after 30 s of silence.
  * Deletes recordings shorter than MIN_DURATION_S, and everything on "Abort & delete".
  * Optional POST_HOOK shell command per finished recording (e.g. ship to the homelab).

Install
  pip install pyaudiowpatch pystray pillow pywin32 psutil
  (tkinter ships with python.org Python; ffmpeg optional but recommended)
Run at login
  Win+R -> shell:startup -> shortcut to:  pythonw.exe "C:\path\teamsrec.py"

Tell the other participants you're recording. Teams shows them nothing for this.
"""

import audioop
import glob
import json
import logging
import os
import queue
import re
import shutil
import subprocess
import sys
import threading
import time
import tomllib
import unicodedata
import warnings
import wave
import winreg
import winsound
import tkinter as tk
from tkinter import simpledialog
from datetime import datetime, timedelta
from pathlib import Path

warnings.filterwarnings("ignore", category=DeprecationWarning)  # audioop is deprecated but fine on 3.12

import psutil
import pyaudiowpatch as pyaudio
import pystray
import ctypes
import win32api
import win32event
import win32gui
import win32process
import win32ui
import winerror
from PIL import Image, ImageDraw

# ------------------------------------------------------------------ config
APP_NAME = "teamsrec-prototype"
APP_VERSION = "0.7.1"
FORMAT_VERSION = 1  # docs/recording-format.md


def _config() -> dict:
    """Shared config with teamsrec-transcribe: %APPDATA%/teamsrec/teamsrec.toml."""
    cfg = Path(os.environ.get("TEAMSREC_CONFIG") or Path(os.environ.get("APPDATA", "")) / "teamsrec" / "teamsrec.toml")
    try:
        return tomllib.loads(cfg.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


CONFIG = _config()
OUT_DIR = Path(CONFIG.get("recordings", {}).get("out_dir", r"D:\meetings")).expanduser()
USE_OUTLOOK = bool(CONFIG.get("calendar", {}).get("outlook", False))  # classic Outlook (COM): title + participants
PROMPT_DEFAULT = str(CONFIG.get("capture", {}).get("prompt_default", "record"))  # record = just notify | ask = show the discard box | skip = box, discards on timeout
USER_NAME = str(CONFIG.get("user", {}).get("name", "")).strip()
ONSITE_MIC = str(CONFIG.get("capture", {}).get("onsite_mic", "")).strip()  # part of the input device name for on-site meetings
SILENCE_STOP_S = 30        # playback mode: stop after this much silence on the system track
SILENCE_LEVEL = 300        # int16 peak below this counts as silence
POLL_S = 3                 # how often to check for a call
PROMPT_TIMEOUT_S = 45      # the "discard?" box closes by itself after this (PROMPT_DEFAULT: record = keep | skip = discard)
MIN_DURATION_S = 5         # shorter recordings are deleted
MAX_DURATION_S = 4 * 3600  # safety net
CALL_END_GRACE_S = 10      # call must look ended this long before we stop
MIX_WITH_FFMPEG = True
SCREEN_CAPTURE = True      # record every Teams window as a low-fps video (name labels -> who speaks when)
SCREEN_FPS = 2
SCREEN_W, SCREEN_H = 1600, 900   # frames are fitted into this canvas (constant size for the encoder)
SCREEN_MIN_WIN = (500, 350)      # smaller Teams windows (toasts, popups) are ignored


def _ffmpeg() -> str | None:
    if shutil.which("ffmpeg"):
        return "ffmpeg"
    d = os.environ.get("TEAMSREC_FFMPEG_DIR")
    cands = [str(Path(d) / "ffmpeg.exe")] if d else []
    cands += glob.glob(str(Path(os.environ.get("LOCALAPPDATA", "")) / "Microsoft/WinGet/Packages/Gyan.FFmpeg_*/ffmpeg-*/bin/ffmpeg.exe"))
    return next((c for c in cands if Path(c).exists()), None)
# Shell command run after each kept recording. Placeholders: {stem} {sys} {mic} {mix} {json} {dir}
# POST_HOOK = r'scp "{mix}" homelab:/data/meetings/ && ssh homelab "~/bin/transcribe.sh {stem}_mix.wav"'
POST_HOOK = None
# Nav sections of the main Teams window (en + cs), so they are not mistaken for a meeting title
TEAMS_NAV = {"activity", "chat", "teams", "calendar", "calls", "files", "apps", "copilot",
             "onedrive", "meet", "viva", "planner", "microsoft teams",
             "aktivita", "týmy", "kalendář", "hovory", "soubory", "aplikace", "schůzka"}
# Titles the meeting window carries before/without a subject (en + cs); a later window title is better
TEAMS_GENERIC = {"meeting", "join meeting", "meeting compact view", "compact view", "call", "teams-call",
                 "připojení ke schůzce", "kompaktní zobrazení schůzky", "kompaktní zobrazení", "hovor", "schůzka",
                 "ovládací panel sdílení", "sharing control bar", "screen sharing toolbar", "sdílení obsahu"}


# The pre-join dialog ("Připojení ke schůzce | <subject> | Microsoft Teams"): Teams already holds the
# microphone for the device preview, but the call has not started (2026-09-21: 6.5 minutes of a join screen
# recorded, no audio on it).
TEAMS_PREJOIN = {"připojení ke schůzce", "pripojeni ke schuzce", "připojit se ke schůzce",
                 "join meeting", "meeting join", "pre-join", "prejoin"}


def is_generic_title(title: str | None) -> bool:
    t = (title or "").strip().lower()
    return not t or t in TEAMS_GENERIC or t in TEAMS_NAV or t.startswith("meeting compact")
TEAMS_EXE = {"ms-teams.exe", "teams.exe"}
# ------------------------------------------------------------------

OUT_DIR.mkdir(parents=True, exist_ok=True)
logging.basicConfig(filename=OUT_DIR / "teamsrec.log", level=logging.INFO,
                    format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("teamsrec")


# ---------------------------------------------------------------- detection
CAM_KEY = r"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"


def _subkeys(key):
    i = 0
    while True:
        try:
            yield winreg.EnumKey(key, i)
            i += 1
        except OSError:
            return


def teams_mic_in_use() -> bool:
    """True while any *teams* app holds the microphone (LastUsedTimeStop == 0)."""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, CAM_KEY) as root:
            candidates = [(root, n) for n in _subkeys(root) if n != "NonPackaged"]
            try:
                np_key = winreg.OpenKey(root, "NonPackaged")
                candidates += [(np_key, n) for n in _subkeys(np_key)]
            except OSError:
                pass
            for parent, name in candidates:
                if "teams" not in name.lower():
                    continue
                try:
                    with winreg.OpenKey(parent, name) as k:
                        start, _ = winreg.QueryValueEx(k, "LastUsedTimeStart")
                        stop, _ = winreg.QueryValueEx(k, "LastUsedTimeStop")
                        if start and stop == 0:
                            return True
                except OSError:
                    continue
    except OSError as e:
        log.warning("registry read failed: %s", e)
    return False


def teams_windows() -> list[tuple[int, str]]:
    """(hwnd, title) of every visible top-level Teams window."""
    pids = {p.pid for p in psutil.process_iter(["name"])
            if (p.info["name"] or "").lower() in TEAMS_EXE}
    out = []

    def cb(hwnd, _):
        if win32gui.IsWindowVisible(hwnd):
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid in pids:
                t = win32gui.GetWindowText(hwnd)
                if t:
                    out.append((hwnd, t))
    win32gui.EnumWindows(cb, None)
    return out


def teams_window_titles() -> list[str]:
    return [t for _, t in teams_windows()]


def _title_parts(title: str) -> list[str]:
    return [p.strip() for p in title.split("|")]


def is_prejoin_title(title: str) -> bool:
    """The join dialog, which exists only before the call is joined."""
    parts = _title_parts(title)
    return len(parts) >= 2 and parts[-1].lower() == "microsoft teams" and parts[0].lower() in TEAMS_PREJOIN


def is_meeting_window(title: str) -> bool:
    """A window that exists only once the call is joined: "<subject> | Microsoft Teams", the compact view,
    the sharing toolbar. Nav sections ("Calendar | …"), chats and the join dialog are not."""
    parts = _title_parts(title)
    if len(parts) < 2 or parts[-1].lower() != "microsoft teams":
        return False
    head = parts[0].lower()
    return head not in TEAMS_PREJOIN and head not in TEAMS_NAV


def prejoin_only(titles: list[str]) -> bool:
    """True while Teams shows the join screen and no meeting window: the microphone is held by the device
    preview of that dialog, so recording now would capture the dialog, not a call."""
    return any(is_prejoin_title(t) for t in titles) and not any(is_meeting_window(t) for t in titles)


# ---------------------------------------------------------------- Outlook calendar (classic Outlook, COM, local)

def _title_norm(s: str) -> str:
    s = unicodedata.normalize("NFKD", s or "").encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9]+", " ", s.lower()).strip()


def candidates_at(items: list[dict], at: datetime, window_s: int = 3600) -> list[dict]:
    """Calendar items whose span comes within `window_s` of `at`, closest first (offered on the review page)."""
    out = [it for it in items if it["start"] - timedelta(seconds=window_s) <= at <= it["end"] + timedelta(seconds=window_s)]
    return sorted(out, key=lambda it: abs((it["start"] - at).total_seconds()))


def pick_meeting(items: list[dict], at: datetime, title: str | None = None,
                 before_s: int = 600, after_s: int = 300) -> tuple[dict | None, str]:
    """The calendar item for a call: by the meeting title first (the Teams window / file carries the subject,
    which settles ad-hoc calls and parallel meetings), else the item running at `at` (start - 10 min .. end +
    5 min; the one containing `at` first, then Teams meetings, then the closest start). Returns (item, match)
    with match "title" | "time" | "". Pure function over dicts (testable without Outlook)."""
    import difflib
    if title and not is_generic_title(title):
        t = _title_norm(title)
        near = candidates_at(items, at, window_s=7200)
        subjects = [_title_norm(it["subject"]) for it in near]
        hit = [it for it, s in zip(near, subjects) if s and (s == t or s in t or t in s)]
        if not hit:
            close = difflib.get_close_matches(t, [s for s in subjects if s], n=1, cutoff=0.8)
            hit = [it for it, s in zip(near, subjects) if close and s == close[0]]
        if hit:
            return hit[0], "title"
    best = None
    for it in items:
        if not (it["start"] - timedelta(seconds=before_s) <= at <= it["end"] + timedelta(seconds=after_s)):
            continue
        inside = it["start"] <= at <= it["end"]
        key = (0 if inside else 1, 0 if it.get("teams") else 1, abs((it["start"] - at).total_seconds()))
        if best is None or key < best[0]:
            best = (key, it)
    return (best[1], "time") if best else (None, "")


def outlook_items(day: datetime) -> list[dict]:
    """Calendar items of one day from the classic Outlook running on this machine (no network, no consent)."""
    import pythoncom
    import win32com.client
    pythoncom.CoInitialize()
    try:
        app = win32com.client.Dispatch("Outlook.Application")
        cal = app.GetNamespace("MAPI").GetDefaultFolder(9)
        items = cal.Items
        items.IncludeRecurrences = True
        items.Sort("[Start]")
        flt = f"[Start] >= '{day:%m/%d/%Y} 00:00' AND [Start] <= '{day:%m/%d/%Y} 23:59'"
        out = []
        for it in items.Restrict(flt):
            try:
                text = f"{it.Location or ''} {it.Body or ''}"
                out.append({
                    "subject": (it.Subject or "").strip(),
                    "start": datetime(it.Start.year, it.Start.month, it.Start.day, it.Start.hour, it.Start.minute),
                    "end": datetime(it.End.year, it.End.month, it.End.day, it.End.hour, it.End.minute),
                    "organizer": (it.Organizer or "").strip(),
                    "attendees": [r.Name for r in it.Recipients if r.Name],
                    "teams": "teams.microsoft.com" in text.lower(),
                })
            except Exception:
                continue
        return out
    finally:
        pythoncom.CoUninitialize()


def outlook_meeting(at: datetime | None = None, title: str | None = None) -> dict | None:
    """The meeting from Outlook for the call starting now (None when Outlook is off, not installed, or nothing
    matches). The result carries `match` ("title"/"time") and `candidates` (other items nearby) for the review page."""
    if not USE_OUTLOOK:
        return None
    at = at or datetime.now()
    try:
        items = outlook_items(at)
    except Exception as e:  # Outlook not running / new Outlook without COM / no profile
        log.info("outlook calendar not available: %s", str(e)[:120])
        return None
    it, match = pick_meeting(items, at, title)
    if it is None:
        return None
    out = dict(it)
    out["match"] = match
    out["candidates"] = [{"subject": c["subject"], "start": c["start"].isoformat(timespec="minutes"),
                          "end": c["end"].isoformat(timespec="minutes"), "teams": c.get("teams", False)}
                         for c in candidates_at(items, at) if c is not it][:5]
    return out


# ---------------------------------------------------------------- screen capture (Teams windows -> mp4)

def grab_window(hwnd) -> Image.Image | None:
    """Screenshot of one window (works when it is covered by other windows, not when minimized)."""
    if win32gui.IsIconic(hwnd):
        return None
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    w, h = right - left, bottom - top
    if w < SCREEN_MIN_WIN[0] or h < SCREEN_MIN_WIN[1]:
        return None
    hwnd_dc = win32gui.GetWindowDC(hwnd)
    mfc_dc = win32ui.CreateDCFromHandle(hwnd_dc)
    save_dc = mfc_dc.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    try:
        bmp.CreateCompatibleBitmap(mfc_dc, w, h)
        save_dc.SelectObject(bmp)
        ok = ctypes.windll.user32.PrintWindow(hwnd, save_dc.GetSafeHdc(), 2)  # 2 = PW_RENDERFULLCONTENT (WebView2)
        if not ok:
            return None
        info = bmp.GetInfo()
        img = Image.frombuffer("RGB", (info["bmWidth"], info["bmHeight"]), bmp.GetBitmapBits(True), "raw", "BGRX", 0, 1)
        return img
    finally:
        win32gui.DeleteObject(bmp.GetHandle())
        save_dc.DeleteDC(); mfc_dc.DeleteDC()
        win32gui.ReleaseDC(hwnd, hwnd_dc)


def fit_canvas(img: Image.Image) -> Image.Image:
    """Scale to fit SCREEN_W x SCREEN_H keeping the aspect ratio, black borders (constant frame size)."""
    scale = min(SCREEN_W / img.width, SCREEN_H / img.height, 1.0)
    w, h = max(1, int(img.width * scale)), max(1, int(img.height * scale))
    canvas = Image.new("RGB", (SCREEN_W, SCREEN_H))
    canvas.paste(img.resize((w, h), Image.BILINEAR), ((SCREEN_W - w) // 2, (SCREEN_H - h) // 2))
    return canvas


class ScreenCapture:
    """Every Teams window becomes <stem>_screen<N>.mp4 (SCREEN_FPS, x264). Teams uses several windows during a
    call - the meeting window, a popped-out gallery, shared content - so each gets its own file; the analysis
    later looks for name labels in all of them. A window that is minimized or briefly fails to render gets its
    last frame repeated, so the timeline stays aligned with the audio."""

    def __init__(self, stem: Path, ffmpeg: str, started: datetime):
        self.stem, self.ffmpeg, self.started = stem, ffmpeg, started
        self.screens: dict[int, dict] = {}  # hwnd -> {proc, meta, last}
        self.done: list[dict] = []
        self.n = 0
        self.stop_evt = threading.Event()
        self.thread = threading.Thread(target=self._loop, daemon=True)

    def start(self):
        self.thread.start()

    def _open(self, hwnd, title):
        self.n += 1
        path = Path(f"{self.stem}_screen{self.n}.mp4")
        cmd = [self.ffmpeg, "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
               "-s", f"{SCREEN_W}x{SCREEN_H}", "-r", str(SCREEN_FPS), "-i", "-",
               "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p", "-g", str(SCREEN_FPS * 10),
               "-movflags", "+frag_keyframe+empty_moov+default_base_moof",  # playable even if the app dies mid-call
               str(path)]
        proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        meta = {"file": path.name, "fps": SCREEN_FPS, "width": SCREEN_W, "height": SCREEN_H,
                "start_offset_s": round((datetime.now() - self.started).total_seconds(), 1),
                "titles": [title], "frames": 0}
        self.screens[hwnd] = {"proc": proc, "meta": meta, "last": None}
        log.info("screen capture: %s <- '%s'", path.name, title)

    def _close(self, hwnd):
        sc = self.screens.pop(hwnd)
        try:
            sc["proc"].stdin.close()
            sc["proc"].wait(timeout=20)
        except Exception:
            sc["proc"].kill()
        sc["meta"]["end_offset_s"] = round((datetime.now() - self.started).total_seconds(), 1)
        self.done.append(sc["meta"])

    def _loop(self):
        period = 1.0 / SCREEN_FPS
        while not self.stop_evt.is_set():
            t0 = time.time()
            try:
                self._tick()
            except Exception:
                log.exception("screen capture")
            self.stop_evt.wait(max(0.0, period - (time.time() - t0)))
        for hwnd in list(self.screens):
            self._close(hwnd)

    def _tick(self):
        present = {}
        for hwnd, title in teams_windows():
            try:
                l, t, r, b = win32gui.GetWindowRect(hwnd)
            except Exception:
                continue
            if r - l >= SCREEN_MIN_WIN[0] and b - t >= SCREEN_MIN_WIN[1]:
                present[hwnd] = title
        for hwnd in list(self.screens):
            if hwnd not in present and not win32gui.IsWindow(hwnd):
                self._close(hwnd)  # window closed for good
        for hwnd, title in present.items():
            if hwnd not in self.screens:
                self._open(hwnd, title)
            sc = self.screens[hwnd]
            if title not in sc["meta"]["titles"]:
                sc["meta"]["titles"].append(title)
        for hwnd, sc in self.screens.items():
            frame = None
            try:
                img = grab_window(hwnd)
                if img is not None:
                    frame = fit_canvas(img).tobytes()
            except Exception:
                frame = None
            frame = frame or sc["last"] or bytes(SCREEN_W * SCREEN_H * 3)
            try:
                sc["proc"].stdin.write(frame)
                sc["last"] = frame
                sc["meta"]["frames"] += 1
            except (BrokenPipeError, OSError):
                log.warning("screen capture: encoder for %s died", sc["meta"]["file"])

    def stop(self) -> list[dict]:
        self.stop_evt.set()
        self.thread.join(timeout=30)
        return [m for m in self.done if m["frames"] > 0]


def screen_capture_child(stem: Path, started: datetime) -> None:
    """Entry point of the separate screen-capture process (`teamsrec.py --screen-capture <stem> <started>`).
    Runs until the parent closes our stdin (or dies), then writes <stem>_screens.json for the parent."""
    try:
        ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2: real pixels
    except Exception:
        pass
    ff = _ffmpeg()
    if not ff:
        return
    sc = ScreenCapture(stem, ff, started)
    sc.start()
    try:
        sys.stdin.buffer.read()  # blocks until the parent closes the pipe
    except Exception:
        pass
    screens = sc.stop()
    Path(f"{stem}_screens.json").write_text(json.dumps(screens, ensure_ascii=False, indent=1), encoding="utf-8")


class ScreenCaptureProc:
    """The screen capture as a separate process: whatever happens in there (GDI, WebView2, encoder) cannot
    take the audio recording down with it."""

    def __init__(self, stem: Path, started: datetime):
        self.stem = stem
        exe = Path(sys.executable)
        pyw = exe.with_name("pythonw.exe") if exe.name.lower() == "python.exe" else exe
        self.proc = subprocess.Popen([str(pyw), str(Path(__file__).resolve()), "--screen-capture", str(stem),
                                      started.isoformat()],
                                     stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                     creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        log.info("screen capture process started (pid %d)", self.proc.pid)

    def alive(self) -> bool:
        return self.proc.poll() is None

    def stop(self) -> list[dict]:
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=45)
        except Exception:
            self.proc.kill()
        meta = Path(f"{self.stem}_screens.json")
        if meta.exists():
            try:
                data = json.loads(meta.read_text(encoding="utf-8"))
            finally:
                meta.unlink(missing_ok=True)
            return data
        log.warning("screen capture process left no metadata (crashed?); videos on disk are kept")
        return screens_on_disk(self.stem)


def screens_on_disk(stem: Path) -> list[dict]:
    """Fallback metadata for <stem>_screen<N>.mp4 files whose capture process did not report (crash/kill)."""
    out = []
    for p in sorted(stem.parent.glob(f"{stem.name}_screen*.mp4")):
        if p.stat().st_size > 1000:
            out.append({"file": p.name, "fps": SCREEN_FPS, "width": SCREEN_W, "height": SCREEN_H,
                        "start_offset_s": 0.0, "titles": [], "frames": -1, "recovered": True})
    return out


# ---------------------------------------------------------------- recovery of recordings cut by a crash

def _repair_wav(path: Path) -> bool:
    """A killed process never closes the wave file, so the RIFF/data sizes say 0. Fix them from the file size."""
    try:
        size = path.stat().st_size
        if size <= 44:
            return False
        with open(path, "r+b") as f:
            head = f.read(44)
            if head[:4] != b"RIFF" or head[8:12] != b"WAVE" or head[36:40] != b"data":
                return False
            data_size = int.from_bytes(head[40:44], "little")
            if data_size == size - 44:
                return True  # already consistent
            f.seek(4); f.write((size - 8).to_bytes(4, "little"))
            f.seek(40); f.write((size - 44).to_bytes(4, "little"))
        return True
    except OSError:
        return False


def mix_audio(ff: str, stem: Path, audio: list[Path]) -> Path | None:
    mix = Path(f"{stem}_mix.wav")
    cmd = [ff, "-y", "-loglevel", "error"]
    for p in audio:
        cmd += ["-i", str(p)]
    cmd += ["-filter_complex", f"amix=inputs={len(audio)}:duration=longest:normalize=0",
            "-ac", "1", "-ar", "16000", str(mix)]
    r = subprocess.run(cmd, capture_output=True, text=True, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if r.returncode:
        log.error("ffmpeg: %s", r.stderr)
        return None
    return mix


def recover_orphans(out_dir: Path) -> int:
    """Recordings that have audio but no sidecar were cut by a crash or a kill: repair the WAV headers, write the
    sidecar (stop_reason app_crash) and the mix, so nothing recorded is lost. Runs once at startup, so no
    recording is in progress. Returns how many were recovered."""
    n = 0
    for sysfile in sorted(out_dir.glob("[0-9]*/[0-9]*/*/*_sys.wav")):
        stem = sysfile.with_name(sysfile.name[:-len("_sys.wav")])
        if Path(f"{stem}.json").exists():
            continue
        audio = [p for p in (Path(f"{stem}_sys.wav"), Path(f"{stem}_mic.wav")) if p.exists() and _repair_wav(p)]
        if not audio:
            continue
        try:
            with wave.open(str(audio[0]), "rb") as w:
                dur = w.getnframes() / w.getframerate()
                tracks = {}
                for p in audio:
                    with wave.open(str(p), "rb") as t:
                        tracks["mic" if p.name.endswith("_mic.wav") else "sys"] = {
                            "file": p.name, "sample_rate": t.getframerate(), "channels": t.getnchannels()}
        except (wave.Error, OSError) as e:
            log.warning("orphan %s unreadable: %s", stem.name, e)
            continue
        if dur < MIN_DURATION_S:
            for p in stem.parent.iterdir():
                p.unlink(missing_ok=True)
            try:
                stem.parent.rmdir()
            except OSError:
                pass
            log.info("orphan %s deleted (%.0f s)", stem.name, dur)
            continue
        m = re.match(r"(\d{4})-(\d{2})-(\d{2})_(\d{2})(\d{2})_(.*)", stem.name)
        started = datetime(*map(int, m.groups()[:5])) if m else datetime.fromtimestamp(sysfile.stat().st_mtime)
        title = (m.group(6) if m else stem.name).replace("-", " ")
        ff = _ffmpeg() if MIX_WITH_FFMPEG else None
        mix = mix_audio(ff, stem, audio) if ff else None
        meta = {
            "format": FORMAT_VERSION, "app": APP_NAME, "app_version": APP_VERSION,
            "title": title, "slug": slug(title), "source": "live",
            "start": started.isoformat(timespec="seconds"),
            "end": (started + timedelta(seconds=dur)).isoformat(timespec="seconds"), "duration_s": round(dur),
            "stop_reason": "app_crash", "recovered": True, "tracks": tracks, "teams_windows_seen": [],
        }
        if mix:
            meta["mix"] = {"file": mix.name, "sample_rate": 16000, "channels": 1}
        screens = screens_on_disk(stem)
        if screens:
            meta["screens"] = screens
        Path(f"{stem}.json").write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        log.warning("recovered orphan recording %s (%.0f s, cut by a crash)", stem.name, dur)
        n += 1
    return n


def guess_titles(titles: list[str]) -> list[str]:
    """Subjects of meeting windows ('<subject> | Microsoft Teams'), nav/generic titles skipped, in order."""
    out = []
    for t in titles:
        parts = [p.strip() for p in t.split("|")]
        if len(parts) < 2 or parts[-1].lower() != "microsoft teams":
            continue
        head = parts[0]
        if is_generic_title(head) or head.lower() in TEAMS_NAV or head in out:
            continue
        out.append(head)
    return out


def guess_meeting_title(titles: list[str]) -> str | None:
    """The first meeting-window subject, if any."""
    found = guess_titles(titles)
    return found[0] if found else None


def slug(s: str, n: int = 60) -> str:
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode()
    s = re.sub(r"[^A-Za-z0-9]+", "-", s).strip("-").lower()
    return s[:n] or "teams-call"


# ---------------------------------------------------------------- recorder
class Recorder:
    def __init__(self, stem: Path, with_mic: bool = True, mic_only: bool = False, mic_name: str = ""):
        self.stem = stem
        self.with_mic = with_mic
        self.mic_only = mic_only      # on-site meeting: the room microphone carries everybody, no loopback track
        self.mic_name = mic_name      # preferred input device (substring of its name), else the default input
        self.started = datetime.now()
        self.pa = pyaudio.PyAudio()
        self.streams, self.writers = [], []
        self.tracks: dict[str, dict] = {}   # "sys"/"mic" -> {file, sample_rate, channels}
        self.queues: dict[str, queue.Queue] = {}   # per track: the writer's input (kept across a device reopen)
        self.reopens = 0
        self.last_loud = time.time()        # last time the system track was not silent
        self.bytes_received = 0             # watchdog: a sleeping Bluetooth device delivers nothing at all
        self.last_data = time.time()        # watchdog: last time any stream delivered a buffer
        self.last_mic_data = None           # watchdog: last time the mic delivered a buffer (None = never)
        self.last_mic_loud = 0.0            # watchdog: last time the user was audibly speaking
        self.heard_sys = False              # anything but digital silence arrived on the loopback
        self.heard_mic = False              # ... and on the microphone

    def _callback(self, name: str, q: queue.Queue):
        is_sys = name == "sys"

        def cb(data, frames, ti, status):
            q.put(data)
            self.bytes_received += len(data)
            self.last_data = time.time()
            if not is_sys:
                self.last_mic_data = self.last_data
            if audioop.max(data, 2) > SILENCE_LEVEL:
                if is_sys:
                    self.last_loud = time.time()
                    self.heard_sys = True
                else:
                    self.last_mic_loud = time.time()
                    self.heard_mic = True
            return (None, pyaudio.paContinue)
        return cb

    def _open(self, dev, suffix, name):
        rate = int(dev["defaultSampleRate"])
        ch = max(1, min(2, int(dev["maxInputChannels"])))
        path = Path(f"{self.stem}{suffix}")
        wf = wave.open(str(path), "wb")
        wf.setnchannels(ch); wf.setsampwidth(2); wf.setframerate(rate)
        q: queue.Queue = queue.Queue()
        self.tracks[name] = {"file": path.name, "sample_rate": rate, "channels": ch, "device": dev["name"]}
        self.queues[name] = q

        def writer():
            while (chunk := q.get()) is not None:
                wf.writeframes(chunk)
            wf.close()

        s = self.pa.open(format=pyaudio.paInt16, channels=ch, rate=rate, input=True,
                         input_device_index=dev["index"], frames_per_buffer=1024,
                         stream_callback=self._callback(name, q))
        th = threading.Thread(target=writer, daemon=True); th.start()
        self.streams.append(s); self.writers.append((q, th))
        log.info("recording %s  %d Hz x%d  <- %s", path.name, rate, ch, dev["name"])
        return path

    def _find_input(self, name_part: str) -> dict | None:
        """An active WASAPI input device whose name contains name_part (case-insensitive)."""
        wasapi = self.pa.get_host_api_info_by_type(pyaudio.paWASAPI)
        for i in range(wasapi["deviceCount"]):
            dev = self.pa.get_device_info_by_host_api_device_index(wasapi["index"], i)
            if dev["maxInputChannels"] > 0 and name_part.lower() in dev["name"].lower() and not dev.get("isLoopbackDevice"):
                return dev
        return None

    def _default_devices(self) -> dict[str, dict]:
        out = {} if self.mic_only else {"sys": self.pa.get_default_wasapi_loopback()}
        if self.with_mic:
            try:
                dev = self._find_input(self.mic_name) if self.mic_name else None
                if self.mic_name and dev is None:
                    log.warning("input device '%s' not found or not active, using the default input", self.mic_name)
                if dev is None:
                    wasapi = self.pa.get_host_api_info_by_type(pyaudio.paWASAPI)
                    dev = self.pa.get_device_info_by_index(wasapi["defaultInputDevice"])
                out["mic"] = dev
            except Exception as e:
                log.warning("no microphone: %s", e)
        return out

    def reopen(self) -> bool:
        """The streams stopped delivering (a Bluetooth headset woke up and Windows re-registered the device, a
        dongle was re-plugged): open new streams on the current default devices and keep writing into the same
        files. The gap is padded with silence so the timeline stays aligned with the screen videos."""
        gap = max(0.0, time.time() - self.last_data)
        for s in self.streams:
            try:
                s.stop_stream(); s.close()
            except Exception:
                pass
        self.streams = []
        try:
            self.pa.terminate()
        except Exception:
            pass
        self.pa = pyaudio.PyAudio()  # re-enumerates the devices
        devs = self._default_devices()
        ok = 0
        for name, meta in self.tracks.items():
            dev = devs.get(name)
            if dev is None:
                continue
            rate, ch = int(dev["defaultSampleRate"]), max(1, min(2, int(dev["maxInputChannels"])))
            if (rate, ch) != (meta["sample_rate"], meta["channels"]):
                log.warning("reopen %s: device format %d Hz x%d differs from the file (%d Hz x%d), track stays as is",
                            name, rate, ch, meta["sample_rate"], meta["channels"])
                continue
            q = self.queues[name]
            if gap > 0.5:
                q.put(bytes(int(gap * rate) * ch * 2))  # silence for the lost stretch
            try:
                s = self.pa.open(format=pyaudio.paInt16, channels=ch, rate=rate, input=True,
                                 input_device_index=dev["index"], frames_per_buffer=1024,
                                 stream_callback=self._callback(name, q))
                s.start_stream()
                self.streams.append(s)
                meta["device"] = dev["name"]
                ok += 1
                log.info("reopened %s on %s after %.0f s without data", name, dev["name"], gap)
            except Exception as e:
                log.warning("reopen %s failed: %s", name, e)
        self.last_data = time.time()
        self.reopens += 1
        return ok > 0

    def input_names(self) -> list[str]:
        """Names of the WASAPI devices that can be recorded from right now (for error messages)."""
        try:
            wasapi = self.pa.get_host_api_info_by_type(pyaudio.paWASAPI)
            return [d["name"] for i in range(wasapi["deviceCount"])
                    if (d := self.pa.get_device_info_by_host_api_device_index(wasapi["index"], i))["maxInputChannels"] > 0]
        except Exception:
            return []

    def start(self) -> list[Path]:
        devs = self._default_devices()
        files = [self._open(devs["sys"], "_sys.wav", "sys")] if "sys" in devs else []
        if "mic" in devs:
            try:
                files.append(self._open(devs["mic"], "_mic.wav", "mic"))
            except Exception as e:  # no mic is not fatal as long as the loopback track opened
                log.warning("mic not recorded: %s", e)
        if not files:  # nothing to write into: never pretend to record (2026-09-22: 4 h of "recording" nothing)
            have = ", ".join(self.input_names()) or "žádná"
            want = f"'{self.mic_name}'" if self.mic_name else "výchozí vstup"
            raise RuntimeError(f"nepodařilo se otevřít žádné zvukové zařízení (hledáno {want}; "
                               f"dostupná zařízení: {have})")
        for s in self.streams:
            s.start_stream()
        return files

    def stop(self) -> float:
        for s in self.streams:
            s.stop_stream(); s.close()
        for q, th in self.writers:
            q.put(None); th.join(timeout=10)
        self.pa.terminate()
        return (datetime.now() - self.started).total_seconds()


# ---------------------------------------------------------------- prompt
def prompt(title: str) -> bool:
    """Small always-on-top popup in the top-right corner. Returns True to record."""
    res = {"ok": PROMPT_DEFAULT == "record"}
    root = tk.Tk()
    root.overrideredirect(True); root.attributes("-topmost", True)
    w, h = 380, 130
    root.geometry(f"{w}x{h}+{root.winfo_screenwidth() - w - 24}+24")
    f = tk.Frame(root, bg="#1f1f1f", padx=14, pady=10, highlightbackground="#c0392b",
                 highlightthickness=2)
    f.pack(fill="both", expand=True)
    tk.Label(f, text="Teams call detected — record it?", fg="#bbb", bg="#1f1f1f",
             font=("Segoe UI", 9)).pack(anchor="w")
    tk.Label(f, text=title, fg="white", bg="#1f1f1f", font=("Segoe UI", 11, "bold"),
             wraplength=340, justify="left").pack(anchor="w", pady=(2, 6))
    cnt = tk.StringVar()
    row = tk.Frame(f, bg="#1f1f1f"); row.pack(fill="x")
    tk.Label(row, textvariable=cnt, fg="#888", bg="#1f1f1f", font=("Segoe UI", 8)).pack(side="left")

    def yes(): res["ok"] = True; root.destroy()
    def no(): res["ok"] = False; root.destroy()
    tk.Button(row, text="Skip", width=8, command=no).pack(side="right")
    tk.Button(row, text="● Record", width=10, command=yes, bg="#c0392b", fg="white",
              activebackground="#a93226").pack(side="right", padx=(0, 6))
    root.bind("<Return>", lambda e: yes()); root.bind("<Escape>", lambda e: no())
    deadline = time.time() + PROMPT_TIMEOUT_S

    def tick():
        left = int(deadline - time.time())
        if left <= 0:
            root.destroy(); return
        cnt.set(f"{'records' if PROMPT_DEFAULT == 'record' else 'skips'} in {left}s"); root.after(500, tick)
    tick()
    winsound.MessageBeep(winsound.MB_ICONASTERISK)
    root.focus_force(); root.mainloop()
    return res["ok"]


def ask_discard(title: str, timeout_s: int = PROMPT_TIMEOUT_S) -> bool:
    """Recording has already started; ask whether to throw it away. Native Windows message box with a timeout
    (no Tk in this process). Returns True = discard. Default (Enter / timeout) = keep, unless PROMPT_DEFAULT is
    "skip"."""
    MB_YESNO, MB_ICONQUESTION, MB_DEFBUTTON2, MB_SETFOREGROUND, MB_TOPMOST = 0x4, 0x20, 0x100, 0x10000, 0x40000
    IDYES = 6
    flags = MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST | (0 if PROMPT_DEFAULT == "skip" else MB_DEFBUTTON2)
    text = (f"Nahrávám: {title}\n\nZahodit tuto nahrávku?\n"
            f"Ano = nenahrávat a smazat, Ne = nechat nahrávat (za {timeout_s} s automaticky "
            f"{'zahodit' if PROMPT_DEFAULT == 'skip' else 'nechat'}).")
    try:
        winsound.MessageBeep(winsound.MB_ICONASTERISK)
        r = ctypes.windll.user32.MessageBoxTimeoutW(None, text, "teamsrec", flags, 0, int(timeout_s * 1000))
    except Exception:
        log.exception("discard box")
        return False
    if r == IDYES:
        return True
    if r == 7:  # IDNO
        return False
    return PROMPT_DEFAULT == "skip"  # 32000 = timed out


# ---------------------------------------------------------------- app
def icon_image(color):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    ImageDraw.Draw(img).ellipse((8, 8, 56, 56), fill=color)
    return img


IMG_IDLE, IMG_REC, IMG_WARN = icon_image("#7f8c8d"), icon_image("#e74c3c"), icon_image("#f1c40f")


class App:
    def __init__(self):
        self.rec: Recorder | None = None
        self.title = ""
        self.files: list[Path] = []
        self.titles_seen: set[str] = set()
        self.manual = False
        self.playback = False
        self.no_audio_warned = False
        self.declined = False
        self.call_missing_since = None
        self.prejoin_since = None           # Teams sits on the join screen: the call has not started yet
        self.reopen_at = None               # when the last stream reopen happened (waiting for its verdict)
        self.reopen_tries = 0               # failed reopens in a row -> longer backoff
        self.next_reopen_at = 0.0
        self.lock = threading.Lock()
        self.quit = threading.Event()
        self.icon = pystray.Icon("teamsrec", IMG_IDLE, "teamsrec: idle", menu=pystray.Menu(
            pystray.MenuItem(lambda _: self.status(), None, enabled=False),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Record now (manual)", lambda: self.start("manual", manual=True),
                             enabled=lambda _: self.rec is None),
            pystray.MenuItem("Record on-site meeting (microphone only)", self.start_onsite,
                             enabled=lambda _: not self.rec),
            pystray.MenuItem("Record playback (system audio only)", self.start_playback,
                             enabled=lambda _: self.rec is None),
            pystray.MenuItem("Stop & keep", lambda: self.stop("tray stop"),
                             enabled=lambda _: self.rec is not None),
            pystray.MenuItem("Abort & delete", lambda: self.stop("aborted"),
                             enabled=lambda _: self.rec is not None),
            pystray.MenuItem("Open folder", lambda: subprocess.Popen(["explorer", str(OUT_DIR)])),
            pystray.MenuItem("Quit", self.exit),
        ))

    # -- state
    def status(self):
        if self.rec and getattr(self, "audio_warned", None):
            m = int((datetime.now() - self.rec.started).total_seconds() // 60)
            return f"⚠ {m} min — BEZ ZVUKU: {self.audio_warned}"
        if not self.rec:
            if self.prejoin_since:
                return "Teams join screen — recording starts when you join"
            return "Idle — waiting for a Teams call"
        m = int((datetime.now() - self.rec.started).total_seconds() // 60)
        return f"● REC {m} min — {self.title}"

    def _refresh(self):
        self.icon.icon = (IMG_WARN if getattr(self, "audio_warned", None) else IMG_REC) if self.rec else IMG_IDLE
        self.icon.title = self.status()
        self.icon.update_menu()

    def start_playback(self):
        """Record what is being played (a stored Teams recording): system track only, stops on silence."""
        def ask():
            try:
                default = win32gui.GetWindowText(win32gui.GetForegroundWindow()) or "playback"
            except Exception:
                default = "playback"
            root = tk.Tk(); root.withdraw(); root.attributes("-topmost", True)
            title = simpledialog.askstring("teamsrec", "Title of the recording being played:",
                                           initialvalue=default, parent=root)
            root.destroy()
            if title:
                self.start(title, manual=True, playback=True)
        threading.Thread(target=ask, daemon=True).start()

    def start_onsite(self):
        """On-site meeting: only the room microphone (laptop array or whatever `onsite_mic` names), no Teams,
        no loopback, no window capture. Title and participants from the calendar when a meeting is running."""
        cal = outlook_meeting()
        title = cal["subject"] if cal and cal.get("subject") else "onsite"
        self.start(title, manual=True, onsite=True)
        if self.rec:
            dev = self.rec.tracks.get("mic", {}).get("device", "?")
            log.info("on-site recording from '%s'", dev)
            self.icon.notify(f"Nahrávám na místě: {title} (mikrofon {dev}). Ukončete přes Stop & keep.", "teamsrec")

    def start(self, title, manual=False, playback=False, onsite=False):
        with self.lock:
            if self.rec:
                return
            now = datetime.now()
            name = f"{now:%Y-%m-%d}_{now:%H%M}_{slug(title)}"
            stem = OUT_DIR / f"{now:%Y}" / f"{now:%m}" / name / name
            stem.parent.mkdir(parents=True, exist_ok=True)
            try:
                rec = Recorder(stem, with_mic=not playback, mic_only=onsite, mic_name=ONSITE_MIC if onsite else "")
                self.files = rec.start()
            except Exception as e:
                log.exception("start failed")
                try:
                    stem.parent.rmdir()  # the folder we made a moment ago, still empty
                except OSError:
                    pass
                self.icon.notify(f"Nahrávání se NESPUSTILO: {e}", "teamsrec")
                try:
                    winsound.MessageBeep(winsound.MB_ICONHAND)
                except Exception:
                    pass
                return
            self.rec, self.title, self.manual, self.playback, self.onsite = rec, title, manual, playback, onsite
            self.titles_seen, self.call_missing_since, self.no_audio_warned = set(), None, False
            self.audio_warned, self.audio_warned_at = None, 0.0
            self.prejoin_since = None
            self.reopen_at, self.reopen_tries, self.next_reopen_at = None, 0, 0.0
            self.calendar = None if playback else outlook_meeting(now, title)
            self.title_source = "manual" if manual else ("window" if not is_generic_title(title) else "generic")
            if self.calendar and self.calendar.get("subject") and (is_generic_title(title) or not manual):
                self.title = self.calendar["subject"]
                self.title_source = "calendar"
            self.screen = None
            ff = _ffmpeg() if SCREEN_CAPTURE and not onsite else None
            if ff:
                try:
                    self.screen = ScreenCaptureProc(stem, rec.started)
                except Exception:
                    log.exception("screen capture not started")
                    self.screen = None
            elif SCREEN_CAPTURE and not onsite:
                log.warning("screen capture needs ffmpeg (not found), recording audio only")
        self._refresh()
        log.info("START '%s' (%s) -> %s", title, "playback" if playback else "manual" if manual else "live", stem)

    AUDIO_STALL_S = 20     # no buffer from any device for this long = the device went to sleep / was unplugged
    AUDIO_SILENT_S = 90    # the system track stays digitally silent this long during a live call = wrong device
    AUDIO_BACKOFF_S = (0, 30, 60, 180, 300)   # wait this long before the 1st, 2nd … failed reopen is retried
    AUDIO_MAX_REOPENS = 8
    REOPEN_CHECK_S = 12    # a reopen counts as successful only if data still arrives this long afterwards
    AUDIO_REWARN_S = 300   # the "no audio" alarm repeats this often: one notification is missed in a meeting

    def _reopen_result(self, rec, now: float):
        """Did the last reopen bring the audio back? A sleeping Bluetooth dongle answers with one buffer and
        goes quiet again, so the verdict is passed this long after the reopen, and a failed one makes the next
        attempt wait longer: every reopen re-enumerates the devices and Windows answers with a device-change
        storm (2026-09-21: 13 reopens = 13 tray notifications in 6 minutes)."""
        if self.reopen_at is None or now - self.reopen_at < self.REOPEN_CHECK_S:
            return
        if now - rec.last_data <= 4:
            log.info("audio watchdog: data is arriving again after the reopen")
            self.reopen_tries = 0
            self.next_reopen_at = 0.0
        else:
            self.reopen_tries += 1
            wait = self.AUDIO_BACKOFF_S[min(self.reopen_tries, len(self.AUDIO_BACKOFF_S) - 1)]
            log.warning("audio watchdog: reopen #%d brought no data (device asleep or taken by Teams?), "
                        "next attempt in %d s", rec.reopens, wait)
            self.next_reopen_at = now + wait
        self.reopen_at = None

    def _try_reopen(self, rec, now: float):
        """At most one reopen per backoff window, and never while the previous one is still being judged."""
        if self.reopen_at is not None or now < self.next_reopen_at or rec.reopens >= self.AUDIO_MAX_REOPENS:
            return
        log.warning("audio watchdog: no data for %.0f s, reopening the streams on the current devices",
                    now - rec.last_data)
        try:
            if rec.reopen():
                self.reopen_at = time.time()
        except Exception:
            log.exception("reopen")
            self.next_reopen_at = now + self.AUDIO_BACKOFF_S[-1]

    def _audio_watchdog(self, elapsed: float):
        """Warn loudly (tray + log) when the call audio is not arriving: a Bluetooth headset that fell asleep,
        or Teams playing through another device than the Windows default we record."""
        rec = self.rec
        now = time.time()
        if not rec or self.playback or elapsed < self.AUDIO_STALL_S + 5:
            return
        self._reopen_result(rec, now)
        if elapsed < self.AUDIO_SILENT_S and time.time() - rec.last_data <= self.AUDIO_STALL_S:
            return
        if rec.mic_only:
            if now - rec.last_data > self.AUDIO_STALL_S:
                self._try_reopen(rec, now)
            return
        stalled = now - rec.last_data > self.AUDIO_STALL_S
        if stalled:
            self._try_reopen(rec, now)
            if self.reopen_at is not None:
                return  # a reopen is under way: wait for its verdict before alarming the user
        # the others being silent while the user talks (presenting, a monologue) is not a fault
        silent = now - rec.last_loud > self.AUDIO_SILENT_S and now - rec.last_mic_loud > self.AUDIO_SILENT_S
        no_mic = rec.with_mic and rec.last_mic_data is None
        problem = "audio streams stopped (device asleep or unplugged?)" if stalled else \
            "system audio is silent (Teams playing through another device?)" if silent else \
            "the microphone never delivered data" if no_mic else None
        if problem and (problem != getattr(self, "audio_warned", None)
                        or now - getattr(self, "audio_warned_at", 0.0) > self.AUDIO_REWARN_S):
            self.audio_warned, self.audio_warned_at = problem, now
            log.warning("audio watchdog: %s", problem)
            self.icon.notify(f"Zvuk se NENAHRÁVÁ: {problem}. Zkontrolujte sluchátka / vstupní zařízení.", "teamsrec")
            self._refresh()
            try:
                winsound.MessageBeep(winsound.MB_ICONEXCLAMATION)
            except Exception:
                pass
        elif not problem and getattr(self, "audio_warned", None):
            log.info("audio watchdog: audio is back")
            self.audio_warned = None
            self._refresh()
            self.icon.notify("Zvuk se obnovil, nahrávání pokračuje.", "teamsrec")

    def _ask_discard(self, rec, title):
        """Runs beside the recording; only a clear "Ano" discards it."""
        if ask_discard(title):
            with self.lock:
                same = self.rec is rec
            if same:
                log.info("discarded '%s' on request", title)
                self.declined = True
                self.stop("aborted")

    def stop(self, reason):
        with self.lock:
            if not self.rec:
                return
            rec, self.rec = self.rec, None
            dur = rec.stop()
            sc = getattr(self, "screen", None)
            screens = sc.stop() if sc else []
            self.screen = None
            files = [p for p in self.files if p.exists()]
            files += [rec.stem.parent / m["file"] for m in screens if (rec.stem.parent / m["file"]).exists()]
        self._refresh()
        log.info("STOP (%s) after %.0fs", reason, dur)
        if not any(p.suffix.lower() == ".wav" for p in files):  # the devices vanished: nothing was ever written
            for p in files:
                p.unlink(missing_ok=True)
            try:
                rec.stem.parent.rmdir()
            except OSError:
                pass
            log.error("%s: no audio file was written (%d reopens), nothing kept", rec.stem.name, rec.reopens)
            self.icon.notify(f"Nahrávka {rec.stem.name} NEVZNIKLA: zvukové zařízení nedodalo nic. "
                             f"Zkontrolujte mikrofon.", "teamsrec")
            try:
                winsound.MessageBeep(winsound.MB_ICONHAND)
            except Exception:
                pass
            return
        if reason == "aborted" or dur < MIN_DURATION_S:
            for p in files:
                p.unlink(missing_ok=True)
            try:
                rec.stem.parent.rmdir()  # the recording folder, now empty
            except OSError:
                pass
            log.info("deleted (%s)", "aborted" if reason == "aborted" else "too short")
            self.icon.notify("Recording discarded", "teamsrec")
            return
        threading.Thread(target=self._finalize, args=(rec, files, dur, reason, screens), daemon=True).start()

    STOP_REASONS = {"call ended": "call_ended", "max duration": "max_duration", "tray stop": "user_stop",
                    "silence": "silence", "quit": "app_quit"}

    def _rematch(self, rec, cal, title, title_source):
        """At the end of the call the Teams window has carried its real subject for a while. If the calendar
        link was only guessed by time (or missing), match again by that subject; a different meeting wins
        (a 10:59 start looked like the 10:30 meeting, but the window said 'Debrief'). Returns (cal, title,
        title_source, changed)."""
        seen = [t for t in guess_titles(sorted(self.titles_seen)) if not is_generic_title(t)]
        if not seen or (cal and cal.get("match") == "title"):
            return cal, title, title_source, False
        for window_title in seen:
            better = outlook_meeting(rec.started, window_title)
            if better and better.get("match") == "title":
                if not cal or better.get("subject") != cal.get("subject"):
                    log.info("calendar re-matched by the window title '%s': '%s' (was '%s')", window_title,
                             better.get("subject"), cal.get("subject") if cal else None)
                    return better, better["subject"], "calendar", True
                return cal, title, title_source, False
        return cal, title, title_source, False

    @staticmethod
    def _rename_files(stem: Path, new_title: str) -> Path:
        """Folder + every <stem>* file get the stem of the new title (same date/time part). Files are closed by
        now (streams stopped, screen encoders finished)."""
        new_name = f"{stem.name[:15]}_{slug(new_title)}"  # YYYY-MM-DD_HHMM + new slug
        if new_name == stem.name:
            return stem
        new_dir = stem.parent.with_name(new_name)
        if new_dir.exists():
            log.warning("cannot rename to %s: exists", new_name)
            return stem
        stem.parent.rename(new_dir)
        for f in sorted(new_dir.iterdir()):
            if f.name.startswith(stem.name):
                f.rename(f.with_name(new_name + f.name[len(stem.name):]))
        log.info("renamed %s -> %s", stem.name, new_name)
        return new_dir / new_name

    def _finalize(self, rec, files, dur, reason, screens=()):
        stem = rec.stem
        mix = None
        audio = [p for p in files if p.suffix.lower() == ".wav"]
        ff = _ffmpeg() if MIX_WITH_FFMPEG else None
        if ff and audio:
            mix = mix_audio(ff, stem, audio)
        source = "onsite" if getattr(self, "onsite", False) else "playback" if self.playback else "manual" if self.manual else "live"
        title = self.title
        cal = getattr(self, "calendar", None)
        title_source = getattr(self, "title_source", "generic")
        if is_generic_title(title):  # the window got its real subject only later in the call
            better = guess_meeting_title(sorted(self.titles_seen))
            if better:
                log.info("title '%s' replaced by '%s' seen during the call", title, better)
                title = better
                title_source = "window"
        cal, title, title_source, changed = self._rematch(rec, cal, title, title_source)
        if changed or (title_source == "window" and slug(title) != stem.name[17:]):
            new_stem = self._rename_files(stem, title)
            if new_stem != stem:
                files = [new_stem.parent / (new_stem.name + p.name[len(stem.name):]) for p in files]
                for m in screens:
                    m["file"] = new_stem.name + m["file"][len(stem.name):]
                for t in rec.tracks.values():
                    t["file"] = new_stem.name + t["file"][len(stem.name):]
                stem = rec.stem = new_stem
                audio = [p for p in files if p.suffix.lower() == ".wav"]
                mix = Path(f"{stem}_mix.wav") if mix else None  # the mix file was renamed with the rest
        meta = {
            "format": FORMAT_VERSION, "app": APP_NAME, "app_version": APP_VERSION,
            "title": title, "slug": slug(title), "source": source,
            "start": rec.started.isoformat(timespec="seconds"),
            "end": datetime.now().isoformat(timespec="seconds"), "duration_s": round(dur),
            "stop_reason": self.STOP_REASONS.get(reason, "user_stop"),
            "title_source": title_source,
            "audio_reopens": rec.reopens,
            "tracks": {k: v for k, v in rec.tracks.items() if (stem.parent / v["file"]).exists()},
            "teams_windows_seen": sorted(self.titles_seen),
        }
        if mix:
            meta["mix"] = {"file": mix.name, "sample_rate": 16000, "channels": 1}
        if cal:
            meta["participants"] = [{"name": n, "source": "calendar"} for n in cal.get("attendees", [])]
            meta["calendar"] = {"source": "outlook", "subject": cal.get("subject"), "organizer": cal.get("organizer"),
                                "start": cal["start"].isoformat(timespec="minutes"), "end": cal["end"].isoformat(timespec="minutes"),
                                "match": cal.get("match", "time"), "status": "auto", "candidates": cal.get("candidates", [])}
            log.info("calendar: '%s' (by %s), %d participants", cal.get("subject"), cal.get("match"), len(cal.get("attendees", [])))
        if not (rec.heard_sys or rec.heard_mic):  # every buffer was digital silence: the device delivered nothing
            meta["audio_silent"] = True
            log.warning("%s: no audible audio on any track (%d reopens), marked audio_silent",
                        stem.name, rec.reopens)
        kept = [m for m in screens if (stem.parent / m["file"]).exists() and (stem.parent / m["file"]).stat().st_size > 0]
        if kept:
            meta["screens"] = kept
            log.info("screens: %s", ", ".join(f"{m['file']} ({m['frames']} frames)" for m in kept))
        jpath = Path(f"{stem}.json")  # written last: marks the recording as complete
        jpath.write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        if meta.get("audio_silent"):
            self.icon.notify(f"{stem.name}: žádný zvuk (zařízení nedodalo data), nahrávka se nebude zpracovávat.",
                             "teamsrec")
        else:
            self.icon.notify(f"Saved {stem.name} ({round(dur / 60)} min)", "teamsrec")
        if POST_HOOK:
            fmt = dict(stem=stem.name, dir=str(stem.parent), json=str(jpath),
                       sys=str(audio[0]) if audio else "", mic=str(audio[1]) if len(audio) > 1 else "",
                       mix=str(mix) if mix else "")
            cmd = POST_HOOK.format(**fmt)
            log.info("post-hook: %s", cmd)
            r = subprocess.run(cmd, shell=True, capture_output=True, text=True)
            log.info("post-hook rc=%s %s", r.returncode, (r.stderr or r.stdout)[-500:])

    # -- monitor loop
    def loop(self):
        while not self.quit.is_set():
            try:
                in_call = teams_mic_in_use()
                if self.rec:
                    self.titles_seen.update(teams_window_titles())
                    elapsed = (datetime.now() - self.rec.started).total_seconds()
                    if elapsed > 10 and self.rec.bytes_received == 0 and not self.no_audio_warned:
                        self.no_audio_warned = True
                        log.warning("no audio data from the default devices after 10 s (headset off? wrong default device?)")
                        self.icon.notify("No audio is arriving from the default devices. Headset off?", "teamsrec")
                    self._audio_watchdog(elapsed)
                    if elapsed > MAX_DURATION_S:
                        self.stop("max duration")
                    elif self.playback:
                        if elapsed > 15 and time.time() - self.rec.last_loud >= SILENCE_STOP_S:
                            self.stop("silence")
                    elif getattr(self, "onsite", False):
                        pass  # ends only via the tray (Stop & keep) or the safety cap
                    elif not self.manual:
                        if in_call:
                            self.call_missing_since = None
                        else:
                            self.call_missing_since = self.call_missing_since or time.time()
                            if time.time() - self.call_missing_since >= CALL_END_GRACE_S:
                                self.stop("call ended")
                    self._refresh()
                else:
                    if in_call and not self.declined and prejoin_only(teams_window_titles()):
                        if not self.prejoin_since:  # the mic is held by the join dialog's device preview
                            self.prejoin_since = time.time()
                            log.info("Teams is on the join screen, waiting for the call to start")
                            self._refresh()
                    elif in_call and not self.declined:
                        self.prejoin_since = None
                        title = guess_meeting_title(teams_window_titles()) or "teams-call"
                        cal = outlook_meeting(None, title)
                        if cal and cal.get("subject"):
                            title = cal["subject"]
                        self.start(title)  # record first; nothing to click, the tray menu can still discard it
                        if self.rec:
                            if PROMPT_DEFAULT == "ask":
                                threading.Thread(target=self._ask_discard, args=(self.rec, title), daemon=True).start()
                            else:
                                self.icon.notify(f"Nahrávám: {title}. Zahodit lze z menu ikony v liště (Abort & delete).", "teamsrec")
                        else:
                            self.declined = True  # start failed (logged); do not retry until the call ends
                    elif not in_call:
                        self.declined = False
                        if self.prejoin_since:  # the join dialog was closed without joining
                            log.info("Teams left the join screen without a call after %.0f s",
                                     time.time() - self.prejoin_since)
                            self.prejoin_since = None
                            self._refresh()
            except Exception:
                log.exception("loop")
            time.sleep(POLL_S)

    def exit(self):
        self.quit.set()
        self.stop("quit")
        self.icon.stop()

    def run(self):
        threading.Thread(target=self.loop, daemon=True).start()
        self.icon.run()


def _single_instance() -> bool:
    """True if we are the only teamsrec running (autostart + desktop shortcut must not record twice)."""
    global _MUTEX
    _MUTEX = win32event.CreateMutex(None, False, "Local\\teamsrec-capture")
    return win32api.GetLastError() != winerror.ERROR_ALREADY_EXISTS


if __name__ == "__main__":
    if len(sys.argv) >= 4 and sys.argv[1] == "--screen-capture":
        screen_capture_child(Path(sys.argv[2]), datetime.fromisoformat(sys.argv[3]))
        raise SystemExit(0)
    if not _single_instance():
        log.info("teamsrec is already running, exiting")
        raise SystemExit(0)
    log.info("teamsrec %s started (pid %d, %s)", APP_VERSION, os.getpid(), sys.executable)
    try:
        recover_orphans(OUT_DIR)
    except Exception:
        log.exception("orphan recovery")
    App().run()
