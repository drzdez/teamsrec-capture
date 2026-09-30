using TeamsRec.Capture.Audio;
using TeamsRec.Capture.Calendar;
using TeamsRec.Capture.Config;
using TeamsRec.Capture.Contract;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Detection;
using TeamsRec.Capture.Recording;
using TeamsRec.Capture.Screen;

namespace TeamsRec.Capture.App;

/// <summary>The OS-free decisions of the monitor loop (Python App), so they can be tested without a tray,
/// devices, Outlook or Teams.</summary>
internal static class AppLogic
{
    public const int PollS = 3;                 // how often to check for a call
    public const int PromptTimeoutS = 45;       // the "discard?" box closes by itself after this
    public const int MinDurationS = 5;          // shorter recordings are deleted
    public const int MaxDurationS = 4 * 3600;   // safety net
    public const int CallEndGraceS = 10;        // call must look ended this long before we stop
    public const int SilenceStopS = 30;         // playback mode: stop after this much silence on the system track
    public const int PlaybackMinS = 15;         // playback: silence counts only after the player had time to start
    public const int NoAudioWarnS = 10;         // no byte from any device this long after the start = warn once
    public const int OfferWindowS = 240;        // a calendar meeting is offered from its start until this long after it
    public const int OfferCheckS = 30;          // Outlook is asked at most this often (COM calls are slow)

    // tray reason -> contract stop_reason (docs/recording-format.md); anything else counts as the user's stop
    private static readonly Dictionary<string, string> StopReasons = new()
    {
        ["call ended"] = "call_ended", ["max duration"] = "max_duration", ["tray stop"] = "user_stop",
        ["silence"] = "silence", ["quit"] = "app_quit", ["upgraded"] = "onsite_upgraded",
    };

    public static string StopReasonCode(string reason) =>
        StopReasons.TryGetValue(reason, out var code) ? code : "user_stop";

    /// <summary>Contract `source` of a recording.</summary>
    public static string SourceOf(bool onsite, bool playback, bool manual) =>
        onsite ? "onsite" : playback ? "playback" : manual ? "manual" : "live";

    /// <summary>The tray tooltip and the menu's status line (App.status).</summary>
    public static string StatusText(bool recording, double elapsedS, string title, string? audioWarned, bool prejoin)
    {
        int m = (int)Math.Floor(Math.Max(0, elapsedS) / 60);
        if (recording && !string.IsNullOrEmpty(audioWarned))
            return $"⚠ {m} min — BEZ ZVUKU: {audioWarned}";
        if (!recording)
            return prejoin ? "Teams join screen — recording starts when you join" : "Idle — waiting for a call";
        return $"● REC {m} min — {title}";
    }

    /// <summary>NotifyIcon.Text is limited to 127 characters (it throws above that).</summary>
    public static string Tooltip(string status) => status.Length <= 127 ? status : status[..126] + "…";

    /// <summary>Key of a calendar meeting, so each one is offered once.</summary>
    public static string OfferKey(CalendarItem m) => $"{m.Start:yyyy-MM-ddTHH:mm}|{m.Subject}";

    /// <summary>_calendar_offer without the Outlook call: should this meeting be recorded on site now?
    /// Records the meeting in `offered` when it is decided for good (offered, or left to the Teams detection).
    /// `sinceS` = how long ago the meeting started.</summary>
    public static bool ShouldOffer(CalendarItem? m, DateTime now, string onsiteOffer, ISet<string> offered,
                                   out double sinceS)
    {
        sinceS = 0;
        if (m is null || string.IsNullOrEmpty(m.Subject)) return false;
        var key = OfferKey(m);
        if (offered.Contains(key)) return false;
        if (onsiteOffer == "calendar" && m.Teams)
        {
            offered.Add(key);  // an online meeting: the Teams detection records it when the call starts
            return false;
        }
        sinceS = (now - m.Start).TotalSeconds;
        if (sinceS < 0 || sinceS > OfferWindowS) return false;  // not started yet / started long ago
        offered.Add(key);
        return true;
    }

    public enum DeviceOutcome { Use, NoDevices, Fail, Ask }

