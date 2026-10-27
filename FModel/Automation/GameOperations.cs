using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion.Textures;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace FModel.Automation;

internal sealed partial class GameOperations(bool hasGui) : IDisposable
{
    public volatile bool HasGui = hasGui;
    internal bool IsClosing => _closing;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly OperationJobs _jobs = new();
    private volatile bool _closing;
    private ApplicationViewModel App => ApplicationService.ApplicationView;
    private CUE4ParseViewModel Model => App.CUE4Parse;
    public volatile bool Ready;
    public volatile string InitializationError;

    public async Task<object> ExecuteAsync(string operation, JObject args, CancellationToken ct)
    {
        OperationCatalog.Validate(operation, args);
        if (_closing) throw new InvalidOperationException("The session is closing. Wait for it to exit before reconnecting.");
        if (operation == "session_status") return new
        {
            profileId = UserSettings.Default.CurrentProfileId, processId = Environment.ProcessId, hasGui = HasGui,
            state = InitializationError != null ? "failed" : Ready ? "ready" : "loading",
            error = InitializationError, files = Ready ? Model.Provider.Files.Count : 0,
            mountedArchives = Ready ? Model.Provider.MountedVfs.Count : 0,
            unmountedArchives = Ready ? Model.Provider.UnloadedVfs.Count : 0,
            missingKeys = Ready ? Model.Provider.RequiredKeys.Count : 0
        };
        if (operation == "job_status") return _jobs.Status(args);
        if (operation == "read_session_log") return await ReadSessionLogAsync(args, ct);
        if (operation == "cancel_job") return _jobs.Cancel(args);
        if (operation == "show_gui")
        {
            while (!Ready)
            {
                if (InitializationError != null) throw new InvalidOperationException(InitializationError);
                if (_closing) throw new InvalidOperationException("The session is closing.");
                await Task.Delay(100, ct);
            }
            await _gate.WaitAsync(ct);
            try
            {
                if (_closing) throw new InvalidOperationException("The session is closing.");
                await Application.Current.Dispatcher.InvokeAsync(() => ((FModel.App) Application.Current).ShowSessionWindow(),
                    System.Windows.Threading.DispatcherPriority.Normal, ct);
                return new { processId = Environment.ProcessId, hasGui = HasGui };
            }
            finally { _gate.Release(); }
        }
        if (operation == "close_session")
        {
            await _gate.WaitAsync(ct);
            try
            {
                lock (_lifecycle)
                {
                    if (HasGui) throw new InvalidOperationException("A GUI session must be closed by its owner.");
                    if (_jobs.IsBusy) throw new InvalidOperationException("Cancel or finish active jobs before closing the session.");
                    _closing = true;
                    return new { closing = true };
                }
            }
            finally { _gate.Release(); }
        }
        if (!Ready) throw new InvalidOperationException(InitializationError ?? "Session is still loading. Poll session_status.");
        if (operation == "export_assets")
            return StartJob(operation, job => ExclusiveAsync(async () =>
            {
                await ExportAsync(args, job);
                return (object) null;
            }, job.Cancellation.Token));
        if (operation == "load_localization")
            return StartJob(operation, job => ExclusiveAsync(async () =>
            {
                job.Cancellation.Token.ThrowIfCancellationRequested();
                await Model.LoadVirtualPaths();
                await Model.LoadLocalizedResources();
                if (Model.Provider.Internationalization.Culture == null)
                    throw new InvalidOperationException("The profile language could not be loaded from the available localization resources.");
                job.Results.Enqueue(new { localizedResources = Model.LocalizedResourcesCount });
                return (object) null;
            }, job.Cancellation.Token));

        return await ExclusiveAsync(() => ExecuteCoreAsync(operation, args, ct), ct);
    }

