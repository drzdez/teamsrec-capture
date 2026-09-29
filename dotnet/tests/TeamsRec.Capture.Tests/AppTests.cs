using TeamsRec.Capture.App;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Settings;

namespace TeamsRec.Capture.Tests;

/// <summary>The monitor loop's pure decisions (ports of smoke_test.py's App tests that need no hardware).</summary>
public class AppTests
{
    private sealed class FakeNotifier : INotifier
    {
        public readonly List<string> Notes = [];
        public int Beeps;
        public void Notify(string message) => Notes.Add(message);
        public void Beep(bool error = false) => Beeps++;
    }

    private static CalendarItem Meeting(string subject, DateTime start, bool teams) =>
        new(subject, start, start.AddHours(1), "Organizer", [], teams);

    // ------------------------------------------------------------------ calendar offer
    [Fact]
    public void CalendarOffer_is_made_once_within_the_window()
    {
        var now = new DateTime(2026, 9, 25, 10, 1, 0);
        var offered = new HashSet<string>();
        var m = Meeting("Vivo workshop", now.AddSeconds(-60), teams: false);
        Assert.True(AppLogic.ShouldOffer(m, now, "calendar", offered, out var since));
        Assert.Equal(60, since, 3);
        Assert.False(AppLogic.ShouldOffer(m, now, "calendar", offered, out _));  // offered once
    }

    [Fact]
    public void CalendarOffer_leaves_a_Teams_meeting_to_the_call_detection()
    {
        var now = new DateTime(2026, 9, 25, 10, 0, 0);
        var offered = new HashSet<string>();
        var m = Meeting("Online sync", now, teams: true);
        Assert.False(AppLogic.ShouldOffer(m, now, "calendar", offered, out _));
        Assert.Contains(AppLogic.OfferKey(m), offered);

        offered.Clear();
        Assert.True(AppLogic.ShouldOffer(m, now, "always", offered, out _));
    }

    [Theory]
    [InlineData(-7200, false)]  // started long ago
    [InlineData(-241, false)]   // just outside the window
    [InlineData(-240, true)]
    [InlineData(0, true)]
    [InlineData(30, false)]     // not started yet
    public void CalendarOffer_window(int startOffsetS, bool expected)
    {
        var now = new DateTime(2026, 9, 25, 10, 0, 0);
        var m = Meeting("Stará", now.AddSeconds(startOffsetS), teams: false);
        Assert.Equal(expected, AppLogic.ShouldOffer(m, now, "calendar", new HashSet<string>(), out _));
    }

    [Fact]
    public void CalendarOffer_ignores_missing_or_untitled_meetings()
    {
        var now = DateTime.Now;
        Assert.False(AppLogic.ShouldOffer(null, now, "always", new HashSet<string>(), out _));
        Assert.False(AppLogic.ShouldOffer(Meeting("", now, false), now, "always", new HashSet<string>(), out _));
    }

    // ------------------------------------------------------------------ on-site device policy
    private static readonly InputDevice Bt = new("id1", "Mikrofon (Creative BT-W5)", 1, 48000, true);

    [Fact]
    public void DevicePolicy_follows_device_missing()
    {
        var inputs = new List<InputDevice> { Bt };
        var n = new FakeNotifier();
        bool ask = true;
        string? Run(string mic, string policy) =>
            MonitorLoop.OnsiteDevice(mic, policy, inputs, () => [], n, _ => ask);

        Assert.Null(Run("Pole mikrofonu", "fail"));
        Assert.Contains("není k dispozici", n.Notes[^1]);
        Assert.Equal("Mikrofon (Creative BT-W5)", Run("Pole mikrofonu", "fallback"));
        ask = false;
        Assert.Null(Run("Pole mikrofonu", "ask"));
        ask = true;
        Assert.Equal("Mikrofon (Creative BT-W5)", Run("Pole mikrofonu", "ask"));
        Assert.Equal("BT-W5", Run("BT-W5", "fail"));  // configured device is there: used as it is
        Assert.Equal("", Run("", "fail"));            // nothing configured: the Windows default input

        inputs.Clear();                                // nothing at all
        Assert.Null(Run("BT-W5", "fallback"));
        Assert.Contains("žádný aktivní mikrofon", n.Notes[^1]);
        Assert.Contains("Windows zná: žádné", n.Notes[^1]);
        Assert.Equal(1, n.Beeps);
    }

    [Fact]
    public void DevicePolicy_names_the_known_but_unavailable_microphones()
    {
        var n = new FakeNotifier();
        MonitorLoop.OnsiteDevice("Pole", "ask", [],
            () => [new UnavailableInput("Pole mikrofonu", "Realtek", "zakázané ve Windows")], n, _ => true);
        Assert.Contains("Pole mikrofonu (zakázané ve Windows)", n.Notes[^1]);
    }

