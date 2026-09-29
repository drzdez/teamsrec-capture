using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TeamsRec.Capture.Config;

/// <summary>
/// Edits the shared TOML in place. The file is also hand-edited and read by teamsrec-transcribe, so a settings
/// save must keep comments, key order and every key we do not manage - a serializer round-trip would lose them.
/// </summary>
public static class TomlEditor
{
    private static readonly Regex HeadRe = new(@"^\s*\[([^\]]+)\]", RegexOptions.CultureInvariant);
    private static readonly Regex KeyRe = new(@"^(\s*)([A-Za-z0-9_-]+)(\s*=\s*)(.*)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Write keys into the TOML at <paramref name="path"/> in place (changes: section -> key -> value).
    /// Existing keys are rewritten on their line keeping the trailing comment; missing keys go after the last
    /// non-empty line of their section; missing sections are appended at the end of the file.
    /// </summary>
    public static void Set(string path, IDictionary<string, IDictionary<string, object>> changes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var lines = File.Exists(path) ? SplitLines(File.ReadAllText(path, Encoding.UTF8)) : new List<string>();

        // Ordered list, not a dictionary: missing keys/sections are appended in the order the caller gave them.
        var todo = new List<(string Sec, string Key, object Value)>();
        foreach (var (sec, kv) in changes)
            foreach (var (key, value) in kv)
                todo.Add((sec, key, value));

        var section = "";
        var lastOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var head = HeadRe.Match(line);
            if (head.Success)
            {
                section = head.Groups[1].Value.Trim();
                lastOf[section] = i;
                continue;
            }
            // Blank lines do not extend a section, so a new key lands right after its last real line and the
            // empty line separating it from the next section stays where it was.
            if (section.Length > 0 && line.Trim().Length > 0)
                lastOf[section] = i;
            var km = KeyRe.Match(line);
            if (!km.Success)
                continue;
            var at = todo.FindIndex(t => t.Sec == section && t.Key == km.Groups[2].Value);
            if (at < 0)
                continue;
            var value = todo[at].Value;
            todo.RemoveAt(at);
            lines[i] = km.Groups[1].Value + km.Groups[2].Value + km.Groups[3].Value + TomlValue(value)
                       + TrailingComment(km.Groups[4].Value);
        }

        foreach (var (sec, key, value) in todo)
        {
            var line = $"{key} = {TomlValue(value)}";
            if (lastOf.TryGetValue(sec, out var last))
            {
                var at = last + 1;
                lines.Insert(at, line);
                foreach (var other in lastOf.Keys.ToList())
                    if (lastOf[other] >= at)
                        lastOf[other] += 1;
                lastOf[sec] = at;
            }
            else
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                    lines.Add("");
                lines.Add($"[{sec}]");
                lines.Add(line);
                lastOf[sec] = lines.Count - 1;
            }
        }

        // The prototype wrote in Python text mode, i.e. with the platform line ending (CRLF on Windows).
        var nl = Environment.NewLine;
        File.WriteAllText(path, string.Join(nl, lines) + nl, new UTF8Encoding(false));
    }

    /// <summary>A value as TOML: bool true/false, numbers as-is, everything else a basic string with \ and " escaped.</summary>
    internal static string TomlValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        double d => FloatText(d),
        float f => FloatText(f),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong
            => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        _ => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")
                 .Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
    };

    // Python's str(1.0) is "1.0"; .NET prints "1", which TOML would read back as an integer.
    private static string FloatText(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0";
    }

    /// <summary>
    /// The comment after a value, so rewriting a key keeps the explanation next to it. A '#' inside a quoted
    /// value is part of the value, not a comment.
    /// </summary>
    internal static string TrailingComment(string rest)
    {
        var start = 0;
        if (rest.StartsWith('"'))
        {
            // Skip backslash escapes, so the escaped quote in "a \"#\" b" does not end the value early
            // (the prototype stopped at the first quote; values it writes itself can contain \").
            var end = -1;
            for (var i = 1; i < rest.Length; i++)
            {
                if (rest[i] == '\\') { i++; continue; }
                if (rest[i] == '"') { end = i; break; }
            }
            if (end < 0)
                return "";
            start = end + 1;
        }
        var at = rest.IndexOf('#', start);
        return at >= 0 ? "  " + rest[at..].Trim() : "";
    }

    // Python's str.splitlines(): \r\n, \r or \n, and no empty element for a final newline.
    private static List<string> SplitLines(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
