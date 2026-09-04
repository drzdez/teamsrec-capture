"""
teamsrec.py — prompt-to-record for Microsoft Teams calls (Windows, new Teams).

What it does
  * Sits in the tray. Detects a Teams call by watching whether ms-teams is using
    the microphone (Windows CapabilityAccessManager registry — robust, no window hacks).
  * Pops a small always-on-top prompt: "Record <meeting title>?"  Auto-skips after a timeout.
  * Records system audio (WASAPI loopback = the other people) and your mic into two WAVs,
    stops automatically when the call ends (or on max duration / tray "Stop").
  * Names files  <OUT_DIR>\<YYYY>\<MM>\<YYYY-MM-DD>_<HHMM>_<slug>_sys.wav / _mic.wav
    plus a _mix.wav (mono 16 kHz, WhisperX-ready) if ffmpeg is on PATH, plus a .json sidecar.
  * Deletes recordings shorter than MIN_DURATION_S, and everything on "Abort & delete".
  * Optional POST_HOOK shell command per finished recording (e.g. ship to the homelab).

Install
  pip install pyaudiowpatch pystray pillow pywin32 psutil
  (tkinter ships with python.org Python; ffmpeg optional but recommended)
Run at login
  Win+R -> shell:startup -> shortcut to:  pythonw.exe "C:\path\teamsrec.py"

Tell the other participants you're recording. Teams shows them nothing for this.
"""

import json
import logging
import queue
import re
import shutil
import subprocess
import threading
import time
import unicodedata
import wave
import winreg
import winsound
import tkinter as tk
from datetime import datetime
from pathlib import Path

import psutil
import pyaudiowpatch as pyaudio
import pystray
import win32gui
import win32process
from PIL import Image, ImageDraw

# ------------------------------------------------------------------ config
OUT_DIR = Path(r"D:\meetings")
POLL_S = 3                 # how often to check for a call
PROMPT_TIMEOUT_S = 45      # prompt auto-skips after this
MIN_DURATION_S = 120       # shorter recordings are deleted
MAX_DURATION_S = 4 * 3600  # safety net
CALL_END_GRACE_S = 10      # call must look ended this long before we stop
MIX_WITH_FFMPEG = True
# Shell command run after each kept recording. Placeholders: {stem} {sys} {mic} {mix} {json} {dir}
# POST_HOOK = r'scp "{mix}" homelab:/data/meetings/ && ssh homelab "~/bin/transcribe.sh {stem}_mix.wav"'
POST_HOOK = None
# Nav sections of the main Teams window, so they are not mistaken for a meeting title
TEAMS_NAV = {"activity", "chat", "teams", "calendar", "calls", "files", "apps", "copilot",
             "onedrive", "meet", "viva", "planner", "microsoft teams"}
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
        if head.lower() in TEAMS_NAV or head.lower().startswith("meeting compact"):
            continue
        return head
    return None


def slug(s: str, n: int = 60) -> str:
    s = unicodedata.normalize("NFKD", s).encode("ascii", "ignore").decode()
    s = re.sub(r"[^A-Za-z0-9]+", "-", s).strip("-").lower()
    return s[:n] or "teams-call"


# ---------------------------------------------------------------- recorder
class Recorder:
    def __init__(self, stem: Path):
        self.stem = stem
        self.started = datetime.now()
        self.pa = pyaudio.PyAudio()
        self.streams, self.writers = [], []

    def _open(self, dev, suffix):
        rate = int(dev["defaultSampleRate"])
        ch = max(1, min(2, int(dev["maxInputChannels"])))
        path = Path(f"{self.stem}{suffix}")
        wf = wave.open(str(path), "wb")
        wf.setnchannels(ch); wf.setsampwidth(2); wf.setframerate(rate)
        q: queue.Queue = queue.Queue()

        def writer():
            while (chunk := q.get()) is not None:
                wf.writeframes(chunk)
            wf.close()

        def cb(data, frames, ti, status):
            q.put(data)
            return (None, pyaudio.paContinue)

        s = self.pa.open(format=pyaudio.paInt16, channels=ch, rate=rate, input=True,
                         input_device_index=dev["index"], frames_per_buffer=1024,
                         stream_callback=cb)
        th = threading.Thread(target=writer, daemon=True); th.start()
        self.streams.append(s); self.writers.append((q, th))
        log.info("recording %s  %d Hz x%d  <- %s", path.name, rate, ch, dev["name"])
        return path

    def start(self) -> list[Path]:
        files = [self._open(self.pa.get_default_wasapi_loopback(), "_sys.wav")]
        try:
            wasapi = self.pa.get_host_api_info_by_type(pyaudio.paWASAPI)
            mic = self.pa.get_device_info_by_index(wasapi["defaultInputDevice"])
            files.append(self._open(mic, "_mic.wav"))
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
        self.declined = False
        self.call_missing_since = None
        self.lock = threading.Lock()
        self.quit = threading.Event()
        self.icon = pystray.Icon("teamsrec", IMG_IDLE, "teamsrec: idle", menu=pystray.Menu(
            pystray.MenuItem(lambda _: self.status(), None, enabled=False),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Record now (manual)", lambda: self.start("manual", manual=True),
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

    def start(self, title, manual=False):
        with self.lock:
            if self.rec:
                return
            now = datetime.now()
            stem = OUT_DIR / f"{now:%Y}" / f"{now:%m}" / f"{now:%Y-%m-%d}_{now:%H%M}_{slug(title)}"
            stem.parent.mkdir(parents=True, exist_ok=True)
            try:
                rec = Recorder(stem)
                self.files = rec.start()
            except Exception as e:
                log.exception("start failed")
                self.icon.notify(f"Recording failed: {e}", "teamsrec")
                return
            self.rec, self.title, self.manual = rec, title, manual
            self.titles_seen, self.call_missing_since = set(), None
        self._refresh()
        log.info("START '%s' -> %s", title, stem)

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
            log.info("deleted (%s)", "aborted" if reason == "aborted" else "too short")
            self.icon.notify("Recording discarded", "teamsrec")
            return
        threading.Thread(target=self._finalize, args=(rec, files, dur, reason), daemon=True).start()

    def _finalize(self, rec, files, dur, reason):
        stem = rec.stem
        mix = None
        if MIX_WITH_FFMPEG and shutil.which("ffmpeg"):
            mix = Path(f"{stem}_mix.wav")
            cmd = ["ffmpeg", "-y", "-loglevel", "error"]
            for p in files:
                cmd += ["-i", str(p)]
            cmd += ["-filter_complex", f"amix=inputs={len(files)}:duration=longest:normalize=0",
                    "-ac", "1", "-ar", "16000", str(mix)]
            r = subprocess.run(cmd, capture_output=True, text=True)
            if r.returncode:
                log.error("ffmpeg: %s", r.stderr); mix = None
        meta = {
            "title": self.title, "start": rec.started.isoformat(timespec="seconds"),
            "end": datetime.now().isoformat(timespec="seconds"), "duration_s": round(dur),
            "stop_reason": reason, "files": [p.name for p in files],
            "mix": mix.name if mix else None, "teams_windows_seen": sorted(self.titles_seen),
        }
        jpath = Path(f"{stem}.json")
        jpath.write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")
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
                    if elapsed > MAX_DURATION_S:
                        self.stop("max duration")
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


if __name__ == "__main__":
    App().run()
