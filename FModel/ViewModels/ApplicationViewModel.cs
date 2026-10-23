using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse_Conversion.Textures.BC;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Lua.unluac;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels.Commands;
using FModel.Views;
using FModel.Views.Resources.Controls;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using System.Runtime.Intrinsics.X86;

namespace FModel.ViewModels;

public class ApplicationViewModel : ViewModel
{
    private EBuildKind _build;
    public EBuildKind Build
    {
        get => _build;
        private init
        {
            SetProperty(ref _build, value);
            RaisePropertyChanged(nameof(TitleExtra));
        }
    }

    private FStatus _status;
    public FStatus Status
    {
        get => _status;
        private init => SetProperty(ref _status, value);
    }

    public IEnumerable<EAssetCategory> Categories { get; } = AssetCategoryExtensions.GetBaseCategories();

    private bool _isAssetsExplorerVisible;
    public bool IsAssetsExplorerVisible
    {
        get => _isAssetsExplorerVisible;
        set
        {
            if (value && !UserSettings.Default.FeaturePreviewNewAssetExplorer)
                return;

            SetProperty(ref _isAssetsExplorerVisible, value);
        }
    }

    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        internal set => SetProperty(ref _isUpdateAvailable, value);
    }

    private int _selectedLeftTabIndex;
    public int SelectedLeftTabIndex
    {
        get => _selectedLeftTabIndex;
        set
        {
            if (value is < 0 or > 2) return;
            SetProperty(ref _selectedLeftTabIndex, value);
        }
    }

    public RightClickMenuCommand RightClickMenuCommand => _rightClickMenuCommand ??= new RightClickMenuCommand(this);
    private RightClickMenuCommand _rightClickMenuCommand;
    public MenuCommand MenuCommand => _menuCommand ??= new MenuCommand(this);
    private MenuCommand _menuCommand;
    public CopyCommand CopyCommand => _copyCommand ??= new CopyCommand(this);
    private CopyCommand _copyCommand;

    public string InitialWindowTitle => $"FModel ({Constants.APP_SHORT_COMMIT_ID} - {Constants.APP_BUILD_DATE:MMM d, yyyy})";
    public string GameDisplayName => CUE4Parse.Provider.GameDisplayName ?? "Unknown";
    public string TitleExtra => $"({UserSettings.Default.CurrentDir.UeVersion}){(Build != EBuildKind.Release ? $" ({Build})" : "")}";

    public LoadingModesViewModel LoadingModes { get; }
    public CustomDirectoriesViewModel CustomDirectories { get; }
    public CUE4ParseViewModel CUE4Parse { get; }
    public SettingsViewModel SettingsView { get; }
    public AesManagerViewModel AesManager { get; }
    public AudioPlayerViewModel AudioPlayer { get; }

    public ApplicationViewModel()
    {
        Status = new FStatus();
#if DEBUG
        Build = EBuildKind.Debug;
#elif RELEASE
        Build = EBuildKind.Release;
#else
        Build = EBuildKind.Unknown;
#endif
        LoadingModes = new LoadingModesViewModel();

        /* 档案已在 App 层装载（--game）；此处只做安全兜底：没有 CurrentDir 直接硬退出（与旧一致） */
        if (UserSettings.Default.CurrentDir is null)
            Environment.Exit(0);

        CUE4Parse = new CUE4ParseViewModel();
        CUE4Parse.Provider.VfsRegistered += (sender, count) =>
        {
            if (sender is not IAesVfsReader reader) return;
            Status.UpdateStatusLabel($"{count} Archives ({reader.Name})", "Registered");
            CUE4Parse.GameDirectory.Add(reader);
        };
        CUE4Parse.Provider.VfsMounted += (sender, count) =>
        {
            if (sender is not IAesVfsReader reader) return;
            Status.UpdateStatusLabel($"{count:N0} Packages ({reader.Name})", "Mounted");
            CUE4Parse.GameDirectory.Verify(reader);
        };
        CUE4Parse.Provider.VfsUnmounted += (sender, _) =>
        {
            if (sender is not IAesVfsReader reader) return;
            CUE4Parse.GameDirectory.Disable(reader);
        };
        CustomDirectories = new CustomDirectoriesViewModel();
        SettingsView = new SettingsViewModel();
        AesManager = new AesManagerViewModel(CUE4Parse);
        AudioPlayer = new AudioPlayerViewModel();

        Status.SetStatus(EStatusKind.Ready);
    }

    /* 拖入目录：建/复用档案并启动新游戏进程；当前窗口不动。 */
    public DirectorySettings AddGameDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        var vm = new GameSelectorViewModel(directory);
        vm.AddUndetectedDir(directory);
        var selected = vm.SelectedDirectory;
        if (selected is null) return null;

        var profile = GameProfile.FromSettings(
            ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(selected.GameDirectory)),
            selected, new UserSettings());
        profile = ProfileStore.Create(profile);
        LaunchProfileProcess(profile.Id);
        return selected;
    }

    public void Restart()
    {
        /* 先释放档案锁再拉起新进程：新进程要取得同一档案的锁，不能撞"已在运行"（双释放安全）。 */
        App.DisposeProfileLockForRelaunch();
        /* 档案模式下重启仍回到同一游戏（不落进启动器）：
         * 设置变更需重启与致命异常重启都带 --game=<当前档案>；无档案（启动器模式）则原样无参。 */
        var gameId = UserSettings.Default.CurrentProfileId;
        var path = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        if (path.EndsWith(".dll"))
        {
            new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = string.IsNullOrEmpty(gameId) ? $"\"{path}\"" : $"\"{path}\" --game={gameId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                }
            }.Start();
        }
        else if (path.EndsWith(".exe"))
        {
            new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = string.IsNullOrEmpty(gameId) ? string.Empty : $"--game={gameId}",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                }
            }.Start();
        }

        Application.Current.Shutdown();
    }

    /* 返回启动器时，自动化宿主只隐藏窗口，保留其他客户端正在使用的会话。 */
    public void BackToLauncher()
    {
        var keepSession = Automation.AutomationEntryPoint.IsHost;
        if (!keepSession)
        {
            App.DisposeProfileLockForRelaunch();
            UserSettings.Save();
        }
        var launcherExe = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        var startInfo = launcherExe.EndsWith(".dll")
            ? new ProcessStartInfo { FileName = "dotnet", Arguments = $"\"{launcherExe}\" --launcher", UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo { FileName = launcherExe, Arguments = "--launcher", UseShellExecute = false, CreateNoWindow = true };
        Process.Start(startInfo);
        if (keepSession) ((App) Application.Current).HideSessionWindow();
        else Application.Current.Shutdown();
    }

    /* 启动一个游戏进程（--game=<Id>）；exe/dll 判定与 Restart 相同 */
    public static void LaunchProfileProcess(string profileId)
    {
        var path = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        if (path.EndsWith(".dll"))
            startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{path}\" --game={profileId}",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };
        else if (path.EndsWith(".exe"))
            startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                Arguments = $"--game={profileId}",
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

        System.Diagnostics.Process.Start(startInfo);
    }

    public async Task UpdateProvider(bool isLaunch)
    {
        if (!isLaunch && !AesManager.HasChange) return;

        CUE4Parse.ClearProvider();
        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            // TODO: refactor after release, select updated keys only
            var aes = AesManager.AesKeys.Select(x =>
            {
                cancellationToken.ThrowIfCancellationRequested(); // cancel if needed

                var k = x.Key.Trim();
                if (k.Length != 66) k = Constants.ZERO_64_CHAR;
                return new KeyValuePair<FGuid, FAesKey>(x.Guid, new FAesKey(k));
            });

            CUE4Parse.LoadVfs(aes);
            AesManager.SetAesKeys();
        });
        RaisePropertyChanged(nameof(GameDisplayName));
    }

    public static async Task InitVgmStream()
    {
        var vgmZipFilePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", "vgmstream-win.zip");
        var vgmFileInfo = new FileInfo(vgmZipFilePath);

        if (!vgmFileInfo.Exists || vgmFileInfo.LastWriteTimeUtc < DateTime.UtcNow.AddMonths(-4))
        {
            await ApplicationService.ApiEndpointView.DownloadFileAsync("https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win.zip", vgmZipFilePath);
            vgmFileInfo.Refresh();

            if (vgmFileInfo.Length > 0)
            {
                var zipDir = Path.GetDirectoryName(vgmZipFilePath)!;
                await using var zipFs = File.OpenRead(vgmZipFilePath);
                await using var zip = await ZipArchive.CreateAsync(zipFs, ZipArchiveMode.Read, true, null);
                await zip.ExtractToDirectoryAsync(zipDir, true);
            }
            else
            {
                FLogger.Append(ELog.Error, () => FLogger.Text("Could not download vgmstream", Constants.WHITE, true));
            }
        }
    }

    public static async Task InitImGuiSettings(bool forceDownload)
    {
        const string imgui = "imgui.ini";
        var imguiPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", imgui);

        if (File.Exists(imgui)) File.Move(imgui, imguiPath, true);
        if (File.Exists(imguiPath) && !forceDownload) return;

        await ApplicationService.ApiEndpointView.DownloadFileAsync($"https://cdn.fmodel.app/d/configurations/{imgui}", imguiPath);
        if (new FileInfo(imguiPath).Length == 0)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Could not download ImGui settings", Constants.WHITE, true));
        }
    }

    public static async Task InitOodle()
    {
        if (!Avx2.IsSupported)
            return;

        var oodlePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", OodleHelper.OODLE_NAME_OLD);
        if (!File.Exists(oodlePath))
        {
            oodlePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", OodleHelper.OODLE_NAME_CURRENT);
        }

        await OodleHelper.InitializeAsync(oodlePath);
    }

    public static async Task InitZlib()
    {
        var zlibPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", ZlibHelper.DLL_NAME);
        var zlibFileInfo = new FileInfo(zlibPath);

        if (!zlibFileInfo.Exists || zlibFileInfo.LastWriteTimeUtc < DateTime.UtcNow.AddMonths(-4))
        {
            if (!await ZlibHelper.DownloadDllAsync(zlibPath))
            {
                zlibFileInfo.Refresh();
                if (!zlibFileInfo.Exists) return;
            }
        }

        await ZlibHelper.InitializeAsync(zlibPath);
    }

    public static async Task InitDetex()
    {
        var detexPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", DetexHelper.DLL_NAME);
        if (File.Exists(DetexHelper.DLL_NAME))
        {
            File.Move(DetexHelper.DLL_NAME, detexPath, true);
        }
        else if (!File.Exists(detexPath))
        {
            await DetexHelper.LoadDllAsync(detexPath);
        }

        DetexHelper.Initialize(detexPath);
    }

    public static async Task InitUnluac()
    {
        var unluacPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", UnluacHelper.DllName);
        await UnluacHelper.InitializeAsync(unluacPath).ConfigureAwait(false);
        if (UnluacHelper.Instance is null)
            FLogger.Append(ELog.Error, () => FLogger.Text("Failed to download unluac", Constants.WHITE, true));
    }
}
