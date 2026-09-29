using TeamsRec.Capture.App;
using TeamsRec.Capture.Calendar;
using TeamsRec.Capture.Contract;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Screen;

namespace TeamsRec.Capture.Tests;

/// <summary>PARITY.md gaps 4-8, closed 2026-09-29.</summary>
public class ParityGapTests
{
    private static string TempStem()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"teamsrec-gap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "2026-09-29_1000_sync");
    }

    [Fact]
    public void The_screen_capture_report_travels_from_the_child_to_the_parent_once()
    {
        var stem = TempStem();
        try
        {
            var screens = new List<ScreenInfo>
            {
                new("2026-09-29_1000_sync_screen1.mp4", 2, 1600, 900, 0.5, 61.0, 120, ["Schůzka | Microsoft Teams"]),
            };
            ScreenCaptureProcess.WriteReport(stem, screens);
            var read = ScreenCaptureProcess.ReadReport(stem);
            Assert.NotNull(read);
            var s = Assert.Single(read);
            Assert.Equal((screens[0].File, 120, 61.0, false), (s.File, s.Frames, s.EndOffsetS, s.Recovered));
            Assert.Equal(["Schůzka | Microsoft Teams"], s.Titles);
            Assert.Null(ScreenCaptureProcess.ReadReport(stem));  // read = consumed
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(stem)!, true);
        }
    }

    [Fact]
    public void Without_a_report_the_videos_on_disk_are_kept_as_recovered()
    {
        var stem = TempStem();
        try
        {
            File.WriteAllBytes(stem + "_screen1.mp4", new byte[5000]);
            File.WriteAllBytes(stem + "_screen2.mp4", new byte[10]);  // an encoder stub
            var s = Assert.Single(ScreenCaptureProcess.Recovered(stem));
            Assert.True(s.Recovered);
            Assert.Equal(-1, s.Frames);
            var side = SidecarScreen.From(s);
            Assert.Null(side.EndOffsetS);  // unknown, not 0
            Assert.True(side.Recovered);
            Assert.Null(SidecarScreen.From(s with { Recovered = false }).Recovered);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(stem)!, true);
        }
    }

    [Fact]
    public void Teams_windows_are_recorded_for_calls_and_playback_not_on_site_or_other_apps()
    {
        Assert.True(AppLogic.RecordsWindows(onsite: false, callApp: null));  // a Teams call, or playback
        Assert.False(AppLogic.RecordsWindows(onsite: true, callApp: null));
        Assert.False(AppLogic.RecordsWindows(onsite: false, callApp: new CallApp("zoom", "zoom.exe", "Zoom")));
    }

    [Fact]
    public void The_calendar_match_brings_the_meetings_around_it_at_the_start()
    {
        var at = new DateTime(2026, 9, 29, 10, 2, 0);
        CalendarItem Item(string subject, int h, int m) =>
            new(subject, new DateTime(2026, 9, 29, h, m, 0), new DateTime(2026, 9, 29, h, m, 0).AddMinutes(30), "Jan Novák", [], true);
        var items = new List<CalendarItem> { Item("Sync", 10, 0), Item("Review", 10, 30), Item("Oběd", 13, 0) };
        var cal = Outlook.Meeting(true, at, null, _ => items);
        Assert.NotNull(cal);
        Assert.Equal("Sync", cal.Subject);
        Assert.NotNull(cal.Candidates);
        var side = SidecarCalendar.From(cal, cal.Candidates);
        Assert.Equal(["Review"], side.Candidates.Select(c => c.Subject));  // the match itself and far items left out
    }
}
