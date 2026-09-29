using System.Text.Json;
using System.Text.Json.Nodes;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Config;

/// <summary>
/// The settings page's model: which page fields exist, where each lives in the shared TOML and which values
/// are allowed. Saving validates, writes the TOML in place and updates the running config so it takes effect
/// right away.
/// </summary>
public static class SettingsModel
{
    internal enum Kind { Str, Bool, Enum }

    internal sealed record Field(string Name, string Section, string Key, Kind Kind, string[]? Allowed = null);

    /// <summary>Page field -> (TOML section, key, allowed values or type); same order as the prototype's SETTINGS_MAP.</summary>
    internal static readonly Field[] Map =
    [
        new("onsite_mic", "capture", "onsite_mic", Kind.Str),
        new("device_missing", "capture", "device_missing", Kind.Enum, ["ask", "fail", "fallback"]),
        new("onsite_offer", "capture", "onsite_offer", Kind.Enum, ["never", "calendar", "always"]),
        new("onsite_upgrade", "capture", "onsite_upgrade", Kind.Bool),
        new("other_apps", "capture", "other_apps", Kind.Enum, ["record", "off"]),
        new("prompt_default", "capture", "prompt_default", Kind.Enum, ["record", "ask", "skip"]),
        new("calendar_outlook", "calendar", "outlook", Kind.Bool),
        new("user_name", "user", "name", Kind.Str),
    ];

    /// <summary>The current values as the page shows them.</summary>
    public static Dictionary<string, object> Values(AppConfig cfg) => new()
    {
        ["onsite_mic"] = cfg.OnsiteMic,
        ["device_missing"] = cfg.DeviceMissing,
        ["onsite_offer"] = cfg.OnsiteOffer,
        ["onsite_upgrade"] = cfg.OnsiteUpgrade,
        ["other_apps"] = cfg.OtherApps,
        ["prompt_default"] = cfg.PromptDefault,
        ["calendar_outlook"] = cfg.UseOutlook,
        ["user_name"] = cfg.UserName,
    };

    /// <summary>
    /// Validate the page's values, write them to the TOML at <paramref name="path"/> and apply them to
    /// <paramref name="cfg"/>. Fields the page did not send are left alone. Returns the applied field names, sorted.
    /// Throws <see cref="ArgumentException"/> (Czech message for the page) on a value outside an enum; nothing is
    /// written then.
    /// </summary>
    public static List<string> Save(string path, AppConfig cfg, JsonObject values)
    {
        var changes = new Dictionary<string, IDictionary<string, object>>(StringComparer.Ordinal);
        var clean = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var f in Map)
        {
            if (!values.TryGetPropertyValue(f.Name, out var node))
                continue;
            object value;
            switch (f.Kind)
            {
                case Kind.Bool:
                    value = Truthy(node);
                    break;
                case Kind.Str:
                    value = Text(node).Trim();
                    break;
                default:
                    var s = AsString(node);
                    if (s is null || Array.IndexOf(f.Allowed!, s) < 0)
                        throw new ArgumentException($"{f.Name}: neznámá hodnota {Repr(node)}");
                    value = s;
                    break;
            }
            clean[f.Name] = value;
            if (!changes.TryGetValue(f.Section, out var sec))
                changes[f.Section] = sec = new Dictionary<string, object>(StringComparer.Ordinal);
            sec[f.Key] = value;
        }
        if (changes.Count == 0)
            return [];

        TomlEditor.Set(path, changes);
        foreach (var (field, value) in clean)
        {
            switch (field)
            {
                case "onsite_mic": cfg.OnsiteMic = (string)value; break;
                case "device_missing": cfg.DeviceMissing = (string)value; break;
                case "onsite_offer": cfg.OnsiteOffer = (string)value; break;
                case "onsite_upgrade": cfg.OnsiteUpgrade = (bool)value; break;
                case "other_apps": cfg.OtherApps = (string)value; break;
                case "prompt_default": cfg.PromptDefault = (string)value; break;
                case "calendar_outlook": cfg.UseOutlook = (bool)value; break;
                case "user_name": cfg.UserName = (string)value; break;
            }
        }
        var applied = clean.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        Log.Info("settings saved: " + string.Join(", ", applied));
        return applied;
    }

    private static string? AsString(JsonNode? node)
        => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    // Python's bool() on what json.loads produced.
    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => v.GetValue<double>() != 0,
            _ => false,
        },
        _ => false,
    };

    // Python's str(); null becomes "" rather than the prototype's accidental "None".
    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        _ => node.ToJsonString(),
    };

    // Python's repr() for the error message: 'sometimes', 1, True, None.
    private static string Repr(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => "'" + v.GetValue<string>() + "'",
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        _ => node.ToJsonString(),
    };
}
