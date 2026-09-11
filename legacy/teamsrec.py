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
from datetime import datetime
from pathlib import Path

warnings.filterwarnings("ignore", category=DeprecationWarning)  # audioop is deprecated but fine on 3.12

import psutil
import pyaudiowpatch as pyaudio
import pystray
import win32api
import win32event
import win32gui
import win32process
import winerror
from PIL import Image, ImageDraw

# ------------------------------------------------------------------ config
APP_NAME = "teamsrec-prototype"
APP_VERSION = "0.2.0"
FORMAT_VERSION = 1  # docs/recording-format.md


def _config_out_dir() -> Path:
    """Shared config with teamsrec-transcribe: %APPDATA%/teamsrec/teamsrec.toml [recordings].out_dir."""
    cfg = Path(os.environ.get("TEAMSREC_CONFIG") or Path(os.environ.get("APPDATA", "")) / "teamsrec" / "teamsrec.toml")
    try:
        data = tomllib.loads(cfg.read_text(encoding="utf-8"))
        return Path(data["recordings"]["out_dir"]).expanduser()
    except (OSError, KeyError, ValueError):
        return Path(r"D:\meetings")


OUT_DIR = _config_out_dir()
SILENCE_STOP_S = 30        # playback mode: stop after this much silence on the system track
SILENCE_LEVEL = 300        # int16 peak below this counts as silence
POLL_S = 3                 # how often to check for a call
PROMPT_TIMEOUT_S = 45      # prompt auto-skips after this
MIN_DURATION_S = 5         # shorter recordings are deleted
MAX_DURATION_S = 4 * 3600  # safety net
CALL_END_GRACE_S = 10      # call must look ended this long before we stop
MIX_WITH_FFMPEG = True


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
                 "připojení ke schůzce", "kompaktní zobrazení schůzky", "kompaktní zobrazení", "hovor", "schůzka"}


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


def teams_window_titles() -> list[str]:
    pids = {p.pid for p in psutil.process_iter(["name"])
            if (p.info["name"] or "").lower() in TEAMS_EXE}
    titles = []

    def cb(hwnd, _):
        if win32gui.IsWindowVisible(hwnd):
            _, pid = win32process.GetWindowThreadProcessId(hwnd)
            if pid in pids:
                t = win32gui.GetWindowText(hwnd)
                if t:
                    titles.append(t)
    win32gui.EnumWindows(cb, None)
    return titles


def guess_meeting_title(titles: list[str]) -> str | None:
    """Meeting windows are titled '<subject> | Microsoft Teams'; skip the main-window nav titles."""
    for t in titles:
        parts = [p.strip() for p in t.split("|")]
        if len(parts) < 2 or parts[-1].lower() != "microsoft teams":
            continue
        head = parts[0]
        if is_generic_title(head):
            continue
        return head
    return None


def slug(s: str, n: int = 60) -> str:
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode()
    s = re.sub(r"[^A-Za-z0-9]+", "-", s).strip("-").lower()
    return s[:n] or "teams-call"


