using System.Globalization;
using TeamsRec.Capture.Core;
using Tomlyn;
using Tomlyn.Model;

namespace TeamsRec.Capture.Config;

/// <summary>
/// The capture app's view of the shared config (%APPDATA%\teamsrec\teamsrec.toml), which it shares with
/// teamsrec-transcribe. Mutable so the settings page can make a change take effect right away, without a restart.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Where recordings go ([recordings] out_dir), with ~ expanded. The default is in the user's profile,
    /// always writable without admin rights, and the same as teamsrec-transcribe's.</summary>
    public string OutDir { get; set; } = ExpandUser(DefaultOutDir);

    public const string DefaultOutDir = "~/meetings";

    /// <summary>[calendar] outlook: classic Outlook (COM) gives the meeting title and participants.</summary>
    public bool UseOutlook { get; set; }

    /// <summary>[capture] prompt_default: record = just notify | ask = show the discard box | skip = box, discards on timeout.</summary>
    public string PromptDefault { get; set; } = "record";

    /// <summary>[user] name.</summary>
    public string UserName { get; set; } = "";

    /// <summary>[capture] onsite_mic: part of the input device name for on-site meetings.</summary>
    public string OnsiteMic { get; set; } = "";

    /// <summary>[capture] device_missing: ask | fail | fallback.</summary>
    public string DeviceMissing { get; set; } = "ask";

    /// <summary>[capture] onsite_offer: never | calendar | always.</summary>
    public string OnsiteOffer { get; set; } = "never";

    /// <summary>[capture] onsite_upgrade: an on-site meeting that turns into a Teams call switches to call recording.</summary>
    public bool OnsiteUpgrade { get; set; } = true;

    /// <summary>[capture] other_apps: record | off - calls outside Teams.</summary>
    public string OtherApps { get; set; } = "record";

    /// <summary>[capture] tray_open: what a double click on the tray icon opens - app = the review page's desktop
    /// window, web = the same page in the default browser.</summary>
    public string TrayOpen { get; set; } = "app";

    /// <summary>[capture] review_app: teamsrec-review.exe (the desktop shell of teamsrec-transcribe); empty = the
    /// usual install place, %LOCALAPPDATA%\Programs\teamsrec-review\teamsrec-review.exe.</summary>
    public string ReviewApp { get; set; } = "";

    /// <summary>The file this config was loaded from (shown on the settings page, written by SettingsModel.Save).</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>TEAMSREC_CONFIG when set (tests, portable setups), else %APPDATA%\teamsrec\teamsrec.toml.</summary>
    public static string DefaultPath
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TEAMSREC_CONFIG");
            if (!string.IsNullOrEmpty(env))
                return env;
            var appData = Environment.GetEnvironmentVariable("APPDATA") ?? "";
            return Path.Combine(appData, "teamsrec", "teamsrec.toml");
        }
    }

    /// <summary>
    /// Read the shared config. A missing, unreadable or invalid file gives the defaults, like the prototype's
    /// _config(): a broken TOML must never keep the recorder from starting.
    /// </summary>
    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        var root = ReadTable(path);
        var recordings = Section(root, "recordings");
        var calendar = Section(root, "calendar");
        var capture = Section(root, "capture");
        var user = Section(root, "user");
        return new AppConfig
        {
            SourcePath = path,
            OutDir = ExpandUser(Str(Get(recordings, "out_dir"), DefaultOutDir)),
            UseOutlook = Truthy(Get(calendar, "outlook"), false),
            PromptDefault = Str(Get(capture, "prompt_default"), "record"),
            UserName = Str(Get(user, "name"), "").Trim(),
            OnsiteMic = Str(Get(capture, "onsite_mic"), "").Trim(),
            DeviceMissing = Str(Get(capture, "device_missing"), "ask"),
            OnsiteOffer = Str(Get(capture, "onsite_offer"), "never"),
            OnsiteUpgrade = Truthy(Get(capture, "onsite_upgrade"), true),
            OtherApps = Str(Get(capture, "other_apps"), "record"),
            TrayOpen = Str(Get(capture, "tray_open"), "app"),
            ReviewApp = Str(Get(capture, "review_app"), "").Trim(),
        };
    }

    /// <summary>Take over the settings that may change while the app runs (ConfigWatcher): everything but the
    /// recordings folder and the source path. Returns the TOML names of what changed.</summary>
    internal List<string> ApplyRuntime(AppConfig fresh)
    {
        var changed = new List<string>();
        void Set<T>(string name, T now, T next, Action<T> apply)
        {
            if (EqualityComparer<T>.Default.Equals(now, next)) return;
            apply(next);
            changed.Add(name);
        }
        Set("calendar.outlook", UseOutlook, fresh.UseOutlook, v => UseOutlook = v);
        Set("user.name", UserName, fresh.UserName, v => UserName = v);
        Set("capture.prompt_default", PromptDefault, fresh.PromptDefault, v => PromptDefault = v);
        Set("capture.onsite_mic", OnsiteMic, fresh.OnsiteMic, v => OnsiteMic = v);
        Set("capture.device_missing", DeviceMissing, fresh.DeviceMissing, v => DeviceMissing = v);
        Set("capture.onsite_offer", OnsiteOffer, fresh.OnsiteOffer, v => OnsiteOffer = v);
        Set("capture.onsite_upgrade", OnsiteUpgrade, fresh.OnsiteUpgrade, v => OnsiteUpgrade = v);
        Set("capture.other_apps", OtherApps, fresh.OtherApps, v => OtherApps = v);
        Set("capture.tray_open", TrayOpen, fresh.TrayOpen, v => TrayOpen = v);
        Set("capture.review_app", ReviewApp, fresh.ReviewApp, v => ReviewApp = v);
        if (fresh.OutDir != OutDir)
            Log.Info($"recordings folder changed to {fresh.OutDir}: used after the next start");
        return changed;
    }

    /// <summary>Parse TOML text into an untyped table (also used by tests to check what was written).</summary>
    internal static TomlTable ParseToml(string text)
        => TomlSerializer.Deserialize<TomlTable>(text, TomlSerializerOptions.Default) ?? new TomlTable();

    private static TomlTable ReadTable(string path)
    {
        try
        {
            return ParseToml(File.ReadAllText(path, System.Text.Encoding.UTF8));
        }
        catch (Exception e)  // IO, access, TomlException, decoding: whatever it is, fall back to defaults
        {
            // Missing file is the normal first-run case; a broken one is logged but still gives defaults.
            if (e is not FileNotFoundException and not DirectoryNotFoundException)
                Log.Warn($"config {path} not usable: {e.Message}");
            return new TomlTable();
        }
    }

    // A key that is not a table (e.g. "capture = 1") is treated like a missing section instead of crashing.
    private static TomlTable? Section(TomlTable root, string name)
        => root.TryGetValue(name, out var v) ? v as TomlTable : null;

    private static object? Get(TomlTable? table, string key)
        => table != null && table.TryGetValue(key, out var v) ? v : null;

    /// <summary>Python's str(): the prototype accepted any TOML value and stringified it.</summary>
    internal static string Str(object? value, string fallback) => value switch
    {
        null => fallback,
        string s => s,
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? fallback,
    };

    /// <summary>Python's bool(): non-empty strings and non-zero numbers count as true.</summary>
    internal static bool Truthy(object? value, bool fallback) => value switch
    {
        null => fallback,
        bool b => b,
        string s => s.Length > 0,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        _ => true,
    };

    /// <summary>Path.expanduser: a leading ~ is the user's profile folder.</summary>
    internal static string ExpandUser(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrEmpty(home))
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }
        return path;
    }
}
