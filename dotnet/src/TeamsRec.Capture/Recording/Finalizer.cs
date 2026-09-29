using TeamsRec.Capture.Contract;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Recording;

/// <summary>Turns a stopped recording into a finished one: mix, final title (window / calendar re-match),
/// rename, sidecar written last (it marks the recording as complete), tray notification.
/// Python: App._finalize, App._rematch.</summary>
public sealed class Finalizer(Func<DateTime, string?, CalendarItem?> outlookMeeting, INotifier notifier, IClock clock)
{
    /// <summary>ffmpeg lookup, replaceable so tests run without ffmpeg (null = no mix).</summary>
    public Func<string?> FindFfmpeg { get; init; } = Mixer.FindFfmpeg;

    /// <summary>Other calendar items near the start, for the review page's calendar.candidates (Python:
    /// outlook_meeting attached them to the match). CalendarItem cannot carry them, so they are looked up here;
    /// null = none. Must not throw (the finalizer runs on a background thread).</summary>
    public Func<CalendarItem, DateTime, IEnumerable<CalendarItem>>? CalendarCandidates { get; init; }

    /// <summary>No .wav among the files: the devices vanished and nothing was ever written, so the app
    /// deletes the folder instead of finalizing.</summary>
    public static bool NoAudioFile(IEnumerable<string> files) =>
        !files.Any(p => string.Equals(Path.GetExtension(p), ".wav", StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the sidecar and returns its path. Uses only what the recording itself carries
    /// (<paramref name="info"/>): the next recording may already be running, e.g. after an on-site upgrade.</summary>
    public string Finalize(StoppedRecording r, string reason, RecordingInfo info)
    {
        var stem = r.StemPath;
        var files = r.Files.ToList();
        var screens = r.Screens.ToList();
        var tracks = r.Tracks.ToDictionary(kv => kv.Key, kv => kv.Value);
        string? mix = null;
        var audio = files.Where(IsWav).ToList();
        var ff = Mixer.MixWithFfmpeg ? FindFfmpeg() : null;
        if (ff is not null && audio.Count > 0)
            mix = Mixer.Mix(ff, stem, audio);

        var source = string.IsNullOrEmpty(info.Source) ? "live" : info.Source;
        var title = info.Title ?? "";
        var cal = info.Calendar;
        var titleSource = string.IsNullOrEmpty(info.TitleSource) ? "generic" : info.TitleSource;
        var titlesSeen = (info.TitlesSeen ?? new HashSet<string>()).Order(StringComparer.Ordinal).ToList();
        if (IsGenericTitle(title))  // the window got its real subject only later in the call
        {
            var better = GuessTitles(titlesSeen).FirstOrDefault();
            if (better is not null)
            {
                Log.Info($"title '{title}' replaced by '{better}' seen during the call");
                title = better;
                titleSource = "window";
            }
        }
        (cal, title, titleSource, var changed) = Rematch(r.Started, cal, title, titleSource, titlesSeen);

        var stemName = Path.GetFileName(stem);
        // The prototype compared against stem.name[17:], one character past the slug start, so it always
        // tried to rename; RenameFiles is a no-op for the same name, so comparing the real slug is equivalent.
        var currentSlug = stemName.Length > 16 ? stemName[16..] : "";
        if (changed || (titleSource == "window" && Naming.Slug(title) != currentSlug))
        {
            var newStem = Naming.RenameFiles(stem, title);
            if (newStem != stem)
            {
                var newName = Path.GetFileName(newStem);
                var newDir = Path.GetDirectoryName(newStem)!;
                string Renamed(string file) =>
                    file.StartsWith(stemName, StringComparison.Ordinal) ? newName + file[stemName.Length..] : file;
                files = files.Select(p => Path.Combine(newDir, Renamed(Path.GetFileName(p)))).ToList();
                screens = screens.Select(s => s with { File = Renamed(s.File) }).ToList();
                tracks = tracks.ToDictionary(kv => kv.Key, kv => kv.Value with { File = Renamed(kv.Value.File) });
                stem = newStem;
                stemName = newName;
                mix = mix is not null ? stem + "_mix.wav" : null;  // the mix file was renamed with the rest
            }
        }

        var dir = Path.GetDirectoryName(stem)!;
        var sidecar = new Sidecar
        {
            Format = Versions.FormatVersion,
            App = Versions.AppName,
            AppVersion = Versions.AppVersion,
            Title = title,
            Slug = Naming.Slug(title),
            Source = source,
            Start = r.Started,
            End = clock.Now,
            DurationS = (int)Math.Round(r.DurationS),
            StopReason = StopReasons.ToContract(reason),
            TitleSource = titleSource,
            AudioReopens = r.Reopens,
            Tracks = tracks.Where(kv => File.Exists(Path.Combine(dir, kv.Value.File)))
                           .ToDictionary(kv => kv.Key, kv => SidecarTrack.From(kv.Value)),
            TeamsWindowsSeen = titlesSeen,
        };
        if (!string.IsNullOrEmpty(info.Continues))  // this live recording took over from an on-site one
            sidecar.Continues = info.Continues;
        if (!string.IsNullOrEmpty(info.CallApp))  // a call outside Teams
            sidecar.CallApp = info.CallApp;
        if (mix is not null)
            sidecar.Mix = new SidecarMix { File = Path.GetFileName(mix), SampleRate = Mixer.MixSampleRate, Channels = Mixer.MixChannels };
        if (cal is not null)
        {
            sidecar.Participants = SidecarCalendar.ParticipantsOf(cal);
            // contract: source outlook, match (default time), status auto, candidates = other items nearby
            sidecar.Calendar = SidecarCalendar.From(cal, CalendarCandidates?.Invoke(cal, r.Started));
            Log.Info($"calendar: '{cal.Subject}' (by {cal.Match}), {cal.Attendees.Count} participants");
        }
        if (!(r.HeardSys || r.HeardMic))  // every buffer was digital silence: the device delivered nothing
        {
            sidecar.AudioSilent = true;
            Log.Warn($"{stemName}: no audible audio on any track ({r.Reopens} reopens), marked audio_silent");
        }
        var kept = screens.Where(s =>
        {
            var p = Path.Combine(dir, s.File);
            return File.Exists(p) && new FileInfo(p).Length > 0;
        }).ToList();
        if (kept.Count > 0)
        {
            sidecar.Screens = kept.Select(SidecarScreen.From).ToList();
            Log.Info("screens: " + string.Join(", ", kept.Select(s => $"{s.File} ({s.Frames} frames)")));
        }
        var jpath = stem + ".json";  // written last: marks the recording as complete
        sidecar.Write(jpath);
        if (sidecar.AudioSilent == true)
            notifier.Notify($"{stemName}: žádný zvuk (zařízení nedodalo data), nahrávka se nebude zpracovávat.");
        else
            notifier.Notify($"Saved {stemName} ({Math.Round(r.DurationS / 60)} min)");
        return jpath;
    }

    /// <summary>At the end of the call the Teams window has carried its real subject for a while. If the
    /// calendar link was only guessed by time (or missing), match again by that subject; a different meeting
    /// wins (a 10:59 start looked like the 10:30 meeting, but the window said 'Debrief').</summary>
    internal (CalendarItem? Cal, string Title, string TitleSource, bool Changed) Rematch(
        DateTime started, CalendarItem? cal, string title, string titleSource, IEnumerable<string> titlesSeen)
    {
        var seen = GuessTitles(titlesSeen.Order(StringComparer.Ordinal)).Where(t => !IsGenericTitle(t)).ToList();
        if (seen.Count == 0 || (cal is not null && cal.Match == "title"))
            return (cal, title, titleSource, false);
        foreach (var windowTitle in seen)
        {
            var better = outlookMeeting(started, windowTitle);
            if (better is not null && better.Match == "title")
            {
                if (cal is null || better.Subject != cal.Subject)
                {
                    Log.Info($"calendar re-matched by the window title '{windowTitle}': '{better.Subject}' " +
                             $"(was '{cal?.Subject}')");
                    return (better, better.Subject, "calendar", true);
                }
                return (cal, title, titleSource, false);
            }
        }
        return (cal, title, titleSource, false);
    }

    private static bool IsWav(string p) =>
        string.Equals(Path.GetExtension(p), ".wav", StringComparison.OrdinalIgnoreCase);

    // Window-title helpers (Python: TEAMS_NAV, TEAMS_GENERIC, is_generic_title, guess_titles). Kept private
    // here so this module compiles on its own; the integration step may point them to the Teams module.
    private static readonly HashSet<string> TeamsNav = new(StringComparer.Ordinal)
    {
        "activity", "chat", "teams", "calendar", "calls", "files", "apps", "copilot",
        "onedrive", "meet", "viva", "planner", "microsoft teams",
        "aktivita", "týmy", "kalendář", "hovory", "soubory", "aplikace", "schůzka",
    };

    // Titles the meeting window carries before/without a subject (en + cs); a later window title is better.
    private static readonly HashSet<string> TeamsGeneric = new(StringComparer.Ordinal)
    {
        "meeting", "join meeting", "meeting compact view", "compact view", "call", "teams-call",
        "připojení ke schůzce", "kompaktní zobrazení schůzky", "kompaktní zobrazení", "hovor", "schůzka",
        "ovládací panel sdílení", "sharing control bar", "screen sharing toolbar", "sdílení obsahu",
    };

    internal static bool IsGenericTitle(string? title)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        return t.Length == 0 || TeamsGeneric.Contains(t) || TeamsNav.Contains(t) || t.StartsWith("meeting compact");
    }

    /// <summary>Subjects of meeting windows ('&lt;subject&gt; | Microsoft Teams'), nav/generic titles skipped,
    /// in order.</summary>
    internal static List<string> GuessTitles(IEnumerable<string> titles)
    {
        var outList = new List<string>();
        foreach (var t in titles)
        {
            var parts = t.Split('|').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2 || parts[^1].ToLowerInvariant() != "microsoft teams")
                continue;
            var head = parts[0];
            if (IsGenericTitle(head) || TeamsNav.Contains(head.ToLowerInvariant()) || outList.Contains(head))
                continue;
            outList.Add(head);
        }
        return outList;
    }
}