# ---------------------------------------------------------------- recorder
class Recorder:
    def __init__(self, stem: Path, with_mic: bool = True):
        self.stem = stem
        self.with_mic = with_mic
        self.started = datetime.now()
        self.pa = pyaudio.PyAudio()
        self.streams, self.writers = [], []
        self.tracks: dict[str, dict] = {}   # "sys"/"mic" -> {file, sample_rate, channels}
        self.last_loud = time.time()        # last time the system track was not silent
        self.bytes_received = 0             # watchdog: a sleeping Bluetooth device delivers nothing at all

    def _open(self, dev, suffix, name):
        rate = int(dev["defaultSampleRate"])
        ch = max(1, min(2, int(dev["maxInputChannels"])))
        path = Path(f"{self.stem}{suffix}")
        wf = wave.open(str(path), "wb")
        wf.setnchannels(ch); wf.setsampwidth(2); wf.setframerate(rate)
        q: queue.Queue = queue.Queue()
        self.tracks[name] = {"file": path.name, "sample_rate": rate, "channels": ch}
        is_sys = name == "sys"

        def writer():
            while (chunk := q.get()) is not None:
                wf.writeframes(chunk)
            wf.close()

        def cb(data, frames, ti, status):
            q.put(data)
            self.bytes_received += len(data)
            if is_sys and audioop.max(data, 2) > SILENCE_LEVEL:
                self.last_loud = time.time()
            return (None, pyaudio.paContinue)

        s = self.pa.open(format=pyaudio.paInt16, channels=ch, rate=rate, input=True,
                         input_device_index=dev["index"], frames_per_buffer=1024,
                         stream_callback=cb)
        th = threading.Thread(target=writer, daemon=True); th.start()
        self.streams.append(s); self.writers.append((q, th))
        log.info("recording %s  %d Hz x%d  <- %s", path.name, rate, ch, dev["name"])
        return path

    def start(self) -> list[Path]:
        files = [self._open(self.pa.get_default_wasapi_loopback(), "_sys.wav", "sys")]
        if self.with_mic:
            try:
                wasapi = self.pa.get_host_api_info_by_type(pyaudio.paWASAPI)
                mic = self.pa.get_device_info_by_index(wasapi["defaultInputDevice"])
                files.append(self._open(mic, "_mic.wav", "mic"))
            except Exception as e:  # no mic is not fatal
                log.warning("mic not recorded: %s", e)
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
    res = {"ok": False}
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
    def no(): root.destroy()
    tk.Button(row, text="Skip", width=8, command=no).pack(side="right")
    tk.Button(row, text="● Record", width=10, command=yes, bg="#c0392b", fg="white",
              activebackground="#a93226").pack(side="right", padx=(0, 6))
    root.bind("<Return>", lambda e: yes()); root.bind("<Escape>", lambda e: no())
    deadline = time.time() + PROMPT_TIMEOUT_S

    def tick():
        left = int(deadline - time.time())
        if left <= 0:
            root.destroy(); return
        cnt.set(f"auto-skip in {left}s"); root.after(500, tick)
    tick()
    winsound.MessageBeep(winsound.MB_ICONASTERISK)
    root.focus_force(); root.mainloop()
    return res["ok"]


# ---------------------------------------------------------------- app
def icon_image(color):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    ImageDraw.Draw(img).ellipse((8, 8, 56, 56), fill=color)
    return img


