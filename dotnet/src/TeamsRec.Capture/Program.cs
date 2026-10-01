using TeamsRec.Capture.App;
using TeamsRec.Capture.Config;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Recording;
using TeamsRec.Capture.Screen;

namespace TeamsRec.Capture;

static class Program
{
    // The SAME name as the Python prototype: autostart + desktop shortcut, or the prototype and this app,
    // must never record the same call twice.
    internal const string MutexName = @"Local\teamsrec-capture";

    [STAThread]
    static int Main(string[] args)
    {
        var cfg = InstallerFolder.LoadConfig(out var installerFolder);
        string? unusable = null;
        try
        {
            Directory.CreateDirectory(cfg.OutDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // a drive this PC lacks, or a folder without write access: record into the default rather than not at all
            unusable = $"{cfg.OutDir} ({e.Message})";
            cfg.OutDir = AppConfig.ExpandUser(AppConfig.DefaultOutDir);
            Directory.CreateDirectory(cfg.OutDir);
        }
        using var fileLog = new FileLog(Path.Combine(cfg.OutDir, "teamsrec.log"));
        Log.Sink = fileLog.Write;
        if (installerFolder is not null)
            Log.Info($"recordings folder from the installer: {installerFolder}");
        if (unusable is not null)
            Log.Warn($"recordings folder {unusable} not usable, recording into {cfg.OutDir}");

        // the window capture of a running recording, started by the app itself (ScreenCaptureProcess)
        if (args.Length == 3 && args[0] == ScreenCaptureProcess.ChildArg)
            return ScreenCaptureProcess.RunChild(args[1], args[2]);

        using var mutex = new Mutex(false, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Log.Info("teamsrec is already running, exiting");
            return 0;
        }
        Log.Info($"{Versions.AppName} {Versions.AppVersion} started (pid {Environment.ProcessId})");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error($"unhandled exception\n{e.ExceptionObject}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => FileLog.Exception("UI thread", e.Exception);

        try
        {
            Orphans.Recover(cfg.OutDir);  // recordings a crash / power loss left without a sidecar
        }
        catch (Exception e)
        {
            FileLog.Exception("orphan recovery", e);
        }

        ApplicationConfiguration.Initialize();
        using var app = new TrayApp(cfg, SystemClock.Instance);
        Application.Run(app);
        app.WaitForFinalizers(TimeSpan.FromMinutes(2));
        Log.Info("teamsrec stopped");
        Log.Sink = null;
        return 0;
    }
}
