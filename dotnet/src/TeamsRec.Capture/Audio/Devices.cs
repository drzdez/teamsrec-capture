using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Audio;

/// <summary>Audio endpoints: what can be recorded, what Windows knows but cannot record from, and which
/// microphone the running call uses. Port of audio_inputs / unavailable_inputs / find_input /
/// capture_sessions / call_input_device of the prototype.</summary>
public static class Devices
{
    /// <summary>Teams executables; its audio may run in a helper process whose parent is one of these.</summary>
    public static readonly IReadOnlyList<string> TeamsExe = ["ms-teams.exe", "teams.exe"];

    /// <summary>Apps that run calls, in the order they are trusted when several capture at once
    /// (browsers = Meet, Webex web).</summary>
    public static readonly IReadOnlyList<string> CallApps =
    [
        "zoom.exe", "webexmta.exe", "ciscocollabhost.exe", "atmgr.exe", "slack.exe", "discord.exe",
        "skype.exe", "msedge.exe", "chrome.exe", "firefox.exe", "brave.exe", "opera.exe",
    ];

    // The prototype (PortAudio WASAPI) used the multimedia default; Console and Multimedia are the same device
    // in the Windows sound settings anyway.
    private const Role DefaultRole = Role.Multimedia;

    /// <summary>Capture endpoints we can record from right now (loopbacks are render endpoints, so they are
    /// never in this list).</summary>
    public static List<InputDevice> Inputs()
    {
        var result = new List<InputDevice>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var defaultId = DefaultId(en, DataFlow.Capture);
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d)
                {
                    try
                    {
                        var (rate, channels) = MixFormat(d);
                        result.Add(new InputDevice(d.ID, d.FriendlyName, channels, rate, d.ID == defaultId));
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"input device skipped: {e.Message}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"input device list: {e}");
        }
        return result;
    }

