using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FModel.ViewModels;

namespace FModel.Views;

/* 第一层窗口：启动器。本身不承载游戏逻辑，关掉不影响游戏进程。 */
public partial class LauncherWindow
{
    /* 单实例激活（App.NativeMethods 同名注册）：运行时注册的消息 Id，进程首次使用时缓存 */
    private static int _launcherActivateMsgId = -1;
    private DispatcherOperation _refreshOperation;

    public LauncherWindow()
    {
        DataContext = new LauncherViewModel();
        InitializeComponent();
    }

    /* 双击与 Open 使用相同入口；运行中的档案由其宿主显示窗口。 */
    private void OnProfileDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ((LauncherViewModel) DataContext).OpenSelectedProfile();
    }

    /* 激活消息处理完成后再刷新，避免在恢复窗口和键盘焦点期间移除列表项。 */
    private void OnActivated(object sender, System.EventArgs e)
    {
        if (_refreshOperation?.Status == DispatcherOperationStatus.Pending) return;
        _refreshOperation = Dispatcher.InvokeAsync(() =>
        {
            if (IsActive && WindowState != WindowState.Minimized)
                ((LauncherViewModel) DataContext).Refresh();
        }, DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshOperation?.Abort();
        base.OnClosed(e);
    }

    /* 挂 HWND 消息钩子：第二个启动器实例在互斥量上未获所有权时广播激活，此处收到后置顶。 */
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_launcherActivateMsgId == -1)
            _launcherActivateMsgId = (int) RegisterWindowMessage("FModel-Activate-Launcher");
        var source = (HwndSource) PresentationSource.FromVisual(this);
        source?.AddHook(LauncherHook);
    }

    private IntPtr LauncherHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _launcherActivateMsgId)
        {
            Dispatcher.InvokeAsync(() =>
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            });
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
