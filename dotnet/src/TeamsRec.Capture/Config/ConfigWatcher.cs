using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Config;

/// <summary>
/// Picks up edits of the shared teamsrec.toml while the app runs: the settings page of teamsrec-transcribe (or an
/// editor) can change the [capture] keys, the user name and the calendar switch without a restart. The recordings
/// folder stays as it was until the next start (a running recording must not change folders). A file that does not
/// parse right now is skipped, never turned into defaults.
/// </summary>
public sealed class ConfigWatcher : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);  // one save fires several events

    private readonly AppConfig _cfg;
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer _timer;

    public ConfigWatcher(AppConfig cfg)
    {
        _cfg = cfg;
        _path = Path.GetFullPath(string.IsNullOrEmpty(cfg.SourcePath) ? AppConfig.DefaultPath : cfg.SourcePath);
        _timer = new System.Threading.Timer(_ => Reload());
        var dir = Path.GetDirectoryName(_path);
        if (dir is null || !Directory.Exists(dir))
            return;  // no config folder yet: nothing to watch (the first save through the tray creates it)
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
        {
            // the Python side writes a .tmp and renames it over the file
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        _watcher.Changed += (_, _) => Poke();
        _watcher.Created += (_, _) => Poke();
        _watcher.Renamed += (_, _) => Poke();
        _watcher.EnableRaisingEvents = true;
    }

    private void Poke() => _timer.Change(Settle, Timeout.InfiniteTimeSpan);

    /// <summary>Read the file again and apply what may change at run time; the changed names, for the log.</summary>
    internal List<string> Reload()
    {
        try
        {
            AppConfig.ParseToml(File.ReadAllText(_path, System.Text.Encoding.UTF8));  // half-written or broken: skip
        }
        catch (Exception e)
        {
            Log.Warn($"config changed but is not readable now, keeping the current settings: {e.Message}");
            return [];
        }
        var changed = _cfg.ApplyRuntime(AppConfig.Load(_path));
        if (changed.Count > 0)
            Log.Info("config reloaded: " + string.Join(", ", changed));
        return changed;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
