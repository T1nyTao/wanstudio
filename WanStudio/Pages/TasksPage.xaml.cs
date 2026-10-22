using System;
using System.ComponentModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using Windows.System;
using WanStudio.Services;
using WinRT.Interop;

namespace WanStudio.Pages;

public sealed partial class TasksPage : Page
{
    private VideoTaskItem? _selected;
    private string? _pendingSelectId;
    private string? _playerPath;

    public TasksPage()
    {
        InitializeComponent();
        TaskList.ItemsSource = VideoTaskStore.Tasks;
        Loaded += TasksPage_Loaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pendingSelectId = e.Parameter as string;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Player.Source = null;
        _playerPath = null;
    }

    private void TasksPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_pendingSelectId))
        {
            var item = VideoTaskStore.Find(_pendingSelectId);
            if (item is not null) TaskList.SelectedItem = item;
            _pendingSelectId = null;
        }
        else if (TaskList.SelectedItem is null && VideoTaskStore.Tasks.Count > 0)
        {
            TaskList.SelectedIndex = 0;
        }
        RefreshDetail();
    }

    private void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is not null) _selected.PropertyChanged -= Selected_PropertyChanged;
        _selected = TaskList.SelectedItem as VideoTaskItem;
        if (_selected is not null) _selected.PropertyChanged += Selected_PropertyChanged;
        _playerPath = null;
        RefreshDetail();
    }

    private void Selected_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(RefreshDetail);

    private void RefreshDetail()
    {
        if (_selected is null)
        {
            CancelButton.IsEnabled = false;
            RetryButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
            SaveAsButton.IsEnabled = false;
            DetailText.Text = VideoTaskStore.Tasks.Count == 0
                ? "还没有任务。去「新建视频」或「长视频」提交一条。"
                : "选中任务查看详情。";
            ShowPlayer(null);
            return;
        }

        CancelButton.IsEnabled = _selected.CanCancel;
        RetryButton.IsEnabled = _selected.CanRetry;
        OpenFolderButton.IsEnabled = _selected.HasVideo;
        SaveAsButton.IsEnabled = _selected.HasVideo;
        DetailText.Text = string.IsNullOrEmpty(_selected.Error)
            ? $"{_selected.StatusText} · 远程任务 {_selected.RemoteTaskId ?? "（未提交）"}"
            : _selected.Error;

        if (_selected.HasVideo && !string.IsNullOrEmpty(_selected.LocalPath))
        {
            PreviewPath.Visibility = Visibility.Visible;
            PreviewPath.Text = $"已保存：{_selected.LocalPath}";
            ShowPlayer(_selected.LocalPath);
        }
        else
        {
            PreviewPath.Visibility = Visibility.Collapsed;
            ShowPlayer(null);
        }
    }

    private void ShowPlayer(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Player.Source = null;
            Player.Visibility = Visibility.Collapsed;
            PreviewEmpty.Visibility = Visibility.Visible;
            _playerPath = null;
            return;
        }

        if (!string.Equals(_playerPath, path, StringComparison.OrdinalIgnoreCase))
        {
            Player.Source = MediaSource.CreateFromUri(new Uri(path));
            _playerPath = path;
        }
        Player.Visibility = Visibility.Visible;
        PreviewEmpty.Visibility = Visibility.Collapsed;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        var task = _selected;
        if (task is null) return;

        // 只有排队/进行中可取消；终态任务点取消是无操作，不能抛。
        if (!task.CanCancel)
        {
            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title = "无需取消";
            StatusBar.Message = "这条任务已经结束了。";
            StatusBar.IsOpen = true;
            return;
        }

        var cts = task.Cts;
        if (cts is null)
        {
            // 极窄窗口：任务刚提交、runner 还没把 Cts 挂上，或已经跑完把 Cts 清了。
            // 这时不能空引用，直接把状态收成已取消（runner 会在下次检查时退出）。
            task.Phase = VideoTaskPhase.Canceled;
            task.StatusText = "已取消";
            task.Error = null;
            TaskCancelFeedback("任务已在提交前取消。");
            return;
        }

        try
        {
            // 不等待：Cancel() 只是置位，等待会卡住界面线程。
            cts.Cancel();
            task.StatusText = "取消中…";
            TaskCancelFeedback("已请求取消，正在中止轮询。");
        }
        catch (ObjectDisposedException)
        {
            // 连续点两次取消 / runner 已 Dispose：视为已取消，不报错。
            task.Phase = VideoTaskPhase.Canceled;
            task.StatusText = "已取消";
            TaskCancelFeedback("任务已取消。");
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "取消失败";
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
        }
    }

    private void TaskCancelFeedback(string message)
    {
        StatusBar.Severity = InfoBarSeverity.Warning;
        StatusBar.Title = "取消";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
        RefreshDetail();
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Phase = VideoTaskPhase.Queued;
        _selected.StatusText = "排队中";
        _selected.Error = null;
        _selected.Progress = 0;
        _selected.LocalPath = null;
        _selected.RemoteTaskId = null;
        _ = VideoJobRunner.RunAsync(_selected, DispatcherQueue);
        RefreshDetail();
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.LocalPath is null) return;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(_selected.LocalPath);
            await Launcher.LaunchFileAsync(file);
        }
        catch (Exception ex)
        {
            DetailText.Text = $"无法打开文件：{ex.Message}";
        }
    }

    private async void SaveAsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected?.LocalPath is null) return;
        try
        {
            // 同样用 Win32：WinRT FileSavePicker 在未打包 WinUI 3 里同样会 E_FAIL。
            var target = await Win32FilePicker.PickSaveFileAsync(
                PickerHost.BringToFrontAndGetHandle(),
                "另存视频",
                Path.GetFileNameWithoutExtension(_selected.LocalPath),
                "mp4",
                new[] { ("MP4 视频 (*.mp4)", "*.mp4"), ("所有文件 (*.*)", "*.*") });
            if (target is null) return;

            File.Copy(_selected.LocalPath, target, overwrite: true);
            DetailText.Text = $"已另存为：{target}";
        }
        catch (Exception ex)
        {
            DetailText.Text = $"另存失败：{ex.Message}";
        }
    }
}
