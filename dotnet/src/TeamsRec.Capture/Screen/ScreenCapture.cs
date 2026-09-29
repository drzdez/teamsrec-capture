using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Screen;

/// <summary>
/// Every Teams window becomes &lt;stem&gt;_screen&lt;N&gt;.mp4 (2 fps, x264, 1600x900 canvas). Teams uses several
/// windows during a call - the meeting window, a popped-out gallery, shared content - so each gets its own file;
/// the analysis later looks for name labels in all of them. A window that is minimized or briefly fails to
/// render gets its last frame repeated, so the timeline stays aligned with the audio.
/// The prototype ran this in a child process because a Tk crash there once killed a recording; here it is a
/// background thread whose every step is caught and logged - nothing is ever thrown into the caller.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    public const int Fps = 2;
    public const int Width = 1600, Height = 900;   // frames are fitted into this canvas (constant size for the encoder)
    public const int MinWinW = 500, MinWinH = 350;  // smaller Teams windows (toasts, popups) are ignored
    private const int FrameBytes = Width * Height * 3;

    private sealed class WindowVideo
    {
        public required Process Proc;
        public required string File;
        public required double StartOffsetS;
        public readonly List<string> Titles = new();
        public int Frames;
        public byte[]? Last;
        public bool Dead;  // encoder gone: warn once, keep the window so it is not reopened as a new file
    }

    private readonly string _stem;
    private readonly DateTime _started;
    private readonly string _ffmpeg;
    private readonly Func<List<(IntPtr Hwnd, string Title)>> _windows;
    private readonly bool _sizeFilter;
    private readonly IClock _clock;
    private readonly Dictionary<IntPtr, WindowVideo> _screens = new();  // loop thread only
    private readonly List<ScreenInfo> _done = new();                // guarded by _done
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private int _n;
    private List<ScreenInfo>? _result;

    /// <param name="stemPath">Recording stem (full path without extension); files are &lt;stem&gt;_screen&lt;N&gt;.mp4.</param>
    /// <param name="started">Recording start, the zero of start/end offsets (aligned with the audio).</param>
    /// <param name="ffmpeg">ffmpeg executable.</param>
    /// <param name="windows">(hwnd, title) of the visible top-level windows to record (Teams windows).</param>
    /// <param name="isMeetingScreenFilter">true = only windows of at least 500x350 qualify (skips toasts and
    /// popups, as the prototype); false = every window the delegate returns is recorded.</param>
    /// <param name="clock">Time source for the offsets (default: system clock).</param>
    public ScreenCapture(string stemPath, DateTime started, string ffmpeg,
                         Func<List<(IntPtr Hwnd, string Title)>> windows, bool isMeetingScreenFilter = true,
                         IClock? clock = null)
    {
        _stem = stemPath;
        _started = started;
        _ffmpeg = ffmpeg;
        _windows = windows;
        _sizeFilter = isMeetingScreenFilter;
        _clock = clock ?? SystemClock.Instance;
        _thread = new Thread(Loop) { IsBackground = true, Name = "screen-capture" };
        _thread.Start();
    }

    /// <summary>Does a window of this size get its own video? (toasts and popups do not)</summary>
    internal static bool Qualifies(int width, int height) => width >= MinWinW && height >= MinWinH;

    /// <summary>Where a w x h window lands on the canvas: scaled to fit keeping the aspect ratio, never
    /// enlarged, centred with black borders (Python fit_canvas, int truncation included).</summary>
    internal static (int X, int Y, int W, int H) Fit(int w, int h)
    {
        var scale = Math.Min(Math.Min((double)Width / w, (double)Height / h), 1.0);
        int fw = Math.Max(1, (int)(w * scale)), fh = Math.Max(1, (int)(h * scale));
        return ((Width - fw) / 2, (Height - fh) / 2, fw, fh);
    }

    private double Offset() => Math.Round((_clock.Now - _started).TotalSeconds, 1);

    private void Loop()
    {
        var period = 1.0 / Fps;
        while (!_stop.IsSet)
        {
            var t0 = Stopwatch.GetTimestamp();
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Log.Error($"screen capture: {e}");
            }
            var left = period - Stopwatch.GetElapsedTime(t0).TotalSeconds;
            _stop.Wait(TimeSpan.FromSeconds(Math.Max(0.0, left)));
        }
        foreach (var hwnd in _screens.Keys.ToList())
        {
            try
            {
                Close(hwnd);
            }
            catch (Exception e)
            {
                Log.Error($"screen capture close: {e}");
            }
        }
    }

    private void Tick()
    {
        var present = new Dictionary<IntPtr, string>();
        foreach (var (hwnd, title) in _windows())
        {
            if (!Native.GetWindowRect(hwnd, out var r)) continue;
            // A minimized window reports a tiny rect and is not "present"; its file stays open (below) and
            // repeats the last frame until the window comes back or is closed for good.
            if (!_sizeFilter || Qualifies(r.Right - r.Left, r.Bottom - r.Top))
                present[hwnd] = title;
        }
        foreach (var hwnd in _screens.Keys.ToList())
            if (!present.ContainsKey(hwnd) && !Native.IsWindow(hwnd))
                Close(hwnd);  // window closed for good
        foreach (var (hwnd, title) in present)
        {
            if (!_screens.ContainsKey(hwnd))
            {
                var opened = Open(title);
                if (opened is null) continue;
                _screens[hwnd] = opened;
            }
            var sc = _screens[hwnd];
            if (!sc.Titles.Contains(title)) sc.Titles.Add(title);
        }
        foreach (var (hwnd, sc) in _screens)
        {
            if (sc.Dead) continue;
            byte[]? frame = null;
            try
            {
                frame = Grab(hwnd);
            }
            catch (Exception)
            {
                frame = null;  // GDI / WebView2 hiccup: repeat the last frame
            }
            frame ??= sc.Last ?? new byte[FrameBytes];
            try
            {
                sc.Proc.StandardInput.BaseStream.Write(frame, 0, frame.Length);
                sc.Last = frame;
                sc.Frames++;
            }
            catch (Exception)
            {
                sc.Dead = true;
                Log.Warn($"screen capture: encoder for {sc.File} died");
            }
        }
    }

    private WindowVideo? Open(string title)
    {
        _n++;
        var path = $"{_stem}_screen{_n}.mp4";
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
        };
        foreach (var a in new[]
                 {
                     "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgr24",  // GDI bitmaps are BGR
                     "-s", $"{Width}x{Height}", "-r", Fps.ToString(), "-i", "-",
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p",
                     "-g", (Fps * 10).ToString(),
                     "-movflags", "+frag_keyframe+empty_moov+default_base_moof",  // playable even if the app dies mid-call
                     path,
                 })
            psi.ArgumentList.Add(a);
        try
        {
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            var sc = new WindowVideo { Proc = proc, File = Path.GetFileName(path), StartOffsetS = Offset() };
            sc.Titles.Add(title);
            Log.Info($"screen capture: {sc.File} <- '{title}'");
            return sc;
        }
        catch (Exception e)
        {
            Log.Warn($"screen capture: ffmpeg for {Path.GetFileName(path)} not started: {e.Message}");
            return null;
        }
    }

    private void Close(IntPtr hwnd)
    {
        var sc = _screens[hwnd];
        _screens.Remove(hwnd);
        try
        {
            sc.Proc.StandardInput.BaseStream.Close();
            if (!sc.Proc.WaitForExit(20_000)) sc.Proc.Kill();
        }
        catch (Exception)
        {
            try { sc.Proc.Kill(); } catch (Exception) { /* already gone */ }
        }
        finally
        {
            sc.Proc.Dispose();
        }
        var info = new ScreenInfo(sc.File, Fps, Width, Height, sc.StartOffsetS, Offset(), sc.Frames, sc.Titles.ToList());
        lock (_done) _done.Add(info);
    }

    /// <summary>Screenshot of one window fitted into the canvas as raw BGR24 (works when the window is covered by
    /// other windows, not when minimized: null then, and when it is too small or PrintWindow fails).</summary>
    private byte[]? Grab(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) return null;
        if (!Native.GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0 || (_sizeFilter && !Qualifies(w, h))) return null;

        using var shot = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(shot))
        {
            var hdc = g.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT: without it the WebView2 content of the new Teams comes out black.
                if (!Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT)) return null;
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        var (x, y, fw, fh) = Fit(w, h);
        using var canvas = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(shot, new Rectangle(x, y, fw, fh));
        }
        return ToBgr24(canvas);
    }

    private static byte[] ToBgr24(Bitmap canvas)
    {
        var data = canvas.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var buf = new byte[FrameBytes];
            const int row = Width * 3;
            // the stride may be padded to 4 bytes (it is not for 1600 px, but do not depend on it)
            for (var yy = 0; yy < Height; yy++)
                Marshal.Copy(data.Scan0 + yy * data.Stride, buf, yy * row, row);
            return buf;
        }
        finally
        {
            canvas.UnlockBits(data);
        }
    }

    /// <summary>Stops the capture, closes every encoder and returns the videos that got at least one frame.
    /// Idempotent; never throws.</summary>
    public List<ScreenInfo> Stop()
    {
        if (_result is not null) return _result;
        try
        {
            _stop.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(30)))
                Log.Warn("screen capture: thread did not finish in 30 s, videos on disk are kept");
        }
        catch (Exception e)
        {
            Log.Error($"screen capture stop: {e}");
        }
        lock (_done) _result = _done.Where(s => s.Frames > 0).ToList();
        return _result;
    }

    // _stop is not disposed: after a join timeout the thread may still be waiting on it.
    public void Dispose() => Stop();

    private static class Native
    {
        public const uint PW_RENDERFULLCONTENT = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    }
}
