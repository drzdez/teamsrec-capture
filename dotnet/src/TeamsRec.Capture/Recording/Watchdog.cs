using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Recording;

/// <summary>
/// The audio watchdog of a running recording (Python: App._audio_watchdog, _reopen_result, _try_reopen,
/// _follow_teams_mic). Pure state machine: time comes from <see cref="IClock"/>, the tray from
/// <see cref="INotifier"/>, the recorder through <see cref="IAudioSource"/>, so it runs in tests without hardware.
/// The App calls <see cref="Tick"/> and <see cref="FollowCallMic"/> once per tray loop tick and
/// <see cref="Reset"/> when a new recording starts.
/// </summary>
public sealed class Watchdog
{
    public const double AudioStallS = 20;    // no buffer from any device for this long = the device went to sleep / was unplugged
    public const double AudioSilentS = 90;   // the system track stays digitally silent this long during a live call = wrong device
    public static readonly IReadOnlyList<double> AudioBackoffS = [0, 30, 60, 180, 300];  // wait this long before the 1st, 2nd ... failed reopen is retried
    public const int AudioMaxReopens = 8;
    public const double ReopenCheckS = 12;   // a reopen counts as successful only if data still arrives this long afterwards
    public const double AudioRewarnS = 300;  // the "no audio" alarm repeats this often: one notification is missed in a meeting

    public const double TeamsMicCheckS = 30;
    public const double DeadMicS = 120;      // a microphone without even room noise this long is not the one in use

    // A reopen whose data stops again within this many seconds of the verdict did not bring the device back.
    private const double RecoveredWithinS = 4;

    private readonly IClock _clock;
    private readonly INotifier _notifier;

    private double _warnedAt;
    private double? _reopenAt;
    private int _reopenTries;
    private double _nextReopenAt;
    // Not reset by Reset(), like the prototype: the call-mic check runs on its own 30 s rhythm and a microphone
    // that could not be followed is not announced again.
    private double _teamsMicChecked = double.NegativeInfinity;
    private string? _teamsMicWarned;

    public Watchdog(IClock clock, INotifier notifier)
    {
        _clock = clock;
        _notifier = notifier;
        Reset();
    }

    /// <summary>The current alarm text; null = the audio is fine. The tray icon turns yellow while it is set.</summary>
    public string? Warned { get; private set; }

    /// <summary>Raised whenever <see cref="Warned"/> changes, so the App can refresh the tray icon and menu
    /// (Python: self._refresh()).</summary>
    public event Action? Changed;

    // Exposed to the tests (internals are visible to the test project).
    internal double? ReopenAt => _reopenAt;
    internal int ReopenTries => _reopenTries;
    internal double NextReopenAt => _nextReopenAt;

    /// <summary>A new recording starts: forget the alarm and the reopen backoff of the previous one.</summary>
    public void Reset()
    {
        Warned = null;
        _warnedAt = double.NegativeInfinity;
        _reopenAt = null;
        _reopenTries = 0;
        _nextReopenAt = 0.0;
    }

    /// <summary>
    /// Warn loudly (tray + log) when the call audio is not arriving: a Bluetooth headset that fell asleep,
    /// or Teams playing through another device than the Windows default we record.
    /// </summary>
    /// <param name="rec">The running recorder.</param>
    /// <param name="elapsedS">Seconds since the recording started.</param>
    /// <param name="playback">A playback recording (a video played on this PC): silence there is not a fault.</param>
    public void Tick(IAudioSource rec, double elapsedS, bool playback)
    {
        double now = _clock.Seconds;
        if (playback || elapsedS < AudioStallS + 5)
            return;
        ReopenResult(rec, now);
        if (elapsedS < AudioSilentS && _clock.Seconds - rec.LastData <= AudioStallS)
            return;

        if (rec.MicOnly)
        {
            TickOnsite(rec, elapsedS, now);
            return;
        }

        bool stalled = now - rec.LastData > AudioStallS;
        if (stalled)
        {
            TryReopen(rec, now);
            if (_reopenAt is not null)
                return;  // a reopen is under way: wait for its verdict before alarming the user
        }
        // the others being silent while the user talks (presenting, a monologue) is not a fault
        bool silent = now - rec.LastLoud > AudioSilentS && now - rec.LastMicLoud > AudioSilentS;
        bool noMic = rec.WithMic && rec.LastMicData is null;
        // not even room noise for minutes: this microphone is not the one in use (2026-09-29, the whole call)
        bool deadMic = rec.WithMic && rec.Tracks.ContainsKey("mic") && elapsedS > DeadMicS
                       && now - rec.LastMicAlive > DeadMicS;
        string? problem =
            stalled ? "audio streams stopped (device asleep or unplugged?)"
            : silent ? "system audio is silent (Teams playing through another device?)"
            : noMic ? "the microphone never delivered data"
            : deadMic ? $"mikrofon „{MicDevice(rec)}“ je úplně potichu – Teams nejspíš používá jiný, váš hlas se nenahrává"
            : null;

        if (problem is not null && (problem != Warned || now - _warnedAt > AudioRewarnS))
        {
            SetWarned(problem);
            _warnedAt = now;
            Log.Warn($"audio watchdog: {problem}");
            _notifier.Notify($"Zvuk se NENAHRÁVÁ: {problem}. Zkontrolujte sluchátka / vstupní zařízení.");
            _notifier.Beep();
        }
        else if (problem is null && Warned is not null)
        {
            Log.Info("audio watchdog: audio is back");
            SetWarned(null);
            _notifier.Notify("Zvuk se obnovil, nahrávání pokračuje.");
        }
    }

