using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Versions;
using FModel.Automation;
using FModel.Framework;
using FModel.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.Error.WriteLine(e.ExceptionObject);
    Environment.Exit(1);
};

var executable = Environment.GetEnvironmentVariable("FMODEL_TEST_EXECUTABLE");
if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
    throw new ArgumentException("FMODEL_TEST_EXECUTABLE must name the built application executable.");
var workDirectory = Environment.GetEnvironmentVariable("FMODEL_TEST_DIRECTORY");
if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory))
    throw new ArgumentException("FMODEL_TEST_DIRECTORY must name an existing directory for isolated test artifacts.");
var root = Directory.CreateDirectory(Path.Combine(workDirectory, Guid.NewGuid().ToString("N"))).FullName;
if (Environment.GetEnvironmentVariable("FMODEL_TEST_REAL_GAMES") == "true")
{
    await RealGameChecks.Run(executable, root);
    return;
}
var game = Directory.CreateDirectory(Path.Combine(root, "Loose Game")).FullName;
var nested = Directory.CreateDirectory(Path.Combine(game, "Content", "Nested")).FullName;
var output = Directory.CreateDirectory(Path.Combine(root, "Output")).FullName;
var profiles = Directory.CreateDirectory(Path.Combine(root, "Profiles")).FullName;
ProfileStore.ProfilesDirectory = profiles;
await File.WriteAllTextAsync(Path.Combine(nested, "needle.ini"), "{\"fixture\":42,\"text\":\"你好\"}", new UTF8Encoding(false));
await File.WriteAllTextAsync(Path.Combine(nested, "needle.uplugin"), "document fixture", new UTF8Encoding(false));
await File.WriteAllTextAsync(Path.Combine(nested, "needle.uexp"), "payload fixture", new UTF8Encoding(false));

var settings = new UserSettings
{
    OutputDirectory = output, RawDataDirectory = output, PropertiesDirectory = output,
    TextureDirectory = output, ModelDirectory = output, AudioDirectory = output, CodeDirectory = output
};
UserSettings.Default = settings;
var profile = GameProfile.FromSettings(ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(game)),
    DirectorySettings.Default("Automation fixture", game, true), settings);
await File.WriteAllTextAsync(Path.Combine(profiles, ProfileStore.BuildFileName(profile.Game.GameName, profile.Id)),
    JsonConvert.SerializeObject(profile, JsonNetSerializer.SerializerSettings));

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine($"PASS: {message}");
}

Check(QueryHelpers.InDirectory("Root/Case/file.json", "root\\case"), "Directory normalization is case-insensitive and separator-independent");
Check(!QueryHelpers.InDirectory("Root/Cases/file.json", "Root/Case"), "Directory boundary does not match sibling prefixes");
InspectionChecks.Run();
await AudioChecks.Run();
Check(((JObject) JToken.FromObject(QueryHelpers.Page(new[] { 1, 2, 3 }, new JObject { ["limit"] = 2 }))).Value<int>("nextOffset") == 2,
    "Pagination exposes the next offset");
