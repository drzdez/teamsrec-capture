using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.App;

/// <summary>The installer asks a running app to quit before an upgrade or uninstall replaces its files: it drops
/// <c>quit.request</c> next to the exe and waits for the process to end. A file rather than a named event: the
/// installer's custom action may run in another session, where <c>Local\</c> names differ.</summary>
public static class QuitRequest
{
    public const string FileName = "quit.request";

    /// <summary>Watches <paramref name="dir"/> for the request file; deletes a stale one first, so that an app
    /// started after an aborted install does not quit at once. Null when the folder cannot be watched.</summary>
    public static FileSystemWatcher? Watch(string dir, Action onRequest)
    {
        var path = Path.Combine(dir, FileName);
        TryDelete(path);
        try
        {
            var w = new FileSystemWatcher(dir, FileName) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler handler = (_, _) =>
            {
                TryDelete(path);
                onRequest();
            };
            w.Created += handler;
            w.Changed += handler;
            w.EnableRaisingEvents = true;
            return w;
        }
        catch (Exception e)
        {
            Log.Warn($"cannot watch {dir} for the installer's quit request: {e.Message}");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