    /// <summary>The device_missing policy (_onsite_device) without the dialog: Use = record from Device,
    /// Ask = ask whether to record from Device (the replacement) instead, Fail / NoDevices = do not record.
    /// Device is a name fragment; "" = the Windows default input.</summary>
    public static (DeviceOutcome Outcome, string? Device) ChooseOnsiteDevice(string onsiteMic, string deviceMissing,
                                                                            IReadOnlyList<InputDevice> inputs)
    {
        if (inputs.Count == 0) return (DeviceOutcome.NoDevices, null);
        if (string.IsNullOrEmpty(onsiteMic) ||
            inputs.Any(d => d.Name.Contains(onsiteMic, StringComparison.OrdinalIgnoreCase)))
            return (DeviceOutcome.Use, onsiteMic);
        var alt = (inputs.FirstOrDefault(d => d.IsDefault) ?? inputs[0]).Name;
        return deviceMissing switch
        {
            "fallback" => (DeviceOutcome.Use, alt),
            "fail" => (DeviceOutcome.Fail, null),
            _ => (DeviceOutcome.Ask, alt),  // "ask" and anything unknown: the user decides
        };
    }

    /// <summary>Title and its contract title_source when a recording starts: a manual title is the user's,
    /// otherwise the window's (or generic); a calendar subject wins for calls and for generic manual titles.</summary>
    public static (string Title, string Source) ResolveTitle(string title, bool manual, CalendarItem? cal)
    {
        var source = manual ? "manual" : CallDetector.IsGenericTitle(title) ? "generic" : "window";
        if (cal is not null && !string.IsNullOrEmpty(cal.Subject) && (CallDetector.IsGenericTitle(title) || !manual))
            return (cal.Subject, "calendar");
        return (title, source);
    }

    /// <summary>Title of a call in another app: the calendar subject, the meeting's window title, or "<App> call".</summary>
    public static string OtherCallTitle(CallApp oc, CalendarItem? cal) =>
        !string.IsNullOrEmpty(cal?.Subject) ? cal!.Subject
        : !string.IsNullOrEmpty(oc.Title) ? oc.Title
        : $"{oc.App} call";

    /// <summary>Call-end grace: the call must look ended CallEndGraceS before the recording stops (Teams drops
    /// the microphone for a moment when the user switches devices).</summary>
    public static bool CallEnded(bool inCall, ref double? missingSince, double now)
    {
        if (inCall)
        {
            missingSince = null;
            return false;
        }
        missingSince ??= now;
        return now - missingSince.Value >= CallEndGraceS;
    }

    /// <summary>Playback ends after SilenceStopS of silence on the system track, once the player had time to start.</summary>
    public static bool PlaybackSilent(double elapsedS, double now, double lastLoud) =>
        elapsedS > PlaybackMinS && now - lastLoud >= SilenceStopS;

    /// <summary>What to do with a stopped recording before the finalizer: "delete" (aborted / too short) or "keep".</summary>
    public static bool Discard(string reason, double durationS) => reason == "aborted" || durationS < MinDurationS;

    /// <summary>How the tray opens the review page: (exe, arguments). tray_open = web -> "--browser" (the page in
    /// the default browser), anything else -> the desktop window; settings -> "--settings" (its Nastavení, the one
    /// settings page of both apps). The exe is review_app, or the usual install place under localAppData.</summary>
    public static (string Exe, string Args) ReviewLaunch(string trayOpen, string reviewApp, string localAppData,
                                                          bool settings = false, string? stem = null) =>
        (reviewApp.Length > 0 ? reviewApp : Path.Combine(localAppData, "Programs", "teamsrec-review", "teamsrec-review.exe"),
         string.Join(" ", new[] { trayOpen == "web" ? "--browser" : "", settings ? "--settings" : "",
                                  IsStem(stem) ? $"--open {stem}" : "" }.Where(a => a.Length > 0)));

    /// <summary>%TEMP%\teamsrec-capture.json: what the capture app is doing, for the review page (red dot while a
    /// recording runs). The pid lets a reader tell a crashed app from a running one.</summary>
    public static string CaptureStatusPath => Path.Combine(Path.GetTempPath(), "teamsrec-capture.json");

