using System.Globalization;
using System.Text;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.App;

/// <summary>Appends to &lt;out_dir&gt;/teamsrec.log in the prototype's format
/// ("%(asctime)s %(levelname)s %(message)s" = "2026-09-29 16:58:01,123 INFO message"), so the Python and the
/// .NET app can share one log file and the same grep habits keep working.</summary>
public sealed class FileLog : IDisposable
{
    private readonly object _lock = new();
    private readonly string _path;
    private StreamWriter? _writer;

    public FileLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Open();
    }

    private void Open()
    {
        // FileShare.ReadWrite | Delete: the Python prototype (or an editor) may have the same file open;
        // a log must never be the reason the recorder does not start.
        var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>One log line, exactly like Python logging's default asctime + levelname.</summary>
    public static string Format(DateTime at, string level, string message) =>
        at.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture) + " " + level + " " + message;

    /// <summary>Log.Sink target: (level, message).</summary>
    public void Write(string level, string message)
    {
        var line = Format(DateTime.Now, level, message);
        lock (_lock)
        {
            try
            {
                if (_writer is null) Open();
                _writer!.WriteLine(line);
            }
            catch (IOException)
            {
                // the disk went away (a network drive, a full disk): drop the line rather than crash a recording
                try { _writer?.Dispose(); } catch (IOException) { }
                _writer = null;
            }
            catch (ObjectDisposedException)
            {
                _writer = null;
            }
        }
    }

    /// <summary>Python log.exception: the message plus the stack trace, at ERROR.</summary>
    public static void Exception(string message, Exception e) => Log.Error($"{message}\n{e}");

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