    /// <summary>An active input whose name contains namePart (case-insensitive); the Windows default input
    /// when namePart is empty; null when missing.</summary>
    public static InputDevice? FindInput(string namePart)
    {
        var inputs = Inputs();
        if (!string.IsNullOrEmpty(namePart))
            return inputs.FirstOrDefault(d => d.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase));
        return inputs.FirstOrDefault(d => d.IsDefault);
    }

    /// <summary>Name of the loopback of the default output, as the prototype (PyAudioWPatch) called it.</summary>
    public static string LoopbackName()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, DefaultRole))
                return "";
            using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, DefaultRole);
            return d.FriendlyName + " [Loopback]";
        }
        catch (Exception e)
        {
            Log.Warn($"loopback name: {e.Message}");
            return "";
        }
    }

    /// <summary>Why Windows cannot record from an endpoint (DeviceState / registry DeviceState value).</summary>
    public static string StateReason(int state)
    {
        // 0x10000000 = disabled by the user in the Sound control panel (registry flag); NAudio reports it as
        // DeviceState.Disabled (2).
        if ((state & 0x10000000) != 0 || state == 2)
            return "zakázané ve Windows";
        return state switch
        {
            1 => "aktivní",
            4 => "není přítomné",
            8 => "odpojené",
            _ => $"stav {state}",
        };
    }

    /// <summary>Microphones Windows knows but cannot record from (disabled, unplugged, not present), so the
    /// settings page can say why the expected device is not in the list.</summary>
    public static List<UnavailableInput> Unavailable()
    {
        var result = new List<UnavailableInput>();
        try
        {
            using var en = new MMDeviceEnumerator();
            var states = DeviceState.Disabled | DeviceState.NotPresent | DeviceState.Unplugged;
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, states))
            {
                using (d)
                {
                    try
                    {
                        // Properties of a removed device may be gone - skip it like the prototype does.
                        result.Add(new UnavailableInput(d.FriendlyName, d.DeviceFriendlyName, StateReason((int)d.State)));
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"unavailable inputs: lookup failed: {e.Message}");
        }
        return result;
    }

    /// <summary>(microphone, pid) for every active audio capture session on an active input device. The Core
    /// Audio session list says who records from which microphone, whichever app it is.</summary>
    public static List<CaptureSession> CaptureSessions()
    {
        var result = new List<CaptureSession>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (dev)
                {
                    try
                    {
                        var mgr = dev.AudioSessionManager;
                        mgr.RefreshSessions();
                        var sessions = mgr.Sessions;
                        string? name = null;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            using var s = sessions[i];
                            if ((int)s.State == 1)  // AudioSessionStateActive
                            {
                                name ??= dev.FriendlyName;
                                result.Add(new CaptureSession(name, (int)s.GetProcessID));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"microphone sessions: lookup failed: {e.Message}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"microphone sessions: lookup failed: {e.Message}");
        }
        return result;
    }

    /// <summary>The microphone the call is using: Teams first, then a known call app (Zoom, Webex, a browser
    /// with Meet...), then any other app that captures - never this recorder itself. The Windows default input
    /// can be a different microphone entirely (2026-09-29: the default was the BT-W5 dongle, Teams used the
    /// Sony headset's microphone, and the user was missing from the whole recording). Null when nobody else
    /// records. The delegates replace the OS lookups in tests.</summary>
    public static string? CallInputDevice(IEnumerable<CaptureSession>? sessions = null,
                                          Func<int, string>? processName = null,
                                          Func<int, bool>? isTeams = null)
    {
        Dictionary<int, (int Parent, string Name)>? snapshot = null;
        Dictionary<int, (int Parent, string Name)> Snap() => snapshot ??= ProcessTree.Snapshot();
        processName ??= pid => Snap().TryGetValue(pid, out var p) ? p.Name : "";
        isTeams ??= pid => ProcessTree.IsTeams(Snap(), pid);

        var me = new HashSet<int> { Environment.ProcessId };
        if (Snap().TryGetValue(Environment.ProcessId, out var self))
            me.Add(self.Parent);

        string? best = null;
        int rank = 99;
        foreach (var s in sessions ?? CaptureSessions())
        {
            if (s.ProcessId == 0 || me.Contains(s.ProcessId))
                continue;
            int r;
            if (isTeams(s.ProcessId))
                r = 0;
            else
            {
                var name = (processName(s.ProcessId) ?? "").ToLowerInvariant();
                int idx = IndexOf(CallApps, name);
                r = idx >= 0 ? idx + 1 : 50;
            }
            if (r < rank)
            {
                best = s.DeviceName;
                rank = r;
            }
        }
        return best;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value)
                return i;
        return -1;
    }

    // ------------------------------------------------------------ endpoints for the recorder / mic test

    internal static string? DefaultId(MMDeviceEnumerator en, DataFlow flow)
    {
        try
        {
            if (!en.HasDefaultAudioEndpoint(flow, DefaultRole))
                return null;
            using var d = en.GetDefaultAudioEndpoint(flow, DefaultRole);
            return d.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Sample rate and channel count of the endpoint's shared-mode mix format.</summary>
    internal static (int Rate, int Channels) MixFormat(MMDevice d)
    {
        using var client = d.AudioClient;
        WaveFormat f = client.MixFormat;
        return (f.SampleRate, f.Channels);
    }

    /// <summary>Same selection as FindInput, but returns the endpoint to record from (caller disposes).</summary>
    internal static MMDevice? FindInputEndpoint(MMDeviceEnumerator en, string namePart)
    {
        if (string.IsNullOrEmpty(namePart))
            return DefaultInputEndpoint(en);
        MMDevice? found = null;
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (found == null && d.FriendlyName.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                found = d;
            else
                d.Dispose();
        }
        return found;
    }

    internal static MMDevice? DefaultInputEndpoint(MMDeviceEnumerator en) =>
        en.HasDefaultAudioEndpoint(DataFlow.Capture, DefaultRole)
            ? en.GetDefaultAudioEndpoint(DataFlow.Capture, DefaultRole) : null;

    internal static MMDevice? DefaultRenderEndpoint(MMDeviceEnumerator en) =>
        en.HasDefaultAudioEndpoint(DataFlow.Render, DefaultRole)
            ? en.GetDefaultAudioEndpoint(DataFlow.Render, DefaultRole) : null;
}

/// <summary>Process names and parents from one Toolhelp snapshot (psutil in the prototype).</summary>
internal static class ProcessTree
{
    /// <summary>The process or one of its parents (up to 4 levels) is Teams - its audio may run in a helper
    /// process.</summary>
    public static bool IsTeams(IReadOnlyDictionary<int, (int Parent, string Name)> tree, int pid)
    {
        for (int i = 0; i < 4; i++)
        {
            if (!tree.TryGetValue(pid, out var p))
                return false;
            if (Devices.TeamsExe.Contains(p.Name))
                return true;
            if (p.Parent == 0 || p.Parent == pid)
                return false;
            pid = p.Parent;
        }
        return false;
    }

    /// <summary>pid -> (parent pid, lower-case exe name); empty when the snapshot fails.</summary>
    public static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var result = new Dictionary<int, (int, string)>();
        IntPtr h = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (h == IntPtr.Zero || h == new IntPtr(-1))
            return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(h, ref e))
                return result;
            do
            {
                result[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, (e.szExeFile ?? "").ToLowerInvariant());
                e.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();
            } while (Process32NextW(h, ref e));
        }
        finally
        {
            CloseHandle(h);
        }
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
