// The sidecar <stem>.json (docs/recording-format.md, "Sidecar"). Written last: its existence marks the recording
// as complete for teamsrec-transcribe. Keys, value formats and optionality follow what the Python prototype writes
// in App._finalize and recover_orphans (legacy/teamsrec.py), because the Python transcribe side reads them.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Contract;

/// <summary>Maps the app's internal stop reasons to the contract enum (Python App.STOP_REASONS).</summary>
public static class StopReasons
{
    public const string CallEnded = "call_ended";
    public const string MaxDuration = "max_duration";
    public const string UserStop = "user_stop";
    public const string Silence = "silence";
    public const string AppQuit = "app_quit";
    public const string OnsiteUpgraded = "onsite_upgraded";
    public const string AppCrash = "app_crash";  // orphan recovery: the app died mid-recording

    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["call ended"] = CallEnded,
        ["max duration"] = MaxDuration,
        ["tray stop"] = UserStop,
        ["silence"] = Silence,
        ["quit"] = AppQuit,
        ["upgraded"] = OnsiteUpgraded,
    };

    /// <summary>Unknown reasons count as a user stop, as in the prototype (.get(reason, "user_stop")).</summary>
    public static string ToContract(string? reason) =>
        reason is not null && Map.TryGetValue(reason, out var r) ? r : UserStop;
}

public sealed class Sidecar
{
    [JsonPropertyName("format")] public int Format { get; set; } = Versions.FormatVersion;
    [JsonPropertyName("app")] public string App { get; set; } = Versions.AppName;
    [JsonPropertyName("app_version")] public string AppVersion { get; set; } = Versions.AppVersion;
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "live";

    [JsonPropertyName("start"), JsonConverter(typeof(IsoSecondsConverter))]
    public DateTime Start { get; set; }

    [JsonPropertyName("end"), JsonConverter(typeof(IsoSecondsConverter))]
    public DateTime End { get; set; }

    [JsonPropertyName("duration_s")] public int DurationS { get; set; }
    [JsonPropertyName("stop_reason")] public string StopReason { get; set; } = StopReasons.UserStop;

