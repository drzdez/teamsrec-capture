using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TeamsRec.Capture.Detection;

/// <summary>A Teams window: its title, whether it is minimized, its size.</summary>
public sealed record TeamsWindow(string Title, bool Minimized, int Width, int Height);

/// <summary>Titles of visible top-level windows per process (EnumWindows), used to tell a meeting from a join screen.</summary>
public static class WindowTitles
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    /// <summary>Every visible Teams window with its state: minimized, size (the screen capture sees a window that
    /// is covered by others, not one that is minimized or tiny).</summary>
    public static List<TeamsWindow> TeamsStates() =>
        Teams().Select(w =>
        {
            var min = IsIconic(w.Hwnd);
            var ok = GetWindowRect(w.Hwnd, out var r);
            return new TeamsWindow(w.Title, min, ok ? r.Right - r.Left : 0, ok ? r.Bottom - r.Top : 0);
        }).ToList();

    /// <summary>(hwnd, title) of every visible top-level Teams window.</summary>
    public static List<(IntPtr Hwnd, string Title)> Teams() => Windows(CallDetector.TeamsExe);

    /// <summary>Titles of the visible windows of the processes with these exe names.</summary>
    public static List<string> OfExes(IEnumerable<string> exeNames) =>
        Windows(exeNames).Select(w => w.Title).ToList();

    /// <summary>(hwnd, title) of visible, titled top-level windows owned by processes with these exe names.</summary>
    public static List<(IntPtr Hwnd, string Title)> Windows(IEnumerable<string> exeNames)
    {
        var want = new HashSet<string>(exeNames.Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);
        var pids = PidsOf(want);
        var output = new List<(IntPtr, string)>();
        if (pids.Count == 0)
            return output;
        EnumWindowsProc cb = (hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pids.Contains(pid))
                {
                    var t = TextOf(hwnd);
                    if (t.Length > 0)
                        output.Add((hwnd, t));
                }
            }
            return true;  // keep enumerating
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return output;
    }

    private static string TextOf(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // Process.ProcessName has no ".exe"; the prototype (psutil) compares exe file names, so add it back.
    private static HashSet<uint> PidsOf(HashSet<string> exeNames)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (exeNames.Contains(p.ProcessName.ToLowerInvariant() + ".exe"))
                    pids.Add((uint)p.Id);
            }
            catch (InvalidOperationException)
            {
                // exited meanwhile
            }
            finally
            {
                p.Dispose();
            }
        }
        return pids;
    }
}
