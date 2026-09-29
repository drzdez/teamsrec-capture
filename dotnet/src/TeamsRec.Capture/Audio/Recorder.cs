using System.Buffers.Binary;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Audio;

/// <summary>Records the system sound (WASAPI loopback of the default output) and the microphone into
/// &lt;stem&gt;_sys.wav / &lt;stem&gt;_mic.wav, 16-bit PCM at the device rate (contract: "WAV je vždy PCM
/// 16-bit"). Port of the prototype's Recorder, including reopen() and the watchdog fields.</summary>
public sealed class Recorder : IAudioSource, IDisposable
{
    public const int SilenceLevel = 300;  // int16 peak below this counts as silence
    public const int AliveLevel = 8;      // int16 peak: a working microphone always has room noise above this
                                          // (~-72 dBFS); a dongle without its headset, or a mic Teams is not
                                          // using, delivers ~0

    private readonly string _stem;
    private readonly IClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, Track> _state = new();              // "sys"/"mic" -> open track
    private readonly Dictionary<string, TrackInfo> _tracks = new();         // what the sidecar gets
    private WasapiOut? _keepAlive;
    private MMDevice? _keepAliveDevice;
    private bool _stopped;
    private double _stoppedDuration;

    private double _lastData, _lastLoud, _lastMicLoud, _lastMicAlive;
    private double _lastMicData = double.NaN;
    private long _bytesReceived;
    private volatile bool _heardSys, _heardMic;
    private int _reopens;

    public Recorder(string stemPath, bool withMic, bool micOnly, string micName, IClock clock)
    {
        _stem = stemPath;
        WithMic = withMic;
        MicOnly = micOnly;    // on-site meeting: the room microphone carries everybody, no loopback track
        MicName = micName ?? "";  // preferred input device (substring of its name), else the default input
        _clock = clock;
        Started = clock.Now;
        double now = clock.Seconds;
        _lastLoud = now;       // last time the system track was not silent
        _lastData = now;       // watchdog: last time any stream delivered a buffer
        _lastMicLoud = 0.0;    // watchdog: last time the user was audibly speaking
        _lastMicAlive = now;   // last time the mic carried at least room noise
    }

