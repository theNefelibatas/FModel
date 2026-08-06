using AdonisUI.Controls;
using Microsoft.Win32;
using Serilog;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CUE4Parse;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
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

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("winbrand.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern string BrandingFormatString(string format);

    protected override void OnStartup(StartupEventArgs e)
    {
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

            try
            {
                InitializeSettings();
            }
            catch (Exception ex)
            {
                ReportEarlyFailure("settings init", ex);
                Environment.Exit(1);
                return;
            }

            RunHeadless(cliRequest);
            return;
        }

        base.OnStartup(e);
        InitializeSettings();

        /* 设置就绪后才能建窗口：窗口加载时的初始化依赖它 */
        new MainWindow().Show();
    }

    /* CLI 早期失败兜底：Serilog 要等设置初始化后才可用，在那之前（以及它自身初始化失败时）
     * 抛出的异常没有任何去处。写到进程标准错误，调用方连同退出码一起判定。 */
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
            /* CLI 不弹目录选择框（headless 下没人能点）：若设置里没有已配置的游戏目录，
             * ApplicationViewModel 构造时会弹 DirectorySelector 挂死，或 Environment.Exit(0) 伪成功。
             * 与 GUI AvoidEmptyGameDirectory 的免弹框条件保持一致（PerDirectory 命中即放行），
             * 不做 Directory.Exists 校验——实时服务（live game）的触发字符串不是真实路径。 */
            var gameDirectory = UserSettings.Default.GameDirectory;
            var perDirectory = UserSettings.Default.PerDirectory;
            if (string.IsNullOrEmpty(gameDirectory) || perDirectory == null ||
                !perDirectory.TryGetValue(gameDirectory, out _))
            {
                throw new InvalidOperationException(
                    $"CLI: no configured game directory found. Pick one in the GUI first, or fix GameDirectory/PerDirectory in {UserSettings.FilePath}.");
            }

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

    private void InitializeSettings()
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
        Log.Information("––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––––");
        Log.CloseAndFlush();
        UserSettings.Save();
        Environment.Exit(0);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("{Exception}", e.Exception);

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
