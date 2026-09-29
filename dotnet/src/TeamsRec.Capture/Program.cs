using TeamsRec.Capture.App;
using TeamsRec.Capture.Config;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Recording;

namespace TeamsRec.Capture;

static class Program
{
    // The SAME name as the Python prototype: autostart + desktop shortcut, or the prototype and this app,
    // must never record the same call twice.
    internal const string MutexName = @"Local\teamsrec-capture";

    [STAThread]
    static int Main()
    {
        var cfg = AppConfig.Load();
        Directory.CreateDirectory(cfg.OutDir);
        using var fileLog = new FileLog(Path.Combine(cfg.OutDir, "teamsrec.log"));
        Log.Sink = fileLog.Write;

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
