using System.Buffers.Binary;
using System.Text.Json;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Recording;

namespace TeamsRec.Capture.Tests;

public sealed class FinalizerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "teamsrec-fin-" + Guid.NewGuid().ToString("N"));

    public FinalizerTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<string> Messages { get; } = [];
        public List<string> Stems { get; } = [];
        public void Notify(string message) => Messages.Add(message);
        public void NotifyRecording(string message, string stem) { Messages.Add(message); Stems.Add(stem); }
        public void Beep(bool error = false) { }
    }

    private sealed class FakeClock(DateTime now) : IClock
    {
        public DateTime Now => now;
        public double Seconds => 0;
    }

    /// <summary>A canonical 16-bit PCM WAV; with <paramref name="truncated"/> the RIFF/data sizes are 0 as
    /// when the writing process was killed.</summary>
    private static void WriteWav(string path, int rate, int channels, int frames, bool truncated = false)
    {
        var data = frames * channels * 2;
        var head = new byte[44];
        "RIFF"u8.CopyTo(head.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), truncated ? 0u : (uint)(36 + data));
        "WAVE"u8.CopyTo(head.AsSpan(8));
        "fmt "u8.CopyTo(head.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(28), (uint)(rate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(34), 16);
        "data"u8.CopyTo(head.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(40), truncated ? 0u : (uint)data);
        using var f = File.Create(path);
        f.Write(head);
        f.Write(new byte[data]);
    }

    private (string Stem, string Wav) MicRecording(string name)
    {
        var dir = Path.Combine(_tmp, "2026", "09", name);
        Directory.CreateDirectory(dir);
        var stem = Path.Combine(dir, name);
        var wav = stem + "_mic.wav";
        WriteWav(wav, 16000, 1, 16000);
        return (stem, wav);
    }

    private static StoppedRecording Stopped(string stem, string wav, double dur, bool heardSys, bool heardMic,
                                            DateTime started) =>
        new(stem, started, dur, 0, heardSys, heardMic,
            new Dictionary<string, TrackInfo> { ["mic"] = new(Path.GetFileName(wav), 16000, 1, "Mikrofon") },
            [wav], []);

    private static RecordingInfo Info(string title, string source, string? continues = null,
                                      string titleSource = "manual", CalendarItem? cal = null,
                                      IReadOnlySet<string>? seen = null) =>
        new(title, cal, titleSource, seen ?? new HashSet<string>(), continues, source, null);

    private Finalizer NewFinalizer(FakeNotifier n, Func<DateTime, string?, CalendarItem?>? outlook = null) =>
        new(outlook ?? ((_, _) => null), n, new FakeClock(new DateTime(2026, 9, 25, 9, 10, 0)))
        {
            FindFfmpeg = () => null,  // no mix in tests
        };

    private static JsonElement ReadJson(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    [Fact]
    public void Finalize_uses_the_recordings_own_metadata()
    {
        // The upgrade starts the next recording at once, so the sidecar must come from RecordingInfo only.
        var (stem, wav) = MicRecording("2026-09-25_0900_prvni-cast");
        var n = new FakeNotifier();
        var fin = NewFinalizer(n);
        var started = new DateTime(2026, 9, 25, 9, 0, 0);

        var json = fin.Finalize(Stopped(stem, wav, 600.0, false, true, started), "upgraded",
                                Info("První část", "onsite"));
        Assert.Equal(stem + ".json", json);
        var meta = ReadJson(json);
        Assert.Equal("První část", meta.GetProperty("title").GetString());
        Assert.Equal("onsite", meta.GetProperty("source").GetString());
        Assert.Equal("onsite_upgraded", meta.GetProperty("stop_reason").GetString());
        Assert.Equal(600, meta.GetProperty("duration_s").GetInt32());
        Assert.True(meta.GetProperty("tracks").TryGetProperty("mic", out _));
        Assert.Contains(n.Messages, m => m == "Saved 2026-09-25_0900_prvni-cast (10 min)");
        Assert.Contains("2026-09-25_0900_prvni-cast", n.Stems);  // a click on the balloon opens this recording

        json = fin.Finalize(Stopped(stem, wav, 60.0, false, true, started), "call ended",
                            Info("Druhá část", "live", continues: Path.GetFileName(stem)));
        meta = ReadJson(json);
        Assert.Equal(Path.GetFileName(stem), meta.GetProperty("continues").GetString());
        Assert.Equal("call_ended", meta.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void Audio_silent_is_set_only_when_no_track_heard_anything()
    {
        var (stem, wav) = MicRecording("2026-09-25_0900_ticho");
        var n = new FakeNotifier();
        var fin = NewFinalizer(n);
        var started = new DateTime(2026, 9, 25, 9, 0, 0);

        var meta = ReadJson(fin.Finalize(Stopped(stem, wav, 120, false, false, started), "tray stop",
                                         Info("Ticho", "live")));
        Assert.True(meta.GetProperty("audio_silent").GetBoolean());
        Assert.Contains("žádný zvuk", n.Messages[^1]);

        meta = ReadJson(fin.Finalize(Stopped(stem, wav, 120, true, false, started), "tray stop",
                                     Info("Ticho", "live")));
        Assert.True(!meta.TryGetProperty("audio_silent", out var s) || s.ValueKind == JsonValueKind.Null);
        Assert.StartsWith("Saved ", n.Messages[^1]);
    }

    [Fact]
    public void Calendar_matched_only_by_time_is_rematched_by_the_window_title()
    {
        var (stem, wav) = MicRecording("2026-09-25_0900_standup");
        var started = new DateTime(2026, 9, 25, 9, 0, 0);
        var byTime = new CalendarItem("Standup", started, started.AddMinutes(30), "Jana", ["Jana"], true, "time");
        var debrief = new CalendarItem("Debrief", started, started.AddMinutes(30), "Petr", ["Petr", "Eva"], true, "title");
        var fin = NewFinalizer(new FakeNotifier(), (_, t) => t == "Debrief" ? debrief : null);

        var json = fin.Finalize(Stopped(stem, wav, 600, true, true, started), "call ended",
            Info("Standup", "live", titleSource: "calendar", cal: byTime,
                 seen: new HashSet<string> { "Debrief | Microsoft Teams", "Chat | Microsoft Teams" }));
        var meta = ReadJson(json);
        Assert.Equal("Debrief", meta.GetProperty("title").GetString());
        Assert.Equal("calendar", meta.GetProperty("title_source").GetString());
        Assert.Equal("2026-09-25_0900_debrief", Path.GetFileNameWithoutExtension(json));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(json)!, "2026-09-25_0900_debrief_mic.wav")));
        Assert.Equal(2, meta.GetProperty("participants").GetArrayLength());
    }

    [Fact]
    public void At_the_end_of_the_session_the_sidecar_is_written_at_once_without_mix_and_calendar_lookup()
    {
        // Windows logs off / shuts down mid-recording: the meeting's own metadata is kept, Outlook is not asked
        var (stem, wav) = MicRecording("2026-09-25_0900_standup");
        var started = new DateTime(2026, 9, 25, 9, 0, 0);
        var byTime = new CalendarItem("Standup", started, started.AddMinutes(30), "Jana", ["Jana"], true, "time");
        var asked = 0;
        var fin = new Finalizer((_, _) => { asked++; return null; }, new FakeNotifier(),
                                new FakeClock(new DateTime(2026, 9, 25, 9, 10, 0))) { FindFfmpeg = () => "ffmpeg.exe" };
        var json = fin.Finalize(Stopped(stem, wav, 600, true, true, started), "session end",
            Info("Standup", "live", titleSource: "calendar", cal: byTime,
                 seen: new HashSet<string> { "Debrief | Microsoft Teams" }), quick: true);
        var meta = ReadJson(json);
        Assert.Equal("session_end", meta.GetProperty("stop_reason").GetString());
        Assert.Equal("Standup", meta.GetProperty("title").GetString());
        Assert.False(meta.TryGetProperty("mix", out _), "the mix is left to teamsrec-transcribe");
        Assert.Equal(0, asked);
        Assert.Equal(1, meta.GetProperty("participants").GetArrayLength());
    }

    [Fact]
    public void Orphan_with_the_microphone_only_is_recovered_too()
    {
        // a call whose output never appeared, or an on-site meeting: no _sys.wav
        var dir = Path.Combine(_tmp, "2026", "10", "2026-10-08_1032_postgresql-rollout");
        Directory.CreateDirectory(dir);
        var stem = Path.Combine(dir, "2026-10-08_1032_postgresql-rollout");
        WriteWav(stem + "_mic.wav", 16000, 1, 16000 * 20, truncated: true);
        Assert.Equal(1, Orphans.Recover(_tmp, () => null));
        var meta = ReadJson(stem + ".json");
        Assert.Equal(20, meta.GetProperty("duration_s").GetInt32());
        Assert.True(meta.GetProperty("tracks").TryGetProperty("mic", out _));
        Assert.False(meta.GetProperty("tracks").TryGetProperty("sys", out _));
    }

    [Fact]
    public void Rematch_keeps_a_calendar_already_matched_by_title()
    {
        var started = new DateTime(2026, 9, 25, 9, 0, 0);
        var cal = new CalendarItem("Sync", started, started.AddHours(1), "Jana", [], true, "title");
        var fin = NewFinalizer(new FakeNotifier(), (_, _) => throw new InvalidOperationException("not asked"));
        var r = fin.Rematch(started, cal, "Sync", "calendar", ["Other | Microsoft Teams"]);
        Assert.False(r.Changed);
        Assert.Same(cal, r.Cal);
    }

    [Fact]
    public void No_audio_file_means_nothing_to_keep()
    {
        Assert.True(Finalizer.NoAudioFile(["a_screen1.mp4"]));
        Assert.True(Finalizer.NoAudioFile([]));
        Assert.False(Finalizer.NoAudioFile(["a_screen1.mp4", "a_SYS.WAV"]));
    }

    [Fact]
    public void Truncated_wav_header_is_repaired_from_the_file_size()
    {
        var p = Path.Combine(_tmp, "cut_sys.wav");
        WriteWav(p, 48000, 2, 4800, truncated: true);
        Assert.True(Orphans.RepairWav(p));
        var bytes = File.ReadAllBytes(p);
        Assert.Equal((uint)(bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((uint)(bytes.Length - 44), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
        Assert.True(Orphans.RepairWav(p));  // already consistent
        var h = Orphans.ReadHeader(p);
        Assert.Equal(4800, h.Frames);

        var junk = Path.Combine(_tmp, "junk.wav");
        File.WriteAllBytes(junk, new byte[100]);
        Assert.False(Orphans.RepairWav(junk));
    }

    [Fact]
    public void Orphan_recording_gets_a_crash_sidecar_and_short_ones_are_deleted()
    {
        var dir = Path.Combine(_tmp, "2026", "09", "2026-09-25_0900_tydenni-sync");
        Directory.CreateDirectory(dir);
        var stem = Path.Combine(dir, "2026-09-25_0900_tydenni-sync");
        WriteWav(stem + "_sys.wav", 16000, 1, 16000 * 10, truncated: true);
        WriteWav(stem + "_mic.wav", 16000, 1, 16000 * 10, truncated: true);

        var shortDir = Path.Combine(_tmp, "2026", "09", "2026-09-25_1000_kratka");
        Directory.CreateDirectory(shortDir);
        WriteWav(Path.Combine(shortDir, "2026-09-25_1000_kratka_sys.wav"), 16000, 1, 16000, truncated: true);

        Assert.Equal(1, Orphans.Recover(_tmp, () => null));
        var meta = ReadJson(stem + ".json");
        Assert.Equal("app_crash", meta.GetProperty("stop_reason").GetString());
        Assert.True(meta.GetProperty("recovered").GetBoolean());
        Assert.Equal("tydenni sync", meta.GetProperty("title").GetString());
        Assert.Equal(10, meta.GetProperty("duration_s").GetInt32());
        Assert.StartsWith("2026-09-25T09:00:00", meta.GetProperty("start").GetString());
        Assert.True(meta.GetProperty("tracks").TryGetProperty("mic", out _));
        Assert.False(Directory.Exists(shortDir));

        Assert.Equal(0, Orphans.Recover(_tmp, () => null));  // has a sidecar now
    }
}
