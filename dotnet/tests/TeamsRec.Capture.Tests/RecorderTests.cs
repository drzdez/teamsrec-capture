using System.Buffers.Binary;
using System.Text.Json;
using TeamsRec.Capture.Audio;
using TeamsRec.Capture.Contract;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Tests;

/// <summary>1.1.1: a device switched during a call keeps recording into the same file (its rate converted), and the
/// sidecar lists the devices a track was recorded from.</summary>
public class RecorderTests
{
    private static byte[] Sine(int rate, int channels, double seconds, double hz = 1000)
    {
        int frames = (int)(rate * seconds);
        var b = new byte[frames * channels * 2];
        for (int f = 0; f < frames; f++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * hz * f / rate) * 16000);
            for (int c = 0; c < channels; c++)
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan((f * channels + c) * 2), v);
        }
        return b;
    }

    private static (int Frames, int Crossings, int Peak) Measure(byte[] pcm, int channels)
    {
        int frames = pcm.Length / 2 / channels, crossings = 0, peak = 0;
        short prev = 0;
        for (int f = 0; f < frames; f++)
        {
            short v = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(f * channels * 2));
            if (f > 0 && (prev < 0) != (v < 0)) crossings++;
            peak = Math.Max(peak, Math.Abs((int)v));
            prev = v;
        }
        return (frames, crossings, peak);
    }

    [Fact]
    public void A_bluetooth_headsets_16_kHz_is_converted_to_the_files_48_kHz_seamlessly_in_chunks()
    {
        var conv = new RateConverter(16000, 48000, 1);
        var input = Sine(16000, 1, 2.0);
        var output = new List<byte>();
        for (int i = 0; i < input.Length; i += 320)  // 10 ms chunks, as a capture delivers them
            output.AddRange(conv.Convert(input[i..Math.Min(i + 320, input.Length)]));
        var (frames, crossings, peak) = Measure(output.ToArray(), 1);
        Assert.InRange(frames, 96000 - 400, 96000 + 50);  // 2 s at 48 kHz (the filter holds back a few ms)
        Assert.InRange(crossings, 3900, 4010);             // still 1 kHz: ~2 crossings per period
        Assert.InRange(peak, 14000, 17500);                 // the level is kept
    }

    [Fact]
    public void Stereo_44_1_kHz_is_converted_to_48_kHz_keeping_both_channels()
    {
        var conv = new RateConverter(44100, 48000, 2);
        var output = conv.Convert(Sine(44100, 2, 1.0, 500));
        var (frames, crossings, _) = Measure(output, 2);
        Assert.InRange(frames, 48000 - 400, 48000 + 50);
        Assert.InRange(crossings, 950, 1005);
    }

    [Fact]
    public void The_sidecar_lists_the_devices_only_when_the_track_changed_them()
    {
        var one = SidecarTrack.From(new TrackInfo("x_mic.wav", 48000, 1, "Mikrofon (BT-W5)"));
        Assert.Null(one.Devices);
        var two = SidecarTrack.From(new TrackInfo("x_sys.wav", 48000, 2, "Sluchátka [Loopback]",
            [new DeviceUse("Reproduktory [Loopback]", 0), new DeviceUse("Sluchátka [Loopback]", 312.5)]));
        var json = JsonSerializer.Serialize(two);
        Assert.Contains("\"devices\":[{\"device\":\"Reproduktory [Loopback]\",\"from_s\":0}", json);
        Assert.Contains("\"from_s\":312.5", json);
        Assert.DoesNotContain("devices", JsonSerializer.Serialize(one));
    }

    [Fact]
    public void A_session_end_is_its_own_stop_reason()
    {
        Assert.Equal("session_end", StopReasons.ToContract("session end"));
    }
}

/// <summary>Against this PC's real audio devices, only with TEAMSREC_LIVE_AUDIO=1 (CI has none).</summary>
public class RecorderLiveTests
{
    private sealed class RealClock : IClock
    {
        public DateTime Now => DateTime.Now;
        public double Seconds => Environment.TickCount64 / 1000.0;
    }

    [Fact]
    public void Live_recording_of_both_tracks_and_a_reopen_on_the_same_devices()
    {
        if (Environment.GetEnvironmentVariable("TEAMSREC_LIVE_AUDIO") != "1") return;
        var dir = Path.Combine(Path.GetTempPath(), $"teamsrec-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var rec = new Recorder(Path.Combine(dir, "live"), withMic: true, micOnly: false, micName: "", new RealClock());
            var files = rec.Start();
            Console.WriteLine($"LIVE files={string.Join(", ", files.Select(Path.GetFileName))} pending={rec.SysPending}");
            Thread.Sleep(2000);
            Assert.True(rec.Reopen() || rec.SysPending);
            Thread.Sleep(1500);
            var dur = rec.Stop();
            foreach (var (name, t) in rec.Tracks)
                Console.WriteLine($"LIVE {name}: {t.File} {t.SampleRate} Hz x{t.Channels} <- {t.Device} devices={t.Devices?.Count ?? 1}");
            Assert.InRange(dur, 3, 6);
            foreach (var f in files)
            {
                var len = new FileInfo(f).Length;
                Console.WriteLine($"LIVE {Path.GetFileName(f)} {len} bytes");
                Assert.True(len > 44, $"{f} was written");  // a Bluetooth dongle without an active headset delivers little
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
