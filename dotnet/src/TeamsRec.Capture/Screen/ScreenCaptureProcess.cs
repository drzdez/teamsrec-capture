using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Detection;
using TeamsRec.Capture.Recording;

namespace TeamsRec.Capture.Screen;

/// <summary>
/// The window capture in a separate process (<c>teamsrec-capture.exe --screen-capture &lt;stem&gt; &lt;started&gt;</c>),
/// as the prototype ran it: a hung PrintWindow, a GDI or encoder crash stays in there and cannot take the audio
/// recording down with it. The child records until its stdin closes (the parent stops it, or the parent died),
/// then writes &lt;stem&gt;_screens.json. When it reports nothing (crashed, killed after the timeout), the videos
/// on disk are taken as they are.
/// </summary>
public sealed class ScreenCaptureProcess
{
    public const string ChildArg = "--screen-capture";
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(45);

    private readonly Process _proc;
    private readonly string _stem;
    private List<ScreenInfo>? _result;

    public ScreenCaptureProcess(string stemPath, DateTime started)
    {
        _stem = stemPath;
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("own executable not known");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(ChildArg);
        psi.ArgumentList.Add(stemPath);
        psi.ArgumentList.Add(started.ToString("o", CultureInfo.InvariantCulture));
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("screen capture process did not start");
        Log.Info($"screen capture process started (pid {_proc.Id})");
    }

    /// <summary>Stops the child and returns the videos it recorded. Idempotent; never throws.</summary>
    public List<ScreenInfo> Stop(TimeSpan? timeout = null)
    {
        if (_result is not null) return _result;
        try
        {
            _proc.StandardInput.Close();  // the child's signal to finish
            if (!_proc.WaitForExit(timeout ?? StopTimeout))
            {
                Log.Warn($"screen capture process did not finish in {StopTimeout.TotalSeconds:0} s, stopping it");
                // only the child: its encoders see their input close and still finish their files
                _proc.Kill(entireProcessTree: false);
                _proc.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception e)
        {
            Log.Warn($"screen capture process stop: {e.Message}");
        }
        _result = ReadReport(_stem);
        if (_result is null)
        {
            Log.Warn("screen capture process left no report (crashed?); videos on disk are kept");
            _result = Recovered(_stem);
        }
        try { _proc.Dispose(); }
        catch (Exception) { }
        return _result;
    }

    internal static string ReportPath(string stemPath) => stemPath + "_screens.json";

    internal static void WriteReport(string stemPath, List<ScreenInfo> screens) =>
        File.WriteAllText(ReportPath(stemPath), JsonSerializer.Serialize(screens));

    /// <summary>The child's report, deleted once read; null when there is none or it is unreadable.</summary>
    internal static List<ScreenInfo>? ReadReport(string stemPath)
    {
        var path = ReportPath(stemPath);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<ScreenInfo>>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"screen capture report unreadable: {e.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The videos on disk, without what only the capture knew (end, frames, titles).</summary>
    internal static List<ScreenInfo> Recovered(string stemPath) =>
        Orphans.ScreensOnDisk(stemPath)
            .Select(s => new ScreenInfo(s.File, s.Fps, s.Width, s.Height, s.StartOffsetS, 0, s.Frames, [], Recovered: true))
            .ToList();

    /// <summary>The child process: capture the Teams windows until stdin closes, then report.</summary>
    public static int RunChild(string stemPath, string startedIso)
    {
        // frames are measured and grabbed in physical pixels on every monitor, whatever its scale
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var ff = Mixer.FindFfmpeg();
        if (ff is null)
        {
            Log.Warn("screen capture process: ffmpeg not found");
            return 1;
        }
        var started = DateTime.Parse(startedIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var capture = new ScreenCapture(stemPath, started, ff, WindowTitles.Teams);
        try
        {
            using var stdin = Console.OpenStandardInput();
            stdin.CopyTo(Stream.Null);  // returns when the parent closes the pipe or dies
        }
        catch (Exception e)
        {
            Log.Warn($"screen capture process: stdin: {e.Message}");
        }
        var screens = capture.Stop();
        WriteReport(stemPath, screens);
        return 0;
    }
}
