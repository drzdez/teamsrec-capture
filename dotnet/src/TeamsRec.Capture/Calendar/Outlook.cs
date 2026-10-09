using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Calendar;

/// <summary>
/// The meeting from the classic Outlook running on this machine (COM, local: no network, no consent).
/// Capture uses it at call start for the title and the participants; the new Outlook has no COM, so there
/// is simply nothing to find (Meeting returns null, nothing else changes).
/// </summary>
public static class Outlook
{
    // Time window around the call start for a match by time: a meeting is joined up to 10 minutes early and
    // may overrun by 5 minutes (contract: "začátek -10 min ... konec +5 min").
    public const int BeforeS = 600;
    public const int AfterS = 300;
    // A match by title looks further (2 h): the window title carries the subject, which settles ad-hoc calls,
    // late starts and parallel meetings even when the clock alone would not.
    public const int TitleWindowS = 7200;
    // difflib.get_close_matches cutoff of the prototype: tolerates a typo or a shortened subject.
    public const double CloseMatchCutoff = 0.8;

    // Titles the Teams meeting window carries before/without a subject (en + cs) and the nav sections of the
    // main window: they say nothing about which meeting this is, so they must not be matched against subjects.
    // Copy of the prototype's TEAMS_GENERIC / TEAMS_NAV (is_generic_title); the Teams detection module owns the
    // canonical list, kept private here so this module compiles on its own.
    private static readonly HashSet<string> TeamsGeneric = new(StringComparer.Ordinal)
    {
        "meeting", "join meeting", "meeting compact view", "compact view", "call", "teams-call",
        "připojení ke schůzce", "kompaktní zobrazení schůzky", "kompaktní zobrazení", "hovor", "schůzka",
        "ovládací panel sdílení", "sharing control bar", "screen sharing toolbar", "sdílení obsahu",
    };
    private static readonly HashSet<string> TeamsNav = new(StringComparer.Ordinal)
    {
        "activity", "chat", "teams", "calendar", "calls", "files", "apps", "copilot",
        "onedrive", "meet", "viva", "planner", "microsoft teams",
        "aktivita", "týmy", "kalendář", "hovory", "soubory", "aplikace", "schůzka",
    };

