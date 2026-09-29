using TeamsRec.Capture.Calendar;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Screen;

namespace TeamsRec.Capture.Tests;

public class CalendarTests
{
    private static readonly DateTime Day = new(2026, 9, 14);

    private static CalendarItem Item(string subject, string start, string end, bool teams = false) =>
        new(subject, Day + TimeSpan.Parse(start), Day + TimeSpan.Parse(end), "Jana Nováková",
            new[] { "Jana Nováková", "Petr Svoboda" }, teams);

    private static DateTime At(string hhmm) => Day + TimeSpan.Parse(hhmm);

    [Fact]
    public void Title_beats_time()
    {
        var standup = Item("Standup", "10:00", "10:15", teams: true);
        var wfms = Item("WFMS sync", "10:30", "11:00", teams: true);
        var (it, match) = Outlook.Pick(new() { standup, wfms }, At("10:05"), "WFMS sync");
        Assert.Same(wfms, it);
        Assert.Equal("title", match);
    }

    [Fact]
    public void Title_with_window_suffix_matches_by_containment()
    {
        var wfms = Item("WFMS sync", "08:30", "09:15", teams: true);
        var (it, match) = Outlook.Pick(new() { Item("Jiná", "08:30", "09:00"), wfms }, At("08:31"),
                                       "WFMS sync | Microsoft Teams");
        Assert.Same(wfms, it);
        Assert.Equal("title", match);
    }

    [Fact]
    public void Title_normalization_ignores_diacritics_case_and_punctuation()
    {
        Assert.Equal("porada tymu wfms", Outlook.TitleNorm("Porada  týmu – WFMS!"));
        var porada = Item("Porada týmu", "13:00", "14:00");
        var (it, match) = Outlook.Pick(new() { Item("Oběd", "12:30", "13:30"), porada }, At("12:55"), "PORADA TYMU");
        Assert.Same(porada, it);
        Assert.Equal("title", match);
    }

    [Fact]
    public void Close_title_match_tolerates_a_typo()
    {
        var weekly = Item("WFMS sync weekly", "14:00", "15:00");
        var (it, match) = Outlook.Pick(new() { Item("Standup", "14:00", "14:15", teams: true), weekly }, At("14:01"),
                                       "WFMS synk weekly");
        Assert.Same(weekly, it);
        Assert.Equal("title", match);
    }

    [Fact]
    public void Title_far_from_the_call_is_not_matched()
    {
        // the title window is 2 h: the same subject in the evening does not claim a morning call
        var evening = Item("WFMS sync", "18:00", "19:00");
        var now = Item("Standup", "09:00", "09:15");
        var (it, match) = Outlook.Pick(new() { evening, now }, At("09:05"), "WFMS sync");
        Assert.Same(now, it);
        Assert.Equal("time", match);
    }

    [Theory]
    [InlineData("Meeting")]
    [InlineData("Připojení ke schůzce")]
    [InlineData("Calendar")]
    [InlineData("")]
    [InlineData(null)]
    public void Generic_title_falls_back_to_time(string? title)
    {
        var meeting = Item("Meeting", "09:00", "10:00");
        var other = Item("Standup", "09:00", "09:30", teams: true);
        var (it, match) = Outlook.Pick(new() { meeting, other }, At("09:10"), title);
        Assert.Same(other, it);  // both contain 9:10, Teams preferred - the generic title matched nothing
        Assert.Equal("time", match);
    }

    [Fact]
    public void Containing_beats_nearby()
    {
        var running = Item("Plánování", "09:30", "10:30");
        var next = Item("Standup", "10:05", "10:20", teams: true);  // closer start and Teams, but not running yet
        var (it, match) = Outlook.Pick(new() { next, running }, At("10:00"), null);
        Assert.Same(running, it);
        Assert.Equal("time", match);
    }

    [Fact]
    public void Teams_preferred_among_parallel_meetings()
    {
        var local = Item("Rezervace místnosti", "10:00", "11:00");
        var teams = Item("Klient", "09:30", "11:00", teams: true);
        var (it, _) = Outlook.Pick(new() { local, teams }, At("10:01"), null);
        Assert.Same(teams, it);
    }

    [Fact]
    public void Closest_start_wins_otherwise()
    {
        var early = Item("A", "09:00", "11:00", teams: true);
        var late = Item("B", "09:55", "11:00", teams: true);
        var (it, _) = Outlook.Pick(new() { early, late }, At("10:00"), null);
        Assert.Same(late, it);
    }