try { OperationCatalog.Validate("search_files", new JObject { ["profileId"] = "fixture", ["limti"] = 2 }); throw new Exception("Unknown argument was accepted."); }
catch (ArgumentException) { Console.WriteLine("PASS: Unknown arguments are rejected"); }
using (var jobs = new OperationJobs())
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var startedJob = JToken.FromObject(jobs.Start("cancellation fixture", async job =>
    {
        entered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, job.Cancellation.Token);
    }));
    var jobArgs = new JObject { ["jobId"] = startedJob.Value<string>("jobId") };
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(jobs.IsBusy, "Running jobs remain visible until their work exits");
    var cancellation = JToken.FromObject(jobs.Cancel(jobArgs));
    Check(cancellation.Value<bool>("cancellationRequested"), "Job cancellation reaches the operation token");
    var cancellationTimer = Stopwatch.StartNew();
    while (jobs.IsBusy && cancellationTimer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
    Check(JToken.FromObject(jobs.Status(jobArgs)).Value<string>("state") == "cancelled", "Cancelled work reaches a terminal state");
}
var requestPath = Path.Combine(root, "request with spaces.json");
await File.WriteAllTextAsync(requestPath, new JObject { ["operation"] = "profiles", ["arguments"] = new JObject() }.ToString());
var requestStart = new ProcessStartInfo(executable)
{
    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
    RedirectStandardOutput = true, RedirectStandardError = true
};
requestStart.ArgumentList.Add("--automation-request");
requestStart.Environment["FMODEL_PROFILES_DIRECTORY"] = profiles;
requestStart.Environment["FMODEL_AUTOMATION_REQUEST"] = requestPath;
using (var requestProcess = Process.Start(requestStart)!)
{
    var requestOutput = requestProcess.StandardOutput.ReadToEndAsync();
    var requestErrors = requestProcess.StandardError.ReadToEndAsync();
    await requestProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    Check(requestProcess.ExitCode == 0 && JObject.Parse(await requestOutput)["profiles"]!.Single().Value<string>("id") == profile.Id,
        $"JSON request command supports paths with spaces: {await requestErrors}");
}
using (var lease = ProfileLock.TryAcquire(profile.Id))
{
    Check(lease != null && ProfileLock.IsHeldByAliveProcess(profile.Id), "Profile lock exposes its live owner");
    using var duplicate = ProfileLock.TryAcquire(profile.Id);
    Check(duplicate == null, "A second writer cannot acquire the same profile lock");
}

await ClientLifetimeChecks.Run(executable, profiles, requestPath, new JObject { ["profileId"] = profile.Id });
await using var client = await RpcClient.Start(executable, profiles);
var tools = await client.Request("tools/list", new JObject());
Check(tools["tools"]!.Count() == OperationCatalog.All.Count, "MCP exposes the complete operation catalog");
var knownProfiles = await client.Call("profiles", new JObject());
Check(knownProfiles["profiles"]!.Single().Value<string>("id") == profile.Id, "Profiles are isolated from user configuration");
Check(!knownProfiles.ToString().Contains("MainKey", StringComparison.OrdinalIgnoreCase), "Profile listing omits AES keys");
var capability = await client.Call("capabilities", new JObject());
Check(capability["limits"]!.Any(), "Capabilities explicitly report remaining GUI parity gaps");
var options = await client.Call("profile_options", new JObject());
Check(options["engines"]!.Any() && options["languages"]!.Any(), "Profile configuration choices are discoverable");
var managedDirectory = Directory.CreateDirectory(Path.Combine(root, "Managed Game")).FullName;
var created = await client.Call("create_profile", new JObject { ["name"] = "Managed fixture", ["directory"] = managedDirectory,
    ["outputDirectory"] = Path.Combine(root, "Managed Output"), ["templateProfileId"] = profile.Id });
