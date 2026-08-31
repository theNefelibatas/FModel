using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using FModel.Settings;
using FModel.ViewModels;
using Serilog;

namespace FModel;

/* 命令行导出入口（本仓库自定义功能，不随上游）：
 *   FModel.exe -ExportFolder="<内容根>/<目录>" -ExportType=Models
 *   FModel.exe -ExportFile="<内容根>/<目录>/<资产名>.uasset" -ExportType=Properties
 *   FModel.exe -ExportAssets="<清单文件>" -ExportType=Textures     （清单每行一个资产路径）
 * 含空格的路径必须用引号包住（Windows 命令行解析规则）。
 * -ExportType：raw | properties | textures | models | worlds | animations | audio | code，
 * 对应 GUI 右键菜单的各导出项。文件模式（-ExportFile / 清单每行）的路径必须是含扩展名的
 * 完整包内路径（如 .uasset/.umap），省略扩展名不会自动补全；目录模式按目录匹配，不需要扩展名。
 * 文件模式先按 GUI 菜单置灰规则校验类型合法性，不合法即报错；执行层直接复用 GUI 的
 * Extract/ExportData —— 与右键点击后发生的事情完全一致，输出目录/格式/质量等均取 UserSettings。
 * 导出完成后进程退出（0 = 全部导出成功；1 = 任一资产失败或异常，失败详情见日志）。
 *  --game=<Id>：可选，指定要用的游戏档案（Id 见启动器列表或 Profiles 目录文件名）。
 *  不传时使用档案中 LastUsedAt 最新的一个；无任何档案时失败退出（码 1）。 */
public static class CliExporter
{
    public sealed class CliRequest
    {
        public string ExportFolder;
        public string ExportFile;
        public string ExportAssets;
        public EBulkType ExportType;
        public string GameId;
    }

    /* App 用它预判是否进入 CLI 分支（GUI 模式不解析） */
    public static bool HasCliArguments =>
        Environment.GetCommandLineArgs().Any(a => a.StartsWith("-Export", StringComparison.OrdinalIgnoreCase));

    /* 参数非法时抛 ArgumentException（App 在 CLI 分支捕获并报错退出） */
    public static CliRequest ParseRequest()
    {
        string folder = null, file = null, assets = null, type = null, gameId = null;

        foreach (var arg in Environment.GetCommandLineArgs())
        {
            /* 包内路径统一正斜杠后再与 Provider.Files 的键比较（跨进程输入的路径分隔符不一定一致） */
            if (arg.StartsWith("-ExportFolder=", StringComparison.OrdinalIgnoreCase))
                folder = NormalizePackagePath(arg["-ExportFolder=".Length..].Trim('"'));
            else if (arg.StartsWith("-ExportFile=", StringComparison.OrdinalIgnoreCase))
                file = NormalizePackagePath(arg["-ExportFile=".Length..].Trim('"'));
            else if (arg.StartsWith("-ExportAssets=", StringComparison.OrdinalIgnoreCase))
                assets = arg["-ExportAssets=".Length..].Trim('"'); // 文件系统路径，不做分隔符归一化
            else if (arg.StartsWith("-ExportType=", StringComparison.OrdinalIgnoreCase))
                type = arg["-ExportType=".Length..].Trim('"');
            else if (arg.StartsWith("--game=", StringComparison.OrdinalIgnoreCase))
                gameId = arg["--game=".Length..].Trim('"'); // 档案 Id（纯 token，不归一化路径）
        }

        var modes = (folder != null ? 1 : 0) + (file != null ? 1 : 0) + (assets != null ? 1 : 0);
        if (modes == 0 && type == null) return null; // 无 CLI 参数 → GUI 模式
        if (modes == 0) throw new ArgumentException("CLI: missing -ExportFolder / -ExportFile / -ExportAssets");
        if (modes > 1) throw new ArgumentException("CLI: -ExportFolder / -ExportFile / -ExportAssets are mutually exclusive");
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("CLI: missing -ExportType, valid values: raw | properties | textures | models | worlds | animations | audio | code");

        return new CliRequest
        {
            ExportFolder = folder,
            ExportFile = file,
            ExportAssets = assets,
            ExportType = ParseType(type),
            GameId = gameId
        };
    }

    /* 包内路径只以正斜杠存储；命令行输入可能带反斜杠，比较前先归一化 */
    private static string NormalizePackagePath(string path) => path.Replace('\\', '/');

    private static EBulkType ParseType(string type) => type.ToLowerInvariant() switch
    {
        "raw" => EBulkType.Raw,
        "properties" => EBulkType.Properties,
        "textures" => EBulkType.Textures,
        "models" or "meshes" => EBulkType.Meshes,
        "worlds" => EBulkType.Worlds,
        "animations" => EBulkType.Animations,
        "audio" => EBulkType.Audio,
        "code" => EBulkType.Code,
        _ => throw new ArgumentException($"CLI: unknown export type '{type}', valid values: raw | properties | textures | models | worlds | animations | audio | code")
    };

