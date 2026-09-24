r"""Smoke tests for the prototype — no tray, no Teams, no real meeting.

The prototype has no pytest setup (and pulls in pyaudiowpatch, pystray and pywin32), so this is one plain file
that imports teamsrec against a throw-away config and exercises the parts that have gone wrong on real
meetings: the join-screen rule, the audio watchdog, a start with no device, and the on-site flows.

    .venv\Scripts\python.exe smoke_test.py            # everything but the hardware test
    .venv\Scripts\python.exe smoke_test.py --hardware # also records 2 s from the configured microphone
"""
import json
import os
import shutil
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import wave
from datetime import datetime, timedelta
from pathlib import Path

TMP = Path(tempfile.mkdtemp(prefix="teamsrec-smoke-"))
CONFIG = TMP / "teamsrec.toml"
CONFIG.write_text(f'''[user]
name = "Jan Novák"           # a comment that must survive being rewritten

[calendar]
outlook = false

[capture]
onsite_mic = "Pole mikrofonu"
prompt_default = "record"    # keep on timeout

[recordings]
out_dir = "{TMP.as_posix()}"

[transcribe]
model = "large-v3"
batch_size = 16
''', encoding="utf-8")
os.environ["TEAMSREC_CONFIG"] = str(CONFIG)
HARDWARE = "--hardware" in sys.argv   # teamsrec parses sys.argv on import, so read the flag first
sys.argv = [sys.argv[0]]
sys.path.insert(0, str(Path(__file__).parent))

import teamsrec as t  # noqa: E402  (the config path must be set first)


class FakeIcon:
    def __init__(self):
        self.notes = []

    def notify(self, msg, title=None):
        self.notes.append(msg)

    def update_menu(self):
        pass


class FakeRec:
    """Enough of a Recorder for the watchdog and the tray status."""
    mic_only = False
    with_mic = True

    def __init__(self):
        self.started = datetime.now()
        self.reopens = self.reopen_calls = 0
        self.last_data = self.last_loud = self.last_mic_loud = self.last_mic_data = time.time()
        self.heard_sys = self.heard_mic = False
        self.stem = TMP / "fake" / "2026-09-25_0900_fake"

    def reopen(self):
        self.reopen_calls += 1
        self.reopens += 1
        return True


def app_with(rec=None) -> "t.App":
    a = t.App.__new__(t.App)
    a.icon = FakeIcon()
    a.lock = threading.Lock()
    a.rec = rec
    a.files, a.screen, a.title, a.titles_seen = [], None, "test", set()
    a.manual = a.playback = a.onsite = False
    a.calendar = None
    a.prejoin_since = a.audio_warned = a.continues = None
    a.audio_warned_at = a.offer_checked_at = a.next_reopen_at = 0.0
    a.reopen_at, a.reopen_tries = None, 0
    a.offered = set()
    a.declined, a.call_missing_since, a.no_audio_warned = False, None, False
    a.settings = None
    return a


# ---------------------------------------------------------------- tests
def test_prejoin_detection():
    """The join dialog holds the microphone but is not a call (2026-09-21: 6.5 min of the dialog recorded)."""
    join = "Připojení ke schůzce | Archi week plan | Microsoft Teams"
    assert t.prejoin_only([join, "Calendar | Microsoft Teams", "Chat | Jan Novák | Microsoft Teams"])
    assert not t.prejoin_only([join, "Kompaktní zobrazení schůzky | Archi week plan | Microsoft Teams"])
    assert not t.prejoin_only([join, "Ovládací panel sdílení | Microsoft Teams"])
    assert not t.prejoin_only(["Archi standup | Microsoft Teams"])
    assert not t.prejoin_only(["Schůzka s: Jan Novák | Microsoft Teams"])
    assert not t.prejoin_only([])


