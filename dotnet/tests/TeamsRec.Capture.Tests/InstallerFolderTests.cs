using TeamsRec.Capture.Config;

namespace TeamsRec.Capture.Tests;

public class InstallerFolderTests
{
    [Fact]
    public void A_folder_picked_in_the_installer_goes_into_the_toml_once_then_the_toml_wins()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"teamsrec-inst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var toml = Path.Combine(dir, "teamsrec.toml");
        var reg = new Dictionary<string, string>();
        string? Get(string n) => reg.TryGetValue(n, out var v) ? v : null;
        void Set(string n, string v) => reg[n] = v;
        try
        {
            File.WriteAllText(toml, "# shared config\n[user]\nname = \"Jan Novák\"\n");
            Assert.Null(InstallerFolder.Apply(toml, Get, Set));  // nothing chosen (not installed from the MSI)

            reg["OutDir"] = @"C:\Users\jan\meetings\";  // MSI directory properties end with a backslash
            Assert.Equal(@"C:\Users\jan\meetings", InstallerFolder.Apply(toml, Get, Set));
            var cfg = AppConfig.Load(toml);
            Assert.Equal(@"C:\Users\jan\meetings", cfg.OutDir);
            Assert.Equal("Jan Novák", cfg.UserName);
            Assert.StartsWith("# shared config", File.ReadAllText(toml));  // comments and other keys stay

            // changed later in Nastavení: an upgrade (same installer value) must not put the old one back
            TomlEditor.Set(toml, new Dictionary<string, IDictionary<string, object>>
            {
                ["recordings"] = new Dictionary<string, object> { ["out_dir"] = @"E:\rec" },
            });
            Assert.Null(InstallerFolder.Apply(toml, Get, Set));
            Assert.Equal(@"E:\rec", AppConfig.Load(toml).OutDir);

            InstallerFolder.Report("E:/rec", Set);  // the next installer offers this one, in a form MSI accepts
            Assert.Equal(@"E:\rec", reg["OutDirCurrent"]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
