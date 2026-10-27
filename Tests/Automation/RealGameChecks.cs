using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FModel.Framework;
using FModel.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

internal static class RealGameChecks
{
    public static async Task Run(string executable, string root)
    {
        var originals = ProfileStore.Enumerate(out var corrupt).Where(x => Directory.Exists(x.Game.GameDirectory)).ToArray();
        if (corrupt.Count > 0 || originals.Length == 0) throw new InvalidOperationException("Valid local game profiles are required; corrupt profiles must be resolved first.");
        var protectedRoots = originals.SelectMany(p => new[] { p.Game.GameDirectory, p.Settings.OutputDirectory,
            p.Settings.RawDataDirectory, p.Settings.PropertiesDirectory, p.Settings.TextureDirectory, p.Settings.ModelDirectory,
            p.Settings.AudioDirectory, p.Settings.CodeDirectory }).Append(ProfileStore.ProfilesDirectory)
            .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (protectedRoots.Any(p => IsWithin(root, p))) throw new InvalidOperationException("Test output must be outside existing game and saved-data directories.");
        Console.WriteLine($"Real-game test artifacts: {root}");
        var profileHashes = Directory.EnumerateFiles(ProfileStore.ProfilesDirectory, "*.json").ToDictionary(x => x, HashFile);
        var before = protectedRoots.ToDictionary(x => x, Inventory);
        var profiles = Directory.CreateDirectory(Path.Combine(root, "Profiles")).FullName;
        var results = new JArray();
        var report = new JObject { ["profiles"] = results };
        try
        {
            foreach (var original in originals)
            {
                var copy = GameProfile.DeepClone(original);
                var output = Directory.CreateDirectory(Path.Combine(root, "Output", copy.Id)).FullName;
                var cache = Path.Combine(original.Settings.OutputDirectory, ".data");
                if (Directory.Exists(cache))
                    foreach (var source in Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories)
                        .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".dll" or ".exe" or ".usmap" or ".jmap" or ".gz"))
                    {
                        var target = Path.Combine(output, ".data", Path.GetRelativePath(cache, source));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(source, target);
                    }
                copy.Settings.OutputDirectory = copy.Settings.RawDataDirectory = copy.Settings.PropertiesDirectory =
                    copy.Settings.TextureDirectory = copy.Settings.ModelDirectory = copy.Settings.AudioDirectory = copy.Settings.CodeDirectory = output;
                await File.WriteAllTextAsync(Path.Combine(profiles, ProfileStore.BuildFileName(copy.Game.GameName, copy.Id)),
                    JsonConvert.SerializeObject(copy, JsonNetSerializer.SerializerSettings));
            }
            await using var client = await RpcClient.Start(executable, profiles);
            foreach (var profile in originals)
            {
                var result = new JObject { ["id"] = profile.Id, ["name"] = profile.Game.GameName, ["checks"] = new JArray(), ["limitations"] = new JArray() };
                results.Add(result);
                var args = new JObject { ["profileId"] = profile.Id };
                try
                {
                    var timer = Stopwatch.StartNew();
                    JToken status;
                    do
                    {
                        status = await client.Call("session_status", args);
                        if (status.Value<string>("state") == "failed") throw new Exception(status.Value<string>("error"));
                        if (status.Value<string>("state") == "ready") break;
                        await Task.Delay(500);
                    } while (timer.Elapsed < TimeSpan.FromMinutes(3));
                    Require(status.Value<string>("state") == "ready" && status.Value<int>("files") > 0, "Real game index is available");
                    result["session"] = status;
                    Console.WriteLine($"READY: {profile.Game.GameName}: {status.Value<int>("files")} files");
                    if (Environment.GetEnvironmentVariable("FMODEL_TEST_GUI_HANDOFF") == "true")
                    {
                        await GuiSessionChecks.Run(executable, profiles, args, client);
                        Pass(result, "Existing process promoted to GUI, queried concurrently, hidden and shown again without reloading");
                        continue;
                    }
                    result["archives"] = await client.Call("list_archives", new JObject(args) { ["limit"] = 5 });
                    result["keys"] = await client.Call("aes_status", args);
                    result["roots"] = await client.Call("list_directory", args);
                    result["statistics"] = await client.Call("index_statistics", args);
                    Pass(result, "Mounted index, archive metadata, AES GUIDs, directory tree and extension statistics");
                    var backup = Path.Combine(root, profile.Id + ".fbkp");
                    await client.Call("create_index_backup", new JObject(args) { ["backupPath"] = backup });
                    var comparison = await client.Call("compare_index_backup", new JObject(args) { ["backupPath"] = backup });
                    Require(comparison["results"]!.Value<int>("total") == 0, "Backup comparison must be unchanged");
                    await client.ExpectError("create_index_backup", new JObject(args) { ["backupPath"] = backup });
                    Pass(result, "Real index backup round trip and overwrite rejection");
                    await client.Call("select_archives", new JObject(args) { ["archives"] = new JArray() });
                    Require((await client.Call("search_files", args)).Value<int>("total") == 0, "Empty selection must hide all files");
                    await client.Call("select_archives", new JObject(args) { ["reset"] = true });
                    Pass(result, "Archive selection and reset");

                    var candidates = await client.Call("search_files", new JObject(args) { ["extension"] = "uasset", ["limit"] = 80 });
                    var found = new HashSet<string>();
                    var animationAttempts = 0;
                    var total = candidates.Value<int>("total");
                    foreach (var offset in new[] { 0, total / 4, total / 2, total * 3 / 4 }.Distinct())
                    {
                        if (offset != 0) candidates = await client.Call("search_files", new JObject(args) { ["extension"] = "uasset", ["offset"] = offset, ["limit"] = 80 });
                        foreach (var file in candidates["items"]!)
                        {
                            var assetArgs = new JObject(args) { ["path"] = file["path"] };
                            try
                            {
                                var exports = await client.Call("list_exports", new JObject(assetArgs) { ["limit"] = 20 });
                                foreach (var export in exports["items"]!.Where(x => x["type"] != null))
                                {
                                    var type = export.Value<string>("type")!;
                                    var objectArgs = new JObject(assetArgs) { ["index"] = export["index"] };
                                    if (!found.Contains("properties"))
                                    {
                                        var versions = await client.Call("file_versions", assetArgs);
                                        var version = versions["items"]!.First()["file"]!;
                                        var versionInfo = await client.Call("file_info", new JObject(assetArgs) { ["sourceArchive"] = version["archive"] });
                                        Require(versionInfo["file"]!.Value<long>("size") == version.Value<long>("size"), "Explicit container selection must return that version");
                                        result["sample"] = new JObject { ["file"] = file, ["exports"] = exports,
                                            ["versions"] = versions,
                                            ["properties"] = await client.Call("read_export", new JObject(objectArgs) { ["length"] = 4096 }),
                                            ["imports"] = await client.Call("list_imports", new JObject(assetArgs) { ["limit"] = 5 }),
                                            ["metadata"] = await client.Call("package_metadata", new JObject(assetArgs) { ["length"] = 4096 }) };
                                        found.Add("properties"); Pass(result, "Actual package exports, properties, imports and metadata");
                                    }
                                    if (type == "Texture2D" && !found.Contains("texture"))
                                    {
                                        var texture = await Capture(client, "preview_texture", objectArgs, Path.Combine(root, profile.Id + "-texture.png"));
                                        await Capture(client, "preview_texture", new JObject(objectArgs) { ["channel"] = "a", ["mipIndex"] = texture["mipIndex"] }, Path.Combine(root, profile.Id + "-alpha.png"));
                                        found.Add("texture"); Pass(result, "Actual texture and alpha channel decoded at an explicit mip");
                                    }
                                    if (type is "StaticMesh" or "SkeletalMesh" && !found.Contains("model"))
                                    {
                                        var scene = await client.Call("viewer_load", objectArgs);
                                        var modelId = scene["models"]!["items"]!.First()["id"];
                                        result["scene"] = scene;
                                        result["model"] = await client.Call("viewer_model", new JObject(args) { ["modelId"] = modelId });
                                        await client.Call("viewer_model", new JObject(args) { ["modelId"] = modelId, ["focus"] = true });
                                        if (result["model"]!["materials"]!.Any())
                                            result["materialParameters"] = await client.Call("viewer_material_parameters", new JObject(args) { ["modelId"] = modelId, ["index"] = 0,
                                                ["values"] = new JObject { ["roughnessMin"] = 0, ["roughnessMax"] = 1 } });
                                        await client.Call("viewer_configure", new JObject(args) { ["values"] = new JObject { ["showGrid"] = false, ["showSkybox"] = false } });
                                        await client.Call("viewer_timeline", new JObject(args) { ["paused"] = true });
                                        await Capture(client, "viewer_capture", args, Path.Combine(root, profile.Id + "-model.png"));
                                        await client.Call("viewer_model", new JObject(args) { ["modelId"] = modelId, ["values"] = new JObject { ["wireframe"] = true } });
                                        await Capture(client, "viewer_capture", args, Path.Combine(root, profile.Id + "-wireframe.png"));
                                        await client.ExpectError("viewer_model", new JObject(args) { ["modelId"] = modelId, ["values"] = new JObject { ["instance"] = 0.5 } });
                                        await ModelEditChecks.Run(client, objectArgs, modelId!);
                                        await client.Call("viewer_model", new JObject(args) { ["modelId"] = modelId, ["remove"] = true });
                                        Require((await client.Call("viewer_state", args))["models"]!.Value<int>("total") == 0, "Scene model removal must be visible");
                                        await client.Call("viewer_close", args);
                                        found.Add("model"); Pass(result, "Actual mesh rendered, inspected and switched to wireframe");
                                    }
                                    if (type == "AnimSequence" && !found.Contains("animation") && animationAttempts++ < 3)
                                    {
                                        var animation = await client.Call("viewer_animation", objectArgs);
                                        var duration = animation["timeline"]!.Value<double>("duration");
                                        Require(duration > 0 && animation["animations"]!.Any(), "Animation must have a converted timeline");
                                        var seek = await client.Call("viewer_timeline", new JObject(args) { ["paused"] = true, ["seconds"] = duration / 2, ["speed"] = 0.5 });
                                        Require(Math.Abs(seek["timeline"]!.Value<double>("seconds") - duration / 2) < 0.01, "Animation seek must update the renderer time");
                                        await Capture(client, "viewer_capture", args, Path.Combine(root, profile.Id + "-animation.png"));
                                        result["animation"] = animation;
                                        await client.Call("viewer_timeline", new JObject(args) { ["clear"] = true });
                                        await client.Call("viewer_close", args);
                                        found.Add("animation"); Pass(result, "Actual animation converted, paused, sought, rendered and cleared");
                                    }
                                }
                            }
                            catch (Exception ex) { Limit(result, $"{file.Value<string>("path")}: {ex.Message}"); }
                            if (found.Contains("texture") && found.Contains("model") && found.Contains("animation")) break;
                        }
                        if (found.Contains("texture") && found.Contains("model") && found.Contains("animation")) break;
                    }
                    foreach (var kind in new[] { "properties", "texture", "model", "animation" })
                        if (!found.Contains(kind)) Limit(result, $"No successful {kind} check in the bounded package sample.");
                    var audio = await client.Call("search_files", new JObject(args) { ["extension"] = "wem", ["sort"] = "size_ascending", ["limit"] = 5 });
                    if (audio.Value<int>("total") > 5)
                        audio = await client.Call("search_files", new JObject(args) { ["extension"] = "wem", ["sort"] = "size_ascending", ["offset"] = audio.Value<int>("total") / 2, ["limit"] = 5 });
                    foreach (var file in audio["items"]!)
                    {
                        try
                        {
                            result["audio"] = await client.Call("audio_load", new JObject(args) { ["path"] = file["path"] });
                            var track = result["audio"]!["tracks"]!.Last()["id"];
                            var loaded = await client.Call("audio_control", new JObject(args) { ["action"] = "load", ["trackId"] = track });
                            var duration = loaded["playback"]!.Value<double>("duration");
                            Require(duration >= 0.1, "Audio sample must have a measurable playback duration");
                            var playing = await client.Call("audio_control", new JObject(args) { ["action"] = "play", ["volume"] = 0 });
                            Require(playing["playback"]!.Value<string>("state") == "Playing" && playing["playback"]!.Value<double>("volume") == 0, "Audio device must be playing muted");
                            var paused = await client.Call("audio_control", new JObject(args) { ["action"] = "pause" });
                            Require(paused["playback"]!.Value<string>("state") == "Paused", "Audio device must be paused");
                            await client.Call("audio_control", new JObject(args) { ["action"] = "seek", ["fraction"] = 0.5 });
                            result["playback"] = await client.Call("audio_state", args);
                            Require(Math.Abs(result["playback"]!["playback"]!.Value<double>("seconds") - duration / 2) < 0.05, "Audio position must follow seek");
                            var stopped = await client.Call("audio_control", new JObject(args) { ["action"] = "stop" });
                            Require(stopped["playback"]!.Value<string>("state") == "Stopped", "Audio device must stop");
                            var reloaded = await client.Call("audio_control", new JObject(args) { ["action"] = "load" });
                            Require(reloaded["playback"]!.Value<double>("volume") == 0, "Session mute must survive reloading the audio track");
                            Pass(result, "Actual audio decoded, loaded, played muted, paused, sought and stopped");
                            break;
                        }
                        catch (Exception ex) { Limit(result, $"Audio: {ex.Message}"); }
                    }
                    if (!audio["items"]!.Any()) Limit(result, "No standalone Wwise media in the mounted index.");
                    result["log"] = await client.Call("read_session_log", new JObject(args) { ["length"] = 4096 });
                }
                catch (Exception ex) { result["error"] = ex.Message; Console.WriteLine($"FAILED: {profile.Game.GameName}: {ex.Message}"); }
                finally { try { await client.Call("close_session", args); } catch (Exception ex) { Limit(result, $"Close: {ex.Message}"); } }
                await File.WriteAllTextAsync(Path.Combine(root, "real-game-report.json"), report.ToString());
            }
        }
        finally
        {
            var unchanged = protectedRoots.All(p => before[p] == Inventory(p)) && profileHashes.All(p => File.Exists(p.Key) && HashFile(p.Key) == p.Value);
            report["originalDataUnchanged"] = unchanged;
            report["verification"] = "Original profile SHA256 plus original game/output file-path, size and last-write-time inventories; game payloads are not fully hashed.";
            await File.WriteAllTextAsync(Path.Combine(root, "real-game-report.json"), report.ToString());
            Require(unchanged, "Original game, profile and saved-output inventories must remain unchanged");
            Console.WriteLine("PASS: Original profiles and game/saved-output inventories unchanged");
        }
        Require(results.All(x => x["error"] == null), "All real profiles must complete basic checks");
    }

    private static bool IsWithin(string path, string parent) => (Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/') + "/")
        .StartsWith(Path.GetFullPath(parent).Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Inventory(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var file = new FileInfo(path);
            hash.AppendData(Encoding.UTF8.GetBytes($"{path.Replace('\\', '/').ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pass(JObject result, string message) { ((JArray) result["checks"]!).Add(message); Console.WriteLine($"PASS: {message}"); }
    private static void Limit(JObject result, string message)
    {
        var list = (JArray) result["limitations"]!;
        if (list.Count < 30) list.Add(message);
        Console.WriteLine($"LIMITATION: {message}");
    }
    private static async Task<JToken> Capture(RpcClient client, string operation, JObject args, string path)
    {
        var response = await client.Request("tools/call", new JObject { ["name"] = operation, ["arguments"] = args });
        if (response.Value<bool?>("isError") == true) throw new Exception(response.ToString());
        var bytes = Convert.FromBase64String(response["content"]!.First(x => x.Value<string>("type") == "image").Value<string>("data")!);
        using var bitmap = SKBitmap.Decode(bytes);
        Require(bitmap != null && bitmap.Width > 0 && bitmap.Height > 0, "Preview must be a valid image");
        await File.WriteAllBytesAsync(path, bytes);
        return JToken.Parse(response["content"]!.First(x => x.Value<string>("type") == "text").Value<string>("text")!);
    }
}
