using System.Text;
using System.Text.Json.Nodes;
using TeamsRec.Capture.Config;
using Tomlyn.Model;

namespace TeamsRec.Capture.Tests;

/// <summary>Port of smoke_test.test_settings_api_and_toml (the TOML part) plus the editor's edge cases.</summary>
public sealed class ConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    private const string Initial = """
        [user]
        name = "Jan Novák"           # a comment that must survive being rewritten

        [calendar]
        outlook = false

        [capture]
        onsite_mic = "Pole mikrofonu"
        prompt_default = "record"    # keep on timeout

        [recordings]
        out_dir = "C:/tmp/teamsrec-smoke"

        [transcribe]
        model = "large-v3"
        batch_size = 16

        """;

    public ConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "teamsrec-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "teamsrec.toml");
        File.WriteAllText(_path, Initial, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Text() => File.ReadAllText(_path, Encoding.UTF8);

    private TomlTable Parsed() => AppConfig.ParseToml(Text());

    private static TomlTable Tbl(TomlTable t, string key) => (TomlTable)t[key];

    [Fact]
    public void Load_reads_the_shared_toml()
    {
        var cfg = AppConfig.Load(_path);
        Assert.Equal("Pole mikrofonu", cfg.OnsiteMic);
        Assert.Equal("Jan Novák", cfg.UserName);
        Assert.Equal("C:/tmp/teamsrec-smoke", cfg.OutDir);
        Assert.False(cfg.UseOutlook);
        Assert.Equal("record", cfg.PromptDefault);
        Assert.Equal("ask", cfg.DeviceMissing);
        Assert.Equal("never", cfg.OnsiteOffer);
        Assert.True(cfg.OnsiteUpgrade);
        Assert.Equal("record", cfg.OtherApps);
        Assert.Equal("Pole mikrofonu", SettingsModel.Values(cfg)["onsite_mic"]);
    }

    [Fact]
    public void Missing_or_invalid_file_gives_defaults()
    {
        var missing = AppConfig.Load(Path.Combine(_dir, "nope.toml"));
        Assert.Equal(AppConfig.ExpandUser("~/meetings"), missing.OutDir);  // in the profile: writable without admin rights
        Assert.Equal("ask", missing.DeviceMissing);

        var broken = Path.Combine(_dir, "broken.toml");
        File.WriteAllText(broken, "[capture\nonsite_mic = = \"x\"\n");
        var cfg = AppConfig.Load(broken);
        Assert.Equal("", cfg.OnsiteMic);
        Assert.Equal("record", cfg.OtherApps);
    }

    [Fact]
    public void Tilde_in_out_dir_is_expanded()
    {
        File.WriteAllText(_path, "[recordings]\nout_dir = \"~/meetings\"\n");
        var cfg = AppConfig.Load(_path);
        Assert.DoesNotContain("~", cfg.OutDir);
        Assert.EndsWith("meetings", cfg.OutDir);
    }

    [Fact]
    public void Values_has_all_page_fields()
    {
        var v = SettingsModel.Values(new AppConfig());
        Assert.Equal(
            new[] { "calendar_outlook", "device_missing", "onsite_mic", "onsite_offer", "onsite_upgrade",
                    "other_apps", "prompt_default", "user_name" },
            v.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Bad_enum_is_rejected_and_nothing_is_written()
    {
        var cfg = AppConfig.Load(_path);
        var ex = Assert.Throws<ArgumentException>(() =>
            SettingsModel.Save(_path, cfg, new JsonObject { ["onsite_offer"] = "sometimes" }));
        Assert.Contains("onsite_offer: neznámá hodnota 'sometimes'", ex.Message);
        Assert.Equal(Initial, Text().ReplaceLineEndings("\n"));
        Assert.Equal("never", cfg.OnsiteOffer);
    }

    [Fact]
    public void Save_edits_the_toml_in_place()
    {
        var cfg = AppConfig.Load(_path);
        var applied = SettingsModel.Save(_path, cfg, (JsonObject)JsonNode.Parse(
            """{"onsite_offer": "calendar", "onsite_upgrade": true, "device_missing": "fail", "user_name": " Petr Svoboda "}""")!);
        Assert.Equal(new[] { "device_missing", "onsite_offer", "onsite_upgrade", "user_name" }, applied);

        var text = Text();
        Assert.Contains("onsite_offer = \"calendar\"", text);
        Assert.Contains("onsite_upgrade = true", text);
        Assert.Contains("# a comment that must survive being rewritten", text);
        Assert.Contains("name = \"Petr Svoboda\"  # a comment that must survive being rewritten", text);
        Assert.Contains("[transcribe]", text);
        Assert.Contains("model = \"large-v3\"", text);

        // the running config changed right away
        Assert.Equal("calendar", cfg.OnsiteOffer);
        Assert.Equal("Petr Svoboda", cfg.UserName);
        Assert.Equal("fail", cfg.DeviceMissing);

        var parsed = Parsed();
        Assert.Equal("calendar", Tbl(parsed, "capture")["onsite_offer"]);
        Assert.Equal(16L, Convert.ToInt64(Tbl(parsed, "transcribe")["batch_size"]));

        // new keys land at the end of [capture], before the blank line and [recordings]
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var rec = Array.IndexOf(lines, "[recordings]");
        Assert.Equal("", lines[rec - 1]);
        Assert.StartsWith("onsite_", lines[rec - 2].Split(' ')[0]);
        Assert.True(Array.IndexOf(lines, "[capture]") < Array.IndexOf(lines, "device_missing = \"fail\""));
        Assert.True(Array.IndexOf(lines, "device_missing = \"fail\"") < rec);
    }

    [Fact]
    public void Save_with_nothing_known_writes_nothing()
    {
        var cfg = AppConfig.Load(_path);
        Assert.Empty(SettingsModel.Save(_path, cfg, new JsonObject { ["unknown"] = 1 }));
        Assert.Equal(Initial, Text().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void New_section_with_a_quoted_value()
    {
        TomlEditor.Set(_path, new Dictionary<string, IDictionary<string, object>>
        {
            ["brandnew"] = new Dictionary<string, object> { ["flag"] = true, ["text"] = "a \"quoted\" value" },
        });
        var brandnew = Tbl(Parsed(), "brandnew");
        Assert.Equal(2, brandnew.Count);
        Assert.Equal(true, brandnew["flag"]);
        Assert.Equal("a \"quoted\" value", brandnew["text"]);
        Assert.Contains("[transcribe]", Text());
    }

    [Fact]
    public void Missing_file_is_created()
    {
        var fresh = Path.Combine(_dir, "sub", "teamsrec.toml");
        TomlEditor.Set(fresh, new Dictionary<string, IDictionary<string, object>>
        {
            ["capture"] = new Dictionary<string, object> { ["onsite_mic"] = "Mic", ["rate"] = 1.0, ["n"] = 3 },
        });
        var t = AppConfig.ParseToml(File.ReadAllText(fresh));
        Assert.Equal("Mic", Tbl(t, "capture")["onsite_mic"]);
        Assert.Equal(1.0, Convert.ToDouble(Tbl(t, "capture")["rate"]));
        Assert.Equal(3L, Convert.ToInt64(Tbl(t, "capture")["n"]));
        Assert.StartsWith("[capture]", File.ReadAllText(fresh));
    }

    [Fact]
    public void Hash_inside_a_quoted_value_is_not_a_comment()
    {
        Assert.Equal("", TomlEditor.TrailingComment("\"C# room\""));
        Assert.Equal("  # note", TomlEditor.TrailingComment("\"C# room\"   # note"));
        Assert.Equal("  # note", TomlEditor.TrailingComment("\"a \\\"#\\\" b\" # note"));
        Assert.Equal("  # note", TomlEditor.TrailingComment("16 # note"));
        Assert.Equal("", TomlEditor.TrailingComment("\"unterminated # x"));

        File.WriteAllText(_path, "[capture]\nonsite_mic = \"Room #2\"  # which mic\n");
        TomlEditor.Set(_path, new Dictionary<string, IDictionary<string, object>>
        {
            ["capture"] = new Dictionary<string, object> { ["onsite_mic"] = "Room #3" },
        });
        Assert.Contains("onsite_mic = \"Room #3\"  # which mic", Text());
        Assert.Equal("Room #3", Tbl(Parsed(), "capture")["onsite_mic"]);
    }

    [Fact]
    public void Toml_values_are_formatted_like_the_prototype()
    {
        Assert.Equal("true", TomlEditor.TomlValue(true));
        Assert.Equal("false", TomlEditor.TomlValue(false));
        Assert.Equal("16", TomlEditor.TomlValue(16));
        Assert.Equal("1.5", TomlEditor.TomlValue(1.5));
        Assert.Equal("1.0", TomlEditor.TomlValue(1.0));
        Assert.Equal("\"C:\\\\x \\\"y\\\"\"", TomlEditor.TomlValue("C:\\x \"y\""));
    }
}
