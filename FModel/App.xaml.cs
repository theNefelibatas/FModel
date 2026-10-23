using AdonisUI.Controls;
using Microsoft.Win32;
using Serilog;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CUE4Parse;
using FModel.Framework;
using FModel.Services;
using FModel.Automation;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using MessageBoxResult = AdonisUI.Controls.MessageBoxResult;

namespace FModel;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App
{
    /* CLI headless 模式：表现层不参与，异常不弹框（没有人能点） */
    private bool _isHeadless;
    private GameOperations _automationOperations;
    private LocalSessionServer _automationServer;
    internal static string SessionLogPath { get; private set; }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("winbrand.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern string BrandingFormatString(string format);

    protected override void OnStartup(StartupEventArgs e)
    {
        var profilesDirectory = Environment.GetEnvironmentVariable("FMODEL_PROFILES_DIRECTORY");
        if (profilesDirectory != null)
        {
            if (string.IsNullOrWhiteSpace(profilesDirectory) || !Directory.Exists(profilesDirectory))
            {
                ReportEarlyFailure("profiles", new DirectoryNotFoundException("FMODEL_PROFILES_DIRECTORY must name an existing directory."));
                Environment.Exit(1);
                return;
            }
            ProfileStore.ProfilesDirectory = Path.GetFullPath(profilesDirectory);
        }
        if (AutomationEntryPoint.IsClient)
        {
            _isHeadless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunAutomationClient();
            return;
        }
        if (AutomationEntryPoint.IsHost)
        {
            _isHeadless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunAutomationHost();
            return;
        }
#if DEBUG
        AttachConsole(-1);
#endif

        /* 主窗口由代码显式创建（见下方 GUI 分支）而非 App.xaml 的 StartupUri：
         * CLI 模式下表现层完全不参与，逻辑与数据层照常复用，反馈走日志与退出码。
         * ShutdownMode 改为显式，因为没有主窗口可供 OnMainWindowClose 触发退出。 */
        if (CliExporter.HasCliArguments)
        {
            _isHeadless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            /* Release 构建默认没有控制台（顶部 AttachConsole 仅 DEBUG），CLI 调用方在终端里
             * 看不到早期失败输出。无条件接管父进程控制台：有则可见，无（服务/CI）或输出
             * 已重定向则自然失败，无副作用。必须在任何 Console 访问（含 Serilog 控制台 sink）之前。 */
            AttachConsole(-1);

            CliExporter.CliRequest cliRequest;
            try
            {
                cliRequest = CliExporter.ParseRequest();
            }
            catch (Exception ex)
            {
                ReportEarlyFailure("arguments", ex);
                Environment.Exit(1);
                return;
            }

            /* 注意：InitializeSettings 不在 OnStartup 调用——它必须在档案装载
             * （RunHeadless 内 ApplyToCurrent 之后）执行，见该处注释。 */

            RunHeadless(cliRequest);
            return;
        }

        /* 启动模型分派（优先级）：CLI → --game=<Id> → 启动器（单实例） */
        if (!CliExporter.HasCliArguments)
        {
            var profileId = GetProfileIdArg();
            if (profileId != null)
            {
                ShowGameWindow(e, profileId);
                return;
            }
            ShowLauncherWindow(e);
            return;
        }
    }

    private static string GetProfileIdArg()
        => Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--game=", StringComparison.OrdinalIgnoreCase))?
            .Substring("--game=".Length).Trim('"');

    private void ShowLauncherWindow(StartupEventArgs e)
    {
        /* 单实例：第二个启动器实例只广播激活并退出（互斥量名固定，不可改名）。
         * WaitOne(0) 非阻塞判定；已抛弃的互斥量（首实例被杀死）在此处重新取得所有权。 */
        var launcherMutex = new Mutex(false, @"Local\FModel-Launcher-Mutex");
        var acquired = false;
        try
        {
            try { acquired = launcherMutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Launcher mutex acquire failed, exiting");
        }
        if (!acquired)
        {
            NativeMethods.BroadcastLauncherActivate();
            Environment.Exit(0);
            return;
        }
        _launcherMutex = launcherMutex;

        base.OnStartup(e);
        InitializeSettings();
        MigrateLegacySettingsIfNeeded();
        new LauncherWindow().Show();
    }

    private async void ShowGameWindow(StartupEventArgs e, string profileId)
    {
        base.OnStartup(e);

        if (!ProfileStore.TryLoad(profileId, out var profile, out var error))
        {
            EarlyFail($"profile failed to load: {error}", 1);
            return;
        }

        var profileLock = ProfileLock.TryAcquire(profile.Id);
        if (profileLock == null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                using var broker = new SessionBroker();
                await broker.ExecuteAsync("show_gui", new Newtonsoft.Json.Linq.JObject { ["profileId"] = profile.Id }, CancellationToken.None);
                // This process only activated the existing owner; it never loaded settings to save.
                Environment.Exit(0);
            }
            catch (Exception ex) { EarlyFail($"Could not open the running profile: {ex.Message}", 1); }
            return;
        }
        _currentProfileLock = profileLock;

        /* MainWindow/ApplicationViewModel 构造依赖 CurrentDir 与档案设置，必须先完成装载。
         * InitializeSettings 作用于已装配的 Default：新档案快照的目录字段为 null，修正必须落在档案实例
         * （随后随 save 写回），并保证日志/输出目录使用档案自己的设置。
         * loadFromFile=false：全局设置文件不含 CurrentDir/CurrentProfileId（[JsonIgnore]），读文件会整体覆盖已装配的实例。 */
        profile.ApplyToCurrent();
        UserSettings.Default.CurrentProfileId = profile.Id;
        InitializeSettings(false);
        profile.LastUsedAt = DateTime.Now;
        try { ProfileStore.Save(profile); } catch (Exception ex) { Log.Warning(ex, "Could not update LastUsedAt"); }

        Log.Information("Opened game profile {Id} ({Name})", profile.Id, profile.Game.GameName);
        StartAutomationServer(profile.Id, true);
        new MainWindow().Show();
    }

    private void MigrateLegacySettingsIfNeeded()
    {
        if (!File.Exists(UserSettings.FilePath)) return;
        try { ProfileStore.MigrateLegacy(UserSettings.FilePath); }
        catch (Exception ex) { Log.Error(ex, "Legacy settings migration failed"); }
    }

    /* 档案已装载（ApplyToCurrent），后续 UserSettings.Save 都写回该档案；
     * AppExit 释放锁（CLI 直接 Exit 时由锁文件 PID 自愈兜底）。 */
    private static ProfileLock _currentProfileLock;
    private static Mutex _launcherMutex;

    private async void RunAutomationClient() => Environment.Exit(await AutomationEntryPoint.RunAsync());

    private void StartAutomationServer(string profileId, bool hasGui)
    {
        _automationOperations = new GameOperations(hasGui);
        _automationServer = new LocalSessionServer(_automationOperations, profileId);
        _automationServer.Start();
    }

    internal void MarkAutomationReady()
    {
        if (_automationOperations != null) _automationOperations.Ready = true;
    }

    internal bool IsAutomationClosing => _automationOperations?.IsClosing == true;

    internal void ShowSessionWindow()
    {
        Dispatcher.VerifyAccess();
        if (MainWindow is not FModel.MainWindow window)
        {
            window = new MainWindow(true);
            MainWindow = window;
        }
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        _isHeadless = false;
        _automationOperations.HasGui = true;
    }

    internal void HideSessionWindow()
    {
        Dispatcher.VerifyAccess();
        MainWindow?.Hide();
        _automationOperations.HasGui = false;
        _isHeadless = true;
    }

    private async void RunAutomationHost()
    {
        // A persistent host must not retain a dependency on its launching client's console pipes.
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        try
        {
            var id = GetProfileIdArg() ?? Environment.GetEnvironmentVariable(LocalProtocol.ProfileEnvironment);
            var profile = ProfileStore.Enumerate(out _).FirstOrDefault(x => x.Id == id)
                ?? throw new ArgumentException("Pass an existing profile ID through --game or FMODEL_AUTOMATION_PROFILE.");
            _currentProfileLock = ProfileLock.TryAcquire(profile.Id)
                ?? throw new InvalidOperationException("The profile is already open in another process.");
            profile.ApplyToCurrent();
            UserSettings.Default.CurrentProfileId = profile.Id;
            InitializeSettings(false);
            StartAutomationServer(profile.Id, false);
            InstalledCodecs.Initialize();
            var appView = ApplicationService.ApplicationView;
            await appView.CUE4Parse.Initialize();
            if (appView.Status.Kind == EStatusKind.Failed) throw new InvalidOperationException("Archive initialization failed. Inspect the profile log.");
            await appView.AesManager.InitAes();
            await appView.UpdateProvider(true);
            if (appView.Status.Kind == EStatusKind.Failed) throw new InvalidOperationException("Archive mounting failed. Inspect the profile log.");
            await appView.CUE4Parse.VerifyConsoleVariables();
            await appView.CUE4Parse.VerifyOnDemandArchives();
            await appView.CUE4Parse.InitMappings();
            MarkAutomationReady();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Automation session initialization failed");
            ReportEarlyFailure("automation session", ex);
            if (_automationOperations != null) _automationOperations.InitializationError = ex.Message;
            else
            {
                _currentProfileLock?.Dispose();
                Environment.Exit(1);
            }
        }
    }

    /* 重开机（Restart/BackToLauncher 拉起新进程）前先释放档案锁：新进程才能取得同一档案的锁。
     * 与 AppExit 中的释放是双保险——ProfileLock.Dispose 幂等（File.Exists 守卫）。 */
    internal static void DisposeProfileLockForRelaunch() => _currentProfileLock?.Dispose();

    private void EarlyFail(string message, int exitCode)
    {
        Serilog.Log.Error("{Message}", message);
        try { MessageBox.Show(message, "FModel", AdonisUI.Controls.MessageBoxButton.OK, MessageBoxImage.Error); }
        catch { /* 无人可点（无桌面）时静默 */ }
        Environment.Exit(exitCode);
    }

    /* 启动器单实例：第二实例注册窗口消息后 HWND_BROADCAST 通知第一实例置顶。
     * RegisterWindowMessage 返回值同进程内固定、跨进程一致——两端各自注册同名即得到同一 Id。 */
    private static class NativeMethods
    {
        private const int WM_BROADCAST = 0xFFFF;
        private static readonly int MsgId = (int) RegisterWindowMessage("FModel-Activate-Launcher");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        private static extern int PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void BroadcastLauncherActivate()
            => PostMessage(new IntPtr(WM_BROADCAST), MsgId, IntPtr.Zero, IntPtr.Zero);
    }

    /* CLI 早期失败兜底：设置初始化完成前 Serilog 不可用，此期间的异常写到标准错误，调用方连同退出码判定。 */
    private static void ReportEarlyFailure(string stage, Exception ex)
    {
        try
        {
            Console.Error.WriteLine($"[CLI] failed at {stage}: {ex}");
            Console.Error.Flush();
        }
        catch
        {
            /* 兜底本身失败时无处可报，交给退出码 */
        }
    }

    /* CLI headless 执行：与主窗口加载时相同的初始化步骤，但不涉及表现层 */
    private async void RunHeadless(CliExporter.CliRequest request)
    {
        try
        {
            /* CLI：解析档案（--game=<Id> 或最近使用），无档案 → 明确报错。
             * 必须先于 ApplicationService.ApplicationView：单例构造时 CurrentDir 缺失会硬退出。 */
            var profile = ResolveCliProfile(request.GameId);
            if (profile == null)
            {
                throw new InvalidOperationException(
                    $"CLI: no game profile found. Pick one in the launcher first, or pass --game=<profileId>.");
            }
            var cliLock = ProfileLock.TryAcquire(profile.Id);
            if (cliLock == null)
            {
                throw new InvalidOperationException($"CLI: profile '{profile.Id}' is already running (probably the GUI).");
            }
            profile.ApplyToCurrent();
            UserSettings.Default.CurrentProfileId = profile.Id;
            /* ApplyToCurrent 之后才做目录修正/建目录/日志初始化：
             * 保证日志与输出目录落在档案自己的设置上（新档案快照目录字段为 null，修正必须作用在已装配实例上）。
             * loadFromFile=false：不读全局设置文件——CurrentDir/CurrentProfileId 为 [JsonIgnore]，读文件会把已装配的档案实例整体覆盖。 */
            InitializeSettings(false);
            _currentProfileLock = cliLock;

            var appView = ApplicationService.ApplicationView;
            await Task.WhenAll(ApplicationViewModel.InitOodle(), ApplicationViewModel.InitZlib());
            await appView.CUE4Parse.Initialize();
            await appView.AesManager.InitAes();
            await appView.UpdateProvider(true);

            /* GUI 启动序列（MainWindow.OnLoaded）中影响导出的部分：缺 InitDetex 会导致
             * BC7 贴图解码失败（"Detex decompression failed: not initialized"），
             * 缺 InitMappings 会导致 unversioned 包（需 .usmap）解析失败。 */
            await Task.WhenAll(
                appView.CUE4Parse.VerifyConsoleVariables(),
                appView.CUE4Parse.VerifyOnDemandArchives(),
                appView.CUE4Parse.InitMappings(),
                ApplicationViewModel.InitDetex(),
                ApplicationViewModel.InitVgmStream(),
                UserSettings.Default.DecompileLua ? ApplicationViewModel.InitUnluac() : Task.CompletedTask
            );

            await CliExporter.RunAsync(appView.CUE4Parse, request, CancellationToken.None);
            Log.Information("CLI: export completed");
            _currentProfileLock?.Dispose(); // CLI 走 Environment.Exit 不触发 AppExit，锁文件在此清除
            Log.CloseAndFlush(); // Environment.Exit 不走 WPF Exit 事件（AppExit），先冲刷 Serilog 防日志截断
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CLI export failed");
            Log.CloseAndFlush();
            ReportEarlyFailure("export", ex);
            Environment.Exit(1);
        }
    }

    /* CLI 档案解析：--game=<Id> 显式指定；缺省（无 --game）用 LastUsedAt 最近的一个。
     * 显式空值（--game=）按"传参明确失败"处理，不静默回落到最近使用（AGENTS 1.3）。 */
    private static GameProfile ResolveCliProfile(string gameId)
    {
        if (gameId == "") return null;
        if (gameId != null)
            return ProfileStore.TryLoad(gameId, out var p, out _) ? p : null;
        return ProfileStore.Enumerate(out _)
            .OrderByDescending(p => p.LastUsedAt)
            .FirstOrDefault();
    }

    private void InitializeSettings(bool loadFromFile = true)
    {
        /* 档案模式（loadFromFile=false）不读全局文件：CurrentDir/CurrentProfileId 为 [JsonIgnore]，
         * 读文件会把已由 ApplyToCurrent 装配的档案实例整个覆盖（丢 CurrentDir/档案 Id）。 */
        if (loadFromFile)
        {
            try
            {
                UserSettings.Default = JsonConvert.DeserializeObject<UserSettings>(
                    File.ReadAllText(UserSettings.FilePath), JsonNetSerializer.SerializerSettings);
            }
            catch
            {
                UserSettings.Default = new UserSettings();
            }
        }
        else if (UserSettings.Default is null)
        {
            UserSettings.Default = new UserSettings(); // 档案模式：Default 已由 ApplyToCurrent 装配，仅兜底
        }

        var createMe = false;
        if (!Directory.Exists(UserSettings.Default.OutputDirectory))
        {
            var currentDir = AppContext.BaseDirectory;
            try
            {
                var outputDir = Directory.CreateDirectory(Path.Combine(currentDir, "Output"));
                using (File.Create(Path.Combine(outputDir.FullName, Path.GetRandomFileName()), 1, FileOptions.DeleteOnClose))
                {

                }

                UserSettings.Default.OutputDirectory = outputDir.FullName;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new Exception("FModel cannot create the output directory where it is currently located. Please move FModel.exe to a different location.", exception);
            }
        }

        if (!Directory.Exists(UserSettings.Default.RawDataDirectory))
        {
            createMe = true;
            UserSettings.Default.RawDataDirectory = Path.Combine(UserSettings.Default.OutputDirectory, "Exports");
        }

        if (!Directory.Exists(UserSettings.Default.PropertiesDirectory))
        {
            createMe = true;
            UserSettings.Default.PropertiesDirectory = Path.Combine(UserSettings.Default.OutputDirectory, "Exports");
        }

        if (!Directory.Exists(UserSettings.Default.TextureDirectory))
        {
            createMe = true;
            UserSettings.Default.TextureDirectory = Path.Combine(UserSettings.Default.OutputDirectory, "Exports");
        }

        if (!Directory.Exists(UserSettings.Default.AudioDirectory))
        {
            createMe = true;
            UserSettings.Default.AudioDirectory = Path.Combine(UserSettings.Default.OutputDirectory, "Exports");
        }

        if (!Directory.Exists(UserSettings.Default.CodeDirectory))
        {
            createMe = true;
            UserSettings.Default.CodeDirectory = Path.Combine(UserSettings.Default.OutputDirectory, "Exports");
        }

        if (!Directory.Exists(UserSettings.Default.ModelDirectory))
        {
            createMe = true;
            UserSettings.Default.ModelDirectory = Path.Combine(UserSettings.Default.OutputDirectory, "Exports");
        }

        Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel"));
        Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, "Backups"));
        if (createMe) Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, "Exports"));
        Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, "Logs"));
        CacheManager.EnsureDirectories();

