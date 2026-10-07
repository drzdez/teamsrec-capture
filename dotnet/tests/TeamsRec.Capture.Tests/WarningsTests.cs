using TeamsRec.Capture.App;
using TeamsRec.Capture.Calendar;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Detection;

namespace TeamsRec.Capture.Tests;

/// <summary>2026-10-07: a call in the "Archi standup" meeting was not linked to the calendar (Outlook read the
/// filter date 10/07 as 10 July in Czech), and nothing said so; the Teams window may be minimized while presenting.</summary>
public class WarningsTests
{
    private static readonly DateTime Day = new(2026, 10, 7);

    private static CalendarItem Item(string subject, int h, int m, int minutes, bool teams = true) =>
        new(subject, Day.AddHours(h).AddMinutes(m), Day.AddHours(h).AddMinutes(m + minutes), "", new List<string>(), teams);

    [Fact]
    public void The_calendar_lookup_says_why_there_is_no_clear_meeting()
    {
        var items = new List<CalendarItem> { Item("AI discussion", 9, 0, 90, teams: false), Item("Archi standup", 9, 15, 15) };
        var at = Day.AddHours(9).AddMinutes(10);  // both run: AI discussion since 9:00, the standup from 9:15 (10 min ahead counts)
        Assert.Equal("off", Outlook.Lookup(false, at, "Archi standup", _ => items).Problem);
        var (cal, problem) = Outlook.Lookup(true, at, "Archi standup", _ => items);
        Assert.Equal(("Archi standup", ""), (cal!.Subject, problem));  // by title: clear
        (cal, problem) = Outlook.Lookup(true, at, "teams-call", _ => items);
        Assert.Equal("ambiguous", problem);  // by time while two meetings run
        Assert.Contains("víc schůzek", AppLogic.CalendarWarning(problem, cal));
        Assert.Equal("none", Outlook.Lookup(true, Day.AddHours(20), "teams-call", _ => items).Problem);
        Assert.Equal("unavailable", Outlook.Lookup(true, at, "x", _ => throw new InvalidOperationException("no COM")).Problem);
        Assert.Equal("", AppLogic.CalendarWarning("", cal));
    }

    [Fact]
    public void Both_day_orders_are_asked_so_any_locale_finds_the_day()
    {
        Assert.Equal(new[] { "10/07/2026", "07/10/2026" },
                     Outlook.DateOrders.Select(f => Day.ToString(f, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void The_participants_are_seen_only_in_a_meeting_window_that_is_not_minimized()
    {
        var meeting = new TeamsWindow("Archi standup | Microsoft Teams", false, 1200, 800);
        Assert.True(CallDetector.MeetingViewVisible(new[] { meeting }));
        Assert.False(CallDetector.MeetingViewVisible(new[] { meeting with { Minimized = true } }));
        Assert.False(CallDetector.MeetingViewVisible(new[]
        {
            new TeamsWindow("Kompaktní zobrazení schůzky | Archi standup | Microsoft Teams", false, 400, 240),
            new TeamsWindow("Calendar | Microsoft Teams", false, 1600, 1000),
            new TeamsWindow("Ovládací panel sdílení | Microsoft Teams", false, 600, 60),
        }), "the compact view, a nav section and the sharing toolbar show no participants");
        Assert.True(CallDetector.Presenting(new[] { "Ovládací panel sdílení | Microsoft Teams" }));
        Assert.False(CallDetector.Presenting(new[] { "Archi standup | Microsoft Teams" }));
    }

    [Fact]
    public void Text_only_when_the_user_surely_does_not_present()
    {
        Assert.False(AppLogic.Discreet(presenting: false, acceptsNotifications: true));
        Assert.True(AppLogic.Discreet(presenting: true, acceptsNotifications: true));
        Assert.True(AppLogic.Discreet(presenting: false, acceptsNotifications: false));  // full screen / cannot tell
    }
}