def test_watchdog_backoff():
    """A reopen is judged 12 s later; a dead device is not hammered every 20 s (that was a notification storm)."""
    app = app_with(FakeRec())
    rec = app.rec
    rec.last_data = time.time() - 25
    app._audio_watchdog(60)
    assert rec.reopen_calls == 1 and app.reopen_at is not None
    app._audio_watchdog(63)
    assert rec.reopen_calls == 1, "no second reopen while the first is being judged"

    app.reopen_at -= 13                      # 13 s later, still nothing
    rec.last_data = time.time() - 30
    app._audio_watchdog(75)
    assert rec.reopen_calls == 1 and app.reopen_tries == 1
    assert app.next_reopen_at > time.time() + 25, "~30 s before the next attempt"
    assert len(app.icon.notes) == 1 and app.audio_warned, app.icon.notes
    for _ in range(5):
        app._audio_watchdog(90)
    assert rec.reopen_calls == 1 and len(app.icon.notes) == 1, "quiet during the backoff"

    app.next_reopen_at = time.time() - 1
    app._audio_watchdog(200)
    assert rec.reopen_calls == 2
    app.reopen_at -= 13
    rec.last_data = rec.last_loud = rec.last_mic_loud = time.time()
    app._audio_watchdog(215)
    assert app.reopen_tries == 0 and app.audio_warned is None
    assert app.icon.notes[-1].startswith("Zvuk se obnovil")

    rec2 = FakeRec()
    app = app_with(rec2)
    for _ in range(60):
        rec2.last_data = time.time() - 30
        app.next_reopen_at = 0.0
        app._audio_watchdog(300)
        if app.reopen_at:
            app.reopen_at -= 13
            app._audio_watchdog(300)
    assert rec2.reopen_calls == t.App.AUDIO_MAX_REOPENS, rec2.reopen_calls


def test_recorder_without_device():
    """A recorder that opened nothing must say so, not return an empty file list."""
    rec = t.Recorder.__new__(t.Recorder)
    rec.mic_name, rec.mic_only = "Pole mikrofonu", True
    rec.streams, rec.tracks, rec.queues, rec.writers = [], {}, {}, []
    rec._default_devices = lambda: {}
    rec.input_names = lambda: ["Mikrofon (Creative BT-W5)"]
    try:
        rec.start()
        raise AssertionError("start() must raise")
    except RuntimeError as e:
        assert "Pole mikrofonu" in str(e) and "BT-W5" in str(e), e


def test_start_failure_leaves_nothing():
    app = app_with()
    original = t.Recorder

    class Boom:
        def __init__(self, *a, **k):
            pass

        def start(self):
            raise RuntimeError("no device (test)")

    t.Recorder = Boom
    try:
        month = TMP / f"{datetime.now():%Y}" / f"{datetime.now():%m}"
        before = set(month.glob("*")) if month.exists() else set()
        app.start("schuzka bez zarizeni", manual=True, onsite=True)
        after = set(month.glob("*")) if month.exists() else set()
        assert app.rec is None and before == after, "no state, no folder"
        assert app.icon.notes[0].startswith("Nahrávání se NESPUSTILO"), app.icon.notes
    finally:
        t.Recorder = original


def test_stop_without_audio_file():
    app = app_with()
    d = TMP / "no_audio_recording"
    d.mkdir()
    app.rec = type("R", (), {"stem": d / d.name, "reopens": 3, "heard_sys": False, "heard_mic": False,
                             "started": datetime.now(), "stop": lambda self: 600.0})()
    app.manual, app.onsite = True, True
    app.stop("tray stop")
    assert not d.exists(), "an empty recording folder is removed"
    assert "NEVZNIKLA" in app.icon.notes[-1], app.icon.notes


def test_device_policy():
    app = app_with()
    inputs, ask = [{"name": "Mikrofon (Creative BT-W5)", "channels": 1, "rate": 48000, "is_default": True}], [True]
    saved = (t.audio_inputs, t.ask_yes_no, t.ONSITE_MIC, t.DEVICE_MISSING)
    t.audio_inputs = lambda: inputs
    t.ask_yes_no = lambda *a, **k: ask[0]
    try:
        t.ONSITE_MIC = "Pole mikrofonu"
        t.DEVICE_MISSING = "fail"
        assert app._onsite_device() is None and "není k dispozici" in app.icon.notes[-1]
        t.DEVICE_MISSING = "fallback"
        assert app._onsite_device() == "Mikrofon (Creative BT-W5)"
        t.DEVICE_MISSING = "ask"
        ask[0] = False
        assert app._onsite_device() is None
        ask[0] = True
        assert app._onsite_device() == "Mikrofon (Creative BT-W5)"
        t.ONSITE_MIC = "BT-W5"                     # configured device is there: used as it is
        assert app._onsite_device() == "BT-W5"
        inputs.clear()                             # nothing at all
        assert app._onsite_device() is None and "žádný aktivní mikrofon" in app.icon.notes[-1]
    finally:
        t.audio_inputs, t.ask_yes_no, t.ONSITE_MIC, t.DEVICE_MISSING = saved