    public static string CaptureStatusJson(bool running, bool recording, string? title, string? stem, string? source,
                                           DateTime? started, DateTime now)
    {
        var on = running && recording;  // a quitting app records nothing, whatever it was doing
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["app"] = Versions.AppName, ["version"] = Versions.AppVersion, ["pid"] = Environment.ProcessId,
            ["running"] = running, ["recording"] = on,
            ["title"] = on ? title : null, ["stem"] = on ? stem : null, ["source"] = on ? source : null,
            ["started"] = on ? started?.ToString("s") : null, ["updated"] = now.ToString("s"),
        });
    }

    /// <summary>A recording stem as the capture app makes them (date_time_slug): safe on a command line.</summary>
    private static bool IsStem(string? s) =>
        !string.IsNullOrEmpty(s) && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>Teams window video (name tiles): a Teams call or a played-back Teams recording; not on site,
    /// not a call in another app (as the prototype).</summary>
    public static bool RecordsWindows(bool onsite, CallApp? callApp) => !onsite && callApp is null;

    /// <summary>Microphone of a new recording: on-site = the chosen / configured room mic, playback = none,
    /// a call = the one the call app records from (whatever the Windows default input is), else the default.</summary>
    public static string MicFor(bool onsite, bool playback, string? mic, string onsiteMic, Func<string?> callInputDevice) =>
        onsite ? (mic ?? onsiteMic) : playback ? "" : (callInputDevice() ?? "");
}

