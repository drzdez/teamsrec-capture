using NAudio.Wave;
using TeamsRec.Capture.Detection;
using TeamsRec.Capture.Recording;

namespace TeamsRec.Capture.Tests;

public class ParityFixTests
{
    [Fact]
    public void The_recorder_itself_is_not_a_Teams_call()
    {
        MicConsentEntry Active(string key, string ident) => new(key, ident, 133_000_000, 0);
        // while the .NET recorder holds the microphone, its own consent entry must not look like Teams
        Assert.False(MicUsers.TeamsInUse(new[] { Active(@"C:#tools#teamsrec-capture.exe", "teamsrec-capture.exe") }));
        Assert.True(MicUsers.TeamsInUse(new[] { Active("MSTeams_8wekyb3d8bbwe", "MSTeams_8wekyb3d8bbwe") }));
        Assert.True(MicUsers.TeamsInUse(new[] { Active(@"C:#Users#x#AppData#Local#Microsoft#Teams#current#Teams.exe", "Teams.exe") }));
        Assert.False(MicUsers.TeamsInUse(new[] { new MicConsentEntry("MSTeams_8wekyb3d8bbwe", "MSTeams_8wekyb3d8bbwe", 1, 2) }),
                     "a finished Teams session is not a call");
    }

    [Fact]
    public void A_crashed_NAudio_wav_is_repaired_and_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teamsrec-wav-{Guid.NewGuid():N}.wav");
        try
        {
            using (var w = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
                w.Write(new byte[48000 * 2], 0, 48000 * 2);  // one second
            // a killed process never updates the sizes: zero them the way a crash leaves them
            using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                var layout = Orphans.Layout(f)!.Value;
                Assert.True(layout.DataStart > 44, "NAudio writes a longer header than 44 bytes");
                f.Seek(4, SeekOrigin.Begin); f.Write(new byte[4]);
                f.Seek(layout.DataSizePos, SeekOrigin.Begin); f.Write(new byte[4]);
            }
            Assert.True(Orphans.RepairWav(path));
            var h = Orphans.ReadHeader(path);
            Assert.Equal((48000, 1, 48000L), (h.SampleRate, h.Channels, h.Frames));
            using var reader = new WaveFileReader(path);  // and a normal reader accepts it again
            Assert.Equal(TimeSpan.FromSeconds(1), reader.TotalTime);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