def test_calendar_offer():
    app = app_with()
    started = []
    app._start_onsite = lambda title=None, ask=False: started.append((title, ask))
    now = datetime.now()
    saved = (t.outlook_meeting, t.ONSITE_OFFER)
    try:
        t.ONSITE_OFFER = "calendar"
        t.outlook_meeting = lambda *a, **k: {"subject": "Vivo workshop", "start": now - timedelta(seconds=60),
                                             "end": now + timedelta(hours=1), "teams": False}
        app._calendar_offer()
        assert started == [("Vivo workshop", True)], started
        app.offer_checked_at = 0.0
        app._calendar_offer()
        assert len(started) == 1, "offered once"

        app.offered.clear(), setattr(app, "offer_checked_at", 0.0)
        t.outlook_meeting = lambda *a, **k: {"subject": "Online sync", "start": now,
                                             "end": now + timedelta(hours=1), "teams": True}
        app._calendar_offer()
        assert len(started) == 1, "a Teams meeting is left to the call detection"
        app.offered.clear(), setattr(app, "offer_checked_at", 0.0)
        t.ONSITE_OFFER = "always"
        app._calendar_offer()
        assert started[-1][0] == "Online sync"

        app.offered.clear(), setattr(app, "offer_checked_at", 0.0)
        t.outlook_meeting = lambda *a, **k: {"subject": "Stará", "start": now - timedelta(hours=2),
                                             "end": now + timedelta(hours=1), "teams": False}
        app._calendar_offer()
        assert len(started) == 2, "a meeting that started long ago is not offered"
    finally:
        t.outlook_meeting, t.ONSITE_OFFER = saved


def test_upgrade_to_live():
    app = app_with(type("R", (), {"stem": TMP / "x" / "2026-09-25_0900_onsite"})())
    app.title, app.onsite = "Vivo workshop", True
    calls = []
    app.stop = lambda reason: (calls.append(("stop", reason)), setattr(app, "rec", None))

    def fake_start(title, **kw):
        calls.append(("start", title))
        app.rec, app.title = object(), title

    app.start = fake_start
    saved = (t.outlook_meeting, t.teams_window_titles)
    t.outlook_meeting = lambda *a, **k: None
    t.teams_window_titles = lambda: ["Vivo workshop | Microsoft Teams"]
    try:
        app._upgrade_to_live()
    finally:
        t.outlook_meeting, t.teams_window_titles = saved
    assert calls == [("stop", "upgraded"), ("start", "Vivo workshop")], calls
    assert app.continues == "2026-09-25_0900_onsite"


