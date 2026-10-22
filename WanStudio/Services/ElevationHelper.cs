using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WanStudio.Services;

/// <summary>
/// 「需要时才提权」的实现。
///
/// 为什么不做成清单里写死 requireAdministrator：那样每次双击都弹 UAC，
/// 而且提权后的窗口会被 UIPI 隔离——非管理员进程（含自动化工具）再也读不到它的界面，
/// 连界面验收都做不了。
///
/// 这里改成运行时按需提权：当前进程已经是管理员就什么都不做；
/// 不是的话用 ShellExecute 的 <c>runas</c> 动词重新拉起自己（系统弹 UAC），
/// 成功则退出当前实例，避免两个窗口并存。
/// </summary>
public static class ElevationHelper
{
    /// <summary>当前进程是否已具备管理员权限。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 以管理员身份重启自己。返回 null 表示提权流程已发起（调用方应退出当前实例），
    /// 返回字符串则是给用户看的原因。
    /// </summary>
    public static string? RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return "当前进程不是从 exe 启动的，无法自动提权；请手动用管理员身份运行程序。";
            }

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            };

            // 把原始启动参数带上，重启后仍停在同一个页面。
            foreach (var arg in Environment.GetCommandLineArgs().AsSpan(1))
            {
                psi.ArgumentList.Add(arg);
            }

            Process.Start(psi);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            // 用户在 UAC 对话框点了「否」，不是错误，只是没提权。
            return "已取消提权（UAC 未确认）。";
        }
        catch (Exception ex)
        {
            return "提权失败：" + ex.Message;
        }
    }
}
