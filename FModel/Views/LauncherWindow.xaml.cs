using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FModel.ViewModels;

namespace FModel.Views;

/* 第一层窗口：启动器。本身不承载游戏逻辑，关掉不影响游戏进程。 */
public partial class LauncherWindow
{
    /* 单实例激活（App.NativeMethods 同名注册）：运行时注册的消息 Id，进程首次使用时缓存 */
    private static int _launcherActivateMsgId = -1;

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

    /* 窗口每次获得焦点时刷新列表（顺带刷新「运行中」状态，成本 = 枚举目录 + PID 检查） */
    private void OnActivated(object sender, System.EventArgs e)
    {
        ((LauncherViewModel) DataContext).Refresh();
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
            Dispatcher.InvokeAsync(() => { Activate(); Topmost = true; Topmost = false; });
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
}
