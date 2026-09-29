// Names and folders of a recording (docs/recording-format.md, "Adresář a pojmenování").
// Port of slug(), the stem/folder construction in App.start and App._rename_files of legacy/teamsrec.py.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Contract;

public static partial class Naming
{
    /// <summary>Title used when nothing better is known; also the slug fallback.</summary>
    public const string FallbackSlug = "teams-call";

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})_(\d{2})(\d{2})_(.*)$")]
    private static partial Regex StemPattern();

    /// <summary>Python slug(): NFKD, drop everything non-ASCII (the diacritics fall off as combining marks),
    /// runs of non-alphanumerics -> '-', trim '-', lower-case, cut to <paramref name="n"/> characters,
    /// fallback "teams-call". The cut happens after the trim, exactly like the prototype, so a slug cut in
    /// the middle of a word boundary can end with '-' - the transcribe side recomputes slugs the same way,
    /// so both must agree.</summary>
    public static string Slug(string? s, int n = 60)
    {
        string norm;
        try
        {
            norm = (s ?? "").Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)  // lone surrogates cannot be normalized; they are dropped below anyway
        {
            norm = s ?? "";
        }
        var ascii = new StringBuilder(norm.Length);
        foreach (var c in norm)
            if (c < 128)  // encode("ascii", "ignore")
                ascii.Append(c);
        var r = NonAlnum().Replace(ascii.ToString(), "-").Trim('-').ToLowerInvariant();
        if (n < 0) n = 0;
        r = r.Length > n ? r[..n] : r;
        return r.Length == 0 ? FallbackSlug : r;
    }

    /// <summary>"YYYY-MM-DD_HHMM_&lt;slug&gt;", local time of the recording start.</summary>
    public static string Stem(DateTime start, string title) =>
        start.ToString("yyyy-MM-dd'_'HHmm", CultureInfo.InvariantCulture) + "_" + Slug(title);

    /// <summary>&lt;out&gt;\YYYY\MM\&lt;stem&gt;: one folder per recording, filed by the local start time.</summary>
    public static string RecordingDir(string outDir, DateTime start, string stem) =>
        Path.Combine(outDir, start.ToString("yyyy", CultureInfo.InvariantCulture),
                     start.ToString("MM", CultureInfo.InvariantCulture), stem);

    /// <summary>The stem path the recorder works with: &lt;recording dir&gt;\&lt;stem&gt; (files are
    /// &lt;stem path&gt;_sys.wav, &lt;stem path&gt;.json ...), like the prototype's `stem` Path.</summary>
    public static string StemPath(string outDir, DateTime start, string title)
    {
        var stem = Stem(start, title);
        return Path.Combine(RecordingDir(outDir, start, stem), stem);
    }

    /// <summary>Parses a stem name back into its start time and a readable title ("-" -> " "), as the orphan
    /// recovery does for a recording cut by a crash. Null when the name does not follow the pattern.</summary>
    public static (DateTime Start, string Title)? ParseStem(string stemName)
    {
        var m = StemPattern().Match(stemName);
        if (!m.Success)
            return null;
        try
        {
            int G(int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
            var start = new DateTime(G(1), G(2), G(3), G(4), G(5), 0);
            return (start, m.Groups[6].Value.Replace("-", " "));
        }
        catch (ArgumentOutOfRangeException)  // 2026-13-45: looks like a stem but is not a date
        {
            return null;
        }
    }

    /// <summary>Port of _rename_files: the folder and every &lt;stem&gt;* file get the stem of the new title
    /// (same date/time part). Files must be closed by now (streams stopped, screen encoders finished).
    /// Returns the new stem path, or the old one when nothing changes or the target folder already exists
    /// (another recording with that name in the same minute - never merge two recordings).</summary>
    public static string RenameFiles(string stemPath, string newTitle)
    {
        var dir = Path.GetDirectoryName(stemPath) ?? throw new ArgumentException("stem path has no folder", nameof(stemPath));
        var oldName = Path.GetFileName(stemPath);
        var newName = $"{oldName[..Math.Min(15, oldName.Length)]}_{Slug(newTitle)}";  // YYYY-MM-DD_HHMM + new slug
        if (newName == oldName)
            return stemPath;
        var parent = Path.GetDirectoryName(dir) ?? throw new ArgumentException("recording folder has no parent", nameof(stemPath));
        var newDir = Path.Combine(parent, newName);
        if (Directory.Exists(newDir) || File.Exists(newDir))
        {
            Log.Warn($"cannot rename to {newName}: exists");
            return stemPath;
        }
        Directory.Move(dir, newDir);
        foreach (var f in Directory.GetFiles(newDir).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith(oldName, StringComparison.Ordinal))
                File.Move(f, Path.Combine(newDir, newName + name[oldName.Length..]));
        }
        Log.Info($"renamed {oldName} -> {newName}");
        return Path.Combine(newDir, newName);
    }

    /// <summary>A file name of the old stem carried over to the new stem ("&lt;old&gt;_sys.wav" ->
    /// "&lt;new&gt;_sys.wav"), for the track/screen/mix entries after <see cref="RenameFiles"/>.</summary>
    public static string Restem(string fileName, string oldStemName, string newStemName) =>
        fileName.StartsWith(oldStemName, StringComparison.Ordinal)
            ? newStemName + fileName[oldStemName.Length..]
            : fileName;
}
