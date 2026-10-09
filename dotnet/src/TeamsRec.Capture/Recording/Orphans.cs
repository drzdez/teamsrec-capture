using System.Buffers.Binary;
using System.Text.RegularExpressions;
using TeamsRec.Capture.Contract;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Recording;

/// <summary>Recovery of recordings cut by a crash or a kill.</summary>
public static class Orphans
{
    public const int MinDurationS = 5;      // shorter recordings are deleted (as after a normal stop)
    public const int ScreenFps = 2;         // fallback metadata of videos whose capture process never reported
    public const int ScreenW = 1600, ScreenH = 900;

    private static readonly Regex StemPattern = new(@"^(\d{4})-(\d{2})-(\d{2})_(\d{2})(\d{2})_(.*)$");

    /// <summary>Recordings that have audio but no sidecar were cut by a crash or a kill: repair the WAV headers,
    /// write the sidecar (stop_reason app_crash) and the mix, so nothing recorded is lost. Runs once at startup,
    /// so no recording is in progress. Returns how many were recovered.</summary>
    public static int Recover(string outDir, Func<string?>? findFfmpeg = null)
    {
        findFfmpeg ??= Mixer.FindFfmpeg;
        var n = 0;
        foreach (var stem in AudioStems(outDir))
        {
            var dir = Path.GetDirectoryName(stem)!;
            var sysfile = File.Exists(stem + "_sys.wav") ? stem + "_sys.wav" : stem + "_mic.wav";
            if (File.Exists(stem + ".json"))
                continue;
            var audio = new[] { stem + "_sys.wav", stem + "_mic.wav" }
                .Where(p => File.Exists(p) && RepairWav(p)).ToList();
            if (audio.Count == 0)
                continue;
            double dur;
            var tracks = new Dictionary<string, SidecarTrack>();
            try
            {
                var first = ReadHeader(audio[0]);
                dur = first.Frames / (double)first.SampleRate;
                foreach (var p in audio)
                {
                    var h = ReadHeader(p);
                    tracks[p.EndsWith("_mic.wav", StringComparison.OrdinalIgnoreCase) ? "mic" : "sys"] =
                        new SidecarTrack { File = Path.GetFileName(p), SampleRate = h.SampleRate, Channels = h.Channels };  // device unknown after a crash
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Warn($"orphan {Path.GetFileName(stem)} unreadable: {e.Message}");
                continue;
            }
            if (dur < MinDurationS)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                        File.Delete(f);
                    Directory.Delete(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                Log.Info($"orphan {Path.GetFileName(stem)} deleted ({dur:0} s)");
                continue;
            }
            var stemName = Path.GetFileName(stem);
            var m = StemPattern.Match(stemName);
            var started = m.Success
                ? new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                               int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), 0)
                : File.GetLastWriteTime(sysfile);
            var title = (m.Success ? m.Groups[6].Value : stemName).Replace("-", " ");
            var ff = Mixer.MixWithFfmpeg ? findFfmpeg() : null;
            var mix = ff is not null ? Mixer.Mix(ff, stem, audio) : null;
            var sidecar = new Sidecar
            {
                Format = Versions.FormatVersion,
                App = Versions.AppName,
                AppVersion = Versions.AppVersion,
                Title = title,
                Slug = Naming.Slug(title),
                Source = "live",
                Start = started,
                End = started.AddSeconds(dur),
                DurationS = (int)Math.Round(dur),
                StopReason = StopReasons.AppCrash,
                Recovered = true,
                Tracks = tracks,
                TeamsWindowsSeen = new List<string>(),
            };
            if (mix is not null)
                sidecar.Mix = new SidecarMix { File = Path.GetFileName(mix), SampleRate = Mixer.MixSampleRate, Channels = Mixer.MixChannels };
            var screens = ScreensOnDisk(stem);
            if (screens.Count > 0)
                sidecar.Screens = screens;
            sidecar.Write(stem + ".json");
            Log.Warn($"recovered orphan recording {stemName} ({dur:0} s, cut by a crash)");
            n++;
        }
        return n;
    }

    /// <summary>The stems of &lt;out&gt;/[0-9]*/[0-9]*/*/*_sys.wav and *_mic.wav, sorted: a recording may have the
    /// microphone only (on site, or a call whose output device never appeared).</summary>
    private static IEnumerable<string> AudioStems(string outDir) =>
        TrackFiles(outDir, "*_sys.wav").Select(p => p[..^"_sys.wav".Length])
            .Concat(TrackFiles(outDir, "*_mic.wav").Select(p => p[..^"_mic.wav".Length]))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>&lt;out&gt;/[0-9]*/[0-9]*/*/&lt;pattern&gt; (the prototype's glob).</summary>
    private static IEnumerable<string> TrackFiles(string outDir, string pattern)
    {
        if (!Directory.Exists(outDir))
            return [];
        static bool Digit(string p) { var n = Path.GetFileName(p); return n.Length > 0 && char.IsAsciiDigit(n[0]); }
        try
        {
            return Directory.EnumerateDirectories(outDir).Where(Digit)
                .SelectMany(y => Directory.EnumerateDirectories(y).Where(Digit))
                .SelectMany(m => Directory.EnumerateDirectories(m))
                .SelectMany(d => Directory.EnumerateFiles(d, pattern))
                .Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("orphan scan failed: " + e.Message);
            return [];
        }
    }

    /// <summary>Fallback metadata for &lt;stem&gt;_screen&lt;N&gt;.mp4 files whose capture process did not
    /// report (crash/kill). Frames -1 = unknown, no end_offset_s, recovered = true (as the prototype).</summary>
    public static List<SidecarScreen> ScreensOnDisk(string stemPath)
    {
        var dir = Path.GetDirectoryName(stemPath)!;
        var name = Path.GetFileName(stemPath);
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, name + "_screen*.mp4")
            .Order(StringComparer.Ordinal)
            .Where(p => new FileInfo(p).Length > 1000)  // an encoder killed at start leaves a stub
            .Select(p => new SidecarScreen
            {
                File = Path.GetFileName(p), Fps = ScreenFps, Width = ScreenW, Height = ScreenH,
                StartOffsetS = 0.0, Frames = -1, Recovered = true,
            })
            .ToList();
    }

    /// <summary>Where the parts of a WAV file are. The header has no fixed size: NAudio writes an 18-byte fmt
    /// chunk (46-byte header), the Python prototype a 16-byte one (44), other writers add LIST chunks - so the RIFF
    /// chunks are walked instead of reading fixed offsets.</summary>
    internal readonly record struct WavLayout(long DataSizePos, long DataStart, int Channels, int SampleRate,
                                              int BlockAlign);

    internal static WavLayout? Layout(FileStream f)
    {
        var head = new byte[12];
        f.Seek(0, SeekOrigin.Begin);
        if (f.Read(head, 0, 12) < 12 || !head.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !head.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return null;
        int channels = 0, rate = 0, align = 0;
        var chunk = new byte[8];
        var fmt = new byte[16];
        long pos = 12;
        while (pos + 8 <= f.Length)
        {
            f.Seek(pos, SeekOrigin.Begin);
            if (f.Read(chunk, 0, 8) < 8)
                return null;
            var len = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4, 4));
            if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                if (f.Read(fmt, 0, 16) < 16)
                    return null;
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4, 4));
                align = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12, 2));
            }
            else if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                // a crashed writer left the data size at 0: everything after the chunk header is the audio
                return new WavLayout(pos + 4, pos + 8, channels, rate, align);
            }
            pos += 8 + len + (len & 1);
        }
        return null;
    }

    /// <summary>A killed process never closes the wave file, so the RIFF/data sizes say 0. Fix them from the
    /// file size. True when the header is (now) consistent.</summary>
    public static bool RepairWav(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var layout = Layout(f);
            if (layout is not { } l || size <= l.DataStart)
                return false;
            var buf = new byte[4];
            f.Seek(l.DataSizePos, SeekOrigin.Begin);
            f.ReadExactly(buf);
            var riff = new byte[4];
            f.Seek(4, SeekOrigin.Begin);
            f.ReadExactly(riff);
            if (BinaryPrimitives.ReadUInt32LittleEndian(buf) == size - l.DataStart
                && BinaryPrimitives.ReadUInt32LittleEndian(riff) == size - 8)
                return true;  // already consistent
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)(size - 8));
            f.Seek(4, SeekOrigin.Begin);
            f.Write(buf);
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)(size - l.DataStart));
            f.Seek(l.DataSizePos, SeekOrigin.Begin);
            f.Write(buf);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return false;
        }
    }

    internal readonly record struct WavHeader(int SampleRate, int Channels, long Frames);

    /// <summary>Format and length of a WAV file of any header layout (after RepairWav).</summary>
    internal static WavHeader ReadHeader(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (Layout(f) is not { } l)
            throw new InvalidDataException("not a WAV file");
        if (l.Channels == 0 || l.SampleRate == 0 || l.BlockAlign == 0)
            throw new InvalidDataException("bad WAV format chunk");
        return new WavHeader(l.SampleRate, l.Channels, (f.Length - l.DataStart) / l.BlockAlign);
    }
}
