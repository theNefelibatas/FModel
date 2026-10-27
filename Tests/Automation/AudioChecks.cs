using System.IO;
using System.Text;
using System.Windows;
using FModel.ViewModels;

internal static class AudioChecks
{
    private static byte[] CreateFixture()
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bits = 16;
        var dataSize = sampleRate * channels * bits / 8;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short) 1); writer.Write(channels); writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bits / 8); writer.Write((short) (channels * bits / 8)); writer.Write(bits);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataSize); writer.Write(new byte[dataSize]);
        }
        return stream.ToArray();
    }

    public static async Task Run()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application();
            try
            {
                using var player = new AudioPlayerViewModel();
                for (var i = 0; i < 3; i++) player.AddToPlaylist(CreateFixture(), $"silence-{i}.wav", false);
                var tracks = player.AudioFilesView.Cast<AudioFile>().ToArray();
                if (Environment.GetEnvironmentVariable("FMODEL_TEST_AUDIO_DEVICE") == "true")
                {
                    player.SelectedAudioFile = tracks[2];
                    player.Load();
                    Require(player.PlayedFile.Id == 2, "Generated WAV fixture loads without a converter or playback");
                    player.SelectedAudioFile = tracks[0];
                    player.Remove(false);
                    Require(player.PlayedFile.Id == 1 && player.PlayedFile.FileName == tracks[2].FileName,
                        "Removing an earlier audio track preserves the loaded track identity");
                    player.SelectedAudioFile = tracks[2];
                    player.Remove(false);
                    Require(player.PlayedFile.Id == -1 && !player.IsPlaying,
                        "Removing the loaded track does not load or autoplay another track");
                }
                else Console.WriteLine("SKIP: Loaded-track removal checks require FMODEL_TEST_AUDIO_DEVICE=true and an audio output device.");
                while (!player.AudioFilesView.IsEmpty)
                {
                    player.SelectedAudioFile = player.AudioFilesView.Cast<AudioFile>().First();
                    player.Remove(false);
                }
                Require(player.SelectedAudioFile == null, "Removing the final audio track clears its selection");
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { app.Shutdown(); }
            completion.TrySetResult();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