    public static async Task RunAsync(CUE4ParseViewModel cue4Parse, CliRequest request, CancellationToken ct)
    {
        var failedCount = 0;

        if (request.ExportFolder != null)
        {
            /* 与 GUI 文件夹右键一致：目录内全部文件（含非包文件），类型不预校验（文件夹菜单全部可用，
             * 无匹配类型的文件自然无输出，与 GUI "Failed to find any X in directory" 的 warning 行为相同） */
            var prefix = request.ExportFolder + "/";
            var entries = cue4Parse.Provider.Files.Values
                .Where(f => !f.IsUePackagePayload && (f.Directory == request.ExportFolder || f.Directory.StartsWith(prefix)))
                .ToList();

            if (entries.Count == 0)
                throw new InvalidOperationException($"CLI: folder not found or empty: {request.ExportFolder}");

            Log.Information("CLI: exporting {Count} files from {Folder} ({Type})", request.ExportFolder, entries.Count, request.ExportType);
            foreach (var entry in entries)
            {
                try
                {
                    await ExportFileAsync(cue4Parse, entry, request.ExportType, ct, validate: false);
                }
                catch (Exception ex)
                {
                    // 单个资产失败不应中断整个目录：与 GUI BulkFolder 的逐资产容错一致
                    failedCount++;
                    Log.Error(ex, "CLI: failed to export {File}", entry.Path);
                }
            }
        }
        else
        {
            /* 文件模式：单文件或清单，逐文件按 GUI 菜单置灰规则校验类型合法性 */
            List<GameFile> files;
            if (request.ExportFile != null)
            {
                files = [ResolveFile(cue4Parse, request.ExportFile)];
            }
            else
            {
                files = [];
                foreach (var line in File.ReadAllLines(request.ExportAssets))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        files.Add(ResolveFile(cue4Parse, NormalizePackagePath(line)));
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        Log.Error(ex, "CLI: invalid manifest line {Line}", line);
                    }
                }
            }

            foreach (var file in files)
            {
                try
                {
                    await ExportFileAsync(cue4Parse, file, request.ExportType, ct, validate: true);
                }
                catch (Exception ex)
                {
                    failedCount++;
                    Log.Error(ex, "CLI: failed to export {File}", file.Path);
                }
            }
        }

        failedCount += await RunExportSessionAsync(ct);
        if (failedCount > 0)
            throw new InvalidOperationException($"CLI: {failedCount} assets failed to export, see log for details");
    }

    /* 完整包内文件路径（含扩展名）才能精确匹配 Provider.Files 的键；省略扩展名由调用方负责 */
    private static GameFile ResolveFile(CUE4ParseViewModel cue4Parse, string path)
    {
        if (cue4Parse.Provider.Files.TryGetValue(path, out var file)) return file;

        // 容错：路径大小写与包内不一致时按忽略大小写再找一次
        var loose = cue4Parse.Provider.Files.Values
            .FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        return loose ?? throw new InvalidOperationException($"CLI: file not found: {path}");
    }

    /* 单个文件导出：与 GUI 右键点击完全一致的执行路径 —— Raw 走 ExportData（"Raw Data" 菜单），
     * 其余走 Extract（各导出菜单）。bulk 带上 Auto 等价 GUI 批量导出：导出结果一致，只是不更新界面。 */
    private static async Task ExportFileAsync(CUE4ParseViewModel cue4Parse, GameFile file, EBulkType type, CancellationToken ct, bool validate)
    {
        if (validate && type is not (EBulkType.Raw or EBulkType.Properties))
            ValidateAction(cue4Parse, file, type);

        if (type == EBulkType.Raw)
        {
            cue4Parse.ExportData(file);
            return;
        }

        // Extract 内部通过 Application.Current.Dispatcher.Invoke 回 UI 线程，需在后台线程执行（与 GUI ThreadWorker 相同）
        await Task.Run(() => cue4Parse.Extract(ct, file, false, type | EBulkType.Auto), ct);
    }

    /* 合法性 = GUI 文件右键菜单的置灰规则（FileContextMenu 的 ItemActionCondition），
     * 直接复用 GameFileViewModel.ResolveAsync 计算的 AssetActions，与界面展示同一份判断。 */
    private static void ValidateAction(CUE4ParseViewModel cue4Parse, GameFile file, EBulkType type)
    {
        var vm = new GameFileViewModel(file);
        vm.ResolveAsync(EResolveCompute.Category).GetAwaiter().GetResult();
        if (vm.AssetActions.HasFlag(type)) return;

        var supported = vm.AssetActions == EBulkType.None
            ? "none (only Raw / Properties are supported)"
            : vm.AssetActions.ToString();
        throw new InvalidOperationException(
            $"CLI: export type {type} is not valid for this file (the GUI right-click menu item is disabled); supported: {supported}");
    }

    /* 与 ExportSessionViewModel.ExportAsync 相同的会话执行（无 UI 进度），返回失败资产数：
     * 导出器内部的失败（转换/解码等）不抛异常，而是以 Success=false 的 ExportResult 落账，
     * 失败详情只在结果对象里——逐条记日志，"失败详情见日志"才成立。 */
    private static async Task<int> RunExportSessionAsync(CancellationToken ct)
    {
        var exportDirectory = UserSettings.Default.ModelDirectory;
        var exportOptions = UserSettings.GetExportOptions();

        var results = await ExportSessionViewModel.Instance.Session.RunAsync(exportDirectory, exportOptions, null, ct);
        foreach (var result in results.Where(r => !r.Success))
        {
            Log.Error(result.Error, "CLI: export failed: {ObjectPath}", result.ObjectPath);
        }
        return results.Count(r => !r.Success);
    }
}
