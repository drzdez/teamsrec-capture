using System.Diagnostics;
using System.Text.Json;

namespace TeamsRec.Capture.App;

/// <summary>A job of the review server that ended (transcript, minutes…), for the balloon.</summary>
public sealed record ReviewJob(string Key, string Stem, string Title, bool Ok, string Text);

/// <summary>What the review server (teamsrec-transcribe) is doing: busy = a job runs or waits.</summary>
public sealed record ReviewJobsState(bool Busy, string Title, int Queued, IReadOnlyList<ReviewJob> Finished);

/// <summary>%TEMP%\teamsrec-review-jobs.json, written by the review server on every change of its jobs: the tray
/// icon turns yellow while it processes, and a balloon says when a recording is done – so its window can be closed
/// while it works. A file left behind by a server that is gone (no such pid) counts as idle.</summary>
public static class ReviewJobs
{
    public static string StatusPath => Path.Combine(Path.GetTempPath(), "teamsrec-review-jobs.json");

    public static ReviewJobsState? Parse(string json, Func<int, bool> alive)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var n) ? n : 0;
            bool running = Bool(root, "running") && pid > 0 && alive(pid);
            string title = "";
            if (root.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Object)
                title = Str(cur, "title") is { Length: > 0 } t ? t : Str(cur, "stem");
            var finished = new List<ReviewJob>();
            if (root.TryGetProperty("finished", out var fin) && fin.ValueKind == JsonValueKind.Array)
                foreach (var f in fin.EnumerateArray())
                    finished.Add(new ReviewJob($"{pid}|{Raw(f, "id")}|{Str(f, "at")}", Str(f, "stem"),
                                               Str(f, "title") is { Length: > 0 } ft ? ft : Str(f, "stem"),
                                               Bool(f, "ok"), Str(f, "text")));
            int queued = root.TryGetProperty("queued", out var q) && q.TryGetInt32(out var qn) ? qn : 0;
            return new ReviewJobsState(running && Bool(root, "busy"), title, running ? queued : 0, finished);
        }
        catch (JsonException)
        {
            return null;  // caught mid-write: the next poll reads it whole
        }
    }

    public static ReviewJobsState? Read(string? path = null)
    {
        try
        {
            using var fs = new FileStream(path ?? StatusPath, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return Parse(reader.ReadToEnd(), IsAlive);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;  // no review server ran since boot, or it is being replaced right now
        }
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The jobs that ended since the last call. `seen` = null on the first call: everything already in
    /// the file is history (no balloons for jobs that ended before the tray started).</summary>
    public static IReadOnlyList<ReviewJob> NewlyFinished(ReviewJobsState state, ref HashSet<string>? seen)
    {
        if (seen is null)
        {
            seen = state.Finished.Select(f => f.Key).ToHashSet();
            return Array.Empty<ReviewJob>();
        }
        var fresh = new List<ReviewJob>();
        foreach (var f in state.Finished)
            if (seen.Add(f.Key))
                fresh.Add(f);
        return fresh;
    }

    public static string Balloon(ReviewJob j)
    {
        var what = j.Title.Length > 0 ? j.Title : "úloha";
        return j.Ok ? $"Zpracováno: {what} – {j.Text}" : $"Zpracování se nepodařilo: {what} – {j.Text}";
    }

    public static string Tooltip(ReviewJobsState s) =>
        "teamsrec: zpracovává se" + (s.Title.Length > 0 ? $" – {s.Title}" : "") + (s.Queued > 0 ? $" (+{s.Queued} ve frontě)" : "");

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Raw(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetRawText() : "";
}