    // Orphan recovery does not know these, so the recovered sidecar omits them (as the prototype does).
    [JsonPropertyName("title_source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TitleSource { get; set; }

    [JsonPropertyName("audio_reopens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AudioReopens { get; set; }

    [JsonPropertyName("recovered"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Recovered { get; set; }

    /// <summary>"sys" / "mic"; only tracks whose file exists (a failed mic open leaves no file).</summary>
    [JsonPropertyName("tracks")] public Dictionary<string, SidecarTrack> Tracks { get; set; } = new();

    [JsonPropertyName("teams_windows_seen")] public List<string> TeamsWindowsSeen { get; set; } = new();

    [JsonPropertyName("continues"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Continues { get; set; }

    [JsonPropertyName("call_app"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallApp { get; set; }

    [JsonPropertyName("mix"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SidecarMix? Mix { get; set; }

    [JsonPropertyName("participants"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SidecarParticipant>? Participants { get; set; }

    [JsonPropertyName("calendar"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SidecarCalendar? Calendar { get; set; }

    /// <summary>True = every buffer was digital silence (the device delivered nothing); only written when true.</summary>
    [JsonPropertyName("audio_silent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AudioSilent { get; set; }

    [JsonPropertyName("screens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SidecarScreen>? Screens { get; set; }

    /// <summary>Set by teamsrec-transcribe purge-audio (a date string); capture only carries it through.</summary>
    [JsonPropertyName("audio_purged"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AudioPurged { get; set; }

    /// <summary>Keys this class does not know (language, origin_file, fields added by transcribe ...), kept so a
    /// read-modify-write does not lose them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Python round(): half to even, as the prototype rounds duration_s and the screen offsets.</summary>
    public static int RoundSeconds(double seconds) => (int)Math.Round(seconds, MidpointRounding.ToEven);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        // ensure_ascii=False: Czech titles stay readable in the file (the relaxed encoder still escapes
        // characters outside the BMP, e.g. emoji, as \uXXXX pairs - valid JSON, read back identically).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new PyFloatConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions) + "\n";

    /// <summary>Writes the sidecar (UTF-8 without BOM, trailing newline). Via a temp file and a move, so a
    /// reader never sees a half-written sidecar - its existence alone means "recording complete".</summary>
    public void Write(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    public static Sidecar Parse(string json) =>
        JsonSerializer.Deserialize<Sidecar>(json, JsonOptions) ?? throw new InvalidDataException("empty sidecar");

    public static Sidecar Read(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));
}

/// <summary>Contract object `track` (tracks.sys / tracks.mic).</summary>
public sealed class SidecarTrack
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; }
    [JsonPropertyName("channels")] public int Channels { get; set; }

    /// <summary>Recording device name (the prototype keeps it in rec.tracks and writes it; the review page and
    /// the mic-change check use it). Orphan recovery does not know it.</summary>
    [JsonPropertyName("device"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; set; }

    public static SidecarTrack From(TrackInfo t) =>
        new() { File = t.File, SampleRate = t.SampleRate, Channels = t.Channels, Device = t.Device };
}

/// <summary>The mono 16 kHz mix for ASR.</summary>
public sealed class SidecarMix
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; } = 16000;
    [JsonPropertyName("channels")] public int Channels { get; set; } = 1;
}

public sealed class SidecarParticipant
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>"calendar" | "manual".</summary>
    [JsonPropertyName("source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("email"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; set; }

    [JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }
}

/// <summary>The Outlook meeting linked to the recording. Times to the minute, like Outlook shows them.</summary>
public sealed class SidecarCalendar
{
    [JsonPropertyName("source")] public string Source { get; set; } = "outlook";
    [JsonPropertyName("subject")] public string? Subject { get; set; }     // Python writes None as null
    [JsonPropertyName("organizer")] public string? Organizer { get; set; }

    [JsonPropertyName("start"), JsonConverter(typeof(IsoMinutesConverter))]
    public DateTime Start { get; set; }

    [JsonPropertyName("end"), JsonConverter(typeof(IsoMinutesConverter))]
    public DateTime End { get; set; }

    /// <summary>"title" | "time" | "manual"; "time" is only a guess.</summary>
    [JsonPropertyName("match")] public string Match { get; set; } = "time";

    /// <summary>"auto" | "confirmed" (the review page confirms).</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "auto";

    /// <summary>Other meetings around the start, offered on the review page to re-link.</summary>
    [JsonPropertyName("candidates")] public List<SidecarCandidate> Candidates { get; set; } = new();

    /// <summary>The calendar block of _finalize; `candidates` are the other items near the start (max 5,
    /// the matched one excluded - as outlook_meeting builds them).</summary>
    public static SidecarCalendar From(CalendarItem cal, IEnumerable<CalendarItem>? candidates = null) => new()
    {
        Subject = cal.Subject,
        Organizer = cal.Organizer,
        Start = cal.Start,
        End = cal.End,
        Match = string.IsNullOrEmpty(cal.Match) ? "time" : cal.Match,
        Candidates = (candidates ?? [])
            // Python compared identity (c is not it); the matched item carries Match, so a record compare would
            // never exclude it - compare what identifies the meeting instead.
            .Where(c => !(c.Subject == cal.Subject && c.Start == cal.Start && c.End == cal.End))
            .Take(5)
            .Select(SidecarCandidate.From)
            .ToList(),
    };

    /// <summary>participants[] from the calendar attendees (source "calendar").</summary>
    public static List<SidecarParticipant> ParticipantsOf(CalendarItem cal) =>
        cal.Attendees.Select(n => new SidecarParticipant { Name = n, Source = "calendar" }).ToList();
}

public sealed class SidecarCandidate
{
    [JsonPropertyName("subject")] public string Subject { get; set; } = "";

    [JsonPropertyName("start"), JsonConverter(typeof(IsoMinutesConverter))]
    public DateTime Start { get; set; }

    [JsonPropertyName("end"), JsonConverter(typeof(IsoMinutesConverter))]
    public DateTime End { get; set; }

    [JsonPropertyName("teams")] public bool Teams { get; set; }

    public static SidecarCandidate From(CalendarItem c) =>
        new() { Subject = c.Subject, Start = c.Start, End = c.End, Teams = c.Teams };
}

/// <summary>One captured Teams window video (screens[]).</summary>
public sealed class SidecarScreen
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("fps")] public int Fps { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("start_offset_s")] public double StartOffsetS { get; set; }

    /// <summary>Missing for a video recovered after a crash (its capture never reported the end).</summary>
    [JsonPropertyName("end_offset_s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EndOffsetS { get; set; }

    /// <summary>-1 = unknown (recovered after a crash).</summary>
    [JsonPropertyName("frames")] public int Frames { get; set; }

    [JsonPropertyName("titles")] public List<string> Titles { get; set; } = new();

    [JsonPropertyName("recovered"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Recovered { get; set; }

    public static SidecarScreen From(ScreenInfo s) => new()
    {
        File = s.File, Fps = s.Fps, Width = s.Width, Height = s.Height,
        StartOffsetS = s.StartOffsetS, EndOffsetS = s.Recovered ? null : s.EndOffsetS, Frames = s.Frames,
        Titles = s.Titles.ToList(), Recovered = s.Recovered ? true : null,
    };
}

/// <summary>datetime.isoformat(timespec="seconds"): local time, no offset, no fraction.</summary>
public sealed class IsoSecondsConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IsoTime.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
}

/// <summary>datetime.isoformat(timespec="minutes"): the calendar block ("2026-09-14T08:30").</summary>
public sealed class IsoMinutesConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IsoTime.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
}

internal static class IsoTime
{
    /// <summary>Accepts minutes, seconds or fractions (other tools may write any isoformat); the value is
    /// taken as local wall-clock time, never shifted by a zone.</summary>
    public static DateTime Parse(string? s)
    {
        if (string.IsNullOrEmpty(s))
            throw new JsonException("missing time");
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                              DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var t))
            return DateTime.SpecifyKind(t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t, DateTimeKind.Unspecified);
        throw new JsonException($"bad time '{s}'");
    }
}

/// <summary>Writes doubles the way Python's json does (repr): 0.0 and 1748.0 keep their ".0", so the offsets
/// stay floats for the Python reader and the file looks like the prototype's.</summary>
public sealed class PyFloatConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDouble();

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (!double.IsFinite(value))
        {
            writer.WriteNumberValue(value);  // throws: NaN/Infinity is not JSON, fail loudly
            return;
        }
        var s = value.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E'))
            s = s.ToLowerInvariant();  // 1E+16 -> 1e+16 (Python repr)
        else if (!s.Contains('.'))
            s += ".0";
        writer.WriteRawValue(s);
    }
}
