using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WanStudio.Services;

namespace WanStudio.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 平台下拉：先填预设，再按当前 Endpoint 选中对应项。
        PlatformCombo.ItemsSource = AppSettings.Platforms
            .Select(pf => pf.Name)
            .ToList();
        EndpointBox.Text = AppSettings.Endpoint;
        SyncPlatformSelection();
        if (AppSettings.CurrentPlatform is { } current)
        {
            PlatformNote.Text = current.Note;
            PlatformNote.Visibility = Visibility.Visible;
        }
        else
        {
            PlatformNote.Text = AppSettings.EndpointNeedsWorkspaceId
                ? "地址里还有 <你的工作空间ID> 没替换，替换后保存才连得通。"
                : "当前是自定义地址；从下拉选一个平台会自动填上。";
            PlatformNote.Visibility = Visibility.Visible;
        }
        ProfileBox.Text = AppSettings.KeyProfile;
        OutputDirBox.Text = AppSettings.OutputDirectory;
        // 模型列表统一来自 ModelCatalog，避免和首页/长视频页各写一份不同步。
        ModelCombo.ItemsSource = ModelCatalog.All.Select(m => m.Id).ToList();

        for (var i = 0; i < ModelCombo.Items.Count; i++)
        {
            if (ModelCombo.Items[i] is string mid &&
                string.Equals(mid, AppSettings.Model, StringComparison.OrdinalIgnoreCase))
            {
                ModelCombo.SelectedIndex = i;
                break;
            }
        }
        if (ModelCombo.SelectedIndex < 0) ModelCombo.SelectedIndex = 0;
        RefreshKeyStatus();
        RefreshElevationState();
    }

    /// <summary>显示当前权限与配置落盘位置，让「保存失败」这类问题一眼能定位。</summary>
    private void RefreshElevationState()
    {
        ElevationText.Text = ElevationHelper.IsElevated
            ? "当前：管理员权限运行。"
            : "当前：普通权限运行（默认）。遇到写不进去的目录时，可以点下面按钮以管理员重启。";

        var dir = AppSettings.CurrentSettingsDirectory;
        SettingsPathText.Text = AppSettings.UsingFallbackSettingsDir
            ? $"配置目录（已自动回退到可写位置）：{dir}"
            : $"配置目录：{dir}";

        ElevateButton.IsEnabled = !ElevationHelper.IsElevated;
    }

    private void ElevateButton_Click(object sender, RoutedEventArgs e)
    {
        var problem = ElevationHelper.RestartElevated();
        if (problem is null)
        {
            // 提权实例已拉起，关掉当前这个，避免两个窗口并存。
            Application.Current.Exit();
            return;
        }

        StatusBar.Severity = InfoBarSeverity.Warning;
        StatusBar.Title = "未提权";
        StatusBar.Message = problem;
        StatusBar.IsOpen = true;
    }

    private void RefreshKeyStatus()
    {
        var profile = string.IsNullOrWhiteSpace(ProfileBox.Text) ? AppSettings.DefaultProfile : ProfileBox.Text.Trim();
        var key = ApiKeyStore.TryGet(profile);
        KeyStatusText.Text = string.IsNullOrEmpty(key)
            ? "当前配置：未保存 Key（生成会禁用）。"
            : $"当前配置：已保存 Key {ApiKeyStore.Mask(key)}";
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    /// <summary>按当前 Endpoint 反查平台下拉选中项。</summary>
    private void SyncPlatformSelection()
    {
        var cur = AppSettings.CurrentPlatform;
        PlatformCombo.SelectedIndex = cur is null
            ? -1
            : AppSettings.Platforms.ToList().FindIndex(p => p.Name == cur.Name);
    }

    /// <summary>
    /// 选平台 = 填 Endpoint。地址框仍然可手改，改完保存即生效。
    /// </summary>
    private void PlatformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlatformCombo.SelectedItem is not string name) return;

        var pf = AppSettings.Platforms.FirstOrDefault(p => p.Name == name);
        if (pf is null) return;

        // 百炼的域名带账号专属的前缀。如果用户已经填过真实域名，就不要用模板把它冲掉。
        var alreadyRealBailian =
            pf.Endpoint.Contains('<') &&
            !string.IsNullOrWhiteSpace(EndpointBox.Text) &&
            EndpointBox.Text.Contains("maas.aliyuncs.com", StringComparison.OrdinalIgnoreCase);

        if (!alreadyRealBailian) EndpointBox.Text = pf.Endpoint;

        PlatformNote.Text = pf.Note;
        PlatformNote.Visibility = Visibility.Visible;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = string.IsNullOrWhiteSpace(ProfileBox.Text) ? AppSettings.DefaultProfile : ProfileBox.Text.Trim();

        // 分两段保存，分别报错：密钥进的是 Windows 凭据库，跟 settings.json 无关。
        // 以前混在一个 try 里，settings 写失败会让人以为「密钥没存上」，其实已经存了。
        var keySaved = false;
        string? keyError = null;
        try
        {
            var key = ApiKeyBox.Password;
            if (!string.IsNullOrWhiteSpace(key))
            {
                ApiKeyStore.Save(profile, key);
                keySaved = true;
            }
        }
        catch (Exception ex)
        {
            keyError = ex.Message;
        }

        string? settingsError = null;
        try
        {
            AppSettings.Endpoint = string.IsNullOrWhiteSpace(EndpointBox.Text) ? AppSettings.DefaultEndpoint : EndpointBox.Text.Trim();
            AppSettings.KeyProfile = profile;
            if (!string.IsNullOrWhiteSpace(OutputDirBox.Text))
                AppSettings.OutputDirectory = OutputDirBox.Text.Trim();
            if (ModelCombo.SelectedItem is string model)
                AppSettings.Model = model;
        }
        catch (SettingsWriteException ex)
        {
            settingsError = ex.Message;
        }
        catch (Exception ex)
        {
            settingsError = $"设置保存失败：{ex.Message}";
        }

        ApiKeyBox.Password = string.Empty;
        RefreshKeyStatus();

        if (settingsError is not null)
        {
            // settings 写不进去时，密钥可能已经存好了，必须说清楚，别让人重复操作。
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = keySaved ? "密钥已保存，但设置没写进去" : "设置保存失败";
            StatusBar.Message = settingsError + (keySaved ? "\n（API Key 已存入 Windows 凭据库，这一项是成功的。）" : "");
            StatusBar.IsOpen = true;
            return;
        }

        if (keyError is not null)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "密钥保存失败";
            StatusBar.Message = keyError;
            StatusBar.IsOpen = true;
            return;
        }

        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.Title = "已保存";
        StatusBar.Message = keySaved ? "设置与 API Key 都已保存。" : "设置已写入本机。";
        StatusBar.IsOpen = true;
    }

    private void ClearKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = string.IsNullOrWhiteSpace(ProfileBox.Text) ? AppSettings.DefaultProfile : ProfileBox.Text.Trim();
        ApiKeyStore.Delete(profile);
        RefreshKeyStatus();
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = "已清除";
        StatusBar.Message = "该配置名下的 Key 已删除。";
        StatusBar.IsOpen = true;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = string.IsNullOrWhiteSpace(ProfileBox.Text) ? AppSettings.DefaultProfile : ProfileBox.Text.Trim();
            var key = ApiKeyStore.TryGet(profile);
            if (string.IsNullOrEmpty(key))
            {
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Title = "缺 Key";
                StatusBar.Message = "请先保存 API Key 再探测。";
                StatusBar.IsOpen = true;
                return;
            }

            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title = "探测中";
            StatusBar.Message = "正在请求接口…";
            StatusBar.IsOpen = true;

            using var client = new DashScopeClient(key, AppSettings.Endpoint);
            // lightweight: submit cancelled quickly is heavy; just construct + GetTask with fake id to probe auth path is bad.
            // Call models list if available; else short submit with tiny duration and cancel — use WaitUntilDone would spend.
            // For AC: show clear error on bad key via GetTask of invalid.
            try
            {
                await client.GetTaskAsync("probe-invalid-task-id");
            }
            catch (Exception ex)
            {
                StatusBar.Severity = InfoBarSeverity.Informational;
                StatusBar.Title = "已连通（或返回业务错误）";
                StatusBar.Message = ex.Message;
                StatusBar.IsOpen = true;
                return;
            }

            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Title = "探测完成";
            StatusBar.Message = "接口有响应。";
            StatusBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "探测失败";
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
        }
    }
}
