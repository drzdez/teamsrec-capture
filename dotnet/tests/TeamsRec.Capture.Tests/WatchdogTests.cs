using TeamsRec.Capture.Core;
using TeamsRec.Capture.Recording;

namespace TeamsRec.Capture.Tests;

/// <summary>Ports of the watchdog tests of legacy/smoke_test.py, driven by a settable clock instead of time.time().</summary>
public sealed class WatchdogTests
{
    private sealed class WdFakeClock : IClock
    {
        public double Seconds { get; set; } = 1_000_000;
        public DateTime Now => new DateTime(2026, 9, 25, 9, 0, 0).AddSeconds(Seconds - 1_000_000);
        public void Advance(double s) => Seconds += s;
    }

    private sealed class WdFakeNotifier : INotifier
    {
        public List<string> Notes { get; } = [];
        public int Beeps { get; private set; }
        public void Notify(string message) => Notes.Add(message);
        public void Beep(bool error = false) => Beeps++;
    }

    /// <summary>Enough of a Recorder for the watchdog (Python: FakeRec).</summary>
    private sealed class WdFakeRec : IAudioSource
    {
        public WdFakeRec(IClock clock)
        {
            double now = clock.Seconds;
            LastData = LastLoud = LastMicLoud = LastMicAlive = now;
            LastMicData = now;
            Started = clock.Now;
        }

        public double LastData { get; set; }
        public double LastLoud { get; set; }
        public double LastMicLoud { get; set; }
        public double LastMicAlive { get; set; }
        public double? LastMicData { get; set; }
        public bool MicOnly { get; set; }
        public bool WithMic { get; set; } = true;
        public int Reopens { get; set; }
        public int ReopenCalls { get; private set; }
        public long BytesReceived { get; set; }
        public bool HeardSys { get; set; }
        public bool HeardMic { get; set; }
        public DateTime Started { get; }
        public Dictionary<string, TrackInfo> TrackMap { get; } = [];
        public IReadOnlyDictionary<string, TrackInfo> Tracks => TrackMap;
        public string MicName { get; set; } = "";
        public string OutputName { get; set; } = "";
        public bool SysPending { get; set; }
        public int AddSysCalls { get; private set; }
        /// <summary>What a reopen does to the tracks; default: nothing (the same devices are reopened).</summary>
        public Action? OnReopen { get; set; }

        public bool TryAddSys()
        {
            AddSysCalls++;
            return false;
        }

        public bool Reopen()
        {
            ReopenCalls++;
            Reopens++;
            OnReopen?.Invoke();
            return true;
        }
    }

    private static TrackInfo Mic(string device) => new("x.mic.wav", 48000, 1, device);

    [Fact]
    public void The_recording_follows_the_output_the_call_plays_into()
    {
        // 1.1.1: Teams plays into a headset switched on mid-call while the old speakers still exist
        var clock = new WdFakeClock();
        var wd = new Watchdog(clock, new WdFakeNotifier());
        var rec = new WdFakeRec(clock);
        rec.TrackMap["sys"] = new TrackInfo("x_sys.wav", 48000, 2, "Reproduktory (Realtek) [Loopback]");
        string? output = "Reproduktory (Realtek)";
        wd.FollowCallOutput(rec, () => output, applies: true);
        Assert.Equal(0, rec.ReopenCalls);  // the recorded one
        clock.Advance(Watchdog.CallOutputCheckS);
        output = "Sluchátka (Sony WH-1000XM6)";
        wd.FollowCallOutput(rec, () => output, applies: true);
        Assert.Equal(1, rec.ReopenCalls);
        Assert.Equal("Sluchátka (Sony WH-1000XM6)", rec.OutputName);  // where the reopen goes
        wd.FollowCallOutput(rec, () => output, applies: true);
        Assert.Equal(1, rec.ReopenCalls);  // not before the next check
        clock.Advance(Watchdog.CallOutputCheckS);
        wd.FollowCallOutput(rec, () => null, applies: true);  // no call app plays: nothing changes
        Assert.Equal(1, rec.ReopenCalls);
        clock.Advance(Watchdog.CallOutputCheckS);
        wd.FollowCallOutput(rec, () => "Jiné", applies: false);  // playback / on site
        Assert.Equal(1, rec.ReopenCalls);
    }

