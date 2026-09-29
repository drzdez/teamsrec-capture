// Shared types of the .NET capture app. Every module compiles against these; change them only in the
// integration step. The Python prototype (legacy/teamsrec.py) is the behavioural reference, the recording
// contract (docs/recording-format.md) the data reference.
namespace TeamsRec.Capture.Core;

/// <summary>Time source, so the watchdog, the calendar offer and the finalizer are testable.</summary>
public interface IClock
{
    DateTime Now { get; }
    double Seconds { get; }  // monotonic-ish seconds, like Python time.time()
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime Now => DateTime.Now;
    public double Seconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

/// <summary>Tray notifications and sounds, so logic can be tested without a tray icon.</summary>
public interface INotifier
{
    void Notify(string message);
    void Beep(bool error = false);
}

/// <summary>A recorded audio track (contract: tracks.sys / tracks.mic).</summary>
public sealed record TrackInfo(string File, int SampleRate, int Channels, string Device);

/// <summary>An input device that can be recorded from now.</summary>
public sealed record InputDevice(string Id, string Name, int Channels, int SampleRate, bool IsDefault);

/// <summary>A microphone Windows knows but cannot record from (disabled, unplugged, not present).</summary>
public sealed record UnavailableInput(string Name, string Device, string Reason);

/// <summary>An active audio capture session: which process records from which microphone.</summary>
public sealed record CaptureSession(string DeviceName, int ProcessId);

/// <summary>A call running in an app other than Teams (contract: call_app).</summary>
public sealed record CallApp(string App, string Id, string Title);

/// <summary>A calendar item (Outlook). Match = "title" | "time" | "manual".</summary>
public sealed record CalendarItem(string Subject, DateTime Start, DateTime End, string Organizer,
                                  IReadOnlyList<string> Attendees, bool Teams, string Match = "");

/// <summary>One captured Teams window video (contract: screens[]).</summary>
public sealed record ScreenInfo(string File, int Fps, int Width, int Height, double StartOffsetS,
                                double EndOffsetS, int Frames, IReadOnlyList<string> Titles);

/// <summary>What the recording was, captured at stop time (the next recording may start before the sidecar
/// is written - Python: the `info` dict passed to _finalize).</summary>
public sealed record RecordingInfo(string Title, CalendarItem? Calendar, string TitleSource,
                                   IReadOnlySet<string> TitlesSeen, string? Continues, string Source,
                                   string? CallApp);

public enum SourceKind { Live, Manual, Playback, Onsite }

public static class Versions
{
    public const string AppName = "teamsrec-capture";
    public const string AppVersion = "1.0.0";
    public const int FormatVersion = 1;
}

/// <summary>What the watchdog and the finalizer need from a running recorder (Audio.Recorder implements it).
/// Times are IClock.Seconds values.</summary>
public interface IAudioSource
{
    double LastData { get; }        // last buffer from any stream
    double LastLoud { get; }        // last loud buffer on the loopback (sys) track
    double LastMicLoud { get; }     // last loud buffer on the mic
    double LastMicAlive { get; }    // last mic buffer with at least room noise (peak > ALIVE_LEVEL)
    double? LastMicData { get; }    // last mic buffer at all; null = never
    bool MicOnly { get; }           // on-site: only the room microphone
    bool WithMic { get; }
    int Reopens { get; }
    long BytesReceived { get; }
    bool HeardSys { get; }          // anything but digital silence on the loopback
    bool HeardMic { get; }
    DateTime Started { get; }
    IReadOnlyDictionary<string, TrackInfo> Tracks { get; }  // "sys" / "mic"
    string MicName { get; set; }    // preferred input (name fragment); "" = Windows default input
    bool Reopen();                  // reopen the streams on the current devices, pad the gap with silence
}

/// <summary>A recording after its streams were closed, handed to the finalizer.</summary>
public sealed record StoppedRecording(string StemPath, DateTime Started, double DurationS, int Reopens,
                                      bool HeardSys, bool HeardMic,
                                      IReadOnlyDictionary<string, TrackInfo> Tracks,
                                      IReadOnlyList<string> Files, IReadOnlyList<ScreenInfo> Screens);

/// <summary>Logging to &lt;out_dir&gt;/teamsrec.log (the App sets Sink at start; tests leave it null).</summary>
public static class Log
{
    public static Action<string, string>? Sink;  // (level, message)
    public static void Info(string m) => Sink?.Invoke("INFO", m);
    public static void Warn(string m) => Sink?.Invoke("WARNING", m);
    public static void Error(string m) => Sink?.Invoke("ERROR", m);
}
