using TeamsRec.Capture.Detection;

namespace TeamsRec.Capture.Tests;

public class DetectionTests
{
    [Fact]
    public void PrejoinDetection()
    {
        // The join dialog holds the microphone but is not a call (2026-09-21: 6.5 min of the dialog recorded).
        const string join = "Připojení ke schůzce | Archi week plan | Microsoft Teams";
        Assert.True(CallDetector.PrejoinOnly([join, "Calendar | Microsoft Teams", "Chat | Jan Novák | Microsoft Teams"]));
        Assert.False(CallDetector.PrejoinOnly([join, "Kompaktní zobrazení schůzky | Archi week plan | Microsoft Teams"]));
        Assert.False(CallDetector.PrejoinOnly([join, "Ovládací panel sdílení | Microsoft Teams"]));
        Assert.False(CallDetector.PrejoinOnly(["Archi standup | Microsoft Teams"]));
        Assert.False(CallDetector.PrejoinOnly(["Schůzka s: Jan Novák | Microsoft Teams"]));
        Assert.False(CallDetector.PrejoinOnly([]));
    }

    private static readonly Dictionary<string, string[]> BrowserTitles = new()
    {
        ["chrome.exe"] = ["Meet – abc-defg-hij - Google Chrome", "Inbox - Gmail - Google Chrome"],
        ["msedge.exe"] = ["YouTube - Microsoft​ Edge"],
    };

    private static List<string> TitlesOf(IEnumerable<string> exes) =>
        exes.SelectMany(e => BrowserTitles.TryGetValue(e, out var t) ? t : Array.Empty<string>()).ToList();

    private static HashSet<string> Set(params string[] s) => new(s);

    [Fact]
    public void CallsInOtherAppsAreRecognised()
    {
        Assert.Equal("Zoom", CallDetector.OtherCall(Set("zoom.exe"), TitlesOf)?.App);
        Assert.Equal("WhatsApp", CallDetector.OtherCall(Set("5319275a.whatsappdesktop_cv1g1gvanyjgm"), TitlesOf)?.App);
        var web = CallDetector.OtherCall(Set("chrome.exe"), TitlesOf);
        Assert.NotNull(web);
        Assert.Equal("Meet – abc-defg-hij", web.Title);
        Assert.Equal("chrome.exe", web.Id);
        Assert.Equal("Chrome (Meet – abc-defg-hij)", web.App);
        // a browser without a meeting is not a call (dictation, search)
        Assert.Null(CallDetector.OtherCall(Set("msedge.exe"), TitlesOf));
        // another recorder is not a call
        Assert.Null(CallDetector.OtherCall(Set("read_ai_desktop.exe"), TitlesOf));
        // Teams has its own path
        Assert.Null(CallDetector.OtherCall(Set("msteams_8wekyb3d8bbwe", "zoom.exe"), TitlesOf));
        Assert.Null(CallDetector.OtherCall(Set(), TitlesOf));
    }

    [Fact]
    public void KnownAppWinsOverBrowserMeeting()
    {
        var c = CallDetector.OtherCall(Set("chrome.exe", "zoom.exe"), TitlesOf);
        Assert.Equal("Zoom", c?.App);
        Assert.Equal("", c?.Title);
    }

    [Theory]
    [InlineData("Meet – abc-defg-hij - Google Chrome", "Meet – abc-defg-hij")]
    [InlineData("Webex - Microsoft​ Edge", "Webex")]
    [InlineData("Webex - Microsoft Edge", "Webex")]
    [InlineData("Jitsi Meet — Mozilla Firefox", "Jitsi Meet — Mozilla Firefox")]
    [InlineData("Jitsi Meet - Mozilla Firefox", "Jitsi Meet")]
    public void CleanWebTitle(string raw, string expected) =>
        Assert.Equal(expected, CallDetector.CleanWebTitle(raw));

