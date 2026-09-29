using System.Text;
using System.Text.Json;
using TeamsRec.Capture.Contract;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Tests;

public sealed class ContractTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "teamsrec-tests", "contract-" + Guid.NewGuid().ToString("N"));

    public ContractTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ slug / stem

    [Theory]
    [InlineData("Týdenní sync", "tydenni-sync")]
    [InlineData("Příliš žluťoučký kůň úpěl ďábelské ódy", "prilis-zlutoucky-kun-upel-dabelske-ody")]
    [InlineData("  WFMS sync | Microsoft Teams  ", "wfms-sync-microsoft-teams")]
    [InlineData("Hello, World!", "hello-world")]
    [InlineData("", "teams-call")]
    [InlineData("!!! ???", "teams-call")]
    [InlineData("日本語", "teams-call")]
    [InlineData("A&B <C>", "a-b-c")]
    [InlineData("ﬁle №5", "file-no5")]  // NFKD folds the ligature and the numero sign
    public void Slug_MatchesPython(string title, string expected) => Assert.Equal(expected, Naming.Slug(title));

    [Fact]
    public void Slug_NullIsFallback() => Assert.Equal("teams-call", Naming.Slug(null));

    [Fact]
    public void Slug_CutsAfterTrim_LikePython()
    {
        var s = Naming.Slug(new string('a', 59) + " b");
        Assert.Equal(new string('a', 59) + "-", s);  // Python cuts after strip("-"), so a trailing '-' can stay
        Assert.Equal(60, Naming.Slug(new string('x', 200)).Length);
        Assert.Equal("abc", Naming.Slug("abcdef", 3));
        Assert.Equal("teams-call", Naming.Slug("abc", 0));
    }

    [Fact]
    public void Stem_And_RecordingDir()
    {
        var start = new DateTime(2026, 9, 3, 14, 0, 12);
        var stem = Naming.Stem(start, "Týdenní sync");
        Assert.Equal("2026-09-03_1400_tydenni-sync", stem);
        Assert.Equal(Path.Combine(@"D:\rec", "2026", "09", stem), Naming.RecordingDir(@"D:\rec", start, stem));
        Assert.Equal(Path.Combine(@"D:\rec", "2026", "09", stem, stem), Naming.StemPath(@"D:\rec", start, "Týdenní sync"));
        Assert.Equal("2026-01-05_0907_teams-call", Naming.Stem(new DateTime(2026, 1, 5, 9, 7, 59), ""));
    }

    [Fact]
    public void ParseStem_RecoversStartAndTitle()
    {
        var p = Naming.ParseStem("2026-09-25_0900_prvni-cast");
        Assert.NotNull(p);
        Assert.Equal(new DateTime(2026, 9, 25, 9, 0, 0), p.Value.Start);
        Assert.Equal("prvni cast", p.Value.Title);
        Assert.Null(Naming.ParseStem("recording"));
        Assert.Null(Naming.ParseStem("2026-13-45_9999_x"));
    }

    // ------------------------------------------------------------------ rename

    private string MakeRecording(string stem, params string[] suffixes)
    {
        var dir = Path.Combine(_tmp, "2026", "09", stem);
        Directory.CreateDirectory(dir);
        foreach (var s in suffixes)
            File.WriteAllText(Path.Combine(dir, stem + s), s);
        return Path.Combine(dir, stem);
    }

    [Fact]
    public void RenameFiles_MovesFolderAndStemFiles_KeepsOthers()
    {
        var stemPath = MakeRecording("2026-09-29_0915_schuzka", "_sys.wav", "_mic.wav", "_mix.wav", "_screen1.mp4");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(stemPath)!, "notes.txt"), "keep");

        var result = Naming.RenameFiles(stemPath, "WFMS sync");

        var newDir = Path.Combine(_tmp, "2026", "09", "2026-09-29_0915_wfms-sync");
        Assert.Equal(Path.Combine(newDir, "2026-09-29_0915_wfms-sync"), result);
        Assert.False(Directory.Exists(Path.GetDirectoryName(stemPath)));
        foreach (var s in new[] { "_sys.wav", "_mic.wav", "_mix.wav", "_screen1.mp4" })
        {
            Assert.True(File.Exists(result + s), s);
            Assert.Equal(s, File.ReadAllText(result + s));  // same content, only the name changed
        }
        Assert.Equal("keep", File.ReadAllText(Path.Combine(newDir, "notes.txt")));
        Assert.Equal(5, Directory.GetFiles(newDir).Length);
    }

    [Fact]
    public void RenameFiles_TargetExists_KeepsOld()
    {
        var stemPath = MakeRecording("2026-09-29_0915_schuzka", "_sys.wav");
        MakeRecording("2026-09-29_0915_wfms-sync", "_sys.wav");  // another recording in the same minute

        Assert.Equal(stemPath, Naming.RenameFiles(stemPath, "WFMS sync"));
        Assert.True(File.Exists(stemPath + "_sys.wav"));
    }

    [Fact]
    public void RenameFiles_SameSlug_NoChange()
    {
        var stemPath = MakeRecording("2026-09-29_0915_wfms-sync", "_sys.wav");
        Assert.Equal(stemPath, Naming.RenameFiles(stemPath, "WFMS  Sync!"));
        Assert.True(File.Exists(stemPath + "_sys.wav"));
    }

    [Fact]
    public void Restem_ReplacesPrefixOnly()
    {
        Assert.Equal("new_sys.wav", Naming.Restem("old_sys.wav", "old", "new"));
        Assert.Equal("other.wav", Naming.Restem("other.wav", "old", "new"));
    }

    // ------------------------------------------------------------------ stop reasons

    [Theory]
    [InlineData("call ended", "call_ended")]
    [InlineData("max duration", "max_duration")]
    [InlineData("tray stop", "user_stop")]
    [InlineData("silence", "silence")]
    [InlineData("quit", "app_quit")]
    [InlineData("upgraded", "onsite_upgraded")]
    [InlineData("something else", "user_stop")]
    [InlineData(null, "user_stop")]
    public void StopReasons_Map(string? reason, string expected) => Assert.Equal(expected, StopReasons.ToContract(reason));

    // ------------------------------------------------------------------ sidecar

    private static Sidecar FullSidecar()
    {
        const string stem = "2026-09-29_0915_tydenni-sync";
        var cal = new CalendarItem("Týdenní sync", new DateTime(2026, 9, 29, 9, 15, 0), new DateTime(2026, 9, 29, 10, 0, 0),
                                   "Jana Nováková", ["Jana Nováková", "Petr Svoboda"], true, "title");
        var other = new CalendarItem("Debrief", new DateTime(2026, 9, 29, 10, 0, 0), new DateTime(2026, 9, 29, 10, 30, 0),
                                     "Petr Svoboda", [], false);
        return new Sidecar
        {
            Title = "Týdenní sync",
            Slug = Naming.Slug("Týdenní sync"),
            Source = "live",
            Start = new DateTime(2026, 9, 29, 9, 15, 45, 123),
            End = new DateTime(2026, 9, 29, 9, 58, 2),
            DurationS = Sidecar.RoundSeconds(2536.5),  // half to even, like Python round()
            StopReason = StopReasons.ToContract("call ended"),
            TitleSource = "calendar",
            AudioReopens = 2,
            Tracks = new()
            {
                ["sys"] = SidecarTrack.From(new TrackInfo(stem + "_sys.wav", 48000, 2, "Reproduktory (Realtek)")),
                ["mic"] = SidecarTrack.From(new TrackInfo(stem + "_mic.wav", 48000, 1, "Mikrofon (BT-W5)")),
            },
            TeamsWindowsSeen = ["Týdenní sync | Microsoft Teams"],
            Continues = "2026-09-29_0900_na-miste",
            CallApp = null,
            Mix = new SidecarMix { File = stem + "_mix.wav" },
            Participants = SidecarCalendar.ParticipantsOf(cal),
            Calendar = SidecarCalendar.From(cal, [cal, other]),
            AudioSilent = true,
            Screens = [SidecarScreen.From(new ScreenInfo(stem + "_screen1.mp4", 2, 1600, 900, 0.0, 1748.0, 3496,
                                                         ["Připojení ke schůzce | Microsoft Teams", "Týdenní sync | Microsoft Teams"]))],
        };
    }

    [Fact]
    public void Sidecar_Write_ExactKeysAndFormats()
    {
        var path = Path.Combine(_tmp, "s.json");
        FullSidecar().Write(path);

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        var text = Encoding.UTF8.GetString(bytes);
        Assert.EndsWith("}\n", text);
        Assert.DoesNotContain("\r", text);
        Assert.Contains("\"title\": \"Týdenní sync\"", text);         // non-ASCII kept readable
        Assert.Contains("\n  \"format\": 1,", text);                   // indent 2
        Assert.Contains("\"start\": \"2026-09-29T09:15:45\"", text);   // seconds, no fraction, no offset
        Assert.Contains("\"end\": \"2026-09-29T09:58:02\"", text);
        Assert.Contains("\"start\": \"2026-09-29T09:15\"", text);      // calendar to minutes
        Assert.Contains("\"start_offset_s\": 0.0", text);              // floats stay floats
        Assert.Contains("\"end_offset_s\": 1748.0", text);
        Assert.False(File.Exists(path + ".tmp"));

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var keys = root.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[]
        {
            "format", "app", "app_version", "title", "slug", "source", "start", "end", "duration_s", "stop_reason",
            "title_source", "audio_reopens", "tracks", "teams_windows_seen", "continues", "mix", "participants",
            "calendar", "audio_silent", "screens",
        }, keys);  // call_app, recovered, audio_purged are null -> omitted
        Assert.Equal(1, root.GetProperty("format").GetInt32());
        Assert.Equal("teamsrec-capture", root.GetProperty("app").GetString());
        Assert.Equal(2536, root.GetProperty("duration_s").GetInt32());
        Assert.Equal("call_ended", root.GetProperty("stop_reason").GetString());

        var sys = root.GetProperty("tracks").GetProperty("sys");
        Assert.Equal(new[] { "file", "sample_rate", "channels", "device" }, sys.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "file", "sample_rate", "channels" },
                     root.GetProperty("mix").EnumerateObject().Select(p => p.Name));
        Assert.Equal(16000, root.GetProperty("mix").GetProperty("sample_rate").GetInt32());
        Assert.Equal(new[] { "name", "source" },
                     root.GetProperty("participants")[0].EnumerateObject().Select(p => p.Name));

        var cal = root.GetProperty("calendar");
        Assert.Equal(new[] { "source", "subject", "organizer", "start", "end", "match", "status", "candidates" },
                     cal.EnumerateObject().Select(p => p.Name));
        Assert.Equal("title", cal.GetProperty("match").GetString());
        Assert.Equal("auto", cal.GetProperty("status").GetString());
        var cands = cal.GetProperty("candidates");
        Assert.Equal(1, cands.GetArrayLength());  // the matched meeting is not its own candidate
        Assert.Equal(new[] { "subject", "start", "end", "teams" }, cands[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("2026-09-29T10:30", cands[0].GetProperty("end").GetString());

        Assert.Equal(new[] { "file", "fps", "width", "height", "start_offset_s", "end_offset_s", "frames", "titles" },
                     root.GetProperty("screens")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Sidecar_RoundTrip()
    {
        var path = Path.Combine(_tmp, "rt.json");
        var original = FullSidecar();
        original.Write(path);
        var back = Sidecar.Read(path);

        Assert.Equal("Týdenní sync", back.Title);
        Assert.Equal("tydenni-sync", back.Slug);
        Assert.Equal(new DateTime(2026, 9, 29, 9, 15, 45), back.Start);  // fraction dropped on write
        Assert.Equal(original.End, back.End);
        Assert.Equal(2, back.AudioReopens);
        Assert.Equal("Mikrofon (BT-W5)", back.Tracks["mic"].Device);
        Assert.Equal(new DateTime(2026, 9, 29, 10, 0, 0), back.Calendar!.End);
        Assert.Equal("Debrief", back.Calendar.Candidates.Single().Subject);
        Assert.Equal(1748.0, back.Screens![0].EndOffsetS);
        Assert.True(back.AudioSilent);
        Assert.Null(back.CallApp);
        Assert.Equal(File.ReadAllText(path), back.ToJson());  // byte-identical rewrite
    }

    [Fact]
    public void Sidecar_Minimal_OmitsOptional_AndKeepsUnknownKeys()
    {
        // what recover_orphans writes: no title_source/audio_reopens, recovered=true, app_crash
        var s = new Sidecar
        {
            Title = "prvni cast", Slug = "prvni-cast",
            Start = new DateTime(2026, 9, 25, 9, 0, 0), End = new DateTime(2026, 9, 25, 9, 10, 0), DurationS = 600,
            StopReason = StopReasons.AppCrash, Recovered = true,
            Tracks = new() { ["mic"] = new SidecarTrack { File = "x_mic.wav", SampleRate = 48000, Channels = 1 } },
        };
        using (var doc = JsonDocument.Parse(s.ToJson()))
        {
            Assert.Equal(new[]
            {
                "format", "app", "app_version", "title", "slug", "source", "start", "end", "duration_s", "stop_reason",
                "recovered", "tracks", "teams_windows_seen",
            }, doc.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(new[] { "file", "sample_rate", "channels" },
                         doc.RootElement.GetProperty("tracks").GetProperty("mic").EnumerateObject().Select(p => p.Name));
            Assert.Equal("[]", doc.RootElement.GetProperty("teams_windows_seen").GetRawText());
        }

        // fields owned by transcribe survive a read-modify-write
        var json = s.ToJson().TrimEnd().TrimEnd('}') + ",\n  \"language\": \"cs\",\n  \"audio_purged\": \"2026-09-30\"\n}\n";
        var back = Sidecar.Parse(json);
        Assert.Equal("2026-09-30", back.AudioPurged);
        Assert.Contains("\"language\": \"cs\"", back.ToJson());
    }

    [Fact]
    public void Sidecar_ReadsPythonWrittenFile()
    {
        // as the prototype writes it (Windows text mode: CRLF), calendar organizer None -> null
        const string py = "{\r\n  \"format\": 1,\r\n  \"app\": \"teamsrec-capture\",\r\n  \"app_version\": \"0.1.0\",\r\n" +
                          "  \"title\": \"Porada\",\r\n  \"slug\": \"porada\",\r\n  \"source\": \"live\",\r\n" +
                          "  \"start\": \"2026-09-03T14:00:12\",\r\n  \"end\": \"2026-09-03T14:47:50\",\r\n" +
                          "  \"duration_s\": 2858,\r\n  \"stop_reason\": \"call_ended\",\r\n  \"tracks\": {},\r\n" +
                          "  \"teams_windows_seen\": [],\r\n  \"calendar\": {\"source\": \"outlook\", \"subject\": \"Porada\", " +
                          "\"organizer\": null, \"start\": \"2026-09-03T14:00\", \"end\": \"2026-09-03T15:00\", " +
                          "\"match\": \"time\", \"status\": \"auto\", \"candidates\": []}\r\n}\r\n";
        var s = Sidecar.Parse(py);
        Assert.Equal(new DateTime(2026, 9, 3, 14, 47, 50), s.End);
        Assert.Null(s.Calendar!.Organizer);
        Assert.Contains("\"organizer\": null", s.ToJson());  // required calendar keys stay even when null
        Assert.Empty(s.Tracks);
    }

    [Fact]
    public void Calendar_candidates_leave_out_the_matched_meeting_even_with_its_match_set()
    {
        var at = new DateTime(2026, 9, 14, 8, 30, 0);
        var raw = new CalendarItem("Standup", at, at.AddMinutes(15), "Eva", ["Eva"], true);
        var matched = raw with { Match = "title" };  // outlook_meeting sets match on the chosen item
        var others = Enumerable.Range(1, 7)
            .Select(i => new CalendarItem($"M{i}", at.AddMinutes(i), at.AddMinutes(30 + i), "", [], false));
        var c = SidecarCalendar.From(matched, new[] { raw }.Concat(others));
        Assert.Equal("title", c.Match);
        Assert.Equal(["M1", "M2", "M3", "M4", "M5"], c.Candidates.Select(x => x.Subject));
    }
}
