using NAudio.CoreAudioApi;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Audio;

/// <summary>Settings page "test the microphone": open an input the way a recording would and report how loud
/// it is (port of test_input). Compare Peak with Recorder.SilenceLevel.</summary>
public static class MicTest
{
    public static (string Device, int Peak, string? Error) Run(string namePart, double seconds = 2)
    {
        namePart ??= "";
        try
        {
            using var en = new MMDeviceEnumerator();
            using var dev = Devices.FindInputEndpoint(en, namePart);
            if (dev == null)
                return ("", 0, $"zařízení „{(namePart.Length > 0 ? namePart : "výchozí vstup")}“ není k dispozici");
            string name = dev.FriendlyName;
            var capture = new WasapiCapture(dev);
            SampleFormat fmt;
            int outChannels;
            try
            {
                fmt = SampleFormat.Of(capture.WaveFormat);
                outChannels = Math.Clamp(capture.WaveFormat.Channels, 1, 2);
            }
            catch
            {
                capture.Dispose();
                throw;
            }
            int peak = 0;
            Exception? failed = null;
            var gate = new object();
            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0)
                    return;
                int p = Pcm.Peak16(Pcm.ToPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), fmt, outChannels));
                lock (gate)
                    peak = Math.Max(peak, p);
            };
            capture.RecordingStopped += (_, e) =>
            {
                lock (gate)
                    failed ??= e.Exception;
            };
            try
            {
                capture.StartRecording();
                Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0.1, seconds)));
                capture.StopRecording();
            }
            finally
            {
                capture.Dispose();  // joins the capture thread, so peak is final
            }
            lock (gate)
            {
                if (failed != null)
                    throw failed;
                Log.Info($"microphone test: {name} peak {peak}");
                return (name, peak, null);
            }
        }
        catch (Exception e)
        {
            Log.Error($"microphone test: {e}");
            return ("", 0, $"test selhal: {e.Message}");
        }
    }
}