    public bool WithMic { get; }
    public bool MicOnly { get; }
    public string MicName { get; set; }
    public DateTime Started { get; }
    public double LastData => Volatile.Read(ref _lastData);
    public double LastLoud => Volatile.Read(ref _lastLoud);
    public double LastMicLoud => Volatile.Read(ref _lastMicLoud);
    public double LastMicAlive => Volatile.Read(ref _lastMicAlive);
    public double? LastMicData { get { var v = Volatile.Read(ref _lastMicData); return double.IsNaN(v) ? null : v; } }
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);  // a sleeping Bluetooth device delivers nothing
    public bool HeardSys => _heardSys;  // anything but digital silence arrived on the loopback
    public bool HeardMic => _heardMic;  // ... and on the microphone
    public int Reopens => Volatile.Read(ref _reopens);

    public IReadOnlyDictionary<string, TrackInfo> Tracks
    {
        get { lock (_sync) return new Dictionary<string, TrackInfo>(_tracks); }
    }

    /// <summary>Opens the streams and starts writing; returns the WAV files being written.</summary>
    public List<string> Start()
    {
        lock (_sync)
        {
            var files = new List<string>();
            var (sys, mic) = ResolveDevices();
            if (sys != null)
            {
                try
                {
                    files.Add(Open("sys", sys, "_sys.wav"));  // no loopback is fatal, as in the prototype
                }
                catch
                {
                    sys.Dispose();
                    mic?.Dispose();
                    throw;
                }
            }
            if (mic != null)
            {
                try
                {
                    files.Add(Open("mic", mic, "_mic.wav"));
                }
                catch (Exception e)  // no mic is not fatal as long as the loopback track opened
                {
                    Log.Warn($"mic not recorded: {e.Message}");
                    mic.Dispose();
                }
            }
            if (files.Count == 0)  // nothing to write into: never pretend to record (2026-09-22: 4 h of "recording" nothing)
            {
                var names = string.Join(", ", Devices.Inputs().Select(d => d.Name));
                var have = names.Length > 0 ? names : "žádná";
                var want = MicName.Length > 0 ? $"'{MicName}'" : "výchozí vstup";
                throw new InvalidOperationException(
                    $"nepodařilo se otevřít žádné zvukové zařízení (hledáno {want}; dostupná zařízení: {have})");
            }
            if (_state.ContainsKey("sys"))
                StartKeepAlive();
            return files;
        }
    }

    /// <summary>The streams stopped delivering (a Bluetooth headset woke up and Windows re-registered the
    /// device, a dongle was re-plugged): open new streams on the current devices and keep writing into the same
    /// files. The gap is padded with silence so the timeline stays aligned with the screen videos.</summary>
    public bool Reopen()
    {
        lock (_sync)
        {
            if (_stopped)
                return false;
            double gap = Math.Max(0.0, _clock.Seconds - LastData);
            StopKeepAlive();
            foreach (var t in _state.Values)
                CloseCapture(t);

            MMDevice? sys = null, mic = null;
            try
            {
                (sys, mic) = ResolveDevices();  // a fresh enumerator sees the re-registered devices
            }
            catch (Exception e)
            {
                Log.Warn($"reopen: device lookup failed: {e.Message}");
            }

            int ok = 0;
            foreach (var (name, t) in _state)
            {
                var dev = name == "sys" ? sys : mic;
                if (dev == null)
                    continue;
                try
                {
                    var (rate, ch) = OutputFormat(dev);
                    if (rate != t.Rate || ch != t.Channels)
                    {
                        Log.Warn($"reopen {name}: device format {rate} Hz x{ch} differs from the file " +
                                 $"({t.Rate} Hz x{t.Channels}), track stays as is");
                        continue;
                    }
                    if (gap > 0.5)
                        PadSilence(t, gap);  // silence for the lost stretch
                    OpenCapture(t, dev, name == "sys");
                    if (name == "sys") sys = null; else mic = null;  // owned by the track now
                    _tracks[name] = _tracks[name] with { Device = t.DeviceName };
                    ok++;
                    Log.Info($"reopened {name} on {t.DeviceName} after {gap:0} s without data");
                }
                catch (Exception e)
                {
                    Log.Warn($"reopen {name} failed: {e.Message}");
                }
            }
            sys?.Dispose();
            mic?.Dispose();
            if (_state.TryGetValue("sys", out var st) && st.Capture != null)
                StartKeepAlive();
            Volatile.Write(ref _lastData, _clock.Seconds);
            Interlocked.Increment(ref _reopens);
            return ok > 0;
        }
    }

    /// <summary>Stops the streams, flushes and closes the WAV files; returns the duration in seconds.</summary>
    public double Stop()
    {
        lock (_sync)
        {
            if (_stopped)
                return _stoppedDuration;
            _stopped = true;
            StopKeepAlive();
            foreach (var t in _state.Values)
                CloseCapture(t);
            foreach (var t in _state.Values)
            {
                t.Queue.CompleteAdding();
                if (!t.Writer.Join(TimeSpan.FromSeconds(10)))
                    Log.Warn($"{t.Name}: writer did not finish in 10 s");
                t.Device?.Dispose();
                t.Device = null;
            }
            _stoppedDuration = (_clock.Now - Started).TotalSeconds;
            return _stoppedDuration;
        }
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch (Exception e)
        {
            Log.Warn($"recorder dispose: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ internals

    private sealed class Track(string name, string path, int rate, int channels)
    {
        public readonly string Name = name;
        public readonly string Path = path;
        public readonly int Rate = rate;          // the file's format; never changes after the first open
        public readonly int Channels = channels;
        // the writer's input, kept across a device reopen so the file keeps growing
        public readonly BlockingCollection<byte[]> Queue = new();
        public Thread Writer = null!;
        public IWaveIn? Capture;
        public MMDevice? Device;
        public string DeviceName = "";
    }

    /// <summary>(loopback render endpoint, mic capture endpoint) for the tracks this recording wants.</summary>
    private (MMDevice? Sys, MMDevice? Mic) ResolveDevices()
    {
        using var en = new MMDeviceEnumerator();
        MMDevice? sys = null, mic = null;
        if (!MicOnly)
            sys = Devices.DefaultRenderEndpoint(en)
                  ?? throw new InvalidOperationException("no default output device for the loopback");
        if (WithMic)
        {
            try
            {
                mic = MicName.Length > 0 ? Devices.FindInputEndpoint(en, MicName) : null;
                if (MicName.Length > 0 && mic == null)
                    Log.Warn($"input device '{MicName}' not found or not active, using the default input");
                mic ??= Devices.DefaultInputEndpoint(en) ?? throw new InvalidOperationException("no default input device");
            }
            catch (Exception e)
            {
                Log.Warn($"no microphone: {e.Message}");
            }
        }
        return (sys, mic);
    }

    /// <summary>The file format for a device: its own rate, at most 2 channels, 16 bit.</summary>
    private static (int Rate, int Channels) OutputFormat(MMDevice dev)
    {
        var (rate, ch) = Devices.MixFormat(dev);
        return (rate, Math.Clamp(ch, 1, 2));
    }

    private string Open(string name, MMDevice dev, string suffix)
    {
        var (rate, ch) = OutputFormat(dev);
        var path = _stem + suffix;
        var t = new Track(name, path, rate, ch);
        // Start the capture before creating the file: when the device refuses to open, nothing is left on disk.
        // The queue buffers the first few milliseconds until the writer runs.
        OpenCapture(t, dev, name == "sys");
        WaveFileWriter wav;
        try
        {
            wav = new WaveFileWriter(path, new WaveFormat(rate, 16, ch));
        }
        catch
        {
            CloseCapture(t);
            throw;
        }
        t.Writer = new Thread(() => WriterLoop(t, wav)) { IsBackground = true, Name = $"wav-{name}" };
        t.Writer.Start();
        _state[name] = t;
        _tracks[name] = new TrackInfo(System.IO.Path.GetFileName(path), rate, ch, t.DeviceName);
        Log.Info($"recording {System.IO.Path.GetFileName(path)}  {rate} Hz x{ch}  <- {t.DeviceName}");
        return path;
    }

    /// <summary>Opens and starts a capture on dev feeding the track's queue. Takes ownership of dev.</summary>
    private void OpenCapture(Track t, MMDevice dev, bool loopback)
    {
        IWaveIn capture = loopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev);
        try
        {
            var fmt = SampleFormat.Of(capture.WaveFormat);
            bool isSys = loopback;
            string displayName = loopback ? dev.FriendlyName + " [Loopback]" : dev.FriendlyName;
            capture.DataAvailable += (_, e) => OnData(t, isSys, fmt, e);
            capture.RecordingStopped += (_, e) =>
            {
                // A device that disappears (AUDCLNT_E_DEVICE_INVALIDATED) ends here; the watchdog notices the
                // missing data and calls Reopen.
                if (e.Exception != null)
                    Log.Warn($"{t.Name} stream stopped: {e.Exception.Message}");
            };
            capture.StartRecording();
            t.Capture = capture;
            t.Device?.Dispose();
            t.Device = dev;
            t.DeviceName = displayName;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private static void CloseCapture(Track t)
    {
        var c = t.Capture;
        t.Capture = null;
        if (c == null)
            return;
        try
        {
            c.StopRecording();
        }
        catch (Exception)
        {
        }
        try
        {
            c.Dispose();  // joins the capture thread: no callback runs after this
        }
        catch (Exception e)
        {
            Log.Warn($"{t.Name}: closing the stream failed: {e.Message}");
        }
    }

    /// <summary>The capture callback: never blocks (the queue is unbounded, the writer thread does the IO).</summary>
    private void OnData(Track t, bool isSys, SampleFormat fmt, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;
        var pcm = Pcm.ToPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), fmt, t.Channels);
        if (pcm.Length == 0)
            return;
        try
        {
            t.Queue.Add(pcm);
        }
        catch (InvalidOperationException)
        {
            return;  // stopping: the writer is already closed
        }
        Interlocked.Add(ref _bytesReceived, pcm.Length);
        double now = _clock.Seconds;
        Volatile.Write(ref _lastData, now);
        if (!isSys)
            Volatile.Write(ref _lastMicData, now);
        int peak = Pcm.Peak16(pcm);
        if (!isSys && peak > AliveLevel)
            Volatile.Write(ref _lastMicAlive, now);
        if (peak > SilenceLevel)
        {
            if (isSys)
            {
                Volatile.Write(ref _lastLoud, now);
                _heardSys = true;
            }
            else
            {
                Volatile.Write(ref _lastMicLoud, now);
                _heardMic = true;
            }
        }
    }

    private static void PadSilence(Track t, double gapS)
    {
        // In one-second pieces: a gap of an hour must not become one huge allocation.
        long frames = (long)(gapS * t.Rate);
        int frameBytes = t.Channels * 2;
        while (frames > 0)
        {
            int n = (int)Math.Min(frames, t.Rate);
            t.Queue.Add(new byte[n * frameBytes]);
            frames -= n;
        }
    }

    private static void WriterLoop(Track t, WaveFileWriter wav)
    {
        long lastFlush = Environment.TickCount64;
        try
        {
            foreach (var chunk in t.Queue.GetConsumingEnumerable())
            {
                wav.Write(chunk, 0, chunk.Length);
                // Keep the RIFF header current, so a crash leaves a playable file (the prototype needed
                // _repair_wav for that).
                if (Environment.TickCount64 - lastFlush > 5000)
                {
                    wav.Flush();
                    lastFlush = Environment.TickCount64;
                }
            }
        }
        catch (Exception e)
        {
            Log.Error($"{t.Name}: writing {t.Path} failed: {e.Message}");
        }
        finally
        {
            try
            {
                wav.Dispose();
            }
            catch (Exception e)
            {
                Log.Error($"{t.Name}: closing {t.Path} failed: {e.Message}");
            }
        }
    }

    /// <summary>WASAPI loopback delivers no buffers at all while nothing plays, which would shorten the sys
    /// track (misaligned with the mic and the screen videos) and look like a dead device to the watchdog.
    /// Playing silence on the same output keeps the audio engine running, so the loopback delivers a
    /// continuous stream (PortAudio did the equivalent for the prototype).</summary>
    private void StartKeepAlive()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = Devices.DefaultRenderEndpoint(en);
            if (dev == null)
                return;
            WaveFormat mix;
            using (var client = dev.AudioClient)
                mix = client.MixFormat;
            var player = new WasapiOut(dev, AudioClientShareMode.Shared, true, 200);
            try
            {
                player.Init(new SilenceProvider(mix));
                player.Play();
            }
            catch
            {
                player.Dispose();
                dev.Dispose();
                throw;
            }
            _keepAlive = player;
            _keepAliveDevice = dev;
        }
        catch (Exception e)
        {
            Log.Warn($"loopback keep-alive failed, silent stretches may be missing from the sys track: {e.Message}");
        }
    }

    private void StopKeepAlive()
    {
        try
        {
            _keepAlive?.Stop();
            _keepAlive?.Dispose();
        }
        catch (Exception)
        {
        }
        _keepAlive = null;
        _keepAliveDevice?.Dispose();
        _keepAliveDevice = null;
    }
}