    [Fact]
    public void DevicePolicy_prefers_the_default_input_as_replacement()
    {
        var other = new InputDevice("id2", "Headset", 1, 16000, false);
        var (outcome, device) = AppLogic.ChooseOnsiteDevice("Pole", "fallback", [other, Bt]);
        Assert.Equal(AppLogic.DeviceOutcome.Use, outcome);
        Assert.Equal(Bt.Name, device);
        (outcome, device) = AppLogic.ChooseOnsiteDevice("pole", "ask", [other with { Name = "Pole mikrofonu (Realtek)" }]);
        Assert.Equal(AppLogic.DeviceOutcome.Use, outcome);  // case-insensitive fragment match
        Assert.Equal("pole", device);
    }

    // ------------------------------------------------------------------ stop / source / status
    [Theory]
    [InlineData("call ended", "call_ended")]
    [InlineData("max duration", "max_duration")]
    [InlineData("tray stop", "user_stop")]
    [InlineData("silence", "silence")]
    [InlineData("quit", "app_quit")]
    [InlineData("upgraded", "onsite_upgraded")]
    [InlineData("something else", "user_stop")]
    public void StopReason_maps_to_the_contract(string reason, string code) =>
        Assert.Equal(code, AppLogic.StopReasonCode(reason));

    [Fact]
    public void Source_of_a_recording()
    {
        Assert.Equal("onsite", AppLogic.SourceOf(onsite: true, playback: false, manual: true));
        Assert.Equal("playback", AppLogic.SourceOf(false, true, true));
        Assert.Equal("manual", AppLogic.SourceOf(false, false, true));
        Assert.Equal("live", AppLogic.SourceOf(false, false, false));
    }

    [Fact]
    public void Status_text()
    {
        Assert.Equal("Idle — waiting for a call", AppLogic.StatusText(false, 0, "", null, false));
        Assert.Equal("Teams join screen — recording starts when you join", AppLogic.StatusText(false, 0, "", null, true));
        Assert.Equal("● REC 2 min — Vivo workshop", AppLogic.StatusText(true, 179, "Vivo workshop", null, false));
        Assert.Equal("⚠ 3 min — BEZ ZVUKU: mikrofon je potichu",
                     AppLogic.StatusText(true, 180, "x", "mikrofon je potichu", false));
        Assert.Equal("Idle — waiting for a call", AppLogic.StatusText(false, 0, "", "stale warning", false));
    }

    [Fact]
    public void Tooltip_fits_the_NotifyIcon_limit()
    {
        var t = AppLogic.Tooltip(new string('x', 300));
        Assert.True(t.Length <= 127);
        Assert.Equal("short", AppLogic.Tooltip("short"));
    }

    [Fact]
    public void Discard_aborted_or_too_short()
    {
        Assert.True(AppLogic.Discard("aborted", 600));
        Assert.True(AppLogic.Discard("tray stop", 4.9));
        Assert.False(AppLogic.Discard("tray stop", 5));
    }

    // ------------------------------------------------------------------ loop timing
    [Fact]
    public void Call_end_needs_the_grace_period()
    {
        double? since = null;
        Assert.False(AppLogic.CallEnded(false, ref since, 100));
        Assert.Equal(100, since);
        Assert.False(AppLogic.CallEnded(false, ref since, 109.9));
        Assert.False(AppLogic.CallEnded(true, ref since, 110));   // the call came back: the grace restarts
        Assert.Null(since);
        Assert.False(AppLogic.CallEnded(false, ref since, 120));
        Assert.True(AppLogic.CallEnded(false, ref since, 130));
    }

    [Fact]
    public void Playback_stops_after_silence_once_started()
    {
        Assert.False(AppLogic.PlaybackSilent(elapsedS: 10, now: 100, lastLoud: 0));  // player still starting
        Assert.True(AppLogic.PlaybackSilent(16, 100, 70));
        Assert.False(AppLogic.PlaybackSilent(16, 100, 71));
    }

    [Fact]
    public void Microphone_of_a_new_recording()
    {
        int asked = 0;
        string? CallMic() { asked++; return "Headset (BT-W5)"; }
        Assert.Equal("Pole", AppLogic.MicFor(onsite: true, playback: false, mic: null, onsiteMic: "Pole", CallMic));
        Assert.Equal("Alt", AppLogic.MicFor(true, false, "Alt", "Pole", CallMic));
        Assert.Equal("", AppLogic.MicFor(false, true, null, "Pole", CallMic));
        Assert.Equal(0, asked);
        Assert.Equal("Headset (BT-W5)", AppLogic.MicFor(false, false, null, "Pole", CallMic));
        Assert.Equal("", AppLogic.MicFor(false, false, null, "Pole", () => null));
    }