    [Fact]
    public void GuessTitlesSkipsNavAndGeneric()
    {
        var titles = new[]
        {
            "Calendar | Microsoft Teams",
            "Chat | Jan Novák | Microsoft Teams",
            "Meeting | Microsoft Teams",
            "Kompaktní zobrazení schůzky | Microsoft Teams",
            "Archi standup | Microsoft Teams",
            "Archi standup | Microsoft Teams",
            "Some other app window",
            "Schůzka s: Jan Novák | Microsoft Teams",
        };
        Assert.Equal(new[] { "Archi standup", "Schůzka s: Jan Novák" }, CallDetector.GuessTitles(titles));
        Assert.Equal("Archi standup", CallDetector.GuessMeetingTitle(titles));
    }

    [Fact]
    public void GuessTitleCompactView()
    {
        // compact view title carries no subject of its own; the subject comes from the meeting window
        Assert.Null(CallDetector.GuessMeetingTitle(["Meeting compact view | Microsoft Teams", "Compact view | Microsoft Teams"]));
        Assert.Equal("Archi week plan",
            CallDetector.GuessMeetingTitle(["Meeting compact view (1) | Microsoft Teams", "Archi week plan | Microsoft Teams"]));
        Assert.Null(CallDetector.GuessMeetingTitle([]));
    }

    [Fact]
    public void GuessTitleScheduledWith()
    {
        Assert.Equal("Schůzka s: Jan Novák", CallDetector.GuessMeetingTitle(["Schůzka s: Jan Novák | Microsoft Teams"]));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("Meeting", true)]
    [InlineData("Kalendář", true)]
    [InlineData("meeting compact view x", true)]
    [InlineData("Archi standup", false)]
    public void IsGenericTitle(string? title, bool expected) =>
        Assert.Equal(expected, CallDetector.IsGenericTitle(title));

    [Fact]
    public void MeetingWindowAndPrejoinTitles()
    {
        Assert.True(CallDetector.IsPrejoinTitle("Připojení ke schůzce | X | Microsoft Teams"));
        Assert.False(CallDetector.IsPrejoinTitle("Připojení ke schůzce"));
        Assert.True(CallDetector.IsMeetingWindow("Archi standup | Microsoft Teams"));
        Assert.False(CallDetector.IsMeetingWindow("Calendar | Microsoft Teams"));
        Assert.False(CallDetector.IsMeetingWindow("Join meeting | X | Microsoft Teams"));
        Assert.False(CallDetector.IsMeetingWindow("Archi standup"));
    }

    [Fact]
    public void MicUsersFiltersOwnProcessAndReleasedKeys()
    {
        var entries = new[]
        {
            new MicConsentEntry("Zoom.exe-like", "Zoom.exe", 100, 0),
            new MicConsentEntry(@"C:#Python#python.exe", "python.exe", 100, 0),
            new MicConsentEntry(@"C:#App#teamsrec-capture.exe", "teamsrec-capture.exe", 100, 0),
            new MicConsentEntry(@"C:#Chrome#chrome.exe", "chrome.exe", 100, 200),  // released
            new MicConsentEntry("Never", "never.exe", 0, 0),                        // never used
        };
        Assert.Equal(new HashSet<string> { "zoom.exe" }, MicUsers.Current(entries));
    }

    [Fact]
    public void TeamsInUseNeedsAnActiveTeamsKey()
    {
        Assert.True(MicUsers.TeamsInUse([new MicConsentEntry("MSTeams_8wekyb3d8bbwe", "MSTeams_8wekyb3d8bbwe", 5, 0)]));
        Assert.False(MicUsers.TeamsInUse([new MicConsentEntry("MSTeams_8wekyb3d8bbwe", "MSTeams_8wekyb3d8bbwe", 5, 9)]));
        Assert.False(MicUsers.TeamsInUse([new MicConsentEntry("zoom.exe", "zoom.exe", 5, 0)]));
        Assert.False(MicUsers.TeamsInUse([]));
    }
}