/// <summary>The recorder's state machine (Python App minus the tray): the 3 s monitor loop on a background
/// thread, start/stop, on-site, playback, other apps, calendar offer and the handover to the finalizer.
/// Every OS lookup goes through the modules; the pure decisions live in <see cref="AppLogic"/>.</summary>
public sealed class MonitorLoop : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly IClock _clock;
    private readonly INotifier _notifier;
    private readonly Watchdog _watchdog;
    private readonly Finalizer _finalizer;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _quit = new(false);
    private readonly List<Task> _finalizing = [];
    private Thread? _thread;

    // -- state of the running recording (null = idle); read by the tray under _lock or as a snapshot
    private Recorder? _rec;
    private string _stemPath = "";
    private List<string> _files = [];
    private ScreenCaptureProcess? _screen;
    private string _title = "";
    private HashSet<string> _titlesSeen = [];
    private bool _manual, _playback, _onsite, _noAudioWarned;
    private CalendarItem? _calendar;
    private string _titleSource = "generic";
    private string? _continues;           // stem of the on-site recording this live one took over from
    private CallApp? _callApp;            // null = Teams (or no call app: manual, on-site, playback)
    private double? _callMissingSince;

    // -- state between recordings
    private volatile bool _declined;      // discarded on purpose / start failed: not again until the call is over
    private double? _prejoinSince;        // Teams sits on the join screen: the call has not started yet
    private readonly HashSet<string> _offered = [];  // calendar meetings already offered, so they are offered once
    private double _offerCheckedAt;

    /// <summary>Raised whenever the tray should redraw (icon, tooltip). May come from any thread.</summary>
    public event Action? Changed;

    public MonitorLoop(AppConfig cfg, IClock clock, INotifier notifier)
    {
        _cfg = cfg;
        _clock = clock;
        _notifier = notifier;
        _watchdog = new Watchdog(clock, notifier);
        _watchdog.Changed += Refresh;  // the icon turns yellow / red again right away
        _finalizer = new Finalizer((at, title) => Outlook.Meeting(_cfg.UseOutlook, at, title), notifier, clock);
    }

    public bool Recording { get { lock (_lock) return _rec is not null; } }

    public string OutDir => _cfg.OutDir;

    public string? AudioWarned => _rec is null ? null : _watchdog.Warned;

    public string Status()
    {
        lock (_lock)
        {
            double elapsed = _rec is null ? 0 : (_clock.Now - _rec.Started).TotalSeconds;
            return AppLogic.StatusText(_rec is not null, elapsed, _title, _rec is null ? null : _watchdog.Warned,
                                       _prejoinSince is not null);
        }
    }

    private void Refresh()
    {
        WriteCaptureStatus();
        Changed?.Invoke();
    }

    /// <summary>Tell the review page what runs (AppLogic.CaptureStatusPath); never fails the caller.</summary>
    private void WriteCaptureStatus(bool running = true)
    {
        try
        {
            string json;
            lock (_lock)
            {
                var rec = _rec;
                json = AppLogic.CaptureStatusJson(running, rec is not null, _title, rec is null ? null : Path.GetFileName(_stemPath),
                                                  rec is null ? null : AppLogic.SourceOf(_onsite, _playback, _manual),
                                                  rec?.Started, _clock.Now);
            }
            var path = AppLogic.CaptureStatusPath;
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"capture status not written: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ loop
    public void Run()
    {
        WriteCaptureStatus();  // running, not recording
        _thread = new Thread(Loop) { IsBackground = true, Name = "teamsrec-monitor" };
        // STA: Outlook COM and window enumeration behave best on an STA thread, like the Python thread with pywin32
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        while (!_quit.IsSet)
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                FileLog.Exception("loop", e);
            }
            _quit.Wait(TimeSpan.FromSeconds(AppLogic.PollS));
        }
    }

    /// <summary>One pass of App.loop.</summary>
    internal void Tick()
    {
        bool inCall = MicUsers.TeamsInUse();
        CallApp? app;
        Recorder? rec;
        lock (_lock)
        {
            rec = _rec;
            app = rec is not null ? _callApp : null;
        }
        if (app is not null)  // recording a call in another app: it lasts as long as that app holds the microphone
            inCall = MicUsers.Current().Contains(app.Id);

        if (rec is not null)
            TickRecording(rec, inCall);
        else
            TickIdle(inCall);
    }

    private void TickRecording(Recorder rec, bool inCall)
    {
        var teamsTitles = TeamsTitles();
        lock (_lock)
        {
            if (!ReferenceEquals(rec, _rec)) return;
            foreach (var t in teamsTitles) _titlesSeen.Add(t);
        }
        double elapsed = (_clock.Now - rec.Started).TotalSeconds;
        if (elapsed > AppLogic.NoAudioWarnS && rec.BytesReceived == 0 && !_noAudioWarned)
        {
            _noAudioWarned = true;
            Log.Warn("no audio data from the default devices after 10 s (headset off? wrong default device?)");
            _notifier.Notify("No audio is arriving from the default devices. Headset off?");
        }
        _watchdog.Tick(rec, elapsed, _playback);
        // during a call, follow a headset switch; on-site and playback record what they were told to
        _watchdog.FollowCallMic(rec, () => Devices.CallInputDevice(), applies: !_playback && !_onsite);

        if (elapsed > AppLogic.MaxDurationS)
            Stop("max duration");
        else if (_playback)
        {
            if (AppLogic.PlaybackSilent(elapsed, _clock.Seconds, rec.LastLoud))
                Stop("silence");
        }
        else if (_onsite)
        {
            if (_cfg.OnsiteUpgrade && inCall)
                UpgradeToLive();
            // otherwise it ends via the tray (Stop & keep) or the safety cap
        }
        else if (!_manual)
        {
            if (AppLogic.CallEnded(inCall, ref _callMissingSince, _clock.Seconds))
                Stop("call ended");
        }
        Refresh();
    }

    private void TickIdle(bool inCall)
    {
        if (inCall && !_declined && CallDetector.PrejoinOnly(TeamsTitles()))
        {
            if (_prejoinSince is null)  // the mic is held by the join dialog's device preview
            {
                _prejoinSince = _clock.Seconds;
                Log.Info("Teams is on the join screen, waiting for the call to start");
                Refresh();
            }
        }
        else if (inCall && !_declined)
        {
            _prejoinSince = null;
            var title = CallDetector.GuessMeetingTitle(TeamsTitles()) ?? "teams-call";
            var cal = Outlook.Meeting(_cfg.UseOutlook, null, title);
            if (!string.IsNullOrEmpty(cal?.Subject)) title = cal!.Subject;
            var rec = StartRecording(title);  // record first; nothing to click, the tray menu can still discard it
            if (rec is not null)
            {
                if (_cfg.PromptDefault == "ask")
                    AskDiscardInBackground(rec, title);
                else
                    _notifier.Notify($"Nahrávám: {title}. Zahodit lze z menu ikony v liště (Abort & delete).");
            }
            else
                _declined = true;  // start failed (logged); do not retry until the call ends
        }
        else if (!inCall && _cfg.OtherApps == "record" && !_declined && OtherCall() is { } oc)
        {
            var cal = Outlook.Meeting(_cfg.UseOutlook, null, string.IsNullOrEmpty(oc.Title) ? null : oc.Title);
            var title = AppLogic.OtherCallTitle(oc, cal);
            Log.Info($"call in {oc.App} ({oc.Id}) holds the microphone");
            if (StartRecording(title, callApp: oc) is not null)
                _notifier.Notify($"Nahrávám hovor v {oc.App}: {title}. Zahodit lze z menu ikony (Abort & delete).");
            else
                _declined = true;
        }
        else if (!inCall)
        {
            if (_cfg.OnsiteOffer != "never" && _cfg.UseOutlook)
                CalendarOffer();
            if (!(_cfg.OtherApps == "record" && OtherCall() is not null))
                _declined = false;  // the declined call is over
            if (_prejoinSince is not null)  // the join dialog was closed without joining
            {
                Log.Info($"Teams left the join screen without a call after {_clock.Seconds - _prejoinSince.Value:F0} s");
                _prejoinSince = null;
                Refresh();
            }
        }
    }

    private static CallApp? OtherCall() => CallDetector.OtherCall();

    /// <summary>Titles of the visible Teams windows.</summary>
    private static List<string> TeamsTitles() => WindowTitles.Teams().Select(w => w.Title).ToList();

    // ------------------------------------------------------------------ calendar offer / on-site
    /// <summary>A meeting from the calendar started and Teams is not in a call: record the room. `onsite_offer`
    /// decides whether that happens for every meeting or only for those without a Teams link.</summary>
    private void CalendarOffer()
    {
        double now = _clock.Seconds;
        if (now - _offerCheckedAt < AppLogic.OfferCheckS) return;
        _offerCheckedAt = now;
        var m = Outlook.Meeting(_cfg.UseOutlook, null, null);
        if (!AppLogic.ShouldOffer(m, _clock.Now, _cfg.OnsiteOffer, _offered, out var since)) return;
        Log.Info($"calendar: '{m!.Subject}' started {since:F0} s ago, recording it on site");
        StartOnsiteNow(m.Subject, ask: true);
    }

    /// <summary>Which microphone to record the room with, following `device_missing`. Returns a name fragment
    /// ("" = the Windows default input), or null when the recording must not start.</summary>
    internal string? OnsiteDevice() => OnsiteDevice(_cfg.OnsiteMic, _cfg.DeviceMissing, Devices.Inputs(),
                                                    Devices.Unavailable, _notifier,
                                                    text => Dialogs.YesNo(text, 60, false));

    internal static string? OnsiteDevice(string onsiteMic, string deviceMissing, IReadOnlyList<InputDevice> inputs,
                                         Func<IReadOnlyList<UnavailableInput>> unavailable, INotifier notifier,
                                         Func<string, bool> askYesNo)
    {
        var (outcome, device) = AppLogic.ChooseOnsiteDevice(onsiteMic, deviceMissing, inputs);
        switch (outcome)
        {
            case AppLogic.DeviceOutcome.NoDevices:
                var known = string.Join(", ", unavailable().Select(d => $"{d.Name} ({d.Reason})"));
                if (known.Length == 0) known = "žádné";
                Log.Error($"on-site: no active input device (known: {known})");
                notifier.Notify($"Nahrávání na místě nelze spustit: není žádný aktivní mikrofon. Windows zná: {known}.");
                notifier.Beep(error: true);
                return null;
            case AppLogic.DeviceOutcome.Use:
                if (device != onsiteMic)
                    Log.Warn($"on-site: '{onsiteMic}' is not available, using '{device}'");
                return device;
            case AppLogic.DeviceOutcome.Fail:
                Log.Error($"on-site: '{onsiteMic}' is not available, not recording");
                notifier.Notify($"Mikrofon „{onsiteMic}“ není k dispozici, nenahrávám. " +
                                $"Dostupné: {string.Join(", ", inputs.Select(d => d.Name))}.");
                return null;
            default:  // Ask
                if (askYesNo($"Mikrofon „{onsiteMic}“ není k dispozici.\n\nNahrávat z „{device}“?\n" +
                             "(Pozor: sluchátka slyší jen vás, ne místnost.)"))
                    return device;
                Log.Info("on-site: declined the replacement device");
                return null;
        }
    }

    /// <summary>On-site meeting: only the room microphone (laptop array or whatever `onsite_mic` names), no
    /// Teams, no loopback, no window capture. Runs in a thread: it may have to ask about the microphone.</summary>
    public void StartOnsite(string? title = null, bool ask = false) =>
        new Thread(() => Guard("on-site start", () => StartOnsiteNow(title, ask))) { IsBackground = true }.Start();

    private void StartOnsiteNow(string? title, bool ask)
    {
        if (Recording) return;
        var mic = OnsiteDevice();
        if (mic is null) return;
        if (title is null)
        {
            var cal = Outlook.Meeting(_cfg.UseOutlook, null, null);
            title = !string.IsNullOrEmpty(cal?.Subject) ? cal!.Subject : "onsite";
        }
        var rec = StartRecording(title, manual: true, onsite: true, mic: mic);
        if (rec is null) return;
        var dev = rec.Tracks.TryGetValue("mic", out var t) ? t.Device : "?";
        Log.Info($"on-site recording from '{dev}'");
        _notifier.Notify($"Nahrávám na místě: {title} (mikrofon {dev}). Ukončete přes Stop & keep.");
        if (ask) AskDiscardInBackground(rec, title);
    }

    /// <summary>Teams took the microphone while we were recording the room: the meeting turned out to be online.
    /// Finish the on-site recording and start a live one (system + microphone); the new sidecar points back to
    /// the first part with `continues`.</summary>
    private void UpgradeToLive()
    {
        string first, title;
        lock (_lock)
        {
            if (_rec is null) return;
            first = Path.GetFileName(_stemPath);
            title = _title;
        }
        Log.Info($"on-site '{title}' turned into a Teams call, switching to a live recording");
        Stop("upgraded");
        var cal = Outlook.Meeting(_cfg.UseOutlook, null, title);
        var better = CallDetector.GuessMeetingTitle(TeamsTitles());
        var rec = StartRecording(better ?? (string.IsNullOrEmpty(cal?.Subject) ? null : cal!.Subject) ?? title);
        if (rec is not null)
        {
            lock (_lock)
            {
                if (ReferenceEquals(rec, _rec)) _continues = first;
            }
            _notifier.Notify($"Schůzka pokračuje v Teams, nahrávám živě: {_title}");
        }
    }

    // ------------------------------------------------------------------ tray actions
    /// <summary>Record what is being played (a stored Teams recording): system track only, stops on silence.</summary>
    public void StartPlayback(string title) =>
        Task.Run(() => Guard("playback start", () => StartRecording(title, manual: true, playback: true)));

    public void StartManual() => Task.Run(() => Guard("manual start", () => StartRecording("manual", manual: true)));

    public void StopAsync(string reason) => Task.Run(() => Guard("stop", () => Stop(reason)));

    /// <summary>Two seconds from the configured microphone, so a room can be checked before the meeting starts.</summary>
    public void TestMicrophone() => Task.Run(() => Guard("microphone test", () =>
    {
        var res = MicTest.Run(_cfg.OnsiteMic);
        if (!string.IsNullOrEmpty(res.Error))
        {
            _notifier.Notify(res.Error!);
            return;
        }
        bool loud = res.Peak >= Recorder.SilenceLevel;
        _notifier.Notify($"{res.Device}: {(loud ? "slyší" : "TICHO")} (špička {res.Peak}, práh {Recorder.SilenceLevel})");
    }));

    private static void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { FileLog.Exception(what, e); }
    }

    private void AskDiscardInBackground(Recorder rec, string title) =>
        new Thread(() => Guard("discard box", () =>
        {
            // only a clear "Ano" discards it
            if (!Dialogs.AskDiscard(title, _cfg.PromptDefault)) return;
            bool same;
            lock (_lock) same = ReferenceEquals(_rec, rec);
            if (!same) return;
            Log.Info($"discarded '{title}' on request");
            _declined = true;
            Stop("aborted");
        })) { IsBackground = true }.Start();

    // ------------------------------------------------------------------ start / stop
    /// <summary>Start a recording; returns it, or null when one runs already or the start failed (the user is
    /// told loudly: a meeting that silently is not recorded is the worst outcome).</summary>
    internal Recorder? StartRecording(string title, bool manual = false, bool playback = false, bool onsite = false,
                                      string? mic = null, CallApp? callApp = null)
    {
        string stemPath;
        Recorder? started;
        lock (_lock)
        {
            if (_rec is not null) return null;
            var now = _clock.Now;
            var stem = Naming.Stem(now, title);
            var dir = Naming.RecordingDir(_cfg.OutDir, now, stem);
            stemPath = Path.Combine(dir, stem);
            Directory.CreateDirectory(dir);
            Recorder? rec = null;
            List<string> files;
            try
            {
                var micName = AppLogic.MicFor(onsite, playback, mic, _cfg.OnsiteMic, () =>
                {
                    var used = Devices.CallInputDevice();
                    Log.Info("microphone: " + (!string.IsNullOrEmpty(used)
                        ? $"the call uses '{used}'"
                        : "no app records from a microphone, using the Windows default input"));
                    return used;
                });
                rec = new Recorder(stemPath, !playback, onsite, micName, _clock);
                files = rec.Start().ToList();
            }
            catch (Exception e)
            {
                FileLog.Exception("start failed", e);
                try { rec?.Dispose(); } catch (Exception) { }
                try { Directory.Delete(dir); }  // the folder we made a moment ago, still empty
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                _notifier.Notify($"Nahrávání se NESPUSTILO: {e.Message}");
                _notifier.Beep(error: true);
                return null;
            }
            _rec = started = rec;
            _stemPath = stemPath;
            _files = files;
            _manual = manual;
            _playback = playback;
            _onsite = onsite;
            _titlesSeen = [];
            _callMissingSince = null;
            _noAudioWarned = false;
            _prejoinSince = null;
            _continues = null;
            _callApp = callApp;
            _watchdog.Reset();
            _calendar = null;  // looked up below, outside the lock (Outlook COM can take seconds)
            (_title, _titleSource) = AppLogic.ResolveTitle(title, manual, null);
            _screen = null;
            // Teams window video (name tiles -> who speaks when); a played-back Teams recording shows them too
            if (AppLogic.RecordsWindows(onsite, callApp))
            {
                var ff = Mixer.FindFfmpeg();
                if (ff is not null)
                {
                    try
                    {
                        _screen = new ScreenCaptureProcess(stemPath, rec.Started);
                    }
                    catch (Exception e)
                    {
                        FileLog.Exception("screen capture not started", e);
                        _screen = null;
                    }
                }
                else
                    Log.Warn("screen capture needs ffmpeg (not found), recording audio only");
            }
        }
        if (!playback)
        {
            var cal = Outlook.Meeting(_cfg.UseOutlook, _clock.Now, title);
            lock (_lock)
            {
                if (ReferenceEquals(_rec, started))  // not stopped meanwhile
                {
                    _calendar = cal;
                    (_title, _titleSource) = AppLogic.ResolveTitle(title, manual, cal);
                }
            }
        }
        Refresh();
        Log.Info($"START '{title}' ({(playback ? "playback" : manual ? "manual" : "live")}) -> {stemPath}");
        return started;
    }

    /// <summary>Stop the running recording: delete it (aborted, too short, no audio file at all) or hand it to
    /// the finalizer on a background task with the metadata captured NOW - the next recording (on-site
    /// upgrade) may start before the sidecar is written.</summary>
    public void Stop(string reason)
    {
        Recorder rec;
        string stemPath;
        List<string> recorded;
        ScreenCaptureProcess? sc;
        RecordingInfo info;
        lock (_lock)
        {
            if (_rec is null) return;
            rec = _rec;
            _rec = null;
            stemPath = _stemPath;
            if (reason == "aborted")
                _declined = true;  // discarded on purpose: do not start again until this call is over
            sc = _screen;
            _screen = null;
            recorded = _files.ToList();
            info = new RecordingInfo(_title, _calendar, _titleSource, new HashSet<string>(_titlesSeen), _continues,
                                     AppLogic.SourceOf(_onsite, _playback, _manual), _callApp?.App);
        }
        Refresh();  // the tray is idle at once; closing the streams and the videos takes seconds, outside the lock
        var dur = rec.Stop();
        IReadOnlyList<ScreenInfo> screens = [];
        if (sc is not null)
        {
            try { screens = sc.Stop(); }
            catch (Exception e) { FileLog.Exception("screen capture stop", e); }
        }
        var dir = Path.GetDirectoryName(stemPath)!;
        var files = recorded.Where(File.Exists).ToList();
        files.AddRange(screens.Select(s => Path.Combine(dir, s.File)).Where(File.Exists));
        Log.Info($"STOP ({reason}) after {dur:F0}s");
        var stem = Path.GetFileName(stemPath);
        if (Finalizer.NoAudioFile(files))  // the devices vanished: nothing was ever written
        {
            DeleteRecording(stemPath, files);
            Log.Error($"{stem}: no audio file was written ({rec.Reopens} reopens), nothing kept");
            _notifier.Notify($"Nahrávka {stem} NEVZNIKLA: zvukové zařízení nedodalo nic. Zkontrolujte mikrofon.");
            _notifier.Beep(error: true);
            DisposeQuietly(rec);
            return;
        }
        if (AppLogic.Discard(reason, dur))
        {
            DeleteRecording(stemPath, files);
            Log.Info($"deleted ({(reason == "aborted" ? "aborted" : "too short")})");
            _notifier.Notify("Recording discarded");
            DisposeQuietly(rec);
            return;
        }
        var stopped = new StoppedRecording(stemPath, rec.Started, dur, rec.Reopens, rec.HeardSys, rec.HeardMic,
                                           new Dictionary<string, TrackInfo>(rec.Tracks), files, screens);
        DisposeQuietly(rec);
        var task = Task.Run(() => Guard("finalize", () => _finalizer.Finalize(stopped, reason, info)));
        lock (_finalizing)
        {
            _finalizing.RemoveAll(t => t.IsCompleted);
            _finalizing.Add(task);
        }
    }

    private static void DeleteRecording(string stemPath, IEnumerable<string> files)
    {
        foreach (var f in files)
        {
            try { File.Delete(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"cannot delete {f}: {e.Message}"); }
        }
        try { Directory.Delete(Path.GetDirectoryName(stemPath)!); }  // the recording folder, now empty
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void DisposeQuietly(IDisposable d)
    {
        try { d.Dispose(); }
        catch (Exception e) { FileLog.Exception("recorder dispose", e); }
    }

    // ------------------------------------------------------------------ shutdown
    /// <summary>Quit: stop the loop, keep the running recording ("quit" -> app_quit).</summary>
    public void Quit()
    {
        _quit.Set();
        Stop("quit");
        WriteCaptureStatus(running: false);
    }

    /// <summary>Wait for sidecars still being written, so Quit does not leave a half-finished recording behind
    /// (Orphans.Recover would repair it at the next start, but the user expects it done now).</summary>
    public void WaitForFinalizers(TimeSpan timeout)
    {
        Task[] pending;
        lock (_finalizing) pending = _finalizing.Where(t => !t.IsCompleted).ToArray();
        if (pending.Length == 0) return;
        Log.Info($"waiting for {pending.Length} recording(s) to be finalized");
        try { Task.WaitAll(pending, timeout); }
        catch (AggregateException) { }
    }

    public void Dispose()
    {
        _quit.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _quit.Dispose();
    }
}
