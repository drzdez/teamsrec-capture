using Microsoft.Win32;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Detection;

/// <summary>
/// One entry of Windows' microphone privacy registry. KeyName is the raw subkey name (a package family name,
/// or for NonPackaged apps the exe path with '\' replaced by '#'); Ident is the package name or the exe file name.
/// Start/Stop are LastUsedTimeStart/LastUsedTimeStop (FILETIME); Stop == 0 means "holding the mic right now".
/// </summary>
public sealed record MicConsentEntry(string KeyName, string Ident, long Start, long Stop);

/// <summary>
/// Who holds the microphone right now, from HKCU\...\CapabilityAccessManager\ConsentStore\microphone.
/// Windows writes LastUsedTimeStop = 0 while an app captures, which needs no audio API and no admin rights.
/// </summary>
public static class MicUsers
{
    public const string ConsentKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>
    /// This recorder itself holds the microphone while it records; it must not look like a call.
    /// python(w).exe = the prototype (may run side by side), teamsrec-capture.exe = this app.
    /// </summary>
    public static readonly IReadOnlySet<string> NotACall = new HashSet<string>(StringComparer.Ordinal)
    {
        "python.exe", "pythonw.exe", "teamsrec-capture.exe",
    };

    /// <summary>True while any *teams* app holds the microphone (LastUsedTimeStop == 0).</summary>
    public static bool TeamsInUse() => TeamsInUse(ReadEntries());

    /// <summary>Pure rule behind <see cref="TeamsInUse()"/>.</summary>
    public static bool TeamsInUse(IEnumerable<MicConsentEntry> entries) =>
        entries.Any(e => e.Start != 0 && e.Stop == 0 && IsTeams(e.Ident));

    /// <summary>
    /// A Teams entry by its app identity (package name or exe file name), never by the full key path: this
    /// recorder is teamsrec-capture.exe, and while it records the microphone its own entry would otherwise count
    /// as a Teams call that never ends (the Python prototype ran as pythonw.exe and never hit this).
    /// </summary>
    internal static bool IsTeams(string ident)
    {
        var i = ident.ToLowerInvariant();
        return i.Contains("teams") && !i.StartsWith("teamsrec") && i != OwnExeName() && !NotACall.Contains(i);
    }

    /// <summary>
    /// Apps holding the microphone right now: packaged app names and exe file names, lower case.
    /// This recorder itself is left out.
    /// </summary>
    public static HashSet<string> Current() => Current(ReadEntries());

    /// <summary>Pure rule behind <see cref="Current()"/>.</summary>
    public static HashSet<string> Current(IEnumerable<MicConsentEntry> entries)
    {
        var own = OwnExeName();
        var output = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var ident = e.Ident.ToLowerInvariant();
            if (e.Start != 0 && e.Stop == 0 && !NotACall.Contains(ident) && ident != own)
                output.Add(ident);
        }
        return output;
    }

    /// <summary>
    /// Reads every packaged subkey and every NonPackaged subkey. Keys without both timestamps are skipped
    /// (as the prototype's OSError path); a failing registry read is logged and yields nothing.
    /// </summary>
    public static List<MicConsentEntry> ReadEntries()
    {
        var output = new List<MicConsentEntry>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentKey);
            if (root is null)
                return output;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name == "NonPackaged")
                    continue;
                AddEntry(root, name, name, output);
            }
            using var np = root.OpenSubKey("NonPackaged");
            if (np is not null)
                foreach (var name in np.GetSubKeyNames())
                    AddEntry(np, name, name.Split('#')[^1], output);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"registry read failed: {ex.Message}");
        }
        return output;
    }

    private static void AddEntry(RegistryKey parent, string keyName, string ident, List<MicConsentEntry> output)
    {
        try
        {
            using var k = parent.OpenSubKey(keyName);
            if (k is null)
                return;
            var start = k.GetValue("LastUsedTimeStart");
            var stop = k.GetValue("LastUsedTimeStop");
            if (start is null || stop is null)
                return;
            output.Add(new MicConsentEntry(keyName, ident, ToLong(start), ToLong(stop)));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException
                                       or InvalidCastException or FormatException or OverflowException)
        {
            // one unreadable key must not hide the others
        }
    }

    // REG_QWORD comes back as long; tolerate REG_DWORD (int) as well.
    private static long ToLong(object v) => v switch
    {
        long l => l,
        int i => i,
        _ => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string? _ownExe;

    // The exe name we actually run as (a renamed build must still not detect itself as a call).
    private static string OwnExeName()
    {
        if (_ownExe is not null)
            return _ownExe;
        try
        {
            var path = Environment.ProcessPath;
            _ownExe = string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path).ToLowerInvariant();
        }
        catch
        {
            _ownExe = "";
        }
        return _ownExe;
    }
}