#if DEBUG
        var filePath = Path.Combine(UserSettings.Default.OutputDirectory, "Logs", $"FModel-Debug-Log-{DateTime.Now:yyyy-MM-dd}.log");
#else
        var filePath = Path.Combine(UserSettings.Default.OutputDirectory, "Logs", $"FModel-Log-{DateTime.Now:yyyy-MM-dd}.log");
#endif
        SessionLogPath = filePath;
        const string template1 = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Enriched}: {Message:lj}{NewLine}{Exception}";
        const string template2 = "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] [{ClassName}] {ObjectPath}: {Message:lj}{NewLine}{Exception}";
        Log.Logger = new LoggerConfiguration()
#if DEBUG
            .Enrich.With<SourceEnricher>()
            .MinimumLevel.Verbose()
#else
            .Enrich.With<CallerEnricher>()
#endif
            .WriteTo.Logger(lc => lc
                .Filter.ByExcluding(IsConversionLibrary)
                .WriteTo.Console(outputTemplate: template1, theme: AnsiConsoleTheme.Literate)
                .WriteTo.File(outputTemplate: template1, path: filePath, shared: true))
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(IsConversionLibrary)
                .WriteTo.Console(outputTemplate: template2, theme: AnsiConsoleTheme.Literate)
                .WriteTo.File(outputTemplate: template2, path: filePath, shared: true))
            .MinimumLevel.Override("CUE4Parse_Conversion", LogEventLevel.Verbose).WriteTo.Sink(ImGuiSink.Instance)
            .CreateLogger();

        CacheManager.MigrateLegacyFiles();
        Log.Information("Version {Version} ({CommitId})", Constants.APP_VERSION, Constants.APP_COMMIT_ID);
        Log.Information("{OS}", GetOperatingSystemProductName());
        Log.Information("{RuntimeVer}", RuntimeInformation.FrameworkDescription);
        Log.Information("Culture {SysLang}", CultureInfo.CurrentCulture);

        static bool IsConversionLibrary(LogEvent e) =>
            e.Properties.TryGetValue("SourceContext", out var sc) &&
            sc.ToString().Contains("CUE4Parse_Conversion");
    }

    private void AppExit(object sender, ExitEventArgs e)
    {
        _automationServer?.Dispose();
        Log.Information("––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––");
        Log.CloseAndFlush();
        if (!AutomationEntryPoint.IsHost) UserSettings.Save();
        _currentProfileLock?.Dispose();
        _launcherMutex?.Dispose();
        Environment.Exit(0);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("{Exception}", e.Exception);
        if (_automationOperations is { Ready: false }) _automationOperations.InitializationError = e.Exception.Message;

        /* CLI 模式没有人能点对话框：记日志后以非零码退出，让调用方从退出码和日志判断。
         * Serilog 可能还没初始化（那时 Log.Error 是空操作），所以同时走标准错误兜底。 */
        if (_isHeadless)
        {
            ReportEarlyFailure("dispatcher", e.Exception);
            Log.CloseAndFlush();
            e.Handled = true;
            Environment.Exit(1);
            return;
        }

        var messageBox = new MessageBoxModel
        {
            Text = $"An unhandled {e.Exception.GetBaseException().GetType()} occurred: {e.Exception.Message}",
            Caption = "Fatal Error",
            Icon = MessageBoxImage.Error,
            Buttons =
            [
                MessageBoxButtons.Custom("Reset Settings", EErrorKind.ResetSettings),
                MessageBoxButtons.Custom("Restart", EErrorKind.Restart),
                MessageBoxButtons.Custom("OK", EErrorKind.Ignore)
            ],
            IsSoundEnabled = false
        };

        MessageBox.Show(messageBox);
        if (messageBox.Result == MessageBoxResult.Custom && (EErrorKind) messageBox.ButtonPressed.Id != EErrorKind.Ignore)
        {
            if ((EErrorKind) messageBox.ButtonPressed.Id == EErrorKind.ResetSettings)
                UserSettings.Delete();

            ApplicationService.ApplicationView.Restart();
        }

        e.Handled = true;
    }

    private string GetOperatingSystemProductName()
    {
        var productName = string.Empty;
        try
        {
            productName = BrandingFormatString("%WINDOWS_LONG%");
        }
        catch
        {
            // ignored
        }

        if (string.IsNullOrEmpty(productName))
            productName = Environment.OSVersion.VersionString;

        return $"{productName} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit)";
    }

    public static string GetRegistryValue(string path, string name = null, RegistryHive root = RegistryHive.CurrentUser)
    {
        using var rk = RegistryKey.OpenBaseKey(root, RegistryView.Default).OpenSubKey(path);
        if (rk != null)
            return rk.GetValue(name, null) as string;
        return string.Empty;
    }
}
