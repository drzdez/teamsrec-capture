using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Media;
using TeamsRec.Capture.Config;
using TeamsRec.Capture.Core;
using TeamsRec.Capture.Settings;

namespace TeamsRec.Capture.App;

/// <summary>The tray icon: grey idle / red recording / yellow when the watchdog says no audio arrives, or while
/// idle when the review server processes recordings (with a balloon when one is done), the
/// status as tooltip, and the prototype's menu. It is also the INotifier: balloon tips + Windows sounds.
/// All UI work is marshalled to the UI thread; the monitor loop and the finalizer call in from others.</summary>
public sealed class TrayApp : ApplicationContext, INotifier
{
    private static readonly Icon IconIdle = RoundIcon(Color.FromArgb(0x7f, 0x8c, 0x8d));
    private static readonly Icon IconRec = RoundIcon(Color.FromArgb(0xe7, 0x4c, 0x3c));
    private static readonly Icon IconWarn = RoundIcon(Color.FromArgb(0xf1, 0xc4, 0x0f));
    // the same with a small white "!" mark: something to check, said without text on screen (Mark)
    private static readonly Icon IconRecMarked = RoundIcon(Color.FromArgb(0xe7, 0x4c, 0x3c), marked: true);
    private static readonly Icon IconWarnMarked = RoundIcon(Color.FromArgb(0xf1, 0xc4, 0x0f), marked: true);
    private static readonly Icon IconIdleMarked = RoundIcon(Color.FromArgb(0x7f, 0x8c, 0x8d), marked: true);

