using TeamsRec.Capture.App;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Tests;

public class QuitRequestTests
{
    [Fact]
    public void The_installer_request_file_asks_to_quit_and_a_stale_one_does_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"teamsrec-quit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, QuitRequest.FileName);
        try
        {
            File.WriteAllText(path, "");  // left over from an aborted install
            using var asked = new ManualResetEventSlim();
            using var watcher = QuitRequest.Watch(dir, asked.Set);
            Assert.NotNull(watcher);
            Assert.False(File.Exists(path));
            Assert.False(asked.Wait(300));

            File.WriteAllText(path, "");
            Assert.True(asked.Wait(5000));
            Assert.True(SpinWait.SpinUntil(() => !File.Exists(path), 2000));  // handled requests are removed
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_version_comes_from_the_project()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", Versions.AppVersion);
        Assert.NotEqual("0.0.0", Versions.AppVersion);
    }
}
