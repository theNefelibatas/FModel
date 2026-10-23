using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FModel.ViewModels;
using FModel.Framework;
using Newtonsoft.Json.Linq;

namespace FModel.Automation;

internal sealed partial class GameOperations
{
    private AssetViewport _viewport;

    private async Task<object> MediaAsync(string operation, JObject args, CancellationToken ct)
    {
        if (operation == "audio_load")
        {
            var file = Resolve(args);
            using var scope = new AssetOperationScope(file);
            var count = 0;
            var audioRoot = Path.Combine(CacheManager.DataDirectory, "automation-audio", Guid.NewGuid().ToString("N"));
            scope.AudioCaptured = (path, extension, bytes) =>
            {
                ct.ThrowIfCancellationRequested();
                if (bytes.Length == 0) throw new InvalidDataException("Audio decoder returned no data.");
                var playbackPath = Path.Combine(audioRoot, count.ToString(), Path.ChangeExtension(Path.GetFileName(path), extension));
                App.AudioPlayer.AddToPlaylist(bytes, playbackPath, false);
                count++;
            };
            Model.Extract(ct, file, false, EBulkType.Audio | EBulkType.Auto);
            if (count == 0) throw new NotSupportedException("The asset produced no supported audio tracks.");
            return await Application.Current.Dispatcher.InvokeAsync(AudioState);
        }
        if (operation == "audio_state") return await Application.Current.Dispatcher.InvokeAsync(AudioState);
        if (operation == "audio_control") return await Application.Current.Dispatcher.InvokeAsync(() => AudioControl(args));
        if (operation == "viewer_load" || operation == "viewer_animation" || operation == "viewer_material")
        {
            var package = Model.Provider.LoadPackage(ResolvePackage(args));
            var export = package.GetExport(QueryHelpers.Integer(args, "index", -1, 0, package.ExportMapLength - 1));
            return await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _viewport ??= new AssetViewport(QueryHelpers.Integer(args, "width", 512, 32, 2048), QueryHelpers.Integer(args, "height", 512, 32, 2048));
                return operation switch
                {
                    "viewer_load" => _viewport.Load(export, args.Value<bool?>("append") == true, ct),
                    "viewer_animation" => _viewport.Animate(export, args.Value<bool?>("applyToSelected") == true),
                    _ => _viewport.SwapMaterial(export)
                };
            });
        }
        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (operation == "viewer_close")
            {
                _viewport?.Dispose();
                _viewport = null;
                return (object) new { closed = true };
            }
            if (_viewport == null) throw new InvalidOperationException("Load an asset with viewer_load first.");
            return operation switch
            {
                "viewer_state" => _viewport.State(args),
                "viewer_configure" => _viewport.Configure(args),
                "viewer_model" => _viewport.Model(args),
                "viewer_lighting" => _viewport.Lighting(args),
                "viewer_material_parameters" => _viewport.MaterialParameters(args),
                "viewer_timeline" => _viewport.Timeline(args),
                "viewer_capture" => _viewport.Capture(args),
                _ => throw new ArgumentException("Unknown media operation.")
            };
        });
    }

    private object AudioState() => new
    {
        playback = App.AudioPlayer.PlaybackSnapshot(),
        tracks = App.AudioPlayer.AudioFilesView.Cast<AudioFile>().Select(x => new { id = x.Id, name = x.FileName, extension = x.Extension,
            bytes = x.Length, duration = x.Duration.TotalSeconds }).ToArray()
    };

    private object AudioControl(JObject args)
    {
        var player = App.AudioPlayer;
        if (args["trackId"] != null)
        {
            var index = QueryHelpers.Integer(args, "trackId", -1, 0, int.MaxValue);
            player.SelectedAudioFile = player.AudioFilesView.Cast<AudioFile>().FirstOrDefault(x => x.Id == index)
                ?? throw new ArgumentException("Unknown trackId.");
        }
        if (player.SelectedAudioFile == null) throw new InvalidOperationException("Load an audio asset first.");
        switch (args.Value<string>("action"))
        {
            case "load": player.Stop(); player.Load(); break;
            case "play":
                if (player.PlayedFile.Id != player.SelectedAudioFile.Id) { player.Stop(); player.Load(); }
                if (args["volume"] != null) player.SetPlaybackVolume(AssetViewport.Number(args["volume"], 0, 1));
                player.Play(); break;
            case "pause": player.Pause(); break;
            case "resume": player.Resume(); break;
            case "stop": player.Stop(); break;
            case "remove": player.Remove(false); break;
            case "seek": player.SkipTo(AssetViewport.Number(args["fraction"], 0, 1)); break;
            case "volume": player.SetPlaybackVolume(AssetViewport.Number(args["volume"], 0, 1)); break;
            default: throw new ArgumentException("Unknown audio action.");
        }
        if (args.Value<string>("action") is "load" or "play" && player.PlayedFile.Id != player.SelectedAudioFile.Id)
            throw new NotSupportedException("The audio device or codec could not load this track. Inspect the session log.");
        return AudioState();
    }
}
