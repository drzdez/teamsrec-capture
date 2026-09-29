using System.Text.RegularExpressions;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Detection;

/// <summary>
/// Pure call-detection rules ported from the prototype: which Teams window titles mean "in a meeting",
/// which mean "only the join dialog", how a meeting subject is guessed, and whether a call runs in
/// another app (Zoom, Webex, a browser meeting...). No OS access here; lookups are injected.
/// </summary>
public static class CallDetector
{
    /// <summary>Nav sections of the main Teams window (en + cs), so they are not mistaken for a meeting title.</summary>
    public static readonly IReadOnlySet<string> TeamsNav = new HashSet<string>(StringComparer.Ordinal)
    {
        "activity", "chat", "teams", "calendar", "calls", "files", "apps", "copilot",
        "onedrive", "meet", "viva", "planner", "microsoft teams",
        "aktivita", "týmy", "kalendář", "hovory", "soubory", "aplikace", "schůzka",
    };

    /// <summary>Titles the meeting window carries before/without a subject (en + cs); a later window title is better.</summary>
    public static readonly IReadOnlySet<string> TeamsGeneric = new HashSet<string>(StringComparer.Ordinal)
    {
        "meeting", "join meeting", "meeting compact view", "compact view", "call", "teams-call",
        "připojení ke schůzce", "kompaktní zobrazení schůzky", "kompaktní zobrazení", "hovor", "schůzka",
        "ovládací panel sdílení", "sharing control bar", "screen sharing toolbar", "sdílení obsahu",
    };

    /// <summary>
    /// The pre-join dialog ("Připojení ke schůzce | &lt;subject&gt; | Microsoft Teams"): Teams already holds the
    /// microphone for the device preview, but the call has not started (2026-09-21: 6.5 minutes of a join
    /// screen recorded, no audio on it).
    /// </summary>
    public static readonly IReadOnlySet<string> TeamsPrejoin = new HashSet<string>(StringComparer.Ordinal)
    {
        "připojení ke schůzce", "pripojeni ke schuzce", "připojit se ke schůzce",
        "join meeting", "meeting join", "pre-join", "prejoin",
    };