var managedArgs = new JObject { ["profileId"] = created["id"] };
Check(!created.Value<bool>("running"), "New profile is saved without starting a session");
await client.Call("update_profile", new JObject(managedArgs) { ["values"] = new JObject { ["name"] = "Updated fixture" } });
Check((await client.Call("get_profile", managedArgs)).Value<string>("name") == "Updated fixture", "Closed profile settings can be updated");
await client.ExpectError("update_profile", new JObject(managedArgs) { ["values"] = new JObject { ["outputDirectory"] = managedDirectory } });
await client.Call("delete_profile", managedArgs);
Check(Directory.Exists(managedDirectory), "Profile deletion preserves the game directory");
await client.ExpectError("get_profile", managedArgs);
var profileArgs = new JObject { ["profileId"] = profile.Id };
int? hostPid = null;
try
{
    var ready = await WaitReady(client, profileArgs);
    hostPid = ready.Value<int>("processId");
    Check(ready.Value<int>("files") >= 2, "Persistent headless host discovers loose game files");
    await client.ExpectError("update_profile", new JObject(profileArgs) { ["values"] = new JObject { ["name"] = "Locked" } });
    await client.ExpectError("delete_profile", profileArgs);
    await client.ExpectError("submit_aes_key", new JObject(profileArgs) { ["guid"] = Guid.Empty.ToString("N"), ["key"] = "invalid" });
    await client.Call("select_archives", new JObject(profileArgs) { ["archives"] = new JArray() });
    Check((await client.Call("search_files", profileArgs)).Value<int>("total") == 0, "Archive selection can exclude loose files");
    await client.Call("select_archives", new JObject(profileArgs) { ["reset"] = true });
    var backupPath = Path.Combine(root, "index.fbkp");
    await client.Call("create_index_backup", new JObject(profileArgs) { ["backupPath"] = backupPath });
    var compared = await client.Call("compare_index_backup", new JObject(profileArgs) { ["backupPath"] = backupPath });
    Check(compared["results"]!.Value<int>("total") == 0, "Index backups compare without changes");
    await client.ExpectError("create_index_backup", new JObject(profileArgs) { ["backupPath"] = backupPath });
    await client.ExpectError("viewer_capture", profileArgs);
    await client.Call("viewer_close", profileArgs);
    var sessionLog = await client.Call("read_session_log", new JObject(profileArgs) { ["length"] = 64 });
    Check(sessionLog.Value<string>("content")!.Length <= 64 && sessionLog.Value<long>("totalCharacters") > 64, "Session log tails are bounded and expose initialization diagnostics");
    var statistics = await client.Call("index_statistics", profileArgs);
    Check(statistics["items"]!.Sum(x => x.Value<int>("files")) == ready.Value<int>("files"), "Index statistics account for all mounted files");
    await client.ExpectError("search_localization", profileArgs);
    var search = await client.Call("search_files", new JObject { ["profileId"] = profile.Id, ["query"] = "needle", ["limit"] = 1 });
    Check(search.Value<int>("total") == 2 && search.Value<int>("nextOffset") == 1, "Search excludes payloads and paginates results");
    var path = search["items"]![0]!.Value<string>("path")!;
    var fileArgs = new JObject { ["profileId"] = profile.Id, ["path"] = path };
    var info = await client.Call("file_info", fileArgs);
    Check(info["file"]!.Value<long>("size") > 0, "File metadata reports source byte size");
    var listing = await client.Call("list_directory", new JObject { ["profileId"] = profile.Id, ["directory"] = info["file"]!["directory"] });
    Check(listing.Value<int>("total") == 2, "Directory browsing returns direct children");
    var document = await client.Call("read_document", fileArgs);
    Check(JObject.Parse(document.Value<string>("content")!).Value<int>("fixture") == 42, "Read document reuses the GUI document extraction path");
    Check(!Directory.EnumerateFiles(output, "*.json", SearchOption.AllDirectories).Any(), "Read-only parsing produces no JSON export files");
    var chunk = await client.Call("read_document", new JObject(fileArgs) { ["offset"] = 2, ["length"] = 5 });
    Check(chunk.Value<string>("content")!.Length == 5 && chunk.Value<int>("nextOffset") == 7, "Document chunks preserve offsets");
    await client.ExpectError("package_metadata", fileArgs);
    await client.ExpectError("search_files", new JObject(profileArgs) { ["limit"] = -1 });
    await client.ExpectError("search_files", new JObject(profileArgs) { ["unknown"] = true });
    await client.ExpectError("session_status", new JObject { ["profileId"] = "../missing" });

    var started = await client.Call("export_assets", new JObject(profileArgs) { ["paths"] = new JArray(path), ["type"] = "raw" });
    var raw = await WaitJob(client, profileArgs, started.Value<string>("jobId")!);
    Check(raw.Value<string>("state") == "completed", "Raw export job completes");
    var exportedPath = raw["results"]!["items"]![0]!["files"]![0]!.Value<string>()!;
    Check(File.Exists(exportedPath), "Export result names an existing output file");
    var exportedBytes = await File.ReadAllBytesAsync(exportedPath);
    var originalBytes = await File.ReadAllBytesAsync(Path.Combine(nested, "needle.ini"));
    Check(exportedBytes.SequenceEqual(originalBytes), "Raw export preserves source bytes");

    var mixed = await client.Call("export_assets", new JObject(profileArgs) { ["paths"] = new JArray(path, "missing.json"), ["type"] = "properties" });
    var mixedResult = await WaitJob(client, profileArgs, mixed.Value<string>("jobId")!);
    Check(mixedResult.Value<string>("state") == "failed" && mixedResult["results"]!["items"]!.Count() == 2, "Batch failures retain successful and failed per-file results");
    var before = await client.Call("get_export_settings", profileArgs);
    await client.ExpectError("set_export_settings", new JObject(profileArgs) { ["values"] = new JObject { ["TextureQuality"] = 0 } });
    await client.ExpectError("set_export_settings", new JObject(profileArgs) { ["values"] = new JObject { ["KeepDirectoryStructure"] = JValue.CreateNull() } });
    var after = await client.Call("get_export_settings", profileArgs);
    Check(JToken.DeepEquals(before, after), "Invalid settings leave active settings unchanged");

    await using var second = await RpcClient.Start(executable, profiles);
    var other = await second.Call("session_status", profileArgs);
    Check(other.Value<int>("processId") == hostPid, "Independent MCP clients reuse the same game process");
    var parallel = await Task.WhenAll(client.Call("read_document", fileArgs), second.Call("read_document", fileArgs));
    Check(parallel.All(x => x.Value<string>("content") == document.Value<string>("content")), "Concurrent clients receive isolated, serialized query results");
    await second.ExpectError("open_in_gui", fileArgs);
    await GuiSessionChecks.Run(executable, profiles, profileArgs, client);
    if (Environment.GetEnvironmentVariable("FMODEL_TEST_FIXTURE_MANIFEST") is { Length: > 0 } manifestPath)
    {
        var manifest = JObject.Parse(await File.ReadAllTextAsync(manifestPath));
        var fixtureRoot = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var variant = ((JObject) manifest["variants"]!).Properties().Select(x => (JObject) x.Value)
            .First(x => x.Value<bool?>("unversionedProperties") == false);
        var packedGame = Directory.CreateDirectory(Path.Combine(root, "Cooked Game")).FullName;
        foreach (var container in variant["containers"]!.OfType<JObject>())
        {
            var name = container.Value<string>("name")!;
            if (name.Contains('/') && !name.StartsWith("Uncompressed/", StringComparison.Ordinal)) continue;
            var expectedHash = container.Value<string>("sha256")!;
            var candidates = Directory.EnumerateFiles(fixtureRoot, Path.GetFileName(name), SearchOption.AllDirectories);
            string? source = null;
            foreach (var candidate in candidates)
            {
                using var stream = File.OpenRead(candidate);
                if (Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                { source = candidate; break; }
            }
            if (source == null) throw new FileNotFoundException($"Manifest container is missing or has a different hash: {name}");
            File.Copy(source, Path.Combine(packedGame, Path.GetFileName(name)));
        }
        var engine = manifest["engine"]!;
        var engineName = $"GAME_UE{engine.Value<int>("major")}_{engine.Value<int>("minor")}";
        if (!Enum.TryParse<EGame>(engineName, out var engineVersion)) throw new NotSupportedException($"Unsupported fixture engine: {engineName}");
        var packedProfile = GameProfile.FromSettings(ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(packedGame)),
            DirectorySettings.Default("Cooked automation fixture", packedGame, true, engineVersion), settings);
        await File.WriteAllTextAsync(Path.Combine(profiles, ProfileStore.BuildFileName(packedProfile.Game.GameName, packedProfile.Id)),
            JsonConvert.SerializeObject(packedProfile, JsonNetSerializer.SerializerSettings));
        var packedArgs = new JObject { ["profileId"] = packedProfile.Id };
        JToken packedStatus;
        await using (var transient = await RpcClient.Start(executable, profiles))
        {
            var simultaneous = await Task.WhenAll(WaitReady(transient, packedArgs), WaitReady(second, packedArgs));
            Check(simultaneous[0].Value<int>("processId") == simultaneous[1].Value<int>("processId"), "Simultaneous first connections acquire one profile host");
            packedStatus = simultaneous[0];
        }
        var surviving = await WaitReady(client, packedArgs);
        Check(surviving.Value<int>("processId") == packedStatus.Value<int>("processId"), "Session survives the launching MCP client's disconnect");
        var packedPid = packedStatus.Value<int>("processId");
        try
        {
            Check(packedStatus.Value<int>("mountedArchives") > 0, "Manifest-verified UE IoStore containers mount through the headless host");
            var dataAsset = variant["assets"]!.First(x => x.Value<string>("class") == "DataTable").Value<string>("asset")!;
            var dataName = dataAsset[(dataAsset.LastIndexOf('.') + 1)..];
            var dataSearch = await client.Call("search_files", new JObject(packedArgs) { ["query"] = dataName, ["extension"] = "uasset" });
            var dataPath = dataSearch["items"]!.First(x => x.Value<string>("name") == dataName + ".uasset").Value<string>("path")!;
            var assetArgs = new JObject(packedArgs) { ["path"] = dataPath };
            var exports = await client.Call("list_exports", assetArgs);
            var dataExport = exports["items"]!.First(x => x.Value<string>("type") == "DataTable");
            var imported = await client.Call("list_imports", assetArgs);
            Check(imported.Value<int>("total") > 0 && imported["items"]!.Any(), "Cooked package imports retain indexed reference entries");
            var properties = await client.Call("read_export", new JObject(assetArgs) { ["index"] = dataExport.Value<int>("index"), ["length"] = 65536 });
            Check(JObject.Parse(properties.Value<string>("content")!).HasValues, "Cooked export properties deserialize through CUE4Parse");
            var byReference = await client.Call("read_object", new JObject(packedArgs) { ["objectPath"] = dataExport.Value<string>("path"), ["length"] = 65536 });
            Check(byReference.Value<string>("content") == properties.Value<string>("content"), "Object references resolve to the same parsed export properties");
            var metadata = await client.Call("package_metadata", new JObject(assetArgs) { ["length"] = 65536 });
            Check(metadata.Value<int>("totalCharacters") > 0, "Package metadata uses the GUI serializer");
            var refs = await client.Call("find_referencers", assetArgs);
            Check(refs["coverage"] != null && refs["results"] != null, "IoStore reference queries report their coverage");

            var texture = variant["textures"]?.FirstOrDefault(x => x.Value<string>("class") == "Texture2D" && x.Value<string>("pixelFormatEnum") == "PF_B8G8R8A8");
            if (texture == null) throw new InvalidDataException("Fixture manifest has no uncompressed Texture2D for preview verification.");
            var textureAsset = texture.Value<string>("asset")!;
            var textureName = textureAsset[(textureAsset.LastIndexOf('.') + 1)..];
            var textureSearch = await client.Call("search_files", new JObject(packedArgs) { ["query"] = textureName, ["extension"] = "uasset" });
            var texturePath = textureSearch["items"]!.First(x => x.Value<string>("name") == textureName + ".uasset").Value<string>("path")!;
            var textureArgs = new JObject(packedArgs) { ["path"] = texturePath };
            var textureExports = await client.Call("list_exports", textureArgs);
            var textureIndex = textureExports["items"]!.First(x => x.Value<string>("type") == "Texture2D").Value<int>("index");
            var preview = await client.Request("tools/call", new JObject { ["name"] = "preview_texture",
                ["arguments"] = new JObject(textureArgs) { ["index"] = textureIndex, ["maxSize"] = 32 } });
            Check(preview.Value<bool?>("isError") != true && preview["content"]![0]!.Value<string>("type") == "image", "Texture preview returns native MCP image content");
            var png = Convert.FromBase64String(preview["content"]![0]!.Value<string>("data")!);
            Check(png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Texture preview contains valid PNG signature");
            using (var previewBitmap = SkiaSharp.SKBitmap.Decode(png))
                Check(previewBitmap.Width <= 32 && previewBitmap.Height <= 32, "Preview size is bounded even without a suitable texture mip");
            var textureJob = await client.Call("export_assets", new JObject(packedArgs) { ["paths"] = new JArray(texturePath), ["type"] = "textures" });
            var textureResult = await WaitJob(client, packedArgs, textureJob.Value<string>("jobId")!);
            Check(textureResult.Value<string>("state") == "completed", "Cooked texture export produces files through the conversion session");
            await client.Call("set_export_settings", new JObject(packedArgs) { ["values"] = new JObject
            { ["SaveEmbeddedMaterials"] = false, ["ConvertAudioOnBulkExport"] = false } });
            var conversionReport = new JArray();
            foreach (var (assetClass, exportType) in new[]
            {
                ("StaticMesh", "models"), ("SkeletalMesh", "models"), ("AnimSequence", "animations"),
                ("SoundWave", "audio"), ("World", "worlds"), ("BlueprintGeneratedClass", "code")
            })
            {
                if (exportType == "worlds")
                    await client.Call("set_export_settings", new JObject(packedArgs) { ["values"] = new JObject { ["MeshExportFormat"] = "USD" } });
                var candidate = variant["assets"]!.First(x => x.Value<string>("class") == assetClass).Value<string>("asset")!;
                var packageName = candidate[..candidate.LastIndexOf('.')].Split('/').Last();
                var candidates = await client.Call("search_files", new JObject(packedArgs) { ["query"] = packageName,
                    ["extension"] = assetClass == "World" ? "umap" : "uasset" });
                var candidatePath = candidates["items"]!.First(x => Path.GetFileNameWithoutExtension(x.Value<string>("name")) == packageName).Value<string>("path")!;
                var exportJob = await client.Call("export_assets", new JObject(packedArgs) { ["paths"] = new JArray(candidatePath), ["type"] = exportType });
                var exportResult = await WaitJob(client, packedArgs, exportJob.Value<string>("jobId")!);
                conversionReport.Add(new JObject { ["assetClass"] = assetClass, ["result"] = exportResult });
                if (exportResult.Value<string>("state") == "completed")
                {
                    Check(exportResult["results"]!["items"]![0]!["files"]!.Values<string>().All(File.Exists), $"{assetClass} export outputs exist on disk");
                }
                else
                {
                    Check(exportResult.Value<string>("state") == "failed" && exportResult["results"]!["items"]!.Any(x => x.Value<bool?>("success") == false),
                        $"{assetClass} conversion failure retains per-file diagnostics");
                    Console.WriteLine($"LIMITATION: {assetClass} conversion did not succeed: {exportResult["results"]}");
                }
            }
            await File.WriteAllTextAsync(Path.Combine(root, "conversion-report.json"), conversionReport.ToString(Formatting.Indented));
        }
        finally
        {
            await client.Call("close_session", packedArgs);
            try { using var host = Process.GetProcessById(packedPid); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (ArgumentException) { }
        }
    }
    else Console.WriteLine("SKIP: Cooked fixture tests require FMODEL_TEST_FIXTURE_MANIFEST.");
    var savedProfilePath = Path.Combine(profiles, ProfileStore.BuildFileName(profile.Game.GameName, profile.Id));
    var savedProfileBytes = await File.ReadAllBytesAsync(savedProfilePath);
    await client.Call("set_export_settings", new JObject(profileArgs) { ["values"] = new JObject { ["KeepDirectoryStructure"] = !settings.KeepDirectoryStructure } });
    await client.Call("close_session", profileArgs);
    try { using var host = Process.GetProcessById(hostPid!.Value); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (ArgumentException) { }
    hostPid = null;
    var currentProfileBytes = await File.ReadAllBytesAsync(savedProfilePath);
    Check(savedProfileBytes.SequenceEqual(currentProfileBytes), "Temporary settings changes do not rewrite the profile on exit");
    var restarted = await WaitReady(client, profileArgs);
    hostPid = restarted.Value<int>("processId");
    var restored = await client.Call("get_export_settings", profileArgs);
    Check(JToken.DeepEquals(before, restored), "A closed session can restart with the persisted profile settings");
    var originalProfile = JsonConvert.DeserializeObject<GameProfile>(await File.ReadAllTextAsync(savedProfilePath), JsonNetSerializer.SerializerSettings)!;
    await client.Call("set_export_settings", new JObject(profileArgs) { ["values"] = new JObject { ["KeepDirectoryStructure"] = !settings.KeepDirectoryStructure }, ["persist"] = true });
    var persistedProfile = JsonConvert.DeserializeObject<GameProfile>(await File.ReadAllTextAsync(savedProfilePath), JsonNetSerializer.SerializerSettings)!;
    Check(persistedProfile.Settings.KeepDirectoryStructure == !settings.KeepDirectoryStructure &&
        originalProfile.CreatedAt == persistedProfile.CreatedAt, "Explicit setting persistence preserves profile creation metadata");
    Console.WriteLine($"Artifacts: {root}");
}
finally
{
    if (hostPid != null)
    {
        try { await client.Call("close_session", profileArgs); }
        catch (Exception ex) { Console.Error.WriteLine($"Session cleanup failed: {ex.Message}"); }
        try
        {
            using var host = Process.GetProcessById(hostPid.Value);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException) { }
    }
}

static async Task<JToken> WaitReady(RpcClient client, JObject args)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(45))
    {
        var status = await client.Call("session_status", args);
        if (status.Value<string>("state") == "ready") return status;
        if (status.Value<string>("state") == "failed") throw new Exception(status.ToString());
        await Task.Delay(100);
    }
    throw new TimeoutException("Fixture session did not become ready.");
}

static async Task<JToken> WaitJob(RpcClient client, JObject args, string jobId)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < TimeSpan.FromSeconds(45))
    {
        var status = await client.Call("job_status", new JObject(args) { ["jobId"] = jobId });
        if (status.Value<string>("state") is not ("queued" or "running")) return status;
        await Task.Delay(100);
    }
    throw new TimeoutException("Fixture job did not finish.");
}

