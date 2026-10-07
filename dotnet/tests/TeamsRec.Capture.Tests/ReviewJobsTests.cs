using TeamsRec.Capture.App;

namespace TeamsRec.Capture.Tests;

public class ReviewJobsTests
{
    private const string Busy = """
        {"app": "teamsrec-review", "pid": 4242, "running": true, "busy": true, "held": false,
         "current": {"stem": "2026-10-07_0902_archi-standup", "title": "Archi standup", "text": "zpracování spuštěno"},
         "queued": 2, "finished": [{"id": 1, "stem": "2026-10-06_1611_x", "title": "Jana Nováková", "ok": true,
                                     "text": "přepis i zápis hotové", "at": "2026-10-07T10:00:00"}]}
        """;

    [Fact]
    public void A_running_server_with_a_job_is_busy_and_says_what_it_processes()
    {
        var s = ReviewJobs.Parse(Busy, _ => true)!;
        Assert.True(s.Busy);
        Assert.Equal("teamsrec: zpracovává se – Archi standup (+2 ve frontě)", ReviewJobs.Tooltip(s));
    }

    [Fact]
    public void A_file_left_by_a_server_that_is_gone_is_idle()
    {
        var s = ReviewJobs.Parse(Busy, _ => false)!;
        Assert.False(s.Busy);
        Assert.Equal(0, s.Queued);
        Assert.Null(ReviewJobs.Parse("{\"pid\": 1, \"runn", _ => true));  // caught mid-write
    }

    [Fact]
    public void Only_jobs_that_end_after_the_tray_started_get_a_balloon()
    {
        HashSet<string>? seen = null;
        var s = ReviewJobs.Parse(Busy, _ => true)!;
        Assert.Empty(ReviewJobs.NewlyFinished(s, ref seen));  // history
        var later = ReviewJobs.Parse(Busy.Replace("\"finished\": [", """
            "finished": [{"id": 2, "stem": "2026-10-07_0902_archi-standup", "title": "Archi standup", "ok": true,
                          "text": "přepis hotový; zápis počká, až pojmenujete mluvčí", "at": "2026-10-07T11:30:00"},
            """), _ => true)!;
        var fresh = ReviewJobs.NewlyFinished(later, ref seen);
        Assert.Single(fresh);
        Assert.Equal("Zpracováno: Archi standup – přepis hotový; zápis počká, až pojmenujete mluvčí", ReviewJobs.Balloon(fresh[0]));
        Assert.Empty(ReviewJobs.NewlyFinished(later, ref seen));
    }
}