    [Fact]
    public void Without_an_output_at_the_start_the_other_side_is_looked_for_every_few_seconds()
    {
        var clock = new WdFakeClock();
        var wd = new Watchdog(clock, new WdFakeNotifier());
        var rec = new WdFakeRec(clock) { SysPending = true };
        wd.AddPendingSys(rec);
        wd.AddPendingSys(rec);
        Assert.Equal(1, rec.AddSysCalls);
        clock.Advance(Watchdog.SysPendingCheckS);
        wd.AddPendingSys(rec);
        Assert.Equal(2, rec.AddSysCalls);
        rec.SysPending = false;  // the output appeared
        clock.Advance(Watchdog.SysPendingCheckS);
        wd.AddPendingSys(rec);
        Assert.Equal(2, rec.AddSysCalls);
    }

    [Fact]
    public void Watchdog_backoff()
    {
        // A reopen is judged 12 s later; a dead device is not hammered every 20 s (that was a notification storm).
        var clock = new WdFakeClock();
        var notes = new WdFakeNotifier();
        var rec = new WdFakeRec(clock);
        var wd = new Watchdog(clock, notes);

        rec.LastData = clock.Seconds - 25;
        wd.Tick(rec, 60, playback: false);
        Assert.Equal(1, rec.ReopenCalls);
        Assert.NotNull(wd.ReopenAt);
        wd.Tick(rec, 63, playback: false);
        Assert.Equal(1, rec.ReopenCalls);  // no second reopen while the first is being judged

        clock.Advance(13);  // 13 s later, still nothing
        rec.LastData = clock.Seconds - 30;
        wd.Tick(rec, 75, playback: false);
        Assert.Equal(1, rec.ReopenCalls);
        Assert.Equal(1, wd.ReopenTries);
        Assert.True(wd.NextReopenAt > clock.Seconds + 25, "~30 s before the next attempt");
        Assert.Single(notes.Notes);
        Assert.NotNull(wd.Warned);
        for (int i = 0; i < 5; i++)
            wd.Tick(rec, 90, playback: false);
        Assert.Equal(1, rec.ReopenCalls);  // quiet during the backoff
        Assert.Single(notes.Notes);

        clock.Advance(31);  // the backoff window is over
        wd.Tick(rec, 200, playback: false);
        Assert.Equal(2, rec.ReopenCalls);
        clock.Advance(13);
        rec.LastData = rec.LastLoud = rec.LastMicLoud = rec.LastMicAlive = clock.Seconds;
        wd.Tick(rec, 215, playback: false);
        Assert.Equal(0, wd.ReopenTries);
        Assert.Null(wd.Warned);
        Assert.StartsWith("Zvuk se obnovil", notes.Notes[^1]);

        // a device that never comes back is reopened at most AUDIO_MAX_REOPENS times
        var rec2 = new WdFakeRec(clock);
        var wd2 = new Watchdog(clock, new WdFakeNotifier());
        for (int i = 0; i < 60; i++)
        {
            clock.Advance(400);  // past any backoff window
            rec2.LastData = clock.Seconds - 30;
            wd2.Tick(rec2, 300, playback: false);
            if (wd2.ReopenAt is not null)
            {
                clock.Advance(13);
                wd2.Tick(rec2, 300, playback: false);
            }
        }
        Assert.Equal(Watchdog.AudioMaxReopens, rec2.ReopenCalls);
    }

    [Fact]
    public void A_dead_microphone_raises_the_alarm_during_the_call()
    {
        // 2026-09-29: the mic delivered buffers of digital silence for 16 minutes and nobody was told.
        var clock = new WdFakeClock();
        var notes = new WdFakeNotifier();
        var rec = new WdFakeRec(clock);
        rec.TrackMap["mic"] = Mic("Mikrofon (Creative BT-W5)");
        rec.LastMicAlive = clock.Seconds - 200;  // not even room noise for over three minutes
        var wd = new Watchdog(clock, notes);
        wd.Tick(rec, 300, playback: false);
        Assert.NotNull(wd.Warned);
        Assert.Contains("potichu", wd.Warned);
        Assert.Contains("BT-W5", notes.Notes[^1]);

        var rec2 = new WdFakeRec(clock);
        rec2.TrackMap["mic"] = Mic("Sluchátka s mikrofonem (WH-1000XM6)");
        rec2.LastMicAlive = clock.Seconds - 5;  // a live microphone: room noise all the time
        var wd2 = new Watchdog(clock, new WdFakeNotifier());
        wd2.Tick(rec2, 300, playback: false);
        Assert.Null(wd2.Warned);
    }