    internal static bool IsGenericTitle(string? title)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        return t.Length == 0 || TeamsGeneric.Contains(t) || TeamsNav.Contains(t) || t.StartsWith("meeting compact", StringComparison.Ordinal);
    }

    private static readonly Regex NonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Title normalization for matching: diacritics dropped (NFKD, ASCII only), lower case, anything
    /// but letters and digits collapsed to single spaces ("Porada  týmu – WFMS" -> "porada tymu wfms").</summary>
    internal static string TitleNorm(string? s)
    {
        var decomposed = (s ?? "").Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (c < 128) sb.Append(c);  // Python: .encode("ascii", "ignore") drops the combining marks
        return NonAlnum.Replace(sb.ToString().ToLowerInvariant(), " ").Trim();
    }

    /// <summary>Calendar items whose span comes within <paramref name="windowS"/> of <paramref name="at"/>,
    /// closest start first (offered on the review page). Stable order for equal distances, like Python sorted.</summary>
    public static List<CalendarItem> CandidatesAt(IEnumerable<CalendarItem> items, DateTime at, int windowS = 3600)
    {
        var w = TimeSpan.FromSeconds(windowS);
        return items.Where(it => it.Start - w <= at && at <= it.End + w)
                    .OrderBy(it => Math.Abs((it.Start - at).TotalSeconds))
                    .ToList();
    }

    /// <summary>The calendar item for a call: by the meeting title first (the Teams window / file carries the
    /// subject, which settles ad-hoc calls and parallel meetings), else the item running at <paramref name="at"/>
    /// (start - 10 min .. end + 5 min; the one containing <paramref name="at"/> first, then Teams meetings, then
    /// the closest start). Returns (item, match) with match "title" | "time" | "". Pure (testable without Outlook).</summary>
    public static (CalendarItem? Item, string Match) Pick(List<CalendarItem> items, DateTime at, string? title,
                                                          int beforeS = BeforeS, int afterS = AfterS)
    {
        if (!string.IsNullOrEmpty(title) && !IsGenericTitle(title))
        {
            var t = TitleNorm(title);
            var near = CandidatesAt(items, at, TitleWindowS);
            var subjects = near.Select(it => TitleNorm(it.Subject)).ToList();
            // Exact, or one contains the other ("WFMS sync" in "WFMS sync | Microsoft Teams" after normalization).
            var hit = near.Where((it, i) => subjects[i].Length > 0 &&
                                            (subjects[i] == t || t.Contains(subjects[i], StringComparison.Ordinal) ||
                                             subjects[i].Contains(t, StringComparison.Ordinal)))
                          .ToList();
            if (hit.Count == 0)
            {
                var close = CloseMatch(t, subjects.Where(s => s.Length > 0), CloseMatchCutoff);
                if (close is not null)
                    hit = near.Where((it, i) => subjects[i] == close).ToList();
            }
            if (hit.Count > 0)
                return (hit[0], "title");
        }

        CalendarItem? best = null;
        (int Inside, int Teams, double Dist) bestKey = default;
        foreach (var it in items)
        {
            if (!(it.Start - TimeSpan.FromSeconds(beforeS) <= at && at <= it.End + TimeSpan.FromSeconds(afterS)))
                continue;
            var inside = it.Start <= at && at <= it.End;
            var key = (inside ? 0 : 1, it.Teams ? 0 : 1, Math.Abs((it.Start - at).TotalSeconds));
            if (best is null || key.CompareTo(bestKey) < 0)  // strict: the first of equal items wins, as in Python
            {
                best = it;
                bestKey = key;
            }
        }
        return best is null ? (null, "") : (best, "time");
    }

    /// <summary>difflib.get_close_matches(word, possibilities, n=1, cutoff): the possibility with the highest
    /// similarity ratio >= cutoff (ties: the larger string, like heapq.nlargest over (score, x)); null if none.</summary>
    internal static string? CloseMatch(string word, IEnumerable<string> possibilities, double cutoff)
    {
        string? best = null;
        double bestScore = -1;
        foreach (var x in possibilities)
        {
            var r = Ratio(x, word);  // SequenceMatcher(a=x, b=word), as difflib sets seq1 = x, seq2 = word
            if (r < cutoff) continue;
            if (r > bestScore || (r == bestScore && string.CompareOrdinal(x, best) > 0))
            {
                best = x;
                bestScore = r;
            }
        }
        return best;
    }

    /// <summary>difflib.SequenceMatcher(None, a, b).ratio() = 2*M/T, M = total size of the matching blocks
    /// found by recursively taking the longest common substring. No junk heuristic: Python applies "autojunk"
    /// only for b of 200+ characters, meeting titles are far shorter.</summary>
    internal static double Ratio(string a, string b)
    {
        var total = a.Length + b.Length;
        if (total == 0) return 1.0;
        return 2.0 * MatchingSize(a, 0, a.Length, b, 0, b.Length) / total;
    }

    private static int MatchingSize(string a, int alo, int ahi, string b, int blo, int bhi)
    {
        var (i, j, k) = LongestMatch(a, alo, ahi, b, blo, bhi);
        if (k == 0) return 0;
        var size = k;
        if (alo < i && blo < j) size += MatchingSize(a, alo, i, b, blo, j);
        if (i + k < ahi && j + k < bhi) size += MatchingSize(a, i + k, ahi, b, j + k, bhi);
        return size;
    }

    // find_longest_match without junk: the longest block, earliest in a, then earliest in b (same DP order
    // as difflib, so ties resolve identically and the ratio matches Python to the last digit).
    private static (int I, int J, int K) LongestMatch(string a, int alo, int ahi, string b, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var prev = new int[bhi - blo + 1];
        var cur = new int[bhi - blo + 1];
        for (var i = alo; i < ahi; i++)
        {
            Array.Clear(cur);
            for (var j = blo; j < bhi; j++)
            {
                if (a[i] != b[j]) continue;
                var k = (j > blo ? prev[j - blo - 1] : 0) + 1;
                cur[j - blo] = k;
                if (k > bestsize)
                {
                    besti = i - k + 1;
                    bestj = j - k + 1;
                    bestsize = k;
                }
            }
            (prev, cur) = (cur, prev);
        }
        return (besti, bestj, bestsize);
    }

    /// <summary>Month/day and day/month: Outlook reads the filter date by the regional settings (see Items).</summary>
    internal static readonly string[] DateOrders = { "MM/dd/yyyy", "dd/MM/yyyy" };

    /// <summary>Calendar items of one day from the classic Outlook on this machine (COM late binding; throws
    /// when Outlook is not installed / has no COM / no profile - Meeting() turns that into null).</summary>
    public static List<CalendarItem> Items(DateTime day)
    {
        var type = Type.GetTypeFromProgID("Outlook.Application")
                   ?? throw new InvalidOperationException("Outlook.Application is not registered (no classic Outlook)");
        dynamic app = Activator.CreateInstance(type)
                      ?? throw new InvalidOperationException("Outlook.Application could not be created");
        dynamic? ns = null, folder = null, all = null, restricted = null;
        var out_ = new List<CalendarItem>();
        try
        {
            ns = app.GetNamespace("MAPI");
            folder = ns.GetDefaultFolder(9);  // 9 = olFolderCalendar
            all = folder.Items;
            // IncludeRecurrences must be set on a collection sorted by [Start] before Restrict, otherwise the
            // occurrences of a recurring meeting (the daily standup) are missing.
            all.IncludeRecurrences = true;
            all.Sort("[Start]");
            // Restrict parses the date by the Windows regional settings: "10/07/2026" is 7 Oct in the US but
            // 10 July in Czech (2026-10-07: no meeting was found on days 1-12 of any month). Ask with both orders
            // and keep the items that really start on that day.
            var seen = new HashSet<(string, DateTime, DateTime)>();
            foreach (var fmt in DateOrders)
            {
                var d = day.ToString(fmt, CultureInfo.InvariantCulture);
                try
                {
                    restricted = all.Restrict($"[Start] >= '{d} 00:00' AND [Start] <= '{d} 23:59'");
                }
                catch (Exception)
                {
                    continue;  // a date this locale cannot read (day 13+ as a month)
                }
                // GetFirst/GetNext instead of Count/foreach: with IncludeRecurrences the Count is meaningless.
                object? cur = restricted.GetFirst();
                while (cur is not null)
                {
                    try
                    {
                        var item = FromCom(cur);
                        if (item is not null && item.Start.Date == day.Date && seen.Add((item.Subject, item.Start, item.End)))
                            out_.Add(item);
                    }
                    catch (Exception)
                    {
                        // one broken item (no access, odd type) must not cost the whole calendar
                    }
                    finally
                    {
                        Release(cur);
                    }
                    cur = restricted.GetNext();
                }
                Release((object?)restricted);
                restricted = null;
            }
            out_.Sort((a, b) => a.Start.CompareTo(b.Start));
            return out_;
        }
        finally
        {
            Release((object?)restricted);
            Release((object?)all);
            Release((object?)folder);
            Release((object?)ns);
            Release((object)app);
        }
    }

    /// <summary>Only what Outlook's object model guard leaves alone: subject, times, location and the
    /// participants' display names. The body, the organizer and any e-mail address (Recipient.Address, AddressEntry)
    /// make Outlook ask "a program is trying to access e-mail address information" whenever it thinks the antivirus
    /// is not current – 2026-10-09 right after logon, from the calendar offer that asks every 30 s. The Teams link is
    /// recognised by the location Outlook gives Teams meetings ("Microsoft Teams Meeting", "Schůzka Microsoft Teams").</summary>
    internal static bool IsTeamsLocation(string location) =>
        location.Contains("teams", StringComparison.OrdinalIgnoreCase);

    private static CalendarItem? FromCom(object com)
    {
        dynamic it = com;
        string location = (string?)it.Location ?? "";
        DateTime s = it.Start, e = it.End;
        var attendees = new List<string>();
        dynamic recips = it.Recipients;
        try
        {
            int n = recips.Count;
            for (var i = 1; i <= n; i++)  // COM collections are 1-based
            {
                dynamic r = recips.Item(i);
                try
                {
                    string? name = r.Name;
                    if (!string.IsNullOrEmpty(name)) attendees.Add(name);
                }
                finally
                {
                    Release((object)r);
                }
            }
        }
        finally
        {
            Release((object)recips);
        }
        return new CalendarItem(
            Subject: ((string?)it.Subject ?? "").Trim(),
            Start: new DateTime(s.Year, s.Month, s.Day, s.Hour, s.Minute, 0),  // minutes, like the prototype
            End: new DateTime(e.Year, e.Month, e.Day, e.Hour, e.Minute, 0),
            Organizer: "",  // guarded (see above): not read
            Attendees: attendees,
            Teams: IsTeamsLocation(location));
    }

    private static void Release(object? o)
    {
        try
        {
            if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
        catch (Exception)
        {
            // releasing is housekeeping only
        }
    }

    /// <summary>Like Meeting, with why there is no clear meeting: "" (found by title, or the only one at that time),
    /// "off" (calendar disabled), "unavailable" (Outlook / COM), "none" (no meeting near the call), "ambiguous"
    /// (picked by time while two or more meetings run then – the review page lets the user choose).</summary>
    public static (CalendarItem? Item, string Problem) Lookup(bool enabled, DateTime? at = null, string? title = null,
                                                              Func<DateTime, List<CalendarItem>>? items = null)
    {
        if (!enabled) return (null, "off");
        var when = at ?? DateTime.Now;
        List<CalendarItem> list;
        try
        {
            list = (items ?? Items)(when);
        }
        catch (Exception e)  // Outlook not running / new Outlook without COM / no profile
        {
            var msg = e.Message;
            Log.Info($"outlook calendar not available: {(msg.Length > 120 ? msg[..120] : msg)}");
            return (null, "unavailable");
        }
        var (it, match) = Pick(list, when, title);
        if (it is null)
        {
            Log.Info($"calendar: no meeting near {when:HH:mm} ({list.Count} items that day)");
            return (null, "none");
        }
        var running = list.Count(c => c.Start - TimeSpan.FromSeconds(BeforeS) <= when && when <= c.End);
        var problem = match == "time" && running >= 2 ? "ambiguous" : "";
        return (it with { Match = match, Candidates = CandidatesAt(list, when) }, problem);
    }

    /// <summary>The meeting from Outlook for the call starting now, with Match set ("title"/"time"). Null when
    /// the calendar is disabled, Outlook is off / not installed / the new Outlook without COM, or nothing
    /// matches. Never throws: the calendar is an extra, a recording must not depend on it.</summary>
    public static CalendarItem? Meeting(bool enabled, DateTime? at = null, string? title = null,
                                        Func<DateTime, List<CalendarItem>>? items = null)
    {
        if (!enabled) return null;
        var when = at ?? DateTime.Now;
        try
        {
            var list = (items ?? Items)(when);
            var (it, match) = Pick(list, when, title);
            // the other meetings near the start go with the match: at stop Outlook may be closed already
            return it is null ? null : it with { Match = match, Candidates = CandidatesAt(list, when) };
        }
        catch (Exception e)  // Outlook not running / new Outlook without COM / no profile
        {
            var msg = e.Message;
            Log.Info($"outlook calendar not available: {(msg.Length > 120 ? msg[..120] : msg)}");
            return null;
        }
    }
}