    private readonly AppConfig _cfg;
    private readonly Control _ui;             // owns the UI thread's handle, for BeginInvoke from other threads
    private readonly NotifyIcon _icon;
    private readonly MonitorLoop _loop;
    private readonly SettingsServer _settings;
    private readonly ConfigWatcher _watcher;  // edits from the review page's settings apply without a restart
    private readonly FileSystemWatcher? _quitRequest;  // the installer asks to quit before replacing the files
    private bool _quitting;
    private readonly Updater _updater;
    private Release? _balloonUpdate;  // the balloon on screen offers this release (a click installs it)
    private Release? _offerAfterRecording;  // found during a recording: offered when it ends
    private string? _balloonStem;  // the recording the balloon on screen is about (UI thread only)
    private readonly System.Windows.Forms.Timer _reviewPoll = new() { Interval = 3000 };
    private ReviewJobsState? _review;  // what the review server does (ReviewJobs), UI thread only
    private HashSet<string>? _reviewSeen;  // its ended jobs already announced
    private readonly Dictionary<string, string> _marks = new();  // discreet warnings (Mark), UI thread only
    private readonly ToolStripMenuItem _status, _manual, _onsite, _playback, _test, _stopKeep, _abort, _update;

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
        _quitRequest = QuitRequest.Watch(AppContext.BaseDirectory, () => OnUi(OnQuitRequest));

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
        var settings = new ToolStripMenuItem("Settings…", null, (_, _) => OpenReview(settings: true));
        var folder = new ToolStripMenuItem("Open folder", null, (_, _) => OpenFolder());
        _update = new ToolStripMenuItem("", null, (_, _) => { if (_updater?.Available is { } r) InstallUpdate(r); })
        {
            Visible = false,
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
        };
        var checkUpdate = new ToolStripMenuItem("Check for updates", null, (_, _) => CheckUpdatesNow());
        var quit = new ToolStripMenuItem("Quit", null, (_, _) => Quit());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { _status, new ToolStripSeparator(), review, new ToolStripSeparator(),
                             _manual, _onsite, _playback, _stopKeep, _abort, _test, settings, folder,
                             new ToolStripSeparator(), _update, checkUpdate, quit });
        menu.Opening += (_, _) => UpdateMenu();

        _icon = new NotifyIcon
        {
            Icon = IconIdle,
            Text = "teamsrec: idle",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenReview();
        // a click on a balloon opens the review page, on the recording it was about ("Saved …")
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonUpdate is { } rel)
                InstallUpdate(rel);
            else
                OpenReview(stem: _balloonStem);
        };
        _updater = new Updater(cfg, rel => OnUi(() => OnUpdateFound(rel)));
        _reviewPoll.Tick += (_, _) => PollReview();
        _reviewPoll.Start();
        PollReview();
        _loop.Run();
    }

    public MonitorLoop Loop => _loop;

    // ------------------------------------------------------------------ INotifier
    public void Notify(string message)
    {
        OnUi(() =>
        {
            _balloonStem = null;
            _balloonUpdate = null;
            _icon.ShowBalloonTip(5000, "teamsrec", message, ToolTipIcon.None);
        });
    }

    public void NotifyRecording(string message, string stem)
    {
        OnUi(() =>
        {
            _balloonStem = stem;
            _balloonUpdate = null;
            _icon.ShowBalloonTip(8000, "teamsrec – kliknutím otevřete", message, ToolTipIcon.None);
        });
    }

    public void Mark(string key, string? reason)
    {
        OnUi(() =>
        {
            if (reason is null) _marks.Remove(key);
            else _marks[key] = reason;
            RefreshIcon();
        });
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
        if (!rec && _offerAfterRecording is { } offer)
        {
            _offerAfterRecording = null;
            ShowUpdateBalloon(offer);
        }
        bool processing = !rec && _review is { Busy: true };
        bool marked = _marks.Count > 0;
        bool yellow = rec ? !string.IsNullOrEmpty(_loop.AudioWarned) : processing;
        _icon.Icon = (rec && !yellow) ? (marked ? IconRecMarked : IconRec)
                   : yellow ? (marked ? IconWarnMarked : IconWarn)
                   : (marked ? IconIdleMarked : IconIdle);
        var text = processing ? ReviewJobs.Tooltip(_review!) : _loop.Status();
        if (marked) text = "⚠ " + string.Join(" · ", _marks.Values) + "\n" + text;
        _icon.Text = AppLogic.Tooltip(text);
    }

    /// <summary>Every 3 s (UI thread): the review server's jobs – yellow while it processes, a balloon for each
    /// recording it finished (a click opens it). Never during a recording: the balloon waits for its end.</summary>
    private void PollReview()
    {
        try
        {
            var state = ReviewJobs.Read();
            bool changed = (state?.Busy ?? false) != (_review?.Busy ?? false) || state?.Title != _review?.Title
                           || state?.Queued != _review?.Queued;
            if (state is not null)
            {
                if (_loop.Recording && _reviewSeen is not null)
                {
                    // announced after the recording: keep them unseen
                }
                else
                {
                    var done = ReviewJobs.NewlyFinished(state, ref _reviewSeen);
                    if (done.Count > 0)
                    {
                        var last = done[^1];
                        var msg = done.Count == 1 ? ReviewJobs.Balloon(last)
                            : ReviewJobs.Balloon(last) + $" (a {done.Count - 1} další)";
                        if (last.Stem.Length > 0) NotifyRecording(msg, last.Stem);
                        else Notify(msg);
                    }
                }
            }
            _review = state;
            if (changed) RefreshIcon();
        }
        catch (Exception e)
        {
            FileLog.Exception("review jobs", e);
        }
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
    private void OpenReview(bool settings = false, string? stem = null)
    {
        var (exe, args) = AppLogic.ReviewLaunch(_cfg.TrayOpen, _cfg.ReviewApp,
                                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                settings, stem);
        if (!File.Exists(exe))
        {
            if (settings)
            {
                OpenSettings();  // no review app on this PC: the capture app's own page (its fields only)
                return;
            }
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

    // ------------------------------------------------------------------ updates
    /// <summary>A newer release (UI thread): the menu item always, the balloon unless declined; never during a
    /// recording, then right after it.</summary>
    private void OnUpdateFound(Release rel)
    {
        _update.Text = $"Install version {rel.Version}…";
        _update.Visible = true;
        if (Updater.Declined == rel.Version)
            return;
        if (_loop.Recording)
            _offerAfterRecording = rel;
        else
            ShowUpdateBalloon(rel);
    }

    private void ShowUpdateBalloon(Release rel)
    {
        _balloonStem = null;
        _balloonUpdate = rel;
        _icon.ShowBalloonTip(10000, "teamsrec – nová verze",
                             $"Je k dispozici teamsrec-capture {rel.Version} (máte {Versions.AppVersion}). Kliknutím nainstalujete.",
                             ToolTipIcon.Info);
    }

    /// <summary>"Check for updates" in the menu: works with update_check off too, and offers a declined version again.</summary>
    private void CheckUpdatesNow()
    {
        Task.Run(async () =>
        {
            var rel = await _updater.CheckAsync();
            if (rel is null)
                Notify($"Novější verze není k dispozici (máte {Versions.AppVersion}), nebo se ji nepodařilo zjistit.");
            else
                OnUi(() => ShowUpdateBalloon(rel));
        });
    }

    /// <summary>Confirm, download, verify, run the MSI (which quits this app and starts the new one). Not while
    /// recording: the install would have to stop it.</summary>
    private void InstallUpdate(Release rel)
    {
        if (_loop.Recording)
        {
            Notify("Novou verzi nainstalujte po skončení nahrávání (menu ikony).");
            return;
        }
        Task.Run(async () =>
        {
            if (!Dialogs.YesNo($"Nainstalovat teamsrec-capture {rel.Version}? Teď máte {Versions.AppVersion}.\n\n" +
                               "Aplikace se na chvíli ukončí a po instalaci se znovu spustí.", timeoutS: 120))
            {
                Updater.Declined = rel.Version;
                return;
            }
            if (_loop.Recording)  // a call may have started while the box was open
            {
                Notify("Nahrávání právě začalo – novou verzi nainstalujte po něm (menu ikony).");
                return;
            }
            Notify($"Stahuji teamsrec-capture {rel.Version}…");
            try
            {
                await Updater.InstallAsync(rel);
            }
            catch (Exception e)
            {
                FileLog.Exception("update install", e);
                Notify($"Novou verzi se nepodařilo nainstalovat: {e.Message}");
            }
        });
    }

    /// <summary>The installer wants to replace the files. Never in the middle of a recording: the install then
    /// fails with a message and can be run again after the meeting.</summary>
    private void OnQuitRequest()
    {
        if (_loop.Recording)
        {
            Log.Info("the installer asked to quit during a recording: refused");
            Notify("Instalace nové verze počká: právě se nahrává. Spusťte ji znovu po schůzce.");
            return;
        }
        Log.Info("the installer asked to quit");
        Quit();
    }

    private void Quit()
    {
        if (_quitting)
            return;
        _quitting = true;
        _reviewPoll.Stop();
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
    internal static Icon RoundIcon(Color color, bool marked = false)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 4, 4, 24, 24);
            if (marked)  // a white "!" in the dot, readable at 16 px
            {
                using var white = new SolidBrush(Color.White);
                g.FillRectangle(white, 14, 8, 4, 11);
                g.FillEllipse(white, 14, 21, 4, 4);
            }
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
            _quitRequest?.Dispose();
            _updater.Dispose();
            _settings.Dispose();
            _loop.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