IMG_IDLE, IMG_REC = icon_image("#7f8c8d"), icon_image("#e74c3c")


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
        self.lock = threading.Lock()
        self.quit = threading.Event()
        self.icon = pystray.Icon("teamsrec", IMG_IDLE, "teamsrec: idle", menu=pystray.Menu(
            pystray.MenuItem(lambda _: self.status(), None, enabled=False),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Record now (manual)", lambda: self.start("manual", manual=True),
                             enabled=lambda _: self.rec is None),
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
        if not self.rec:
            return "Idle — waiting for a Teams call"
        m = int((datetime.now() - self.rec.started).total_seconds() // 60)
        return f"● REC {m} min — {self.title}"

    def _refresh(self):
        self.icon.icon = IMG_REC if self.rec else IMG_IDLE
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

    def start(self, title, manual=False, playback=False):
        with self.lock:
            if self.rec:
                return
            now = datetime.now()
            name = f"{now:%Y-%m-%d}_{now:%H%M}_{slug(title)}"
            stem = OUT_DIR / f"{now:%Y}" / f"{now:%m}" / name / name
            stem.parent.mkdir(parents=True, exist_ok=True)
            try:
                rec = Recorder(stem, with_mic=not playback)
                self.files = rec.start()
            except Exception as e:
                log.exception("start failed")
                self.icon.notify(f"Recording failed: {e}", "teamsrec")
                return
            self.rec, self.title, self.manual, self.playback = rec, title, manual, playback
            self.titles_seen, self.call_missing_since, self.no_audio_warned = set(), None, False
        self._refresh()
        log.info("START '%s' (%s) -> %s", title, "playback" if playback else "manual" if manual else "live", stem)

    def stop(self, reason):
        with self.lock:
            if not self.rec:
                return
            rec, self.rec = self.rec, None
            dur = rec.stop()
            files = [p for p in self.files if p.exists()]
        self._refresh()
        log.info("STOP (%s) after %.0fs", reason, dur)
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
        threading.Thread(target=self._finalize, args=(rec, files, dur, reason), daemon=True).start()

    STOP_REASONS = {"call ended": "call_ended", "max duration": "max_duration", "tray stop": "user_stop",
                    "silence": "silence", "quit": "app_quit"}

    def _finalize(self, rec, files, dur, reason):
        stem = rec.stem
        mix = None
        ff = _ffmpeg() if MIX_WITH_FFMPEG else None
        if ff:
            mix = Path(f"{stem}_mix.wav")
            cmd = [ff, "-y", "-loglevel", "error"]
            for p in files:
                cmd += ["-i", str(p)]
            cmd += ["-filter_complex", f"amix=inputs={len(files)}:duration=longest:normalize=0",
                    "-ac", "1", "-ar", "16000", str(mix)]
            r = subprocess.run(cmd, capture_output=True, text=True)
            if r.returncode:
                log.error("ffmpeg: %s", r.stderr); mix = None
        source = "playback" if self.playback else "manual" if self.manual else "live"
        title = self.title
        if is_generic_title(title):  # the window got its real subject only later in the call
            better = guess_meeting_title(sorted(self.titles_seen))
            if better:
                log.info("title '%s' replaced by '%s' seen during the call", title, better)
                title = better
        meta = {
            "format": FORMAT_VERSION, "app": APP_NAME, "app_version": APP_VERSION,
            "title": title, "slug": slug(title), "source": source,
            "start": rec.started.isoformat(timespec="seconds"),
            "end": datetime.now().isoformat(timespec="seconds"), "duration_s": round(dur),
            "stop_reason": self.STOP_REASONS.get(reason, "user_stop"),
            "tracks": {k: v for k, v in rec.tracks.items() if (stem.parent / v["file"]).exists()},
            "teams_windows_seen": sorted(self.titles_seen),
        }
        if mix:
            meta["mix"] = {"file": mix.name, "sample_rate": 16000, "channels": 1}
        jpath = Path(f"{stem}.json")  # written last: marks the recording as complete
        jpath.write_text(json.dumps(meta, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        self.icon.notify(f"Saved {stem.name} ({round(dur / 60)} min)", "teamsrec")
        if POST_HOOK:
            fmt = dict(stem=stem.name, dir=str(stem.parent), json=str(jpath),
                       sys=str(files[0]) if files else "", mic=str(files[1]) if len(files) > 1 else "",
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
                    if elapsed > MAX_DURATION_S:
                        self.stop("max duration")
                    elif self.playback:
                        if elapsed > 15 and time.time() - self.rec.last_loud >= SILENCE_STOP_S:
                            self.stop("silence")
                    elif not self.manual:
                        if in_call:
                            self.call_missing_since = None
                        else:
                            self.call_missing_since = self.call_missing_since or time.time()
                            if time.time() - self.call_missing_since >= CALL_END_GRACE_S:
                                self.stop("call ended")
                    self._refresh()
                else:
                    if in_call and not self.declined:
                        time.sleep(2)  # let the meeting window get its title
                        title = guess_meeting_title(teams_window_titles()) or "teams-call"
                        if prompt(title):
                            self.start(title)
                        else:
                            self.declined = True
                            log.info("skipped '%s'", title)
                    elif not in_call:
                        self.declined = False
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
    if not _single_instance():
        log.info("teamsrec is already running, exiting")
        raise SystemExit(0)
    App().run()
