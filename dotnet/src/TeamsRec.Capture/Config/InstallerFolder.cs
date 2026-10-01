using Microsoft.Win32;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Config;

/// <summary>
/// The recordings folder chosen in the installer. The MSI offers a folder (prefilled with the current one, or
/// %USERPROFILE%\meetings – always writable without admin rights) and stores the choice in
/// HKCU\Software\teamsrec\capture\OutDir. The shared TOML stays the source of truth, read by both apps:
/// <list type="bullet">
/// <item>a choice the app has not applied yet (OutDir differs from OutDirApplied) is written into the TOML once –
///   the user just picked it in the installer;</item>
/// <item>otherwise the TOML wins (changed later in Nastavení), and the app reports the folder it uses as
///   OutDirCurrent, which the next installer offers as its default.</item>
/// </list>
/// </summary>
public static class InstallerFolder
{
    public const string Key = @"Software\teamsrec\capture";

    /// <summary>Apply a fresh installer choice to the TOML at <paramref name="tomlPath"/>. Returns the folder written,
    /// or null when there was nothing new.</summary>
    public static string? Apply(string tomlPath, Func<string, string?> get, Action<string, string> set)
    {
        var chosen = Clean(get("OutDir"));
        if (chosen.Length == 0 || SamePath(chosen, Clean(get("OutDirApplied"))))
            return null;
        TomlEditor.Set(tomlPath, new Dictionary<string, IDictionary<string, object>>
        {
            ["recordings"] = new Dictionary<string, object> { ["out_dir"] = chosen },
        });
        set("OutDirApplied", chosen);
        return chosen;
    }

    /// <summary>The folder the app uses, for the next installer's default: a full path with backslashes, since
    /// MSI rejects "D:/meetings" as a directory (error 1606).</summary>
    public static void Report(string outDir, Action<string, string> set) => set("OutDirCurrent", Path.GetFullPath(outDir));

    /// <summary>Apply + load + report against the real registry; a registry problem never stops the app.</summary>
    public static AppConfig LoadConfig(out string? applied)
    {
        var path = AppConfig.DefaultPath;
        applied = null;
        try
        {
            applied = Apply(path, Get, Set);
        }
        catch (Exception e)
        {
            Log.Warn($"installer folder not applied: {e.Message}");
        }
        var cfg = AppConfig.Load(path);
        try
        {
            Report(cfg.OutDir, Set);
        }
        catch (Exception e)
        {
            Log.Warn($"installer folder not reported: {e.Message}");
        }
        return cfg;
    }

    private static string? Get(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(name) as string;
    }

    private static void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue(name, value);
    }

    // MSI directory properties end with a backslash
    private static string Clean(string? path) => (path ?? "").Trim().TrimEnd('\\', '/');

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