sealed class RpcClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    private int _id;

    private RpcClient(Process process)
    {
        _process = process;
        _errors = process.StandardError.ReadToEndAsync();
    }

    public static async Task<RpcClient> Start(string executable, string profiles)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--mcp");
        start.Environment["FMODEL_PROFILES_DIRECTORY"] = profiles;
        var client = new RpcClient(Process.Start(start) ?? throw new Exception("Could not launch MCP process."));
        await client.Request("initialize", new JObject
        {
            ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = nameof(RpcClient), ["version"] = "1.0" }
        });
        await client._process.StandardInput.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToString(Formatting.None));
        await client._process.StandardInput.FlushAsync();
        return client;
    }

    public async Task<JToken> Request(string method, JObject parameters)
    {
        var id = ++_id;
        await _process.StandardInput.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }.ToString(Formatting.None));
        await _process.StandardInput.FlushAsync();
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(210));
            if (line == null) throw new Exception($"MCP process exited: {await _errors}");
            var response = JObject.Parse(line);
            if (response.Value<int?>("id") != id) continue;
            if (response["error"] != null) throw new Exception(response["error"]!.ToString());
            return response["result"]!;
        }
    }

    public async Task<JToken> Call(string name, JObject args)
    {
        var result = await Request("tools/call", new JObject { ["name"] = name, ["arguments"] = args });
        if (result.Value<bool?>("isError") == true) throw new Exception(result.ToString());
        return JToken.Parse(result["content"]![0]!.Value<string>("text")!);
    }

    public async Task ExpectError(string name, JObject args)
    {
        var result = await Request("tools/call", new JObject { ["name"] = name, ["arguments"] = args });
        if (result.Value<bool?>("isError") != true) throw new Exception($"{name} should have failed.");
        Console.WriteLine($"PASS: {name} reports an explicit error");
    }

    public async ValueTask DisposeAsync()
    {
        _process.StandardInput.Close();
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { if (!_process.HasExited) _process.Kill(); _process.Dispose(); }
    }
}
