using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WanStudio.Services;
using WanStudio.Services.Media;

namespace WanStudio.Pages;

public sealed partial class HomePage : Page
{
    private bool _ready;
    private GenerationMode _mode = GenerationMode.TextToVideo;
    private bool _platformSyncing;
    private string? _primaryPath;

    /// <summary>参考图列表：多人人物替换就是多张（官方 r2v 每份素材只含一个角色）。</summary>
    private readonly System.Collections.Generic.List<string> _refImages = new();

    /// <summary>参考图列表的 UI 行（用 {Binding}，不用 x:Bind）。</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<RefImageRow> _refImageRows = new();

    /// <summary>参考音色（reference_voice）：按顺序对应参考图/参考视频。</summary>
    private readonly System.Collections.Generic.List<string> _refVoices = new();

    /// <summary>音色列表的 UI 行。</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<RefVoiceRow> _refVoiceRows = new();

    /// <summary>人物替换时原视频的时长（秒），用于自动定时长；0 表示还没探测。</summary>
    private double _originalVideoSeconds;

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        // API 平台下拉：与设置页共用同一套预设。
        PlatformCombo.ItemsSource = AppSettings.Platforms.Select(pf => pf.Name).ToList();
        SelectCurrentPlatform();
        ModelCombo.SelectedItem = AppSettings.Model;
        if (ModelCombo.SelectedItem is null) ModelCombo.SelectedIndex = 0;
        ResolutionCombo.SelectedIndex = 0;
        RatioCombo.SelectedIndex = 0;
        DurationCombo.SelectedItem = "5";
        if (DurationCombo.SelectedItem is null) DurationCombo.SelectedIndex = 1;
        // NumberBox 不设 Value 就是 NaN；先落到默认尾部秒数，再开 _ready，避免 NaN 传到锚点里。
        AnchorTailBox.Value = ClipAnchor.DefaultAnchorSeconds;
        // 素材库勾选变化要立刻重算「生成」按钮的可用性（勾了片段就算有参考素材）。
        Bin.SelectionChanged += Bin_SelectionChanged;
        _ready = true;
        ApplyModeUi();
        RefreshReadyState();
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        _mode = tag switch
        {
            "ImageToVideo" => GenerationMode.ImageToVideo,
            "CharacterReplace" => GenerationMode.CharacterReplace,
            "MotionReference" => GenerationMode.MotionReference,
            _ => GenerationMode.TextToVideo
        };
        ApplyModeUi();
        RefreshReadyState();
    }

    private void ApplyModeUi()
    {
        HighlightModeButtons();
        switch (_mode)
        {
            case GenerationMode.TextToVideo:
                ModeHint.Text = "当前：文生视频 — 只需提示词。";
                MediaPanel.Visibility = Visibility.Collapsed;
                break;
            case GenerationMode.ImageToVideo:
                ModeHint.Text = "当前：图生视频 — 添加参考图 + 提示词。";
                MediaPanel.Visibility = Visibility.Visible;
                MediaLabel.Text = "参考图";
                break;
            case GenerationMode.CharacterReplace:
                // 只有「参考图」一个概念：每张图放一个角色，几张就是几个人。
                // 视频素材（要替换的目标片子）从下方素材库里选，不在这一栏。
                ModeHint.Text = "当前：人物替换 — 添加参考图（每张一个角色），要替换的视频从素材库点选。" +
                                "推荐模型 wan2.7-videoedit（专做视频编辑，按原片时长输出、保留原声）。";
                MediaPanel.Visibility = Visibility.Visible;
                MediaLabel.Text = "参考图";
                break;
            case GenerationMode.MotionReference:
                ModeHint.Text = "当前：动作参考 — 添加主体图，动作视频从素材库选。";
                MediaPanel.Visibility = Visibility.Visible;
                MediaLabel.Text = "主体参考图";
                break;
        }
        RefreshMediaLabel();
        ApplyDurationUi();
        // 续接开启时才覆盖上面的四模式原文；关闭时这里什么都不做，原文照旧（AC-M09 取证不受影响）。
        ApplyAnchorModeHint();
    }

    /// <summary>
    /// 人物替换不给选时长：按原视频长度走。
    /// 官方 r2v 的 duration 在带参考视频时只能 2–10s，所以原片超 10s 时单次最多出 10s，
    /// 要覆盖整段得切段逐段替换（下面会把这件事写在提示里，不假装能一次出满）。
    /// </summary>
    private void ApplyDurationUi()
    {
        var auto = _mode == GenerationMode.CharacterReplace;
        DurationCombo.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        DurationAutoPanel.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;
        if (!auto) return;

        var source = FindOriginalVideoPath();
        if (string.IsNullOrEmpty(source))
        {
            DurationAutoText.Text = "按原视频";
            DurationAutoHint.Text = "选好目标视频后自动取它的长度。";
            return;
        }

        var seconds = _originalVideoSeconds;
        if (seconds <= 0)
        {
            DurationAutoText.Text = "按原视频";
            DurationAutoHint.Text = "正在读取原视频时长…";
            return;
        }

        var clamped = Math.Clamp(Math.Round(seconds), 2, 10);
        DurationAutoText.Text = $"按原视频 {seconds:0.#}s";
        DurationAutoHint.Text = seconds > 10
            ? $"单次上限 10s（带参考视频），本次按 {clamped:0}s 生成；要覆盖整段请切段逐段替换。"
            : "时长取自原视频，不需要手选。";
    }

    /// <summary>
    /// 从素材库勾选里取第一个视频。
    /// 人物替换的目标视频、动作参考的动作视频都走这里——视频素材只有一个来源：素材库。
    /// </summary>
    private string? FindOriginalVideoPath()
    {
        static bool IsVideo(string p)
        {
            var ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
            return ext is ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" or ".m4v";
        }

        foreach (var picked in Bin.SelectedItems)
        {
            if (!string.IsNullOrEmpty(picked.LocalPath) && IsVideo(picked.LocalPath)) return picked.LocalPath;
        }
        return null;
    }

    /// <summary>人物替换的实际时长：原视频长度夹到官方窗口；没有原视频时回落 5s。</summary>
    private int ResolveCharacterDuration()
    {
        var seconds = _originalVideoSeconds;
        if (seconds <= 0)
        {
            var src = FindOriginalVideoPath();
            if (!string.IsNullOrEmpty(src)) seconds = ProbeSecondsSafe(src!);
        }
        if (seconds <= 0) return 5;

        // 上限跟模型走：wan3.0 带参考视频可到 30s（输入+输出≤30），wan2.7-r2v 只到 10s。
        var modelId = ModelCombo.SelectedItem as string ?? AppSettings.Model;
        var info = ModelCatalog.Find(modelId);
        var upper = info?.MaxDurationWithVideo ?? 10;
        return (int)Math.Clamp(Math.Round(seconds), 2, Math.Max(2, upper));
    }

    private static double ProbeSecondsSafe(string path)
    {
        try
        {
            var engine = MediaEngines.CreateDefault();
            return engine.ProbeAsync(path).GetAwaiter().GetResult().DurationSeconds;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 续接开启时覆盖 ModeHint，点明本段不吃参考图/素材、以 first_clip 续写。
    /// 只在续接开启时生效，所以四条模式原文在默认态下仍逐字节可取证。
    /// </summary>
    private void ApplyAnchorModeHint()
    {
        if (AnchorToggle.IsChecked != true) return;

        ModeHint.Text = _mode switch
        {
            GenerationMode.ImageToVideo =>
                "当前：图生视频 + 衔接上一段 — 本段不需要参考图，将以首片段（first_clip）续写。",
            GenerationMode.CharacterReplace =>
                "当前：人物替换 + 衔接上一段 — 本段不需要人物参考/目标素材，将以首片段（first_clip）续写。",
            GenerationMode.MotionReference =>
                "当前：动作参考 + 衔接上一段 — 本段不需要动作视频/主体图，将以首片段（first_clip）续写。",
            _ =>
                "当前：文生视频 + 衔接上一段 — 只需提示词，将以首片段（first_clip）续写。"
        };

        // 素材面板保持可用（续接与素材可分属不同批次），只补一句说明免得用户以为选了会生效。
        const string note = "（续接时本批不使用这些素材）";
        if (MediaPanel.Visibility == Visibility.Visible && !MediaLabel.Text.Contains(note))
            MediaLabel.Text += note;
    }

    private void HighlightModeButtons()
    {
        void Style(Button b, bool on)
        {
            if (on)
                b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            else
                b.ClearValue(FrameworkElement.StyleProperty);
        }
        Style(ModeTextBtn, _mode == GenerationMode.TextToVideo);
        Style(ModeImageBtn, _mode == GenerationMode.ImageToVideo);
        Style(ModeCharBtn, _mode == GenerationMode.CharacterReplace);
        Style(ModeMotionBtn, _mode == GenerationMode.MotionReference);
    }

    private void RefreshMediaLabel()
    {
        // 参考图改成列表：多人人物替换就是多张参考图（官方 r2v 每份素材只含一个角色）。
        var modelIdNow = ModelCombo.SelectedItem as string ?? AppSettings.Model;
            var max = ModelCatalog.Find(modelIdNow)?.MaxReferenceImages ?? 5;
        var limitText = max > 0 ? $" / 上限 {max}" : "";
        if (_mode == GenerationMode.ImageToVideo)
        {
            // 图生视频只有一张首帧图，存在 _primaryPath 里，不在 _refImages 列表。
            MediaCountText.Text = string.IsNullOrEmpty(_primaryPath) ? "未选择首帧图" : "首帧图 1 张";
        }
        else
        {
            MediaCountText.Text = _refImages.Count == 0 ? "未选择参考图" : $"参考图 {_refImages.Count} 张{limitText}";
        }
        RefImageList.ItemsSource = null;
        RefImageList.ItemsSource = _refImageRows;
    }

    private void RebuildRefImageRows()
    {
        _refImageRows.Clear();
        for (var i = 0; i < _refImages.Count; i++)
        {
            var path = _refImages[i];
            var row = new RefImageRow
            {
                Index = i,
                Label = $"{i + 1}. {System.IO.Path.GetFileName(path)}"
            };
            try
            {
                row.Thumb = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
            }
            catch
            {
                // 缩略图装饰性
            }
            _refImageRows.Add(row);
        }
    }

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) RefreshReadyState();
    }

    private async void Bin_SelectionChanged(object? sender, EventArgs e)
    {
        if (!_ready) return;
        // 人物替换的原视频可能来自素材库勾选，勾选变化要重算「按原视频」的时长。
        if (_mode == GenerationMode.CharacterReplace) await RefreshOriginalDurationAsync();
        RefreshReadyState();
    }

    private void Any_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) RefreshReadyState();
    }

    private void PickPrimary_Click(object sender, RoutedEventArgs e)
    {
        // 「添加参考图」永远只选图片：视频素材一律从素材库选，不在这一栏。
        var path = PickAsync("image");
        if (path is null) return;

        if (_mode is GenerationMode.CharacterReplace or GenerationMode.MotionReference)
        {
            var max = ModelCatalog.MaxReferenceMaterials(ModelCombo.SelectedItem as string ?? AppSettings.Model);
            if (max > 0 && _refImages.Count >= max)
            {
                StatusBar.Severity = InfoBarSeverity.Warning;
                StatusBar.Title = "参考图已满";
                StatusBar.Message = $"当前模型最多 {max} 份参考素材（图像 + 视频合计）。";
                StatusBar.IsOpen = true;
                return;
            }
            if (!_refImages.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            {
                _refImages.Add(path);
                RebuildRefImageRows();
            }
        }
        else
        {
            // 图生视频：只要一张首帧图。
            _primaryPath = path;
        }

        RefreshMediaLabel();
        RefreshReadyState();
    }

    /// <summary>
    /// 添加参考音色（reference_voice）。官方：wav/mp3、1–10s、≤15MB；
    /// 只参考音色不看内容；与带音轨的 reference_video 同时存在时优先用它。
    /// </summary>
    private async void PickRefVoice_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 同样走 Win32 对话框：WinRT FileOpenPicker 在未打包 WinUI 3 里会直接 E_FAIL。
            var picked = await Win32FilePicker.PickFilesAsync(
                PickerHost.BringToFrontAndGetHandle(), "选择参考音频",
                new[] { "wav", "mp3" });
            if (picked.Count == 0) return;

            var info = new System.IO.FileInfo(picked[0]);
            if (info.Length > 15L * 1024 * 1024)
            {
                RefVoiceHint.Text = $"音频过大（{info.Length / 1024.0 / 1024.0:0.#}MB），官方上限 15MB。";
                return;
            }

            var duration = 0.0;
            try
            {
                var engine = MediaEngines.CreateDefault();
                var probe = await engine.ProbeAsync(picked[0]);
                duration = probe.DurationSeconds;
            }
            catch
            {
                // 探测失败不拦，交给服务端；下面只做能做的校验
            }

            if (duration > 0 && (duration < 1.0 || duration > 10.0))
            {
                RefVoiceHint.Text = $"音频时长 {duration:0.#}s 超出官方窗口（1–10s）。";
                return;
            }

            if (_refVoices.Exists(p => string.Equals(p, picked[0], StringComparison.OrdinalIgnoreCase)))
            {
                RefVoiceHint.Text = "这个音频已经在列表里了。";
                return;
            }

            _refVoices.Add(picked[0]);
            RebuildRefVoiceRows();
            var slots = _refImages.Count + CountSelectedVideos();
            var isWan30 = (ModelCombo.SelectedItem as string ?? AppSettings.Model)
                .StartsWith("wan3.0", StringComparison.OrdinalIgnoreCase);
            if (isWan30)
            {
                // wan3.0 的音频是独立参考（提示词里叫「音频1」），不和图/视频配对。
                RefVoiceHint.Text = _refVoices.Count > 5
                    ? $"音色最多 5 段，当前 {_refVoices.Count} 段。"
                    : $"已添加 {_refVoices.Count} 个音色（wan3.0 里是独立的「音频N」参考）。";
            }
            else
            {
                RefVoiceHint.Text = _refVoices.Count > slots
                    ? $"已添加 {_refVoices.Count} 个音色，但当前参考素材只有 {slots} 份：音色要按顺序对应到参考图/参考视频上。"
                    : $"已添加 {_refVoices.Count} 个音色（按顺序对应参考素材）。";
            }
            RefreshReadyState();
        }
        catch (Exception ex)
        {
            RefVoiceHint.Text = $"添加音色失败：{ex.Message}";
        }
    }

    private void RemoveRefVoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int idx) return;
        if (idx < 0 || idx >= _refVoices.Count) return;
        _refVoices.RemoveAt(idx);
        RebuildRefVoiceRows();
        RefreshReadyState();
    }

    private void ClearRefVoice_Click(object sender, RoutedEventArgs e)
    {
        _refVoices.Clear();
        RebuildRefVoiceRows();
        RefVoiceHint.Text = "已清空音色。";
        RefreshReadyState();
    }

    private void RebuildRefVoiceRows()
    {
        _refVoiceRows.Clear();
        for (var i = 0; i < _refVoices.Count; i++)
        {
            _refVoiceRows.Add(new RefVoiceRow
            {
                Index = i,
                Label = $"{i + 1}. {System.IO.Path.GetFileName(_refVoices[i])}"
            });
        }
        RefVoiceList.ItemsSource = null;
        RefVoiceList.ItemsSource = _refVoiceRows;
    }

    /// <summary>素材库里勾选的视频条数（音色配对槽位要用）。</summary>
    private int CountSelectedVideos()
    {
        var n = 0;
        foreach (var picked in Bin.SelectedItems)
        {
            if (string.IsNullOrEmpty(picked.LocalPath)) continue;
            var ext = System.IO.Path.GetExtension(picked.LocalPath).ToLowerInvariant();
            if (ext is ".mp4" or ".mov" or ".webm") n++;
        }
        return n;
    }

    private void RemoveRefImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int idx) return;
        if (idx < 0 || idx >= _refImages.Count) return;
        _refImages.RemoveAt(idx);
        RebuildRefImageRows();
        RefreshMediaLabel();
        RefreshReadyState();
    }

    /// <summary>
    /// 选参考视频 → 自动切成每段 ≤30s（官方 reference_video 上限）→ 切好的片段进统一素材库。
    /// 这是本地剪辑，不是 AI 生成。
    /// </summary>


    /// <summary>探测原视频时长，并刷新「按原视频」那块提示。</summary>
    private async System.Threading.Tasks.Task RefreshOriginalDurationAsync()
    {
        var src = FindOriginalVideoPath();
        if (string.IsNullOrEmpty(src))
        {
            _originalVideoSeconds = 0;
            ApplyDurationUi();
            return;
        }

        try
        {
            var engine = MediaEngines.CreateDefault();
            var info = await engine.ProbeAsync(src!);
            _originalVideoSeconds = info.DurationSeconds;
        }
        catch
        {
            _originalVideoSeconds = 0;
        }
        ApplyDurationUi();
    }

    private void ClearMedia_Click(object sender, RoutedEventArgs e)
    {
        _primaryPath = null;
        _refImages.Clear();
        RebuildRefImageRows();
        RefreshMediaLabel();
        RefreshReadyState();
    }

    private string? PickAsync(string kind)
    {
        // Win32 对话框：WinRT FileOpenPicker 在未打包 WinUI 3 里会直接 E_FAIL（0x80004005）。
        var extensions = kind switch
        {
            "video" => new[] { "mp4", "mov", "webm", "mkv", "avi", "m4v" },
            "any" => new[] { "png", "jpg", "jpeg", "webp", "mp4", "mov" },
            _ => new[] { "png", "jpg", "jpeg", "webp" }
        };

        var picked = Win32FilePicker.PickFiles(
            PickerHost.BringToFrontAndGetHandle(),
            kind == "video" ? "选择视频" : "选择图片",
            extensions);

        return picked.Count > 0 ? picked[0] : null;
    }

    /// <summary>按当前 Endpoint 反查平台下拉选中项（自定义地址就不选中）。</summary>
    private void SelectCurrentPlatform()
    {
        if (_platformSyncing) return;
        _platformSyncing = true;
        try
        {
            var cur = AppSettings.CurrentPlatform;
            PlatformCombo.SelectedIndex = cur is null
                ? -1
                : AppSettings.Platforms.ToList().FindIndex(p => p.Name == cur.Name);
            RefreshPlatformHint();
        }
        finally
        {
            _platformSyncing = false;
        }
    }

    private void RefreshPlatformHint()
    {
        var cur = AppSettings.CurrentPlatform;
        PlatformHint.Text = AppSettings.EndpointNeedsWorkspaceId
            ? "地址里还留着 <你的工作空间ID>，到「设置」把它换成你自己的那段。"
            : cur is null
                ? $"自定义地址：{AppSettings.Endpoint}"
                : cur.Note;
    }

    /// <summary>
    /// 首页直接切平台：写回 Endpoint，后续提交立刻走新域名。
    /// 用标志位防止「赋值 → 触发事件 → 又赋值」的回环。
    /// </summary>
    private void PlatformCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_platformSyncing) return;
        if (PlatformCombo.SelectedItem is not string name) return;

        var pf = AppSettings.Platforms.FirstOrDefault(p => p.Name == name);
        if (pf is null) return;

        // 同上：已有真实百炼域名就别用模板覆盖。
        var alreadyRealBailian =
            pf.Endpoint.Contains('<') &&
            AppSettings.Endpoint.Contains("maas.aliyuncs.com", StringComparison.OrdinalIgnoreCase);
        var target = alreadyRealBailian ? AppSettings.Endpoint : pf.Endpoint;

        _platformSyncing = true;
        try
        {
            AppSettings.Endpoint = target;
        }
        catch (SettingsWriteException ex)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = "平台没切成功";
            StatusBar.Message = $"地址写不进设置文件：{ex.Message}";
            StatusBar.IsOpen = true;
        }
        finally
        {
            _platformSyncing = false;
        }

        RefreshPlatformHint();
        RefreshReadyState();
    }

    private void RefreshReadyState()
    {
        string? key = null;
        try { key = ApiKeyStore.TryGet(AppSettings.KeyProfile); } catch { }
        var hasKey = !string.IsNullOrEmpty(key);
        var hasPrompt = !string.IsNullOrWhiteSpace(PromptBox.Text);

        if (!hasKey)
        {
            ReadyHint.Text = "未就绪：请先到「设置」保存 API Key。";
            GenerateButton.IsEnabled = false;
            return;
        }
        if (!hasPrompt)
        {
            ReadyHint.Text = "未就绪：请填写提示词。";
            GenerateButton.IsEnabled = false;
            return;
        }

        // 续接优先判定：勾了「衔接上一段」就走 first_clip 续接，本段不吃首帧图/参考素材，
        // 所以四模式的素材校验必须跳过（缺 Key / 缺提示词这两个基础校验上面已经拦过了）。
        var anchorOn = AnchorToggle.IsChecked == true;
        var anchorPick = anchorOn ? PickAnchorSource() : null;

        if (anchorOn && anchorPick is null)
        {
            // 勾了续接但拿不到有效来源：只报续接的原因，不回落到素材校验，
            // 否则用户会被要求去选一张续接根本不用的图。
            var usable = UsableAnchorSources();
            if (usable.Count == 0)
            {
                ReadyHint.Text = "未就绪：已开启衔接上一段，但视频池里还没有可续接的生成结果，先独立生成一段。";
            }
            else
            {
                // 选中的来源可能已被清理或换了身份，按原因拦住而不是让 runner 提交时炸。
                var why = (AnchorSourceCombo.SelectedItem as VideoPoolItem)?.ContinueBlockReason;
                ReadyHint.Text = "未就绪：" + (string.IsNullOrWhiteSpace(why) ? "锚点来源文件不存在，可能已被清理。" : why);
            }
            GenerateButton.IsEnabled = false;
            return;
        }

        if (anchorPick is not null)
        {
            // 勾了续接且有有效来源：本段以 first_clip 续写，不需要首帧图，跳过下面的四模式素材校验。
            ReadyHint.Text = $"就绪：当前模式「{ModeLabel(_mode)}」，将衔接「{anchorPick.Title}」尾部 {TailSeconds:0.#}s 后提交任务。";
            GenerateButton.IsEnabled = true;
            return;
        }

        // 未开启续接：保持原来的四模式素材校验，文案一个字都不改（AC-M02/04/06 依赖它）。
        // 视频素材只能进参考生视频（wan2.7-r2v）。
        // 用 wan3.0-video / wan2.7-i2v 时把 mp4 当首帧图发出去，服务端会回
        // 「Error validating image, can not read image from ...mp4」——提前挡住并指路。
        var currentModel = ModelCombo.SelectedItem as string ?? AppSettings.Model;
        var hasVideoMaterial = FindOriginalVideoPath() is not null;

        if (hasVideoMaterial)
        {
            // 动作参考必须走「参考视频」（wan3.0）；人物替换两种路子都行
            // （wan2.7-videoedit 的视频编辑，或 wan3.0 的 reference_image+reference_video）。
            var needsRefVideo = _mode == GenerationMode.MotionReference;
            var capable = needsRefVideo
                ? ModelCatalog.SupportsReference(currentModel)
                : ModelCatalog.SupportsCharacterReplace(currentModel);

            if (!capable)
            {
                ReadyHint.Text = $"未就绪：{currentModel} 用不了视频素材。" + (needsRefVideo
                    ? "动作参考请把「模型」换成 wan3.0-video。"
                    : "人物替换请换成 wan2.7-videoedit 或 wan3.0-video。");
                GenerateButton.IsEnabled = false;
                return;
            }
        }

        // 模式与模型能力要对得上，否则提交时才会报错，用户白等。
        var mi = ModelCatalog.Find(currentModel);
        if (_mode == GenerationMode.TextToVideo && mi is { TextToVideo: false })
        {
            ReadyHint.Text = $"未就绪：{currentModel} 不支持文生视频。请换成 wan3.0-video 系列。";
            GenerateButton.IsEnabled = false;
            return;
        }
        if (_mode == GenerationMode.ImageToVideo && mi is { ImageToVideo: false })
        {
            ReadyHint.Text = $"未就绪：{currentModel} 不支持图生视频。请换成 wan3.0-video 或 wan2.7-i2v。";
            GenerateButton.IsEnabled = false;
            return;
        }

        switch (_mode)
        {
            case GenerationMode.ImageToVideo:
                if (string.IsNullOrEmpty(_primaryPath))
                {
                    ReadyHint.Text = "未就绪：图生视频需要选择参考图。";
                    GenerateButton.IsEnabled = false;
                    return;
                }
                break;
            case GenerationMode.CharacterReplace:
                // 两块分开判：缺哪块就明确说哪块，别让用户猜。
                if (_refImages.Count == 0)
                {
                    ReadyHint.Text = "未就绪：人物替换需要先「添加参考图」，每张图放一个角色。";
                    GenerateButton.IsEnabled = false;
                    return;
                }
                if (FindOriginalVideoPath() is null)
                {
                    ReadyHint.Text = "未就绪：人物替换需要目标视频。在下方素材库里点选一段（原视频或裁切片段）。";
                    GenerateButton.IsEnabled = false;
                    return;
                }
                break;
            case GenerationMode.MotionReference:
                if (_refImages.Count == 0)
                {
                    ReadyHint.Text = "未就绪：动作参考需要先「添加参考图」放主体。";
                    GenerateButton.IsEnabled = false;
                    return;
                }
                if (FindOriginalVideoPath() is null)
                {
                    ReadyHint.Text = "未就绪：动作参考需要动作视频。在下方素材库里点选一段。";
                    GenerateButton.IsEnabled = false;
                    return;
                }
                break;
        }

        ReadyHint.Text = $"就绪：当前模式「{ModeLabel(_mode)}」，点击生成将提交任务。";
        GenerateButton.IsEnabled = true;
    }

    // ---------------------------------------------------------------- 续接（内嵌到生成里）
    // 模型单次生成上限 15s，长片子只能一段段接：勾上「衔接上一段」，
    // 就从已生成视频池里挑一段，取它尾部 2–10s 当 first_clip 接着写。
    // 裁尾段 + 上传换 oss:// 由 VideoJobRunner 做，页面只负责把 ClipAnchorSpec 挂到任务上。

    private void AnchorToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var on = AnchorToggle.IsChecked == true;
        AnchorPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) SyncAnchorSources();
        else AnchorHint.Text = "";
        // 模式提示跟着续接状态走：ApplyModeUi 先写四模式原文，续接开着时末尾再覆盖成续接版；
        // 取消勾选时同一路径自动恢复原文。
        ApplyModeUi();
        RefreshReadyState();
    }

    private void AnchorSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateAnchorHint();
        RefreshReadyState();
    }

    private void AnchorTail_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready) return;
        // NumberBox 没设过值就是 NaN，先兜默认值再夹到 first_clip 窗口。
        var raw = double.IsNaN(AnchorTailBox.Value) ? ClipAnchor.DefaultAnchorSeconds : AnchorTailBox.Value;
        var v = Math.Clamp(raw, ClipAnchor.MinAnchorSeconds, ClipAnchor.MaxAnchorSeconds);
        // 回写会再触发一次 ValueChanged；第二次值已相等，自然收敛，不会递归。
        // NaN 时 Math.Abs(NaN - v) 仍是 NaN，比较恒为 false，所以要把 NaN 单独判一次。
        if (double.IsNaN(AnchorTailBox.Value) || Math.Abs(AnchorTailBox.Value - v) > 0.0001)
            AnchorTailBox.Value = v;
        UpdateAnchorHint();
        RefreshReadyState();
    }

    /// <summary>把池里「能续接」的片段填进下拉，默认选最近一条（池是 Insert(0)，第一条即最新）。</summary>
    private void SyncAnchorSources()
    {
        var usable = UsableAnchorSources();
        var previous = (AnchorSourceCombo.SelectedItem as VideoPoolItem)?.Id;

        AnchorSourceCombo.ItemsSource = usable;
        var pick = usable.FirstOrDefault(i => i.Id == previous) ?? usable.FirstOrDefault();
        AnchorSourceCombo.SelectedItem = pick;
        UpdateAnchorHint();
    }

    /// <summary>
    /// 池里可续接的条目：CanContinue（有公网 URL，本地导入的不算）且本地文件还在。
    /// 顺序沿用池的顺序，所以第一条就是最近生成的那一段。
    /// </summary>
    private static List<VideoPoolItem> UsableAnchorSources()
        => VideoPool.Items.Where(i => i.CanContinue && File.Exists(i.LocalPath)).ToList();

    /// <summary>当前要取的尾部秒数，夹在 first_clip 允许的 2–10s。</summary>
    private double TailSeconds
    {
        get
        {
            var raw = double.IsNaN(AnchorTailBox.Value) ? ClipAnchor.DefaultAnchorSeconds : AnchorTailBox.Value;
            return Math.Clamp(raw, ClipAnchor.MinAnchorSeconds, ClipAnchor.MaxAnchorSeconds);
        }
    }

    /// <summary>锚点提示：可用时写清「谁的尾巴、几秒」，不可用时写清原因。</summary>
    private void UpdateAnchorHint()
    {
        if (AnchorToggle.IsChecked != true)
        {
            AnchorHint.Text = "";
            return;
        }

        var usable = UsableAnchorSources();
        if (usable.Count == 0)
        {
            // 池里有片段但都不能续接时，把第一条的阻断原因写出来（例如本地导入没有公网 URL）。
            var any = VideoPool.Items.FirstOrDefault();
            AnchorHint.Text = any is null
                ? "视频池是空的：先生成一段，再把它的尾巴接到下一段上。"
                : any.ContinueBlockReason;
            return;
        }

        var pick = AnchorSourceCombo.SelectedItem as VideoPoolItem ?? usable[0];
        AnchorHint.Text = $"将把「{pick.Title}」的尾部 {TailSeconds:0.#} 秒作为 first_clip 提交；"
                        + "模型单次上限 15s，长片子靠这样一段段接。";
    }

    /// <summary>
    /// 当前选中的可续接来源；拿不到有效来源时返回 null。
    /// 没选时回落到池里第一条（池是 Insert(0)，第一条即最新生成的那段）。
    /// </summary>
    private VideoPoolItem? PickAnchorSource()
    {
        var usable = UsableAnchorSources();
        if (usable.Count == 0) return null;

        var pick = AnchorSourceCombo.SelectedItem as VideoPoolItem ?? usable[0];
        if (!pick.CanContinue || !File.Exists(pick.LocalPath)) return null;
        return pick;
    }

    /// <summary>
    /// 构建本段的续接锚点：只有「勾了续接 + 池里确实有可用来源」才返回非空。
    /// 返回非空时 runner 走 first_clip 续接分支（先裁尾段再上传），不看 Mode。
    /// </summary>
    private ClipAnchorSpec? BuildAnchor()
    {
        if (AnchorToggle.IsChecked != true) return null;

        var pick = PickAnchorSource();
        if (pick is null) return null;

        return new ClipAnchorSpec(
            SourceLocalPath: pick.LocalPath,
            SourcePoolItemId: pick.Id,
            TailSeconds: TailSeconds,
            SourceDurationSeconds: 0);   // runner 会用媒体引擎探测真实时长
    }

    private static string ModeLabel(GenerationMode m) => m switch
    {
        GenerationMode.ImageToVideo => "图生视频",
        GenerationMode.CharacterReplace => "人物替换",
        GenerationMode.MotionReference => "动作参考",
        _ => "文生视频"
    };

    private void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshReadyState();
            if (!GenerateButton.IsEnabled) return;

            var duration = 5;
            if (_mode == GenerationMode.CharacterReplace)
            {
                // 人物替换不让选时长，按原视频长度来（夹到官方 2–10s 窗口）。
                duration = ResolveCharacterDuration();
            }
            else if (DurationCombo.SelectedItem is string ds && int.TryParse(ds, out var d))
            {
                // 上限跟模型走：wan3.0 支持 2–30s，wan2.7 系列到 15s。
                var mi = ModelCatalog.Find(ModelCombo.SelectedItem as string ?? AppSettings.Model);
                var upper = mi?.MaxDurationWithoutVideo ?? 15;
                duration = Math.Clamp(d, 2, Math.Max(2, upper));
            }

            // 续接：勾了就用池里那段的尾巴当 first_clip，此时不带首帧图（runner 靠 Anchor 是否为空分流）。
            var anchor = BuildAnchor();

            var media = new List<string>();
            if (anchor is null)
            {
                // 独立生成才带素材；续接分支不需要首帧图。
                // 图/主体图来自「添加参考图」，视频一律来自素材库勾选。
                if (!string.IsNullOrEmpty(_primaryPath)) media.Add(_primaryPath);
                var pickedVideo = FindOriginalVideoPath();
                if (!string.IsNullOrEmpty(pickedVideo) &&
                    !string.Equals(pickedVideo, _primaryPath, StringComparison.OrdinalIgnoreCase))
                {
                    media.Add(pickedVideo!);
                }
            }

            var model = ModelCombo.SelectedItem as string ?? AppSettings.Model;

            // 参考生视频（wan2.7-r2v）：人物替换走多张参考图 + 素材库里勾选的参考视频。
            // 「多人」= 多张 reference_image；官方合计上限 5 份。
            var refImages = new List<string>();
            var refVideos = new List<string>();
            var refVoices = new List<string>();
            // 注意：这里必须用 SupportsCharacterReplace 而不是 SupportsReference——
            // wan2.7-videoedit 走的是「视频编辑」，ReferenceToVideo=false，
            // 用旧判据会导致参考图和素材库视频根本没被收集，提交时无素材可用。
            if (anchor is null && ModelCatalog.SupportsCharacterReplace(model))
            {
                // 两个模式的图片都走 _refImages（「添加参考图」按钮存的就是它）。
                // 之前动作参考把 _primaryPath（图片）塞进 refVideos，
                // 服务端会按 reference_video 去解一张 png，必然失败。
                if (_mode is GenerationMode.CharacterReplace or GenerationMode.MotionReference)
                {
                    refImages.AddRange(_refImages);
                }

                // 素材库里勾选的剪切片段，按类型并入参考素材。
                foreach (var picked in Bin.SelectedItems)
                {
                    if (string.IsNullOrEmpty(picked.LocalPath)) continue;
                    var ext = System.IO.Path.GetExtension(picked.LocalPath).ToLowerInvariant();
                    if (ext is ".mp4" or ".mov" or ".webm") refVideos.Add(picked.LocalPath);
                    else refImages.Add(picked.LocalPath);
                }

                // 音色按顺序配对到参考素材（图像在前、视频在后，与提交时的 media 顺序一致）。
                refVoices.AddRange(_refVoices);
            }

            var item = new VideoTaskItem
            {
                Mode = _mode,          // 续接不改 Mode，runner 靠 Anchor != null 判断
                Prompt = PromptBox.Text.Trim(),
                Model = model,
                Resolution = ResolutionCombo.SelectedItem as string ?? "480P",
                Ratio = RatioCombo.SelectedItem as string ?? "adaptive",
                Duration = duration,
                MediaPaths = media,
                ReferenceImages = refImages,
                ReferenceVideos = refVideos,
                ReferenceVoices = refVoices,
                Anchor = anchor
            };
            VideoTaskStore.Add(item);
            AppSettings.Model = item.Model;
            _ = VideoJobRunner.RunAsync(item, DispatcherQueue);

            StatusBar.Severity = InfoBarSeverity.Informational;
            StatusBar.Title = "已提交";
            StatusBar.Message = "任务已创建，正在跳转到「任务」。";
            StatusBar.IsOpen = true;
            Frame.Navigate(typeof(TasksPage), item.Id);
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "错误";
            StatusBar.Message = ex.Message;
            StatusBar.IsOpen = true;
        }
    }
}

/// <summary>
/// 参考图列表的一行。刻意用 {Binding}（不用 x:Bind）：本项目里同页多个
/// x:Bind DataTemplate 会把 XAML 编译器打崩（WMC9999 / WMC0610）。
/// </summary>
public sealed class RefImageRow : System.ComponentModel.INotifyPropertyChanged
{
    private Microsoft.UI.Xaml.Media.ImageSource? _thumb;

    /// <summary>在 _refImages 里的下标，删除时用。</summary>
    public int Index { get; init; }

    public string Label { get; init; } = "";

    public Microsoft.UI.Xaml.Media.ImageSource? Thumb
    {
        get => _thumb;
        set
        {
            if (ReferenceEquals(_thumb, value)) return;
            _thumb = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumb)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 参考音色列表的一行。同样用 {Binding}，避开 x:Bind DataTemplate 的编译器问题。
/// </summary>
public sealed class RefVoiceRow
{
    /// <summary>在 _refVoices 里的下标，删除时用。</summary>
    public int Index { get; init; }

    public string Label { get; init; } = "";
}