    /// <summary>On site only the room microphone is recorded: a stall is reopened, and a microphone without even
    /// room noise gets its own alarm (and its own all-clear), not the call-oriented ones.</summary>
    private void TickOnsite(IAudioSource rec, double elapsedS, double now)
    {
        if (now - rec.LastData > AudioStallS)
            TryReopen(rec, now);
        bool dead = elapsedS > DeadMicS && now - rec.LastMicAlive > DeadMicS;
        if (dead && Warned is null)
        {
            string dev = MicDevice(rec);
            SetWarned($"mikrofon místnosti „{dev}“ je úplně potichu");
            _warnedAt = now;
            Log.Warn($"audio watchdog: on-site microphone '{dev}' carries no signal at all");
            _notifier.Notify($"Nahrávání na místě: mikrofon „{dev}“ je úplně potichu (vypnutý, ztlumený "
                             + "přepínačem, nebo jiný vstup). Zvuk místnosti se nenahrává.");
            _notifier.Beep();
        }
        else if (!dead && Warned is not null)
        {
            SetWarned(null);
            _notifier.Notify("Mikrofon místnosti už nahrává.");
        }
    }

    /// <summary>
    /// During a call, check now and then which microphone the call app uses; when the user switched headsets,
    /// reopen the mic track on the new one (same format) or say loudly that it cannot be followed.
    /// </summary>
    /// <param name="rec">The running recorder.</param>
    /// <param name="callInputDevice">OS lookup: the input device the call app records from now (null = unknown).</param>
    /// <param name="applies">False for playback and on-site recordings (Python: not playback and not onsite).</param>
    public void FollowCallMic(IAudioSource rec, Func<string?> callInputDevice, bool applies)
    {
        if (!applies || !rec.Tracks.ContainsKey("mic"))
            return;
        double now = _clock.Seconds;
        if (now - _teamsMicChecked < TeamsMicCheckS)
            return;
        _teamsMicChecked = now;
        string? current = callInputDevice();
        string recorded = rec.Tracks["mic"].Device ?? "";
        if (string.IsNullOrEmpty(current) || current == recorded || current == _teamsMicWarned)
            return;
        Log.Warn($"the call now uses the microphone '{current}', the recording has '{recorded}'");
        rec.MicName = current;
        try
        {
            rec.Reopen();
        }
        catch (Exception e)
        {
            Log.Error($"reopen on the Teams microphone: {e}");
        }
        if (rec.Tracks.TryGetValue("mic", out var mic) && mic.Device == current)
        {
            _notifier.Notify($"Mikrofon přepnut na „{current}“ (ten teď používá hovor).");
        }
        else  // different sample format: one file cannot hold both
        {
            _teamsMicWarned = current;
            _notifier.Notify($"Hovor používá mikrofon „{current}“, ale nahrávka ho nezachytí (jiný formát). "
                             + "Váš hlas bude v nahrávce chybět.");
            _notifier.Beep();
        }
    }

    /// <summary>
    /// Did the last reopen bring the audio back? A sleeping Bluetooth dongle answers with one buffer and
    /// goes quiet again, so the verdict is passed this long after the reopen, and a failed one makes the next
    /// attempt wait longer: every reopen re-enumerates the devices and Windows answers with a device-change
    /// storm (2026-09-21: 13 reopens = 13 tray notifications in 6 minutes).
    /// </summary>
    private void ReopenResult(IAudioSource rec, double now)
    {
        if (_reopenAt is null || now - _reopenAt.Value < ReopenCheckS)
            return;
        if (now - rec.LastData <= RecoveredWithinS)
        {
            Log.Info("audio watchdog: data is arriving again after the reopen");
            _reopenTries = 0;
            _nextReopenAt = 0.0;
        }
        else
        {
            _reopenTries++;
            double wait = AudioBackoffS[Math.Min(_reopenTries, AudioBackoffS.Count - 1)];
            Log.Warn($"audio watchdog: reopen #{rec.Reopens} brought no data (device asleep or taken by Teams?), "
                     + $"next attempt in {wait:0} s");
            _nextReopenAt = now + wait;
        }
        _reopenAt = null;
    }

    /// <summary>At most one reopen per backoff window, and never while the previous one is still being judged.</summary>
    private void TryReopen(IAudioSource rec, double now)
    {
        if (_reopenAt is not null || now < _nextReopenAt || rec.Reopens >= AudioMaxReopens)
            return;
        Log.Warn($"audio watchdog: no data for {now - rec.LastData:0} s, reopening the streams on the current devices");
        try
        {
            if (rec.Reopen())
                _reopenAt = _clock.Seconds;
        }
        catch (Exception e)
        {
            Log.Error($"reopen: {e}");
            _nextReopenAt = now + AudioBackoffS[^1];
        }
    }

    private static string MicDevice(IAudioSource rec) =>
        rec.Tracks.TryGetValue("mic", out var mic) && mic.Device is not null ? mic.Device : "?";

    private void SetWarned(string? text)
    {
        if (Warned == text)
            return;
        Warned = text;
        Changed?.Invoke();
    }
}