    private async Task<object> ExclusiveAsync(Func<Task<object>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var acquired = false;
        try
        {
            if (_closing) throw new InvalidOperationException("The session is closing.");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (!App.Status.IsReady || ExportSessionViewModel.Instance.IsRunning || Model.IsSnooperOpen)
                    throw new InvalidOperationException("FModel is busy. Finish the current GUI operation or close the 3D viewer, then retry.");
                App.Status.SetStatus(EStatusKind.Loading, "Automation");
                acquired = true;
            });
            return await action();
        }
        finally
        {
            if (acquired) await Application.Current.Dispatcher.InvokeAsync(() => App.Status.SetStatus(EStatusKind.Ready));
            _gate.Release();
        }
    }

    private object StartJob(string operation, Func<OperationJobs.Job, Task> action)
    {
        lock (_lifecycle)
        {
            if (_closing) throw new InvalidOperationException("The session is closing.");
            return _jobs.Start(operation, action);
        }
    }

    private static async Task<object> ReadSessionLogAsync(JObject args, CancellationToken ct)
    {
        var length = QueryHelpers.Integer(args, "length", 16000, 1, 65536);
        var path = FModel.App.SessionLogPath ?? throw new InvalidOperationException("The session log is not initialized.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var tail = new StringBuilder();
        var buffer = new char[4096];
        long total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total += read;
            tail.Append(buffer, 0, read);
            if (tail.Length > length) tail.Remove(0, tail.Length - length);
        }
        return new { path = path.Replace('\\', '/'), totalCharacters = total, content = tail.ToString(), truncated = total > length };
    }

    private async Task<object> ExecuteCoreAsync(string operation, JObject args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (operation.StartsWith("viewer_", StringComparison.Ordinal) || operation.StartsWith("audio_", StringComparison.Ordinal))
            return await MediaAsync(operation, args, ct);
        switch (operation)
        {
            case "select_archives": return SelectArchives(args);
            case "file_versions":
                if (!Model.Provider.Files.TryGetValues(QueryHelpers.NormalizePath(args.Value<string>("path")), out var versions))
                    throw new FileNotFoundException("Virtual file not found.");
                return QueryHelpers.Page(versions.Select((file, i) => new { priority = i, effective = i == 0, file = Describe(file) }), args);
            case "aes_status": return AesStatus();
            case "submit_aes_key": return await SubmitAesAsync(args);
            case "create_index_backup": return IndexBackups.Create(Model.Provider.Files.Values, args.Value<string>("backupPath"), ct);
            case "compare_index_backup": return IndexBackups.Compare(Model.Provider.Files.Values, args, ct);
            case "list_archives":
                return QueryHelpers.Page(Model.Provider.MountedVfs.Concat(Model.Provider.UnloadedVfs).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new { name = x.Name, mounted = Model.Provider.MountedVfs.Contains(x), files = x.Files.Count,
                        encrypted = x.IsEncrypted, guid = x.EncryptionKeyGuid.ToString(), selected = _archiveSelection == null || _archiveSelection.Contains(x.Name) }), args);
            case "list_directory": return Directory(args);
            case "search_files": return Search(args, ct);
            case "index_statistics":
                return QueryHelpers.Page(SelectedFiles().Where(x => QueryHelpers.InDirectory(x.Path, args.Value<string>("directory")))
                    .GroupBy(x => x.Extension, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new { extension = group.Key, files = group.Count(), bytes = group.Sum(x => x.Size) })
                    .OrderByDescending(x => x.bytes).ThenBy(x => x.extension, StringComparer.OrdinalIgnoreCase), args);
            case "read_object":
                return QueryHelpers.Json(ObjectReferences.Load(args.Value<string>("objectPath"), path => Model.Provider.LoadPackage(path),
                    Model.Provider.StringComparison, ct), args);
            case "list_imports":
            {
                var package = Model.Provider.LoadPackage(ResolvePackage(args));
                var offset = QueryHelpers.Integer(args, "offset", 0, 0, int.MaxValue);
                var limit = QueryHelpers.Integer(args, "limit", 100, 1, 500);
                var imports = new List<object>();
                for (var i = offset; i < package.ImportMapLength && imports.Count < limit; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var reference = package.ResolvePackageIndex(new FPackageIndex(package, -i - 1));
                        imports.Add(new { index = i, resolved = reference != null, name = reference?.Name.Text,
                            path = reference?.GetPathName(), type = reference?.Class?.Name.Text });
                    }
                    catch (Exception ex) { imports.Add(new { index = i, resolved = false, error = ex.Message }); }
                }
                return new { coverage = "Package import table only; unresolved entries are retained and soft references are not scanned.",
                    total = package.ImportMapLength, offset, items = imports,
                    nextOffset = offset + imports.Count < package.ImportMapLength ? (int?) (offset + imports.Count) : null };
            }
            case "search_localization":
            {
                var resources = Model.Provider.Internationalization;
                if (resources.Culture == null) throw new InvalidOperationException("Localization is not loaded. Call load_localization and wait for its job to finish.");
                var ns = args.Value<string>("namespace");
                var query = args.Value<string>("query") ?? "";
                var entries = resources.Where(x => ns == null || x.Key.Equals(ns, StringComparison.Ordinal))
                    .SelectMany(x => x.Value.Select(value => new { @namespace = x.Key, key = value.Key, text = value.Value }))
                    .Where(x => x.key.Contains(query, StringComparison.OrdinalIgnoreCase) || x.text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.@namespace, StringComparer.Ordinal).ThenBy(x => x.key, StringComparer.Ordinal);
                return new { culture = resources.Culture, results = QueryHelpers.Page(entries, args) };
            }
            case "file_info":
            {
                var file = Resolve(args);
                object resolved = null;
                if (args.Value<bool?>("resolveType") == true)
                {
                    var vm = new GameFileViewModel(file);
                    await vm.ResolveAsync(EResolveCompute.Category);
                    resolved = new { type = vm.ResolvedAssetType, category = vm.AssetCategory.ToString(), actions = vm.AssetActions.ToString(),
                        diagnostic = "Uses the GUI classifier; unknown types may only support raw/properties export." };
                }
                return new { file = Describe(file), resolved };
            }
            case "package_metadata": return QueryHelpers.Json(Model.Provider.LoadPackage(ResolvePackage(args)), args);
            case "list_exports":
            {
                var package = Model.Provider.LoadPackage(ResolvePackage(args));
                var offset = QueryHelpers.Integer(args, "offset", 0, 0, int.MaxValue);
                var limit = QueryHelpers.Integer(args, "limit", 100, 1, 500);
                var objects = new List<object>();
                for (var i = offset; i < package.ExportMapLength && objects.Count < limit; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var export = package.GetExport(i);
                        objects.Add(new { index = i, name = export?.Name, type = export?.ExportType, path = export?.GetPathName() });
                    }
                    catch (Exception ex) { objects.Add(new { index = i, error = ex.Message }); }
                }
                return new { total = package.ExportMapLength, offset, items = objects,
                    nextOffset = offset + objects.Count < package.ExportMapLength ? (int?) (offset + objects.Count) : null };
            }
            case "read_export":
            {
                var package = Model.Provider.LoadPackage(ResolvePackage(args));
                var index = QueryHelpers.Integer(args, "index", -1, 0, package.ExportMapLength - 1);
                return QueryHelpers.Json(package.GetExport(index), args);
            }
            case "read_document":
            {
                var file = Resolve(args);
                if (file.IsUePackage) throw new ArgumentException("Use list_exports and read_export for UE packages.");
                using var scope = new AssetOperationScope(file, true);
                Model.Extract(ct, file, false, EBulkType.Properties | EBulkType.Auto);
                return QueryHelpers.Text(scope.Document, args);
            }
            case "find_referencers":
            {
                var file = ResolvePackage(args);
                if (file is not FIoStoreEntry) throw new NotSupportedException("Reference scanning only supports IoStore package import tables; this format is unsupported.");
                return new { coverage = "Mounted IoStore container import tables only; excludes soft references and other formats.",
                    results = QueryHelpers.Page(Model.Provider.ScanForPackageRefs(file).OrderBy(x => x.Path).Select(Describe), args) };
            }
            case "decompile":
            {
                var file = ResolvePackage(args);
                using var scope = new AssetOperationScope(file);
                if (!Model.Decompile(file, false)) throw new NotSupportedException("No supported blueprint class was found in this package.");
                var text = Application.Current.Dispatcher.Invoke(() => scope.Tab.Document?.Text);
                return QueryHelpers.Text(text, args);
            }
            case "preview_texture": return Preview(args);
            case "get_export_settings": return ExportSettings();
            case "set_export_settings": return await SetExportSettingsAsync(args);
            case "open_in_gui":
            {
                if (!HasGui) throw new InvalidOperationException("This session has no visible GUI. Use show_gui first.");
                var file = Resolve(args);
                switch (args.Value<string>("view") ?? "preview")
                {
                    case "metadata": Model.ShowMetadata(file); break;
                    case "references": Model.FindReferences(file); break;
                    case "code": if (!Model.Decompile(file)) throw new NotSupportedException("No supported blueprint class found."); break;
                    default: Model.Extract(ct, file, true); break;
                }
                return new { opened = file.Path };
            }
            default: throw new ArgumentException($"Operation is unavailable in a game session: {operation}");
        }
    }

    private GameFile Resolve(JObject args)
    {
        if (args["sourceArchive"] == null) return Resolve(args.Value<string>("path"));
        var archive = args.Value<string>("sourceArchive");
        if (Model.Provider.Files.TryGetValues(QueryHelpers.NormalizePath(args.Value<string>("path")), out var versions))
        {
            var file = versions.FirstOrDefault(x => x is VfsEntry entry && entry.Vfs.Name.Equals(archive, StringComparison.OrdinalIgnoreCase));
            if (file != null) return file;
        }
        throw new FileNotFoundException("The requested file version is not present in sourceArchive.");
    }
    private GameFile Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A complete virtual file path is required.");
        path = QueryHelpers.NormalizePath(path);
        if (Model.Provider.Files.TryGetValue(path, out var file)) return file;
        throw new FileNotFoundException($"Virtual file not found: {path}");
    }

    private GameFile ResolvePackage(JObject args)
    {
        var file = Resolve(args);
        if (!file.IsUePackage) throw new ArgumentException("This operation requires a UE package.");
        return file;
    }

    private static object Describe(GameFile file) => new
    {
        path = file.Path, directory = file.Directory, name = file.Name, extension = file.Extension,
        size = file.Size, encrypted = file.IsEncrypted, compression = file.CompressionMethod.ToString(),
        isPackage = file.IsUePackage, isPayload = file.IsUePackagePayload,
        archive = (file as VfsEntry)?.Vfs.Name, offset = (file as VfsEntry)?.Offset
    };

    private object Search(JObject args, CancellationToken ct)
    {
        IEnumerable<GameFile> files = SelectedFiles();
        var directory = args.Value<string>("directory");
        var query = args.Value<string>("query") ?? "";
        var extension = args.Value<string>("extension")?.TrimStart('.');
        var archive = args.Value<string>("archive");
        Regex regex = args.Value<bool?>("regex") == true ? new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)) : null;
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        files = files.Where(file =>
        {
            ct.ThrowIfCancellationRequested();
            return (args.Value<bool?>("includePayloads") == true || !file.IsUePackagePayload) &&
                QueryHelpers.InDirectory(file.Path, directory) &&
                (string.IsNullOrEmpty(extension) || file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(archive) || file is VfsEntry entry && entry.Vfs.Name.Equals(archive, StringComparison.OrdinalIgnoreCase)) &&
                (regex?.IsMatch(file.Path) ?? terms.All(term => file.Path.Contains(term, StringComparison.OrdinalIgnoreCase)));
        });
        files = (args.Value<string>("sort") ?? "path") switch
        {
            "size_ascending" => files.OrderBy(x => x.Size).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase),
            "size_descending" => files.OrderByDescending(x => x.Size).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
        };
        return QueryHelpers.Page(files.Select(Describe), args);
    }

    private object Directory(JObject args)
    {
        var directory = QueryHelpers.NormalizePath(args.Value<string>("directory"));
        var prefixLength = directory.Length == 0 ? 0 : directory.Length + 1;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<GameFile>();
        foreach (var file in SelectedFiles())
        {
            if (file.IsUePackagePayload || !QueryHelpers.InDirectory(file.Path, directory)) continue;
            var remaining = file.Path[prefixLength..];
            var slash = remaining.IndexOf('/');
            if (slash >= 0) directories.Add(file.Path[..(prefixLength + slash)]);
            else files.Add(file);
        }
        if (directory.Length > 0 && directories.Count == 0 && files.Count == 0)
            throw new DirectoryNotFoundException($"Virtual directory not found: {directory}");
        var entries = directories.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => (object) new { kind = "directory", path = x })
            .Concat(files.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => (object) new { kind = "file", file = Describe(x) }));
        return QueryHelpers.Page(entries, args);
    }

    private object Preview(JObject args)
    {
        var package = Model.Provider.LoadPackage(ResolvePackage(args));
        var index = QueryHelpers.Integer(args, "index", -1, 0, package.ExportMapLength - 1);
        if (package.GetExport(index) is not UTexture texture) throw new NotSupportedException("Selected export is not a texture.");
        var size = QueryHelpers.Integer(args, "maxSize", 512, 32, 2048);
        var mipIndex = args["mipIndex"] != null ? QueryHelpers.Integer(args, "mipIndex", -1, 0, int.MaxValue) : texture.GetMipIndexByMaxSize(size);
        var decoded = texture.DecodeMip(mipIndex, UserSettings.Default.CurrentDir.TexturePlatform)
            ?? throw new NotSupportedException("Texture decoding returned no image for the requested mip.");
        using var image = decoded.ToSkBitmap();
        var channel = args.Value<string>("channel") ?? "rgba";
        using var preview = TexturePreview.Create(image, size, channel);
        using var data = preview.Encode(SKEncodedImageFormat.Png, 100);
        return new { mimeType = "image/png", imageData = Convert.ToBase64String(data.ToArray()), width = preview.Width, height = preview.Height, mipIndex, channel,
            mips = texture.PlatformData.Mips?.Select((m, i) => new { index = i, width = m.SizeX, height = m.SizeY }).ToArray() };
    }

    private async Task ExportAsync(JObject args, OperationJobs.Job job)
    {
        var type = args.Value<string>("type") switch
        {
            "raw" => EBulkType.Raw, "properties" => EBulkType.Properties, "textures" => EBulkType.Textures,
            "models" => EBulkType.Meshes, "worlds" => EBulkType.Worlds, "animations" => EBulkType.Animations,
            "audio" => EBulkType.Audio, "code" => EBulkType.Code, "embedded" => EBulkType.EmbeddedFiles,
            _ => throw new ArgumentException("Unknown export type.")
        };
        var paths = args["paths"].Values<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        job.Total = paths.Length;
        var failed = 0;
        foreach (var path in paths)
        {
            var ct = job.Cancellation.Token;
            ct.ThrowIfCancellationRequested();
            try
            {
                var file = Resolve(path);
                using var scope = new AssetOperationScope(file);
                if (type == EBulkType.Raw) Model.ExportData(file);
                else Model.Extract(ct, file, false, type | EBulkType.Auto);
                var outputDirectory = type == EBulkType.Raw ? UserSettings.Default.RawDataDirectory : UserSettings.Default.ModelDirectory;
                var results = await scope.Session.RunAsync(outputDirectory, UserSettings.GetExportOptions(), null, ct);
                foreach (var result in results)
                {
                    if (result.DiskFilePaths != null) scope.Files.AddRange(result.DiskFilePaths);
                    if (!result.Success) scope.Errors.Add(result.Error?.Message ?? $"Export failed: {result.ObjectPath}");
                }
                if (scope.Files.Count == 0 && scope.Errors.Count == 0) scope.Errors.Add("No output was produced for this file and export type.");
                if (scope.Errors.Count > 0) failed++;
                job.Results.Enqueue(new { path, success = scope.Errors.Count == 0, files = scope.Files.Distinct().ToArray(), errors = scope.Errors.ToArray() });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { failed++; job.Results.Enqueue(new { path, success = false, error = ex.Message }); }
            Interlocked.Increment(ref job.Completed);
        }
        if (failed > 0) throw new InvalidOperationException($"{failed} asset exports failed. Inspect the per-file results.");
    }

    private static readonly string[] ExportSettingNames =
    [
        nameof(UserSettings.KeepDirectoryStructure), nameof(UserSettings.MeshExportFormat), nameof(UserSettings.MeshQuality),
        nameof(UserSettings.NaniteMeshExportFormat), nameof(UserSettings.SocketExportFormat), nameof(UserSettings.TextureExportFormat),
        nameof(UserSettings.TextureQuality), nameof(UserSettings.SaveHdrTexturesAsHdr), nameof(UserSettings.ExportAllTextureMips),
        nameof(UserSettings.MaterialExportFormat), nameof(UserSettings.SaveEmbeddedMaterials), nameof(UserSettings.MaterialJsonFormat),
        nameof(UserSettings.SaveMorphTargets), nameof(UserSettings.CompressionFormat), nameof(UserSettings.ConvertAudioOnBulkExport)
    ];

    private static object ExportSettings() => new
    {
        directories = new { raw = UserSettings.Default.RawDataDirectory, properties = UserSettings.Default.PropertiesDirectory,
            textures = UserSettings.Default.TextureDirectory, models = UserSettings.Default.ModelDirectory,
            audio = UserSettings.Default.AudioDirectory, code = UserSettings.Default.CodeDirectory },
        settings = ExportSettingNames.Select(name =>
        {
            var property = typeof(UserSettings).GetProperty(name);
            return new { name, value = property.GetValue(UserSettings.Default), type = property.PropertyType.Name,
                choices = property.PropertyType.IsEnum ? Enum.GetNames(property.PropertyType) : null };
        }).ToArray()
    };

    private async Task<object> SetExportSettingsAsync(JObject args)
    {
        var persist = args.Value<bool?>("persist") == true;
        if (HasGui && !AutomationEntryPoint.IsHost && !persist) throw new InvalidOperationException("GUI settings are saved on exit. Pass persist=true to explicitly accept saving these changes.");
        var changes = new List<(PropertyInfo Property, object Value)>();
        foreach (var item in ((JObject) args["values"]).Properties())
        {
            if (!ExportSettingNames.Contains(item.Name)) throw new ArgumentException($"Setting is not editable through this operation: {item.Name}");
            var property = typeof(UserSettings).GetProperty(item.Name);
            if (item.Value.Type == JTokenType.Null) throw new ArgumentException($"Setting cannot be null: {item.Name}");
            if (property.PropertyType == typeof(bool) && item.Value.Type != JTokenType.Boolean)
                throw new ArgumentException($"Setting requires a boolean: {item.Name}");
            var value = item.Value.ToObject(property.PropertyType);
            if (property.PropertyType.IsEnum && !Enum.IsDefined(property.PropertyType, value)) throw new ArgumentException($"Invalid enum value: {item.Name}");
            if (item.Name == nameof(UserSettings.TextureQuality) && Convert.ToInt32(value) is < 1 or > 100) throw new ArgumentException("TextureQuality must be between 1 and 100.");
            changes.Add((property, value));
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            foreach (var change in changes) change.Property.SetValue(UserSettings.Default, change.Value);
            if (persist)
            {
                if (!ProfileStore.TryLoad(UserSettings.Default.CurrentProfileId, out var profile, out var error))
                    throw new InvalidOperationException($"Cannot persist export settings: {error}");
                profile.Settings = GameProfile.DeepClone(UserSettings.Default);
                profile.Game = GameProfile.DeepClone(UserSettings.Default.CurrentDir);
                ProfileStore.Save(profile);
            }
        });
        return ExportSettings();
    }

    public void Dispose()
    {
        _jobs.Dispose();
        Application.Current.Dispatcher.Invoke(() => { _viewport?.Dispose(); _viewport = null; });
    }
}
