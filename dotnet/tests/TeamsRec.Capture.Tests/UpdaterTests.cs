using TeamsRec.Capture.App;

namespace TeamsRec.Capture.Tests;

public class UpdaterTests
{
    // trimmed answer of GET /repos/drzdez/teamsrec-capture/releases/latest
    private const string Latest = """
        {"tag_name": "v1.0.8", "html_url": "https://github.com/drzdez/teamsrec-capture/releases/tag/v1.0.8",
         "draft": false, "prerelease": false,
         "assets": [
           {"name": "notes.txt", "size": 10, "browser_download_url": "https://github.com/drzdez/teamsrec-capture/releases/download/v1.0.8/notes.txt"},
           {"name": "teamsrec-capture-1.0.8-x64.msi", "size": 46362624,
            "digest": "sha256:D2899080D4B925278F65EB9E4166A115EF72C407F050AF47B1DFC550BCFA9556",
            "browser_download_url": "https://github.com/drzdez/teamsrec-capture/releases/download/v1.0.8/teamsrec-capture-1.0.8-x64.msi"}
         ]}
        """;

    [Fact]
    public void The_latest_release_gives_its_msi_with_size_and_hash()
    {
        var rel = UpdateLogic.Parse(Latest);
        Assert.NotNull(rel);
        Assert.Equal("1.0.8", rel.Version);
        Assert.Equal("teamsrec-capture-1.0.8-x64.msi", rel.MsiName);
        Assert.Equal(46362624, rel.Size);
        Assert.Equal("d2899080d4b925278f65eb9e4166a115ef72c407f050af47b1dfc550bcfa9556", rel.Sha256);
        Assert.True(UpdateLogic.TrustedUrl(rel.MsiUrl));
    }

    [Fact]
    public void A_release_without_an_msi_or_a_prerelease_is_not_offered()
    {
        Assert.Null(UpdateLogic.Parse("""{"tag_name": "v1.0.8", "assets": []}"""));
        Assert.Null(UpdateLogic.Parse(Latest.Replace("\"prerelease\": false", "\"prerelease\": true")));
        Assert.Null(UpdateLogic.Parse(Latest.Replace("v1.0.8\", \"html_url", "nightly\", \"html_url")));
        Assert.False(UpdateLogic.TrustedUrl("https://example.com/teamsrec-capture-1.0.8-x64.msi"));
        Assert.False(UpdateLogic.TrustedUrl("https://github.com/someone/else/releases/download/v1/x-x64.msi"));
    }

    [Theory]
    [InlineData("1.0.8", "1.0.7", true)]
    [InlineData("1.1.0", "1.0.10", true)]
    [InlineData("1.0.10", "1.0.9", true)]  // numeric, not text
    [InlineData("1.0.7", "1.0.7", false)]
    [InlineData("1.0.6", "1.0.7", false)]
    [InlineData("garbage", "1.0.7", false)]
    public void Only_a_higher_version_is_newer(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateLogic.IsNewer(candidate, current));

    [Fact]
    public void A_check_is_due_at_start_then_once_a_day()
    {
        var now = new DateTime(2026, 10, 1, 9, 0, 0);
        Assert.True(UpdateLogic.Due(null, now));
        Assert.False(UpdateLogic.Due(now.AddHours(-23), now));
        Assert.True(UpdateLogic.Due(now.AddDays(-1), now));
        Assert.True(UpdateLogic.Due(now.AddDays(2), now));  // the clock went back: check rather than never
    }
}