    // ------------------------------------------------------------------ titles
    [Fact]
    public void Title_from_calendar_window_or_user()
    {
        var cal = Meeting("Vivo workshop", DateTime.Now, false);
        Assert.Equal(("Vivo workshop", "calendar"), AppLogic.ResolveTitle("Debrief", manual: false, cal));
        Assert.Equal(("Debrief", "window"), AppLogic.ResolveTitle("Debrief", manual: false, null));
        Assert.Equal(("teams-call", "generic"), AppLogic.ResolveTitle("teams-call", manual: false, null));
        Assert.Equal(("Můj název", "manual"), AppLogic.ResolveTitle("Můj název", manual: true, cal));
        Assert.Equal(("Vivo workshop", "calendar"), AppLogic.ResolveTitle("meeting", manual: true, cal));
    }

    [Fact]
    public void Title_of_a_call_in_another_app()
    {
        var oc = new CallApp("Chrome (Meet – abc-defg-hij)", "chrome.exe", "Meet – abc-defg-hij");
        Assert.Equal("Meet – abc-defg-hij", AppLogic.OtherCallTitle(oc, null));
        Assert.Equal("Zoom call", AppLogic.OtherCallTitle(new CallApp("Zoom", "zoom.exe", ""), null));
        Assert.Equal("Sync", AppLogic.OtherCallTitle(oc, Meeting("Sync", DateTime.Now, false)));
    }

    // ------------------------------------------------------------------ dialogs
    [Fact]
    public void Discard_box_defaults_follow_prompt_default()
    {
        Assert.Contains("automaticky nechat", Dialogs.DiscardText("x", 45, "ask"));
        Assert.Contains("automaticky zahodit", Dialogs.DiscardText("x", 45, "skip"));
        Assert.True(Dialogs.Answer(6, defaultYes: false));      // Ano
        Assert.False(Dialogs.Answer(7, defaultYes: true));      // Ne
        Assert.True(Dialogs.Answer(32000, defaultYes: true));   // timed out
        Assert.False(Dialogs.Answer(32000, defaultYes: false));
    }

    [Fact]
    public void Discard_box_timeout_keeps_unless_skip()
    {
        Dialogs.BoxOverride = (_, _, _, _) => 32000;
        try
        {
            Assert.False(Dialogs.AskDiscard("x", "ask"));
            Assert.True(Dialogs.AskDiscard("x", "skip"));
            Assert.True(Dialogs.YesNo("?", 1, defaultYes: true));
        }
        finally
        {
            Dialogs.BoxOverride = null;
        }
    }

    // ------------------------------------------------------------------ log / settings server
    [Fact]
    public void Log_line_has_the_prototype_format()
    {
        var line = FileLog.Format(new DateTime(2026, 9, 29, 16, 58, 1, 123), "INFO", "hello");
        Assert.Equal("2026-09-29 16:58:01,123 INFO hello", line);
    }

    [Fact]
    public void FileLog_appends_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teamsrec-test-{Guid.NewGuid():N}", "teamsrec.log");
        try
        {
            using (var log = new FileLog(path))
            {
                log.Write("INFO", "první");
                log.Write("WARNING", "second");
            }
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.EndsWith(" INFO první", lines[0]);
            Assert.EndsWith(" WARNING second", lines[1]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    [Fact]
    public void Settings_page_is_embedded()
    {
        using var s = typeof(SettingsServer).Assembly.GetManifestResourceStream("settings.html");
        Assert.NotNull(s);
        var html = new StreamReader(s!).ReadToEnd();
        Assert.Contains("onsite_offer", html);
    }

    [Fact]
    public void Settings_json_shapes()
    {
        var devs = SettingsServer.DevicesJson([Bt]);
        Assert.Equal(Bt.Name, devs[0]["name"]);
        Assert.Equal(48000, devs[0]["rate"]);
        Assert.Equal(true, devs[0]["is_default"]);
        var un = SettingsServer.UnavailableJson([new UnavailableInput("Pole", "Realtek", "odpojené")]);
        Assert.Equal("odpojené", un[0]["reason"]);
        var json = System.Text.Json.JsonSerializer.Serialize(un, SettingsServer.Json);
        Assert.Contains("odpojené", json);  // no \u escapes: ensure_ascii=False
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:5001", false)]
    [InlineData("https://evil.example", false)]
    public void Settings_posts_only_from_the_page(string? origin, bool ok) =>
        Assert.Equal(ok, SettingsServer.SameOrigin(origin, "http://127.0.0.1:5000/"));
}
