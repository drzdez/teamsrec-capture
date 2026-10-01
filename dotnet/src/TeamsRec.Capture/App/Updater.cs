using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using TeamsRec.Capture.Config;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.App;

/// <summary>A release on GitHub with its MSI.</summary>
public sealed record Release(string Version, string MsiName, string MsiUrl, long Size, string? Sha256, string PageUrl);

/// <summary>The pure part of the update check: parsing the release, comparing versions, when to check.</summary>
public static class UpdateLogic
{
    public const string LatestUrl = "https://api.github.com/repos/drzdez/teamsrec-capture/releases/latest";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>The latest release from the GitHub API answer; null without an x64 MSI (or for a draft or
    /// pre-release, which /latest never returns anyway).</summary>
    public static Release? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease"))
            return null;
        var tag = Str(root, "tag_name");
        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out _))
            return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name") ?? "";
            var url = Str(a, "browser_download_url");
            if (!name.EndsWith("-x64.msi", StringComparison.OrdinalIgnoreCase) || url is null)
                continue;
            var digest = Str(a, "digest");  // "sha256:<hex>", given by GitHub for every asset
            var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..].ToLowerInvariant() : null;
            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            return new Release(tag.TrimStart('v', 'V'), name, url, size, sha, Str(root, "html_url") ?? "");
        }
        return null;
    }

    /// <summary>True when <paramref name="candidate"/> is a higher x.y.z than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var c) && Version.TryParse(current, out var cur) && c > cur;

    /// <summary>Check at start and then once a day; a clock set back does not stop the checks.</summary>
    public static bool Due(DateTime? last, DateTime now) => last is null || last > now || now - last >= Interval;

    /// <summary>Only an MSI from this repository's releases is downloaded and run.</summary>
    public static bool TrustedUrl(string url) =>
        url.StartsWith("https://github.com/drzdez/teamsrec-capture/releases/download/", StringComparison.Ordinal);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Looks for a newer teamsrec-capture on GitHub Releases at start and then once a day ([capture] update_check),
/// and installs it on request: downloads the MSI into %TEMP%, checks its size and SHA-256, and runs
/// <c>msiexec /passive</c>. The MSI asks this app to quit (quit.request) and starts the new version afterwards.
/// The last check and a version the user said no to are kept in HKCU\Software\teamsrec\capture.
/// </summary>
public sealed class Updater : IDisposable
{
    private static readonly HttpClient Http = CreateClient();
    private readonly AppConfig _cfg;
    private readonly Action<Release> _found;
    private readonly System.Threading.Timer _timer;
    private int _checking;

    /// <param name="found">Called (on a pool thread) when a newer release is found.</param>
    public Updater(AppConfig cfg, Action<Release> found)
    {
        _cfg = cfg;
        _found = found;
        // the first check a minute after start (not in the way of the start itself), then hourly whether it is due
        _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
    }

    public Release? Available { get; private set; }

    /// <summary>The version the user declined: no balloon for it again (the menu item stays).</summary>
    public static string? Declined
    {
        get => Get("UpdateDeclined");
        set => Set("UpdateDeclined", value ?? "");
    }

    private async Task TickAsync()
    {
        if (!_cfg.UpdateCheck || !UpdateLogic.Due(LastCheck(), DateTime.Now))
            return;
        await CheckAsync();
    }

    /// <summary>Ask GitHub now. Returns the newer release, or null (none, or the check failed – logged).</summary>
    public async Task<Release?> CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
            return Available;
        try
        {
            var json = await Http.GetStringAsync(UpdateLogic.LatestUrl);
            Set("UpdateLastCheck", DateTime.Now.ToString("s"));
            var rel = UpdateLogic.Parse(json);
            if (rel is null || !UpdateLogic.IsNewer(rel.Version, Versions.AppVersion))
                return null;
            var first = Available?.Version != rel.Version;
            Available = rel;
            if (first)
            {
                Log.Info($"update available: {rel.Version} (running {Versions.AppVersion})");
                _found(rel);
            }
            return rel;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn($"update check failed: {e.Message}");
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    /// <summary>Download, verify and start the installer. Throws with a readable message when something is off.</summary>
    public static async Task InstallAsync(Release rel)
    {
        if (!UpdateLogic.TrustedUrl(rel.MsiUrl))
            throw new InvalidOperationException($"unexpected download address {rel.MsiUrl}");
        var path = Path.Combine(Path.GetTempPath(), rel.MsiName);
        Log.Info($"downloading {rel.MsiUrl}");
        await using (var src = await Http.GetStreamAsync(rel.MsiUrl))
        await using (var dst = File.Create(path))
            await src.CopyToAsync(dst);
        var size = new FileInfo(path).Length;
        if (rel.Size > 0 && size != rel.Size)
            throw new InvalidOperationException($"the download has {size} bytes, the release says {rel.Size}");
        if (rel.Sha256 is not null)
        {
            await using var f = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(f));
            if (hash != rel.Sha256)
                throw new InvalidOperationException("the download does not match the release's SHA-256");
        }
        Log.Info($"starting the installer of {rel.Version}");
        // /passive: progress only, no questions (the folder page is for a fresh install); the MSI quits this app
        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{path}\" /passive") { UseShellExecute = false });
    }

    private static DateTime? LastCheck() =>
        DateTime.TryParse(Get("UpdateLastCheck"), out var t) ? t : null;

    private static string? Get(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallerFolder.Key);
            var v = key?.GetValue(name) as string;
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Set(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(InstallerFolder.Key);
            key.SetValue(name, value);
        }
        catch (Exception e)
        {
            Log.Warn($"update state not saved: {e.Message}");
        }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API refuses requests without a User-Agent
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(Versions.AppName, Versions.AppVersion));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public void Dispose() => _timer.Dispose();
}