def test_finalize_uses_the_recordings_own_metadata():
    """The upgrade starts the next recording at once, so the sidecar must not read the app's current state."""
    app = app_with()
    app.title = "UŽ BĚŽÍ DALŠÍ NAHRÁVKA"
    d = TMP / "finalize"
    d.mkdir()
    stem = d / "2026-09-25_0900_prvni-cast"
    wav = Path(f"{stem}_mic.wav")
    with wave.open(str(wav), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(16000)
        w.writeframes(b"\0\0" * 16000)
    rec = type("R", (), {"stem": stem, "started": datetime.now(), "reopens": 0, "heard_sys": False,
                         "heard_mic": True,
                         "tracks": {"mic": {"file": wav.name, "sample_rate": 16000, "channels": 1}}})()
    mix_was, t.MIX_WITH_FFMPEG = t.MIX_WITH_FFMPEG, False
    try:
        info = {"title": "První část", "cal": None, "title_source": "manual", "titles_seen": set(),
                "continues": None, "source": "onsite"}
        app._finalize(rec, [wav], 600.0, "upgraded", (), info)
        meta = json.loads(Path(f"{stem}.json").read_text(encoding="utf-8"))
        assert meta["title"] == "První část" and meta["source"] == "onsite", meta
        assert meta["stop_reason"] == "onsite_upgraded", meta
        app._finalize(rec, [wav], 60.0, "call ended", (), dict(info, title="Druhá část", source="live",
                                                               continues=stem.name))
        meta = json.loads(Path(f"{stem}.json").read_text(encoding="utf-8"))
        assert meta["continues"] == stem.name, meta
    finally:
        t.MIX_WITH_FFMPEG = mix_was


def test_settings_api_and_toml():
    """The page's API writes the shared TOML in place: comments, other sections and formatting survive."""
    from http.server import ThreadingHTTPServer
    t.SettingsHandler.app = None
    srv = ThreadingHTTPServer(("127.0.0.1", 0), t.SettingsHandler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    base = f"http://127.0.0.1:{srv.server_port}"

    def post(path, obj):
        req = urllib.request.Request(base + path, data=json.dumps(obj).encode(),
                                     headers={"Content-Type": "application/json"}, method="POST")
        try:
            with urllib.request.urlopen(req) as r:
                return json.loads(r.read())
        except urllib.error.HTTPError as e:
            return json.loads(e.read())

    try:
        with urllib.request.urlopen(base + "/") as r:
            page = r.read().decode("utf-8")
        assert "teamsrec" in page and "onsite_offer" in page
        with urllib.request.urlopen(base + "/api/settings") as r:
            state = json.loads(r.read())
        assert state["version"] == t.APP_VERSION and state["values"]["onsite_mic"] == "Pole mikrofonu"
        assert isinstance(state["devices"], list) and isinstance(state["unavailable"], list)

        assert "error" in post("/api/settings", {"values": {"onsite_offer": "sometimes"}})
        res = post("/api/settings", {"values": {"onsite_offer": "calendar", "onsite_upgrade": True,
                                                "device_missing": "fail", "user_name": "Petr Svoboda"}})
        assert res.get("ok"), res
        text = CONFIG.read_text(encoding="utf-8")
        assert 'onsite_offer = "calendar"' in text and "onsite_upgrade = true" in text
        assert "# a comment that must survive being rewritten" in text, "comment lost"
        assert "[transcribe]" in text and 'model = "large-v3"' in text, "other sections lost"
        assert t.ONSITE_OFFER == "calendar" and t.USER_NAME == "Petr Svoboda"

        import tomllib
        parsed = tomllib.loads(text)
        assert parsed["capture"]["onsite_offer"] == "calendar" and parsed["transcribe"]["batch_size"] == 16
        t.config_set({"brandnew": {"flag": True, "text": 'a "quoted" value'}})
        parsed = tomllib.loads(CONFIG.read_text(encoding="utf-8"))
        assert parsed["brandnew"] == {"flag": True, "text": 'a "quoted" value'}
    finally:
        srv.shutdown()


def test_microphone_records():
    """Hardware: open the configured microphone and check that samples really arrive."""
    res = t.test_input(t.ONSITE_MIC, seconds=2.0)
    assert not res.get("error"), res["error"]
    assert res["peak"] > 0, f"{res['device']} delivered digital silence"
    print(f"      {res['device']}: peak {res['peak']} (silence below {res['silence_level']})")


def main() -> int:
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_")]
    if not HARDWARE:
        tests = [f for f in tests if f is not test_microphone_records]
    failed = 0
    for fn in tests:
        try:
            fn()
            print(f"  ok   {fn.__name__}")
        except Exception as e:
            failed += 1
            print(f"  FAIL {fn.__name__}: {e!r}")
    print(f"{len(tests) - failed}/{len(tests)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        code = main()
    finally:
        shutil.rmtree(TMP, ignore_errors=True)
    raise SystemExit(code)