/// <summary>Layout of the samples a WASAPI stream delivers.</summary>
public readonly record struct SampleFormat(bool IsFloat, int BytesPerSample, int Channels)
{
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");  // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    public static SampleFormat Of(WaveFormat wf)
    {
        int channels = Math.Max(1, wf.Channels);
        int bytes = Math.Max(1, wf.BlockAlign / channels);  // container size (24-in-32 is left-justified)
        bool isFloat = wf switch
        {
            WaveFormatExtensible ext => ext.SubFormat == FloatSubtype,
            _ when wf.Encoding == WaveFormatEncoding.IeeeFloat => true,
            _ when wf.Encoding == WaveFormatEncoding.Extensible => wf.BitsPerSample >= 32,  // shared mode mixes in float
            _ => false,
        };
        return new SampleFormat(isFloat, bytes, channels);
    }
}

/// <summary>Sample conversion and level measurement (audioop in the prototype).</summary>
public static class Pcm
{
    /// <summary>Converts interleaved device samples to 16-bit PCM with outChannels channels. More device channels
    /// than outChannels: the first ones are kept (front left/right of a surround output); fewer: the last one
    /// is repeated.</summary>
    public static byte[] ToPcm16(ReadOnlySpan<byte> src, SampleFormat fmt, int outChannels)
    {
        int frameBytes = fmt.BytesPerSample * fmt.Channels;
        if (frameBytes <= 0 || outChannels <= 0)
            return [];
        int frames = src.Length / frameBytes;
        var dst = new byte[frames * outChannels * 2];
        int o = 0;
        for (int f = 0; f < frames; f++)
        {
            int frameStart = f * frameBytes;
            for (int c = 0; c < outChannels; c++)
            {
                int sc = Math.Min(c, fmt.Channels - 1);
                short s = ReadSample(src.Slice(frameStart + sc * fmt.BytesPerSample, fmt.BytesPerSample), fmt);
                BinaryPrimitives.WriteInt16LittleEndian(dst.AsSpan(o, 2), s);
                o += 2;
            }
        }
        return dst;
    }

    private static short ReadSample(ReadOnlySpan<byte> b, SampleFormat fmt)
    {
        if (fmt.IsFloat)
        {
            double v = fmt.BytesPerSample >= 8
                ? BinaryPrimitives.ReadDoubleLittleEndian(b)
                : BinaryPrimitives.ReadSingleLittleEndian(b);
            if (double.IsNaN(v))
                return 0;
            return (short)Math.Clamp(Math.Round(v * 32767.0), -32768.0, 32767.0);
        }
        return fmt.BytesPerSample switch
        {
            1 => (short)((b[0] - 128) << 8),                     // 8-bit PCM is unsigned
            2 => BinaryPrimitives.ReadInt16LittleEndian(b),
            3 => (short)(b[1] | (b[2] << 8)),                   // the top 16 of 24 bits
            _ => (short)(BinaryPrimitives.ReadInt32LittleEndian(b) >> 16),
        };
    }

    /// <summary>Largest absolute sample value of 16-bit PCM (audioop.max(data, 2)); 0..32768.</summary>
    public static int Peak16(ReadOnlySpan<byte> pcm16)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            int v = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i, 2)));
            if (v > peak)
                peak = v;
        }
        return peak;
    }
}
