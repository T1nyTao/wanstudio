using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace WanStudio.Services;

/// <summary>
/// 文件选择器的统一入口。
///
/// 为什么需要它：WinUI 3 的 FileOpenPicker / FileSavePicker 是系统对话框，
/// 它依附于传给它的 HWND。如果主窗口当时不在前台（比如刚从别的窗口切回来、
/// 或者主窗口被别的程序压住），对话框会开在**主窗口后面**——
/// 用户看到的现象就是「点了按钮没反应」，其实对话框已经开了，只是看不见。
///
/// 所以这里统一做两件事：
///   1. 拿到主窗口 HWND（拿不到就抛明确错误，而不是让选择器静默失败）
///   2. 弹之前先把主窗口激活/置前，保证对话框出现在用户眼前
/// </summary>
public static class PickerHost
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    /// <summary>
    /// 取主窗口 HWND 并把它拉到前台。返回的 hwnd 用于 InitializeWithWindow。
    /// 主窗口还没就绪时抛 <see cref="InvalidOperationException"/>，消息可直接展示给用户。
    /// </summary>
    public static IntPtr BringToFrontAndGetHandle()
    {
        var window = App.MainWindowHint
            ?? throw new InvalidOperationException("窗口还没准备好，请等界面加载完成后再试。");

        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("拿不到窗口句柄，无法打开文件选择器。");

        try
        {
            // 最小化状态下 SetForegroundWindow 不生效，先还原。
            // 注意：不要在这里调 window.Activate() —— 它会触发 Activated 事件，
            // 在 async 的点击处理器里可能造成重入，把窗口状态搞乱（实测会让进程直接退出）。
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // 置前失败不该阻断选择器：退化为「可能开在后面」，但仍可用。
        }

        return hwnd;
    }

    /// <summary>把一个选择器绑定到主窗口并置前。</summary>
    public static void Attach(object picker)
    {
        var hwnd = BringToFrontAndGetHandle();
        InitializeWithWindow.Initialize(picker, hwnd);
    }
}
