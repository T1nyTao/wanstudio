using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WanStudio.Pages;

using WanStudio.Services;

namespace WanStudio;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        try
        {
            InitializeComponent();
            try
            {
                AppWindow.SetIcon("Assets/AppIcon.ico");
            }
            catch { /* icon optional */ }

            // 给一个合适的默认尺寸：WinUI 不给就很小（约 800x600），
            // 首页的双栏素材库与两列参数区会一直降级成堆叠，用户看不到设计意图。
            // 必须在窗口激活之后再 Resize：构造函数里 AppWindow 还没拿到真实 HWND，
            // Resize 会被忽略（实测宽度一直停在 773）。
            Activated += MainWindow_FirstActivated;
        }
        catch (Exception ex)
        {
            Log("MainWindow ctor", ex);
            throw;
        }
    }

    private bool _sized;

    private void MainWindow_FirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_sized) return;
        _sized = true;

        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (area is null) return;

            var work = area.WorkArea;
            var w = Math.Clamp((int)(work.Width * 0.88), 1180, 1680);
            var h = Math.Clamp((int)(work.Height * 0.88), 760, 1040);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        }
        catch
        {
            // 尺寸失败不影响功能（用户可自己拖窗口）
        }
    }

    /// <summary>--import= 的路径只交给长视频页一次，避免之后手动点导航又重复导入。</summary>
    private string? _pendingImport = App.StartupImportPath;

    /// <summary>
    /// 所有导航统一走这里。
    /// 早先 NavView_Loaded 设置 SelectedItem（会触发 SelectionChanged 导航一次）之后又手动 Navigate 一次，
    /// 长视频页被实例化两次，而且第一次不带导入路径，第二次才带。
    /// </summary>
    private void NavigateTo(Type page)
    {
        object? parameter = null;
        if (page == typeof(LongVideoPage) && _pendingImport is not null)
        {
            parameter = _pendingImport;
            _pendingImport = null;
        }
        else if (NavFrame.CurrentSourcePageType == page)
        {
            return;   // 已经在这一页，不重建
        }

        NavFrame.Navigate(page, parameter);
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var page = App.StartupPage switch
            {
                "tasks" => typeof(TasksPage),
                "long" => typeof(LongVideoPage),
                "settings" => typeof(SettingsPage),
                _ => typeof(HomePage)
            };

            // 选中对应菜单项：若选中项发生变化，SelectionChanged 会同步完成导航。
            if (App.StartupPage == "settings")
            {
                NavView.SelectedItem = NavView.SettingsItem;
            }
            else
            {
                var target = page == typeof(HomePage) ? "new" : App.StartupPage;
                foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
                {
                    if ((item.Tag as string) == target) NavView.SelectedItem = item;
                }
            }

            // 选中项没变（默认就是「新建视频」）时不会有 SelectionChanged，这里兜底导航。
            if (NavFrame.Content is null) NavigateTo(page);
        }
        catch (Exception ex)
        {
            Log("NavView_Loaded", ex);
            throw;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            if (args.IsSettingsSelected)
            {
                NavigateTo(typeof(SettingsPage));
                return;
            }

            if (args.SelectedItem is NavigationViewItem item)
            {
                switch (item.Tag as string)
                {
                    case "new":
                        NavigateTo(typeof(HomePage));
                        break;
                    case "tasks":
                        NavigateTo(typeof(TasksPage));
                        break;
                    case "long":
                        NavigateTo(typeof(LongVideoPage));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log("SelectionChanged", ex);
        }
    }

    private static void Log(string where, Exception ex)
    {
        try
        {
            AppPaths.AppendCrashLog("[" + where + "] " + ex);
        }
        catch { }
    }
}