    [Fact]
    public void Time_window_is_start_minus_10_to_end_plus_5_minutes()
    {
        var m = Item("A", "10:00", "11:00");
        Assert.Same(m, Outlook.Pick(new() { m }, At("09:50"), null).Item);
        Assert.Null(Outlook.Pick(new() { m }, At("09:49"), null).Item);
        Assert.Same(m, Outlook.Pick(new() { m }, At("11:05"), null).Item);
        var (none, match) = Outlook.Pick(new() { m }, At("11:06"), null);
        Assert.Null(none);
        Assert.Equal("", match);
    }

    [Fact]
    public void Empty_calendar_picks_nothing()
    {
        var (it, match) = Outlook.Pick(new(), At("10:00"), "WFMS sync");
        Assert.Null(it);
        Assert.Equal("", match);
    }

    [Fact]
    public void CandidatesAt_window_and_order()
    {
        var a = Item("A", "08:00", "08:30");  // ends 1:30 before 10:00 -> outside the 1 h window
        var b = Item("B", "09:00", "09:30");  // ends 0:30 before -> inside
        var c = Item("C", "10:20", "11:00");
        var d = Item("D", "11:00", "12:00");  // starts exactly 1 h after -> inside (inclusive)
        var e = Item("E", "11:01", "12:00");
        var got = Outlook.CandidatesAt(new[] { a, b, c, d, e }, At("10:00"));
        Assert.Equal(new[] { "C", "B", "D" }, got.Select(x => x.Subject));
        Assert.Equal(new[] { "A", "B", "C", "D", "E" },
                     Outlook.CandidatesAt(new[] { a, b, c, d, e }, At("10:00"), 7200).Select(x => x.Subject).OrderBy(s => s));
    }

    [Theory]
    [InlineData("abcd", "bcde", 0.75)]
    [InlineData("wfms sync weekly", "wfms synk weekly", 0.9375)]
    [InlineData("porada vyvoje", "planovani sprintu", 0.2)]
    [InlineData("", "", 1.0)]
    public void Ratio_matches_python_difflib(string a, string b, double expected)
    {
        Assert.Equal(expected, Outlook.Ratio(a, b), 10);
    }

    [Fact]
    public void CloseMatch_respects_cutoff()
    {
        Assert.Equal("wfms sync weekly", Outlook.CloseMatch("wfms synk weekly", new[] { "standup", "wfms sync weekly" }, 0.8));
        Assert.Null(Outlook.CloseMatch("porada vyvoje", new[] { "planovani sprintu" }, 0.8));
    }

    [Fact]
    public void Meeting_disabled_or_failing_returns_null_and_sets_match()
    {
        var m = Item("WFMS sync", "10:00", "11:00", teams: true);
        Assert.Null(Outlook.Meeting(false, At("10:00"), items: _ => throw new InvalidOperationException("no call expected")));
        Assert.Null(Outlook.Meeting(true, At("10:00"), items: _ => throw new InvalidOperationException("no Outlook")));
        Assert.Null(Outlook.Meeting(true, At("15:00"), items: _ => new() { m }));
        var got = Outlook.Meeting(true, At("10:02"), "WFMS sync", _ => new() { m });
        Assert.NotNull(got);
        Assert.Equal("title", got!.Match);
        Assert.Equal("time", Outlook.Meeting(true, At("10:02"), null, _ => new() { m })!.Match);
    }

    [Fact]
    public void Screen_window_size_filter()
    {
        Assert.True(ScreenCapture.Qualifies(500, 350));
        Assert.False(ScreenCapture.Qualifies(499, 900));
        Assert.False(ScreenCapture.Qualifies(1920, 349));
        Assert.False(ScreenCapture.Qualifies(160, 28));  // a minimized window's rect
    }

    [Theory]
    [InlineData(3200, 1800, 0, 0, 1600, 900)]    // exact aspect, scaled down
    [InlineData(1000, 500, 300, 200, 1000, 500)]  // smaller than the canvas: never enlarged, centred
    [InlineData(2000, 900, 0, 90, 1600, 720)]     // wider: black bars top and bottom
    [InlineData(900, 1800, 575, 0, 450, 900)]     // taller: black bars left and right
    public void Screen_letterbox(int w, int h, int x, int y, int fw, int fh)
    {
        Assert.Equal((x, y, fw, fh), ScreenCapture.Fit(w, h));
    }
}