    /// <summary>Exe names of the Teams client (new and classic).</summary>
    public static readonly IReadOnlySet<string> TeamsExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ms-teams.exe", "teams.exe",
    };

    /// <summary>
    /// ConsentStore identifier fragment (packaged app name or exe) -> the app a call runs in.
    /// An ordered list, not a dictionary: the first matching fragment wins, as in the prototype's dict order.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> CallAppIds =
    [
        new("zoom.exe", "Zoom"), new("webexmta.exe", "Webex"), new("ciscocollabhost.exe", "Webex"),
        new("atmgr.exe", "Webex"), new("slack", "Slack"), new("discord.exe", "Discord"),
        new("whatsappdesktop", "WhatsApp"), new("whatsapp.exe", "WhatsApp"), new("skype", "Skype"),
        new("signal.exe", "Signal"),
    ];

    /// <summary>Browser exe -> display name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Browsers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["chrome.exe"] = "Chrome", ["msedge.exe"] = "Edge", ["firefox.exe"] = "Firefox",
        ["brave.exe"] = "Brave", ["opera.exe"] = "Opera",
    };

    /// <summary>
    /// A browser holding the microphone is a call only with one of these in a window title (dictation, a voice
    /// search or a recorder in a tab is not).
    /// </summary>
    public static readonly Regex WebMeeting = new(
        @"google meet|meet\.google\.com|^meet\s*[-–]|\bzoom\b|\bwebex\b|\bjitsi\b|whereby|microsoft teams|\bteams\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // "... - Google Chrome" suffixes; Edge puts a zero-width space into "Microsoft​ Edge".
    private static readonly Regex BrowserSuffix = new(
        @"\s+[-–]\s+(google chrome|microsoft​? edge|mozilla firefox|brave|opera)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string TeamsSuffix = "microsoft teams";

    private static string Lower(string s) => s.ToLowerInvariant();

    private static string[] TitleParts(string title) =>
        title.Split('|').Select(p => p.Trim()).ToArray();

    /// <summary>Empty, generic ("Meeting", "Kompaktní zobrazení") or a nav section: not a meeting subject.</summary>
    public static bool IsGenericTitle(string? title)
    {
        var t = Lower((title ?? "").Trim());
        return t.Length == 0 || TeamsGeneric.Contains(t) || TeamsNav.Contains(t) || t.StartsWith("meeting compact", StringComparison.Ordinal);
    }

    /// <summary>The join dialog, which exists only before the call is joined.</summary>
    public static bool IsPrejoinTitle(string title)
    {
        var parts = TitleParts(title);
        return parts.Length >= 2 && Lower(parts[^1]) == TeamsSuffix && TeamsPrejoin.Contains(Lower(parts[0]));
    }

    /// <summary>
    /// A window that exists only once the call is joined: "&lt;subject&gt; | Microsoft Teams", the compact view,
    /// the sharing toolbar. Nav sections ("Calendar | …"), chats and the join dialog are not.
    /// </summary>
    public static bool IsMeetingWindow(string title)
    {
        var parts = TitleParts(title);
        if (parts.Length < 2 || Lower(parts[^1]) != TeamsSuffix)
            return false;
        var head = Lower(parts[0]);
        return !TeamsPrejoin.Contains(head) && !TeamsNav.Contains(head);
    }

    /// <summary>
    /// True while Teams shows the join screen and no meeting window: the microphone is held by the device
    /// preview of that dialog, so recording now would capture the dialog, not a call.
    /// </summary>
    public static bool PrejoinOnly(IEnumerable<string> titles)
    {
        var list = titles as IReadOnlyCollection<string> ?? titles.ToList();
        return list.Any(IsPrejoinTitle) && !list.Any(IsMeetingWindow);
    }

    /// <summary>Subjects of meeting windows ('&lt;subject&gt; | Microsoft Teams'), nav/generic titles skipped, in order.</summary>
    public static List<string> GuessTitles(IEnumerable<string> titles)
    {
        var output = new List<string>();
        foreach (var t in titles)
        {
            var parts = TitleParts(t);
            if (parts.Length < 2 || Lower(parts[^1]) != TeamsSuffix)
                continue;
            var head = parts[0];
            if (IsGenericTitle(head) || TeamsNav.Contains(Lower(head)) || output.Contains(head))
                continue;
            output.Add(head);
        }
        return output;
    }

    /// <summary>The first meeting-window subject, if any.</summary>
    public static string? GuessMeetingTitle(IEnumerable<string> titles)
    {
        var found = GuessTitles(titles);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>'Meet – abc-defg-hij - Google Chrome' -> 'Meet – abc-defg-hij'.</summary>
    public static string CleanWebTitle(string title) => BrowserSuffix.Replace(title, "").Trim();

    /// <summary>
    /// A call in an app other than Teams. Known call apps count as soon as they hold the microphone; a browser
    /// only with a meeting in a window title. Null otherwise.
    /// </summary>
    /// <param name="users">Microphone holders (lower case), as from <see cref="MicUsers.Current"/>.</param>
    /// <param name="titlesOf">Window titles of the given exe names, as <see cref="WindowTitles.OfExes"/>.</param>
    public static CallApp? OtherCall(ISet<string> users, Func<IEnumerable<string>, List<string>> titlesOf)
    {
        if (users.Any(u => u.Contains("teams", StringComparison.Ordinal)))
            return null;  // Teams has its own path (join screen, window capture)
        var sorted = users.OrderBy(u => u, StringComparer.Ordinal).ToList();
        foreach (var ident in sorted)
            foreach (var (frag, app) in CallAppIds)
                if (ident.Contains(frag, StringComparison.Ordinal))
                    return new CallApp(app, ident, "");
        foreach (var ident in sorted)
        {
            if (!Browsers.TryGetValue(ident, out var browser))
                continue;
            var hit = titlesOf([ident]).FirstOrDefault(t => WebMeeting.IsMatch(t));
            if (hit is not null)
            {
                var clean = CleanWebTitle(hit);
                return new CallApp($"{browser} ({clean})", ident, clean);
            }
        }
        return null;
    }

    /// <summary><see cref="OtherCall(ISet{string}, Func{IEnumerable{string}, List{string}})"/> against the live system.</summary>
    public static CallApp? OtherCall() => OtherCall(MicUsers.Current(), WindowTitles.OfExes);
}
