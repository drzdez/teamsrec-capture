using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using TeamsRec.Capture.Config;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Settings;

namespace TeamsRec.Capture.App;

/// <summary>The tray icon: grey idle / red recording / yellow when the watchdog says no audio arrives, the
/// status as tooltip, and the prototype's menu. It is also the INotifier: balloon tips + Windows sounds.
/// All UI work is marshalled to the UI thread; the monitor loop and the finalizer call in from others.</summary>
public sealed class TrayApp : ApplicationContext, INotifier
{
    private static readonly Icon IconIdle = RoundIcon(Color.FromArgb(0x7f, 0x8c, 0x8d));
    private static readonly Icon IconRec = RoundIcon(Color.FromArgb(0xe7, 0x4c, 0x3c));
    private static readonly Icon IconWarn = RoundIcon(Color.FromArgb(0xf1, 0xc4, 0x0f));

    private readonly AppConfig _cfg;
    private readonly Control _ui;             // owns the UI thread's handle, for BeginInvoke from other threads
    private readonly NotifyIcon _icon;
    private readonly MonitorLoop _loop;
    private readonly SettingsServer _settings;
    private readonly ConfigWatcher _watcher;  // edits from the review page's settings apply without a restart
    private readonly ToolStripMenuItem _status, _manual, _onsite, _playback, _test, _stopKeep, _abort;

    public TrayApp(AppConfig cfg, IClock clock)
    {
        _cfg = cfg;
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle;  // force the handle now: BeginInvoke from the loop must work before the first repaint

        _loop = new MonitorLoop(cfg, clock, this);
        _loop.Changed += () => OnUi(RefreshIcon);
        _settings = new SettingsServer(cfg, () => _loop.Recording);
        _watcher = new ConfigWatcher(cfg);

        _status = new ToolStripMenuItem("Idle — waiting for a call") { Enabled = false };
        _manual = new ToolStripMenuItem("Record now (manual)", null, (_, _) => _loop.StartManual());
        _onsite = new ToolStripMenuItem("Record on-site meeting (microphone only)", null, (_, _) => _loop.StartOnsite());
        _playback = new ToolStripMenuItem("Record playback (system audio only)", null, (_, _) => StartPlayback());
        _stopKeep = new ToolStripMenuItem("Stop & keep", null, (_, _) => _loop.StopAsync("tray stop"));
        _abort = new ToolStripMenuItem("Abort & delete", null, (_, _) => _loop.StopAsync("aborted"));
        _test = new ToolStripMenuItem("Test microphone", null, (_, _) => _loop.TestMicrophone());
        var review = new ToolStripMenuItem("Otevřít přepisy", null, (_, _) => OpenReview())
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),  // the double-click action
            ToolTipText = "stránka kontroly přepisů, zápisů a nastavení (poklepání na ikonu)",
        };
        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => OpenSettings());
        var folder = new ToolStripMenuItem("Open folder", null, (_, _) => OpenFolder());
        var quit = new ToolStripMenuItem("Quit", null, (_, _) => Quit());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { _status, new ToolStripSeparator(), review, new ToolStripSeparator(),
                             _manual, _onsite, _playback, _stopKeep, _abort, _test, settings, folder, quit });
        menu.Opening += (_, _) => UpdateMenu();

        _icon = new NotifyIcon
        {
            Icon = IconIdle,
            Text = "teamsrec: idle",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenReview();
        _loop.Run();
    }

    public MonitorLoop Loop => _loop;

    // ------------------------------------------------------------------ INotifier
    public void Notify(string message)
    {
        OnUi(() => _icon.ShowBalloonTip(5000, "teamsrec", message, ToolTipIcon.None));
    }

    public void Beep(bool error = false)
    {
        try
        {
            // MB_ICONHAND for a recording that did not happen, MB_ICONEXCLAMATION for a warning
            (error ? SystemSounds.Hand : SystemSounds.Exclamation).Play();
        }
        catch (Exception)
        {
            // no sound device: the balloon still says it
        }
    }

    // ------------------------------------------------------------------ UI
    private void OnUi(Action a)
    {
        try
        {
            if (_ui.IsDisposed) return;
            if (_ui.InvokeRequired) _ui.BeginInvoke(a);
            else a();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // shutting down
        }
    }

    private void RefreshIcon()
    {
        bool rec = _loop.Recording;
        _icon.Icon = rec ? (string.IsNullOrEmpty(_loop.AudioWarned) ? IconRec : IconWarn) : IconIdle;
        _icon.Text = AppLogic.Tooltip(_loop.Status());
    }

    /// <summary>The same enabled rules as the prototype: starting only while idle, stopping only while recording.</summary>
    private void UpdateMenu()
    {
        bool rec = _loop.Recording;
        _status.Text = _loop.Status();
        _manual.Enabled = _onsite.Enabled = _playback.Enabled = _test.Enabled = !rec;
        _stopKeep.Enabled = _abort.Enabled = rec;
    }

    /// <summary>Ask the title of the recording being played (the foreground window's title as default).</summary>
    private void StartPlayback()
    {
        var fg = Dialogs.ForegroundWindowTitle();
        var title = Dialogs.InputBox("Title of the recording being played:", fg.Length > 0 ? fg : "playback");
        if (title is not null) _loop.StartPlayback(title);
    }

    private void OpenSettings()
    {
        try
        {
            _settings.Open();
        }
        catch (Exception e)
        {
            FileLog.Exception("settings page", e);
            Notify($"Nastavení se nepodařilo otevřít: {e.Message}");
        }
    }

    /// <summary>The review page (teamsrec-transcribe) in its desktop window or in the browser, as tray_open says.</summary>
    private void OpenReview()
    {
        var (exe, args) = AppLogic.ReviewLaunch(_cfg.TrayOpen, _cfg.ReviewApp,
                                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!File.Exists(exe))
        {
            Notify($"Aplikace pro přepisy není nainstalovaná ({exe}). Nastavte [capture] review_app.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        }
        catch (Exception e)
        {
            FileLog.Exception("open review", e);
            Notify($"Přepisy se nepodařilo otevřít: {e.Message}");
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_cfg.OutDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_cfg.OutDir}\"") { UseShellExecute = false });
        }
        catch (Exception e)
        {
            FileLog.Exception("open folder", e);
        }
    }

    private void Quit()
    {
        _icon.Visible = false;
        // stopping may take a moment (streams, screen encoders): off the UI thread, then leave the message loop
        Task.Run(() =>
        {
            try { _loop.Quit(); }
            catch (Exception e) { FileLog.Exception("quit", e); }
            OnUi(ExitThread);
        });
    }

    /// <summary>After Application.Run: let running finalizers write their sidecars.</summary>
    public void WaitForFinalizers(TimeSpan timeout) => _loop.WaitForFinalizers(timeout);

    // ------------------------------------------------------------------ icons
    /// <summary>A filled circle like the prototype's 64 px PIL image (ellipse 8..56).</summary>
    internal static Icon RoundIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 24, 24);
        }
        // three icons for the process lifetime: the HICON is intentionally never destroyed
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _watcher.Dispose();
            _settings.Dispose();
            _loop.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
