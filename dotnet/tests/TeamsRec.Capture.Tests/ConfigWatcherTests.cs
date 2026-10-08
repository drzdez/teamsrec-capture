using TeamsRec.Capture.Config;

namespace TeamsRec.Capture.Tests;

public class ConfigWatcherTests
{
    [Fact]
    public void Edits_of_the_shared_file_apply_at_run_time_but_not_the_folder_or_a_broken_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"teamsrec-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "teamsrec.toml");
        try
        {
            File.WriteAllText(path, "[recordings]\nout_dir = \"D:/meetings\"\n[capture]\nonsite_offer = \"never\"\n");
            var cfg = AppConfig.Load(path);
            using var watcher = new ConfigWatcher(cfg);

            File.WriteAllText(path, "[recordings]\nout_dir = \"E:/elsewhere\"\n[user]\nname = \"Jan Novák\"\n" +
                                    "[capture]\nonsite_offer = \"calendar\"\nonsite_mic = \"Mikrofon (USB)\"\n");
            var changed = watcher.Reload();
            Assert.Equal(["user.name", "capture.onsite_mic", "capture.onsite_offer"], changed);
            Assert.Equal(("Jan Novák", "calendar", "Mikrofon (USB)"), (cfg.UserName, cfg.OnsiteOffer, cfg.OnsiteMic));
            Assert.Equal(AppConfig.ExpandUser("D:/meetings"), cfg.OutDir);  // a running app keeps its folder

            File.WriteAllText(path, "[capture]\nonsite_offer = \"always");  // half-written: not defaults
            Assert.Empty(watcher.Reload());
            Assert.Equal("calendar", cfg.OnsiteOffer);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_tray_opens_the_review_window_or_the_browser_as_configured()
    {
        var local = @"C:\Users\jan\AppData\Local";
        var app = Path.Combine(local, "Programs", "teamsrec-review", "teamsrec-review.exe");
        Assert.Equal((app, ""), App.AppLogic.ReviewLaunch("app", "", local));
        Assert.Equal((app, "--browser"), App.AppLogic.ReviewLaunch("web", "", local));
        Assert.Equal((app, "--settings"), App.AppLogic.ReviewLaunch("app", "", local, settings: true));
        Assert.Equal((app, "--browser --settings"), App.AppLogic.ReviewLaunch("web", "", local, settings: true));
        Assert.Equal((app, "--wizard"), App.AppLogic.ReviewLaunch("app", "", local, wizard: true));
        Assert.Equal((app, "--open 2026-09-30_1827_zina"), App.AppLogic.ReviewLaunch("app", "", local, stem: "2026-09-30_1827_zina"));
        // the suite MSI installs the window next to this app: that one wins over the earlier separate install
        var suiteDir = Path.Combine(Path.GetTempPath(), $"teamsrec-suite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(suiteDir);
        try
        {
            Assert.Equal((app, ""), App.AppLogic.ReviewLaunch("app", "", local, appDir: suiteDir));  // not there
            File.WriteAllText(Path.Combine(suiteDir, "teamsrec-review.exe"), "");
            Assert.Equal(Path.Combine(suiteDir, "teamsrec-review.exe"), App.AppLogic.ReviewLaunch("app", "", local, appDir: suiteDir).Exe);
        }
        finally
        {
            Directory.Delete(suiteDir, true);
        }
        Assert.Equal((app, "--browser --open 2026-09-30_1827_zina"),
                     App.AppLogic.ReviewLaunch("web", "", local, stem: "2026-09-30_1827_zina"));
        Assert.Equal((app, ""), App.AppLogic.ReviewLaunch("app", "", local, stem: "x\" & calc"));  // no command-line tricks
        Assert.Equal((@"D:\tools\review.exe", ""), App.AppLogic.ReviewLaunch("app", @"D:\tools\review.exe", local));
        var cfg = AppConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.toml"));
        Assert.Equal(("app", ""), (cfg.TrayOpen, cfg.ReviewApp));
    }

    [Fact]
    public void The_capture_status_tells_the_review_page_what_is_recorded()
    {
        var now = new DateTime(2026, 9, 30, 18, 30, 0);
        using var on = System.Text.Json.JsonDocument.Parse(App.AppLogic.CaptureStatusJson(
            true, true, "Plánování", "2026-09-30_1827_planovani", "onsite", new DateTime(2026, 9, 30, 18, 27, 5), now));
        var r = on.RootElement;
        Assert.True(r.GetProperty("recording").GetBoolean());
        Assert.Equal(("Plánování", "2026-09-30_1827_planovani", "onsite", "2026-09-30T18:27:05"),
                     (r.GetProperty("title").GetString(), r.GetProperty("stem").GetString(),
                      r.GetProperty("source").GetString(), r.GetProperty("started").GetString()));
        Assert.Equal(Environment.ProcessId, r.GetProperty("pid").GetInt32());
        using var off = System.Text.Json.JsonDocument.Parse(App.AppLogic.CaptureStatusJson(
            false, true, "x", "y", "live", now, now));
        Assert.False(off.RootElement.GetProperty("recording").GetBoolean(), "a quitting app records nothing");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, off.RootElement.GetProperty("title").ValueKind);
    }
}
