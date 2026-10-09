using System.Buffers.Binary;
using System.Collections.Concurrent;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Audio;

/// <summary>Records the system sound (WASAPI loopback of the default output) and the microphone into
/// &lt;stem&gt;_sys.wav / &lt;stem&gt;_mic.wav, 16-bit PCM at the device rate (contract: "WAV je vždy PCM
/// 16-bit"). Port of the prototype's Recorder, including reopen() and the watchdog fields.
///
/// The devices may change during a call (a headset switched on, another one taken): each file keeps the format
/// of its first device, and the sound of a later device with another rate is converted to it in the writer
/// thread (never in the capture callback, so a busy machine only delays the writing). With no output device at
/// the start the call is recorded from the microphone alone and the loopback track is added once an output
/// appears, its start padded with silence (2026-10-08: the headset was off when the call started, nothing was
/// recorded for 4 minutes).</summary>
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
    private bool _stopped;
    private double _stoppedDuration;

    private readonly double _startS;  // the clock's seconds at the start: the offset of a track added later
    private bool _sysPending;          // no output device at the start: the loopback track is still to come
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
        _startS = now;
        _lastLoud = now;       // last time the system track was not silent
        _lastData = now;       // watchdog: last time any stream delivered a buffer
        _lastMicLoud = 0.0;    // watchdog: last time the user was audibly speaking
        _lastMicAlive = now;   // last time the mic carried at least room noise
    }

    public bool WithMic { get; }
    public bool MicOnly { get; }
    public string MicName { get; set; }
    public string OutputName { get; set; } = "";  // the output the call plays into (Teams), else the default
    public bool SysPending { get { lock (_sync) return _sysPending; } }
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
            if (sys == null && !MicOnly)
            {
                _sysPending = true;
                Log.Warn("no output device: recording the microphone; the other side joins once an output appears");
            }
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
            double lastBefore = LastData;  // the last sound of the old streams (each track pads from here)
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
                    // silence for the stretch without data – measured now, after closing and looking up the devices,
                    // so the time the reopen itself takes is covered too (it was ~0.7 s per reopen before 1.1.1)
                    double gap = Math.Max(0.0, _clock.Seconds - lastBefore);
                    if (gap > 0.05)
                        PadSilence(t, gap);
                    var before = t.DeviceName;
                    OpenCapture(t, dev, name == "sys");
                    if (name == "sys") sys = null; else mic = null;  // owned by the track now
                    UseDevice(name, t);
                    ok++;
                    Log.Info($"reopened {name} on {t.DeviceName} after {gap:0} s without data" +
                             (t.DeviceName != before ? $" (was {before})" : "") +
                             (rate != t.Rate ? $", {rate} Hz converted to the file's {t.Rate} Hz" : ""));
                }
                catch (Exception e)
                {
                    Log.Warn($"reopen {name} failed: {e.Message}");
                }
            }
            sys?.Dispose();
            mic?.Dispose();
            Volatile.Write(ref _lastData, _clock.Seconds);
            Interlocked.Increment(ref _reopens);
            return ok > 0;
        }
    }

    /// <summary>The loopback track that had no output device at the start: open it on the output that exists
    /// now (the call's, else the default), its file padded with silence from the start of the recording so it
    /// stays aligned with the microphone and the window videos. False while there is still no output.</summary>
    public bool TryAddSys()
    {
        lock (_sync)
        {
            if (!_sysPending || _stopped)
                return false;
            MMDevice? sys;
            try
            {
                using var en = new MMDeviceEnumerator();
                sys = RenderEndpoint(en);
            }
            catch (Exception e)
            {
                Log.Warn($"output lookup failed: {e.Message}");
                return false;
            }
            if (sys == null)
                return false;
            double lead = Math.Max(0.0, _clock.Seconds - _startS);
            try
            {
                Open("sys", sys, "_sys.wav", lead);
            }
            catch (Exception e)
            {
                Log.Warn($"the output appeared but its loopback did not open: {e.Message}");
                sys.Dispose();
                return false;
            }
            _sysPending = false;
            Log.Info($"the other side joins the recording after {lead:0} s (padded with silence)");
            return true;
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
        // the writer's input, kept across a device reopen so the file keeps growing; each chunk says its rate
        public readonly BlockingCollection<Chunk> Queue = new();
        public readonly List<DeviceUse> Devices = new();  // every device the track was recorded from
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
            sys = RenderEndpoint(en);  // null = no output device now: the loopback track waits (TryAddSys)
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

    /// <summary>The output to record the other side from: the one the call plays into (OutputName), else the
    /// Windows default output; null when there is none.</summary>
    private MMDevice? RenderEndpoint(MMDeviceEnumerator en)
    {
        if (OutputName.Length > 0)
        {
            var found = Devices.FindRenderEndpoint(en, OutputName);
            if (found != null)
                return found;
            Log.Warn($"output device '{OutputName}' not found or not active, using the default output");
        }
        return Devices.DefaultRenderEndpoint(en);
    }

    /// <summary>The track's current device into the sidecar info (the device list only once there are two).</summary>
    private void UseDevice(string name, Track t)
    {
        if (t.Devices.Count == 0 || t.Devices[^1].Device != t.DeviceName)
            t.Devices.Add(new DeviceUse(t.DeviceName, Math.Round(Math.Max(0.0, _clock.Seconds - _startS), 1)));
        _tracks[name] = new TrackInfo(System.IO.Path.GetFileName(t.Path), t.Rate, t.Channels, t.DeviceName,
                                      t.Devices.Count > 1 ? t.Devices.ToList() : null);
    }

    /// <summary>The file format for a device: its own rate, at most 2 channels, 16 bit.</summary>
    private static (int Rate, int Channels) OutputFormat(MMDevice dev)
    {
        var (rate, ch) = Devices.MixFormat(dev);
        return (rate, Math.Clamp(ch, 1, 2));
    }

    private string Open(string name, MMDevice dev, string suffix, double leadSilenceS = 0)
    {
        var (rate, ch) = OutputFormat(dev);
        var path = _stem + suffix;
        var t = new Track(name, path, rate, ch);
        if (leadSilenceS > 0)
            PadSilence(t, leadSilenceS);  // a track added later starts where the recording started
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
        UseDevice(name, t);
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
            int rate = capture.WaveFormat.SampleRate;  // may differ from the file's after a device switch
            bool isSys = loopback;
            string displayName = loopback ? dev.FriendlyName + " [Loopback]" : dev.FriendlyName;
            capture.DataAvailable += (_, e) => OnData(t, isSys, fmt, rate, e);
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
    private void OnData(Track t, bool isSys, SampleFormat fmt, int rate, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;
        var pcm = Pcm.ToPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), fmt, t.Channels);
        if (pcm.Length == 0)
            return;
        try
        {
            t.Queue.Add(new Chunk(pcm, rate));
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
            t.Queue.Add(new Chunk(new byte[n * frameBytes], t.Rate));
            frames -= n;
        }
    }

    private static void WriterLoop(Track t, WaveFileWriter wav)
    {
        long lastFlush = Environment.TickCount64;
        RateConverter? conv = null;  // the sound of a device with another rate than the file's
        try
        {
            foreach (var chunk in t.Queue.GetConsumingEnumerable())
            {
                var data = chunk.Data;
                if (chunk.Rate != t.Rate)
                {
                    if (conv == null || conv.InRate != chunk.Rate)
                        conv = new RateConverter(chunk.Rate, t.Rate, t.Channels);
                    data = conv.Convert(data);
                }
                wav.Write(data, 0, data.Length);
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

}

/// <summary>16-bit PCM from one device, at its rate (the file may have another).</summary>
public readonly record struct Chunk(byte[] Data, int Rate);

/// <summary>Converts 16-bit PCM between sample rates (NAudio's WDL resampler, the windowed-sinc mode), keeping its
/// state across chunks so the joins are seamless. Runs in the writer thread.</summary>
public sealed class RateConverter
{
    private readonly NAudio.Dsp.WdlResampler _r = new();
    private readonly int _channels;
    public int InRate { get; }
    public int OutRate { get; }

    public RateConverter(int inRate, int outRate, int channels)
    {
        InRate = inRate;
        OutRate = outRate;
        _channels = channels;
        _r.SetMode(true, 2, false);
        _r.SetFilterParms();
        _r.SetFeedMode(true);  // input driven: every chunk goes in whole
        _r.SetRates(inRate, outRate);
    }

    public byte[] Convert(byte[] pcm16)
    {
        int inFrames = pcm16.Length / 2 / _channels;
        if (inFrames == 0)
            return [];
        int need = _r.ResamplePrepare(inFrames, _channels, out var inBuf, out int inOff);
        int n = Math.Min(need, inFrames) * _channels;
        for (int i = 0; i < n; i++)
            inBuf[inOff + i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * 2, 2)) / 32768f;
        int maxOut = (int)((long)inFrames * OutRate / InRate) + 64;
        var outBuf = new float[maxOut * _channels];
        int outFrames = _r.ResampleOut(outBuf, 0, Math.Min(need, inFrames), maxOut, _channels);
        var result = new byte[outFrames * _channels * 2];
        for (int i = 0; i < outFrames * _channels; i++)
        {
            float v = Math.Clamp(outBuf[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2, 2), (short)Math.Round(v * 32767f));
        }
        return result;
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