    [Fact]
    public void A_dead_room_microphone_is_reported_on_site()
    {
        var clock = new WdFakeClock();
        var notes = new WdFakeNotifier();
        var rec = new WdFakeRec(clock) { MicOnly = true };
        rec.TrackMap["mic"] = Mic("Pole mikrofonu");
        rec.LastMicAlive = clock.Seconds - 200;
        var wd = new Watchdog(clock, notes);
        int changed = 0;
        wd.Changed += () => changed++;

        wd.Tick(rec, 300, playback: false);
        Assert.NotNull(wd.Warned);
        Assert.Contains("Pole mikrofonu", notes.Notes[^1]);

        clock.Advance(10);
        rec.LastMicAlive = clock.Seconds;  // someone switched it on
        wd.Tick(rec, 310, playback: false);
        Assert.Null(wd.Warned);
        Assert.Contains("už nahrává", notes.Notes[^1]);
        Assert.Equal(2, changed);  // the tray icon went yellow and back
    }

    [Fact]
    public void A_headset_switch_during_the_call_is_followed()
    {
        var clock = new WdFakeClock();
        var notes = new WdFakeNotifier();
        var rec = new WdFakeRec(clock);
        rec.TrackMap["mic"] = Mic("Mikrofon (Creative BT-W5)");
        rec.Reopens = 0;
        bool sameFormat = true;
        rec.OnReopen = () =>
        {
            if (sameFormat)
                rec.TrackMap["mic"] = rec.TrackMap["mic"] with { Device = rec.MicName };
        };
        var wd = new Watchdog(clock, notes);
        Func<string?> callMic = () => "Sluchátka s mikrofonem (WH-1000XM6)";

        wd.FollowCallMic(rec, callMic, applies: true);
        Assert.Equal("Sluchátka s mikrofonem (WH-1000XM6)", rec.Tracks["mic"].Device);
        Assert.Contains("přepnut", notes.Notes[^1]);
        clock.Advance(5);
        wd.FollowCallMic(rec, callMic, applies: true);  // checked again only after TEAMS_MIC_CHECK_S
        Assert.Equal(1, rec.Reopens);

        // a microphone that would not open (another format is converted since 1.1.1): say so, once
        rec.TrackMap["mic"] = Mic("Mikrofon (Creative BT-W5)");
        sameFormat = false;
        clock.Advance(Watchdog.TeamsMicCheckS);
        wd.FollowCallMic(rec, callMic, applies: true);
        Assert.Contains("nepodařilo otevřít", notes.Notes[^1]);
        int n = notes.Notes.Count;
        clock.Advance(Watchdog.TeamsMicCheckS + 30);
        wd.FollowCallMic(rec, callMic, applies: true);
        Assert.Equal(n, notes.Notes.Count);  // warned once, not every 30 s
        Assert.Equal(2, rec.Reopens);
    }

    [Fact]
    public void The_call_mic_is_not_followed_on_site_or_in_playback()
    {
        var clock = new WdFakeClock();
        var notes = new WdFakeNotifier();
        var rec = new WdFakeRec(clock);
        rec.TrackMap["mic"] = Mic("Pole mikrofonu");
        var wd = new Watchdog(clock, notes);
        wd.FollowCallMic(rec, () => "Mikrofon (Creative BT-W5)", applies: false);
        Assert.Equal(0, rec.Reopens);
        Assert.Empty(notes.Notes);
    }

    [Fact]
    public void Playback_and_the_first_seconds_are_never_alarmed()
    {
        var clock = new WdFakeClock();
        var notes = new WdFakeNotifier();
        var rec = new WdFakeRec(clock);
        rec.LastData = clock.Seconds - 60;
        var wd = new Watchdog(clock, notes);
        wd.Tick(rec, 300, playback: true);
        wd.Tick(rec, Watchdog.AudioStallS + 4, playback: false);
        Assert.Equal(0, rec.ReopenCalls);
        Assert.Empty(notes.Notes);
        Assert.Null(wd.Warned);
    }
}
