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
        Assert.Equal((@"D:\tools\review.exe", ""), App.AppLogic.ReviewLaunch("app", @"D:\tools\review.exe", local));
        var cfg = AppConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.toml"));
        Assert.Equal(("app", ""), (cfg.TrayOpen, cfg.ReviewApp));
    }
}
