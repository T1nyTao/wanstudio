using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using WanStudio.Services;
using WanStudio.Services.Media;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace WanStudio.Pages;

/// <summary>
/// Long video: import → split (fixed seconds / shot cut) → preview &amp; trim → checkbox select →
/// generate selected → pool → official first_clip continuation. Local stitch is optional and off by default.
/// No generation API is called before the user confirms a split and checks segments.
/// </summary>
public sealed partial class LongVideoPage : Page
{
    private readonly IMediaEngine _engine;
    private readonly ObservableCollection<PoolRow> _poolRows = new();
    private LongVideoSegment? _previewSegment;
    private PoolRow? _firstClipRow;
    private bool _ready;
    private string? _pendingImportPath;

    public LongVideoPage()
    {
        InitializeComponent();
        _engine = MediaEngines.CreateDefault();

        SegmentList.ItemsSource = LongVideoWorkspace.Segments;
        PoolList.ItemsSource = _poolRows;

        Loaded += LongVideoPage_Loaded;
        Unloaded += LongVideoPage_Unloaded;
    }

    private static string CacheDir
    {
        get
        {
            // 用统一的可写目录解析：首选位置写不进去会自动回退，不再整页不可用。
            return AppPaths.CacheDir;
        }
    }

    private void LongVideoPage_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = false;

        EngineText.Text = _engine.Name == "ffmpeg"
            ? $"切段引擎：ffmpeg"
            : _engine.SupportsCut
                ? $"切段引擎：{_engine.Name}"
                : "切段引擎：内置（不支持本地切段）";
        ToolTipService.SetToolTip(EngineText, _engine.Detail);

        SplitModeCombo.SelectedIndex = LongVideoWorkspace.SplitMode == "scene" ? 1 : 0;
        SegmentSecondsBox.Value = LongVideoWorkspace.FixedSeconds;
        BatchPromptBox.Text = LongVideoWorkspace.BatchPrompt;

        // 续接两条路都算能续：wan2.7-i2v 的 first_clip，和 wan3.0 的 reference_video 延长。
        ContinueModelCombo.ItemsSource = ModelCatalog.All
            .Where(m => m.FirstClipContinue || m.Id.StartsWith("wan3.0", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .ToList();
        ContinueModelCombo.SelectedIndex = ContinueModelCombo.Items.Count > 0 ? 0 : -1;
        ContinueDurationBox.Value = 5.0;

        StitchToggle.IsOn = LongVideoWorkspace.StitchEnabled;

        AnchorToggle.IsChecked = LongVideoWorkspace.AnchorEnabled;
        AnchorPanel.Visibility = LongVideoWorkspace.AnchorEnabled ? Visibility.Visible : Visibility.Collapsed;
        AnchorTailBox.Value = Math.Clamp(LongVideoWorkspace.AnchorTailSeconds, ClipAnchor.MinAnchorSeconds, ClipAnchor.MaxAnchorSeconds);
        AnchorHint.Text = "续接会把上一段生成结果的尾巴当作首片段（first_clip）提交；模型单次上限 15s，长视频靠这样一段段接。";

        VideoPool.Items.CollectionChanged += Pool_CollectionChanged;
        foreach (var s in LongVideoWorkspace.Segments)
        {
            s.SelectionChanged -= Segment_SelectionChanged;
            s.SelectionChanged += Segment_SelectionChanged;
        }

        _ready = true;

        UpdateSourceCard();
        SplitMode_SelectionChanged(this, null!);
        SyncPool();
        if (LongVideoWorkspace.StitchEnabled) ApplyStitchVisibility();
        RefreshGenerateState();
        RefreshContinueState();
        RefreshStitchState();
        UpdateSegmentHeader();

        if (LongVideoWorkspace.Segments.Count > 0)
        {
            SplitResult.Text = $"已恢复上次切段结果：{LongVideoWorkspace.Segments.Count} 段（本页累计提交生成 {LongVideoWorkspace.ApiSubmitCount} 次）。";
            SegmentEmpty.Visibility = Visibility.Collapsed;
            _ = LoadSegmentThumbsAsync();
        }

        if (!string.IsNullOrEmpty(_pendingImportPath))
        {
            var path = _pendingImportPath;
            _pendingImportPath = null;
            _ = LoadSourceAsync(path, autoSplit: true);
        }
    }

    private void LongVideoPage_Unloaded(object sender, RoutedEventArgs e)
    {
        VideoPool.Items.CollectionChanged -= Pool_CollectionChanged;
        // 分段集合是静态的、比页面活得久：不解除订阅，旧页面会一直被它持有并被回调。
        foreach (var s in LongVideoWorkspace.Segments) s.SelectionChanged -= Segment_SelectionChanged;
        try { SegmentPlayer.MediaPlayer?.Pause(); } catch { /* player optional */ }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            _pendingImportPath = path;
        }
    }

    private void Pool_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(SyncPool);
            return;
        }
        SyncPool();
    }

    // ---------------------------------------------------------------- import / split

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Win32 对话框：WinRT FileOpenPicker 在未打包 WinUI 3 应用里会直接 E_FAIL。
            var picked = await Win32FilePicker.PickFilesAsync(
                PickerHost.BringToFrontAndGetHandle(),
                "选择要导入的长视频",
                new[] { "mp4", "mov", "webm", "mkv", "avi", "m4v" });
            if (picked.Count == 0) return;

            await LoadSourceAsync(picked[0], autoSplit: false);
        }
        catch (System.Runtime.InteropServices.COMException com)
        {
            ShowInfo("导入失败", $"HRESULT=0x{com.HResult:X8} {com.Message}", InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowInfo("导入失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>读 NumberBox：用户清空输入时 Value 是 NaN，NaN 过不了 &lt;=0 判断，必须单独处理。</summary>
    private static double ReadNumber(NumberBox box, double fallback)
        => double.IsNaN(box.Value) || double.IsInfinity(box.Value) ? fallback : box.Value;

    private async Task LoadSourceAsync(string path, bool autoSplit)
    {
        MediaInfo info;
        try
        {
            if (!File.Exists(path))
            {
                ShowInfo("找不到文件", path, InfoBarSeverity.Error);
                return;
            }

            info = await _engine.ProbeAsync(path);
        }
        catch (Exception ex)
        {
            // 此前 Probe 抛出的异常在 Loaded 的 fire-and-forget 路径里无人观察，导入静默失败。
            AppPaths.AppendCrashLog("[LongVideo import] " + ex);
            ShowInfo("无法读取视频", ex.Message, InfoBarSeverity.Error);
            return;
        }

        if (info.DurationSeconds < 1)
        {
            ShowInfo("无法读取视频", "时长小于 1 秒或文件损坏，换一个文件试试。", InfoBarSeverity.Error);
            return;
        }

        LongVideoWorkspace.Reset();
        LongVideoWorkspace.SourcePath = path;
        LongVideoWorkspace.SourceInfo = info;
        LongVideoWorkspace.BatchPrompt = BatchPromptBox.Text;

        // Reset() 清掉了续接/拼接开关的底层状态，界面控件要同步，否则显示与实际不一致。
        _ready = false;
        AnchorToggle.IsChecked = false;
        AnchorPanel.Visibility = Visibility.Collapsed;
        StitchToggle.IsOn = false;
        _ready = true;

        SegmentEmpty.Visibility = Visibility.Visible;
        SegmentList.ItemsSource = LongVideoWorkspace.Segments;
        PreviewCard.Visibility = Visibility.Collapsed;
        _previewSegment = null;
        _firstClipRow = null;
        SplitResult.Text = "";
        UpdateSourceCard();
        UpdateSegmentHeader();
        SplitMode_SelectionChanged(this, null);   // 导入后才有源，刷新「切段」按钮可用性（此前一直灰着）
        RefreshGenerateState();
        RefreshContinueState();
        RefreshStitchState();

        ShowInfo("已导入", $"{Path.GetFileName(path)} · {LongVideoSegment.FormatTime(info.DurationSeconds)} · {info.Width}×{info.Height}。先切段，再勾选生成。",
            InfoBarSeverity.Success);

        if (autoSplit) Split_Click(this, null!);
    }

    private void UpdateSourceCard()
    {
        if (string.IsNullOrEmpty(LongVideoWorkspace.SourcePath) || LongVideoWorkspace.SourceInfo is null)
        {
            SourceName.Text = "尚未导入视频";
            SourceMeta.Text = "支持 mp4 / mov / webm";
            return;
        }

        var info = LongVideoWorkspace.SourceInfo;
        SourceName.Text = Path.GetFileName(LongVideoWorkspace.SourcePath);
        SourceMeta.Text = $"{LongVideoSegment.FormatTime(info.DurationSeconds)} · {info.Width}×{info.Height}" +
                          (info.HasAudio ? " · 含音轨" : " · 无音轨");
    }

    private void SplitMode_SelectionChanged(object sender, SelectionChangedEventArgs? e)
    {
        if (!_ready) return;
        var scene = SplitModeCombo.SelectedIndex == 1;
        SegmentSecondsBox.IsEnabled = !scene;
        LongVideoWorkspace.SplitMode = scene ? "scene" : "fixed";

        if (scene)
        {
            SplitHint.Text = _engine.SupportsSceneDetect
                ? "按画面硬切点分段；过长的镜头会自动再切，保证每段 ≤15s。"
                : "当前引擎不支持切镜检测（缺 ffmpeg），切镜模式不可用；可用固定秒数。";
            SplitButton.IsEnabled = _engine.SupportsSceneDetect && LongVideoWorkspace.SourcePath is not null;
        }
        else
        {
            SplitHint.Text = "从 0 秒起按固定秒数切，末段不足 1 秒会并入前一段。";
            SplitButton.IsEnabled = LongVideoWorkspace.SourcePath is not null;
        }
    }

    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        var source = LongVideoWorkspace.SourcePath;
        var info = LongVideoWorkspace.SourceInfo;
        if (source is null || info is null)
        {
            ShowInfo("还没导入", "先导入一个长视频再切段。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            SplitButton.IsEnabled = false;
            var sw = Stopwatch.StartNew();
            IReadOnlyList<SegmentSpec> plan;
            string modeNote;

            var scene = SplitModeCombo.SelectedIndex == 1;
            if (scene)
            {
                if (!_engine.SupportsSceneDetect)
                {
                    ShowInfo("切镜不可用", "当前切段引擎不支持场景检测（缺 ffmpeg）。", InfoBarSeverity.Error);
                    return;
                }

                var cuts = await _engine.DetectSceneCutsAsync(source, MediaEngineDefaults.DefaultSceneThreshold);
                plan = SegmentPlanner.PlanFromSceneCuts(info.DurationSeconds, cuts);
                modeNote = $"切镜检测到 {cuts.Count} 个切点";
            }
            else
            {
                var raw = ReadNumber(SegmentSecondsBox, 5);
                var seconds = Math.Clamp(raw <= 0 ? 5 : raw, 1, 15);
                SegmentSecondsBox.Value = seconds;
                LongVideoWorkspace.FixedSeconds = seconds;
                plan = SegmentPlanner.PlanFixed(info.DurationSeconds, seconds);
                modeNote = $"固定 {seconds:0.#} 秒";
            }

            LongVideoWorkspace.Segments.Clear();
            var index = 1;
            foreach (var spec in plan)
            {
                var seg = new LongVideoSegment
                {
                    Index = index++,
                    StartSeconds = spec.StartSeconds,
                    DurationSeconds = spec.DurationSeconds
                };
                seg.SelectionChanged += Segment_SelectionChanged;
                LongVideoWorkspace.Segments.Add(seg);
            }

            SegmentEmpty.Visibility = LongVideoWorkspace.Segments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _previewSegment = null;
            PreviewCard.Visibility = Visibility.Collapsed;
            UpdateSegmentHeader();
            RefreshGenerateState();

            SplitResult.Text = plan.Count == 0
                ? "没有切出任何段，检查视频时长与切段参数。"
                : $"{modeNote}，切出 {plan.Count} 段（{sw.ElapsedMilliseconds} ms，本地计算，未提交任何生成请求）。";

            ShowInfo("切段完成",
                plan.Count == 0
                    ? "没有可用分段。"
                    : $"共 {plan.Count} 段。勾选要生成的段，再点「生成所选」；不勾选不会提交。",
                InfoBarSeverity.Success);

            _ = LoadSegmentThumbsAsync();
        }
        catch (Exception ex)
        {
            ShowInfo("切段失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SplitButton.IsEnabled = true;
        }
    }

    /// <summary>路径 + 大小 + 修改时间的稳定短哈希（跨进程一致，用作缓存文件名）。</summary>
    private static string StableKey(string path)
    {
        string seed = path;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists) seed = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        }
        catch { /* 取不到属性就只用路径 */ }

        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash, 0, 4);
    }

    private async Task LoadSegmentThumbsAsync()
    {
        var source = LongVideoWorkspace.SourcePath;
        if (source is null) return;

        var take = LongVideoWorkspace.Segments.Take(120).ToList();
        var sourceKey = StableKey(source);
        foreach (var seg in take)
        {
            // 导入了新视频：旧批次的缩略图不要再写进新分段。
            if (!string.Equals(LongVideoWorkspace.SourcePath, source, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                // 缓存键带上起点：重新切段（段长变了）后同序号的段不会拿到旧帧；
                // string.GetHashCode 每次进程启动都会随机化，缓存永远不命中，所以换稳定哈希。
                var path = Path.Combine(CacheDir, $"thumb-{sourceKey}-{seg.Index:000}-{(int)Math.Round(seg.StartSeconds * 100):D7}.jpg");
                if (!File.Exists(path))
                {
                    await _engine.ExtractFrameAsync(source, seg.StartSeconds + Math.Min(0.4, seg.DurationSeconds / 4), path);
                }

                seg.Thumb = new BitmapImage(new Uri(path));
            }
            catch
            {
                // thumbnail is cosmetic; ignore
            }
        }
    }

    // ---------------------------------------------------------------- anchor (chained continue)

    private void AnchorToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        LongVideoWorkspace.AnchorEnabled = AnchorToggle.IsChecked == true;
        AnchorPanel.Visibility = LongVideoWorkspace.AnchorEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (LongVideoWorkspace.AnchorEnabled) SyncAnchorSources();
        RefreshGenerateState();
    }

    private void AnchorSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (AnchorSourceCombo.SelectedItem is PoolRow row)
        {
            LongVideoWorkspace.AnchorPoolItemId = row.Item.Id;
        }
        RefreshGenerateState();
    }

    private void AnchorTail_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready) return;
        // NumberBox 未设 Value 时是 NaN，NaN<=0 为 false，不能只判 <=0。
        var raw = AnchorTailBox.Value;
        if (double.IsNaN(raw) || raw <= 0) raw = ClipAnchor.DefaultAnchorSeconds;
        var v = Math.Clamp(raw, ClipAnchor.MinAnchorSeconds, ClipAnchor.MaxAnchorSeconds);
        if (Math.Abs(AnchorTailBox.Value - v) > 0.001) AnchorTailBox.Value = v;
        LongVideoWorkspace.AnchorTailSeconds = v;
        RefreshGenerateState();
    }

    /// <summary>把池里「能续接」的片段填进锚点下拉，默认选最近一条。</summary>
    private void SyncAnchorSources()
    {
        var usable = _poolRows.Where(r => r.Item.CanContinue && File.Exists(r.Item.LocalPath)).ToList();
        var previous = LongVideoWorkspace.AnchorPoolItemId;

        AnchorSourceCombo.ItemsSource = usable;
        var pick = usable.FirstOrDefault(r => r.Item.Id == previous) ?? usable.FirstOrDefault();
        AnchorSourceCombo.SelectedItem = pick;
        LongVideoWorkspace.AnchorPoolItemId = pick?.Item.Id;
    }

    /// <summary>
    /// 构建本段的续接锚点。只有开启续接、且池里确实有可用来源时才返回非空。
    /// 来源超过 10s 时，真正的裁切在上传前由 runner 完成。
    /// </summary>
    private ClipAnchorSpec? BuildAnchor()
    {
        if (!LongVideoWorkspace.AnchorEnabled) return null;

        var row = AnchorSourceCombo.SelectedItem as PoolRow;
        if (row is null)
        {
            var usable = _poolRows.Where(r => r.Item.CanContinue && File.Exists(r.Item.LocalPath)).ToList();
            row = usable.FirstOrDefault();
        }
        if (row is null || !row.Item.CanContinue || !File.Exists(row.Item.LocalPath)) return null;

        return new ClipAnchorSpec(
            SourceLocalPath: row.Item.LocalPath,
            SourcePoolItemId: row.Item.Id,
            TailSeconds: LongVideoWorkspace.AnchorTailSeconds,
            SourceDurationSeconds: 0);   // runner 会用引擎探测真实时长
    }

    /// <summary>续接不可用时的中文原因（可用时为空串）。</summary>
    private string? AnchorBlockReason()
    {
        if (!LongVideoWorkspace.AnchorEnabled) return null;

        var usable = _poolRows.Where(r => r.Item.CanContinue && File.Exists(r.Item.LocalPath)).ToList();
        if (usable.Count == 0)
            return "已开启衔接上一段，但视频池里还没有可续接的生成结果：先独立生成一段，再开启续接。";

        var row = AnchorSourceCombo.SelectedItem as PoolRow ?? usable[0];
        if (!row.Item.CanContinue) return row.Item.ContinueBlockReason;
        if (!File.Exists(row.Item.LocalPath)) return "锚点来源文件不存在，可能已被清理。";
        return null;
    }

    // ---------------------------------------------------------------- selection

    private void Segment_SelectionChanged(object? sender, EventArgs e) => RefreshGenerateState();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllSelected(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllSelected(false);

    private void SetAllSelected(bool value)
    {
        foreach (var seg in LongVideoWorkspace.Segments) seg.IsSelected = value;
        RefreshGenerateState();
    }

    private void UpdateSegmentHeader()
    {
        var total = LongVideoWorkspace.Segments.Count;
        var selected = LongVideoWorkspace.SelectedCount;
        SegmentHeader.Text = $"分段（{total} 段 · 已选 {selected}）";
        GenerateSelectedButton.Content = $"生成所选（{selected}）";
    }

    private void BatchPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        LongVideoWorkspace.BatchPrompt = BatchPromptBox.Text;
        if (_ready) RefreshGenerateState();
    }

    private void RefreshGenerateState()
    {
        UpdateSegmentHeader();

        var selected = LongVideoWorkspace.Segments.Where(s => s.IsSelected).ToList();
        string? reason = null;

        if (LongVideoWorkspace.Segments.Count == 0) reason = "先切段，再勾选要生成的段。";
        else if (selected.Count == 0) reason = "已选 0 段：勾选后才能生成，未勾选不会提交任何任务。";
        else if (string.IsNullOrWhiteSpace(BatchPromptBox.Text)) reason = "请先填写本批提示词。";
        else if (selected.Any(s => s.State is SegmentGenState.Running or SegmentGenState.Queued))
            reason = "有段正在生成，等结束后再提交下一批。";
        else reason = AnchorBlockReason();

        GenerateSelectedButton.IsEnabled = reason is null;

        var anchorNote = LongVideoWorkspace.AnchorEnabled
            ? $" · 衔接：{(AnchorSourceCombo.SelectedItem as PoolRow)?.Title ?? "最近一段"}"
            : "";
        GenerateHint.Text = reason ?? $"将提交 {selected.Count} 段 · 模型 {SegmentModelId} · " +
            (LongVideoWorkspace.AnchorEnabled
                ? $"每段用上一段尾巴当 first_clip 续写{anchorNote}。"
                : "每段用首帧走图生（first_frame）。") +
            $"本页累计提交 {LongVideoWorkspace.ApiSubmitCount} 次。";
    }

    private static string SegmentModelId
    {
        get
        {
            var current = ModelCatalog.Find(AppSettings.Model);
            if (current is { ImageToVideo: true }) return current.Id;
            return ModelCatalog.DefaultModelId;
        }
    }

    // ---------------------------------------------------------------- preview / trim

    private async void SegmentPreview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index) return;
        var seg = LongVideoWorkspace.Segments.FirstOrDefault(s => s.Index == index);
        if (seg is null) return;
        await ShowPreviewAsync(seg);
    }

    private async Task ShowPreviewAsync(LongVideoSegment seg)
    {
        _previewSegment = seg;
        PreviewCard.Visibility = Visibility.Visible;
        PreviewTitle.Text = $"{seg.Label} · {seg.RangeLabel} · {seg.DurationLabel}";
        TrimStartBox.Value = Math.Round(seg.StartSeconds, 2);
        TrimDurationBox.Value = Math.Round(seg.DurationSeconds, 2);

        try
        {
            var path = await EnsurePreviewClipAsync(seg);
            SegmentPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
            SegmentPlayer.MediaPlayer?.Play();
            PreviewHint.Text = "预览片段由本地切出（未调用 API）。拖动播放条可 scrub，改起止后用「应用调整」重切。";
        }
        catch (Exception ex)
        {
            PreviewHint.Text = $"无法预览：{ex.Message}";
            SegmentPlayer.Source = null;
        }
    }

    private async Task<string> EnsurePreviewClipAsync(LongVideoSegment seg)
    {
        var source = LongVideoWorkspace.SourcePath ?? throw new InvalidOperationException("没有源视频。");
        if (!string.IsNullOrEmpty(seg.PreviewPath) && File.Exists(seg.PreviewPath)) return seg.PreviewPath;

        if (!_engine.SupportsCut)
            throw new InvalidOperationException("当前切段引擎不支持本地切段（缺 ffmpeg），无法生成预览片段。");

        var path = Path.Combine(CacheDir, $"preview-{seg.Index:000}-{Guid.NewGuid():N}.mp4");
        await _engine.CutAsync(source, seg.StartSeconds, seg.DurationSeconds, path);
        seg.PreviewPath = path;
        return path;
    }

    private async void ApplyTrim_Click(object sender, RoutedEventArgs e)
    {
        var seg = _previewSegment;
        if (seg is null) return;

        var info = LongVideoWorkspace.SourceInfo;
        if (info is null) return;

        var rawStart = ReadNumber(TrimStartBox, seg.StartSeconds);
        var rawDuration = ReadNumber(TrimDurationBox, seg.DurationSeconds);
        var start = Math.Clamp(rawStart <= 0 ? 0 : rawStart, 0, Math.Max(0, info.DurationSeconds - 1));
        var duration = Math.Clamp(rawDuration <= 0 ? 1 : rawDuration,
            MediaEngineDefaults.MinSegmentSeconds, MediaEngineDefaults.MaxSegmentSeconds);
        if (start + duration > info.DurationSeconds + 0.001)
        {
            duration = Math.Max(MediaEngineDefaults.MinSegmentSeconds, info.DurationSeconds - start);
        }

        seg.StartSeconds = Math.Round(start, 2);
        seg.DurationSeconds = Math.Round(duration, 2);
        if (!string.IsNullOrEmpty(seg.PreviewPath) && File.Exists(seg.PreviewPath))
        {
            try { File.Delete(seg.PreviewPath); } catch { /* cache file */ }
        }
        seg.PreviewPath = null;

        PreviewTitle.Text = $"{seg.Label} · {seg.RangeLabel} · {seg.DurationLabel}";
        await ShowPreviewAsync(seg);
        PreviewHint.Text = "已按新起止重切（本地）。";
    }

    private async void RefreshPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_previewSegment is null) return;
        if (!string.IsNullOrEmpty(_previewSegment.PreviewPath) && File.Exists(_previewSegment.PreviewPath))
        {
            try { File.Delete(_previewSegment.PreviewPath); } catch { /* cache file */ }
        }
        _previewSegment.PreviewPath = null;
        await ShowPreviewAsync(_previewSegment);
    }

    // ---------------------------------------------------------------- generate selected

    private async void GenerateSelected_Click(object sender, RoutedEventArgs e)
    {
        RefreshGenerateState();
        var selected = LongVideoWorkspace.Segments.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0 || !GenerateSelectedButton.IsEnabled) return;   // hard zero-submit guard

        var prompt = BatchPromptBox.Text.Trim();
        var model = SegmentModelId;
        var anchor = BuildAnchor();
        var submitted = 0;

        try
        {
            foreach (var seg in selected)
            {
                if (seg.State is SegmentGenState.Running or SegmentGenState.Queued) continue;

                seg.State = SegmentGenState.Queued;
                seg.StatusText = anchor is not null ? "准备续接锚点…" : "准备首帧…";
                seg.Error = null;

                try
                {
                    var item = new VideoTaskItem
                    {
                        Mode = anchor is not null ? GenerationMode.ContinueFromClip : GenerationMode.ImageToVideo,
                        Prompt = prompt,
                        Model = model,
                        Resolution = "480P",
                        Ratio = "adaptive",
                        Duration = (int)Math.Clamp(Math.Round(seg.DurationSeconds), 2,
                            Math.Max(2, ModelCatalog.Find(model)?.MaxDurationWithoutVideo ?? 15)),
                        MediaPaths = new List<string>(),
                        SegmentLabel = $"{seg.Label} · {seg.RangeLabel}",
                        Anchor = anchor
                    };

                    if (anchor is null)
                    {
                        // 独立生成：抽本段首帧，走图生 first_frame。
                        var framePath = Path.Combine(CacheDir, $"frame-{seg.Index:000}-{Guid.NewGuid():N}.jpg");
                        await _engine.ExtractFrameAsync(LongVideoWorkspace.SourcePath!, seg.StartSeconds, framePath);
                        item.MediaPaths.Add(framePath);
                    }

                    seg.Task = item;
                    seg.State = SegmentGenState.Queued;
                    seg.StatusText = "排队中…";
                    item.PropertyChanged += (_, args) => SegmentTaskChanged(seg, args);

                    VideoTaskStore.Add(item);
                    _ = VideoJobRunner.RunAsync(item, DispatcherQueue);
                    LongVideoWorkspace.ApiSubmitCount++;
                    submitted++;
                }
                catch (Exception ex)
                {
                    seg.State = SegmentGenState.Failed;
                    seg.StatusText = "失败";
                    seg.Error = $"首帧提取失败：{ex.Message}";
                }
            }

            if (submitted > 0)
            {
                ShowInfo("已提交", $"{submitted} 段已提交到「任务」，进度会同步到这里。累计提交 {LongVideoWorkspace.ApiSubmitCount} 次。",
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowInfo("提交失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            RefreshGenerateState();
            RefreshStitchState();
        }
    }

    private void SegmentTaskChanged(LongVideoSegment seg, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(VideoTaskItem.Phase) or nameof(VideoTaskItem.StatusText) or nameof(VideoTaskItem.Error)))
            return;

        DispatcherQueue.TryEnqueue(() =>
        {
            var task = seg.Task;
            if (task is null) return;

            switch (task.Phase)
            {
                case VideoTaskPhase.Queued:
                    seg.State = SegmentGenState.Queued;
                    seg.StatusText = "排队中…";
                    break;
                case VideoTaskPhase.Running:
                    seg.State = SegmentGenState.Running;
                    seg.StatusText = string.IsNullOrEmpty(task.RemoteTaskId) ? "提交中…" : $"生成中（{task.RemoteTaskId}）";
                    break;
                case VideoTaskPhase.Succeeded:
                    seg.State = SegmentGenState.Succeeded;
                    seg.StatusText = string.IsNullOrEmpty(task.LocalPath) ? "成功" : "成功 · 已入池";
                    seg.Error = null;
                    break;
                case VideoTaskPhase.Failed:
                    seg.State = SegmentGenState.Failed;
                    seg.StatusText = "失败";
                    seg.Error = task.Error ?? "未知错误";
                    break;
                case VideoTaskPhase.Canceled:
                    seg.State = SegmentGenState.Failed;
                    seg.StatusText = "已取消";
                    seg.Error = "任务已取消，可重试。";
                    break;
            }

            RefreshGenerateState();
        });
    }

    private void SegmentRetry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int index) return;
        var seg = LongVideoWorkspace.Segments.FirstOrDefault(s => s.Index == index);
        if (seg?.Task is null) return;

        var task = seg.Task;
        task.Phase = VideoTaskPhase.Queued;
        task.StatusText = "排队中";
        task.Error = null;
        task.Progress = 0;
        task.LocalPath = null;
        task.RemoteTaskId = null;

        seg.State = SegmentGenState.Queued;
        seg.StatusText = "排队中…";
        seg.Error = null;

        _ = VideoJobRunner.RunAsync(task, DispatcherQueue);
        LongVideoWorkspace.ApiSubmitCount++;
        RefreshGenerateState();
    }

    // ---------------------------------------------------------------- pool

    private void SyncPool()
    {
        var existing = _poolRows.ToDictionary(r => r.Item.Id);
        var wanted = VideoPool.Items.ToList();

        // drop rows that vanished
        for (var i = _poolRows.Count - 1; i >= 0; i--)
        {
            if (wanted.All(w => w.Id != _poolRows[i].Item.Id)) _poolRows.RemoveAt(i);
        }

        // insert in pool order
        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            if (!existing.TryGetValue(item.Id, out var row))
            {
                row = new PoolRow(item) { ShowSelect = StitchToggle.IsOn };
                row.SelectionChanged += (_, _) => RefreshStitchState();
                _poolRows.Insert(Math.Min(i, _poolRows.Count), row);
                _ = LoadPoolExtrasAsync(row);
            }
            else
            {
                var currentIndex = _poolRows.IndexOf(row);
                if (currentIndex != i && currentIndex >= 0)
                {
                    _poolRows.Move(currentIndex, Math.Min(i, _poolRows.Count - 1));
                }
            }
        }

        PoolHeader.Text = $"已生成视频池（{_poolRows.Count}）";
        PoolEmpty.Visibility = _poolRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (LongVideoWorkspace.AnchorEnabled) SyncAnchorSources();

        if (_firstClipRow is not null)
        {
            var refreshed = _poolRows.FirstOrDefault(r => r.Item.Id == _firstClipRow.Item.Id);
            _firstClipRow = refreshed;
        }
        RefreshContinueState();
        RefreshStitchState();
    }

    private async Task LoadPoolExtrasAsync(PoolRow row)
    {
        try
        {
            var thumb = Path.Combine(CacheDir, $"pool-{row.Item.Id}.jpg");
            if (!File.Exists(thumb))
            {
                await _engine.ExtractFrameAsync(row.Item.LocalPath, 0.2, thumb);
            }
            row.Thumb = new BitmapImage(new Uri(thumb));
        }
        catch
        {
            // cosmetic
        }

        try
        {
            var info = await _engine.ProbeAsync(row.Item.LocalPath);
            row.DurationSeconds = info.DurationSeconds;
            row.DurationLabel = LongVideoSegment.FormatTime(info.DurationSeconds);
        }
        catch
        {
            // cosmetic
        }
    }

    private void UseAsFirstClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not PoolRow row) return;

        if (!row.Item.CanContinue)
        {
            ShowInfo("不能作为首片段", row.Item.ContinueBlockReason, InfoBarSeverity.Warning);
            return;
        }

        _firstClipRow = row;
        if (string.IsNullOrWhiteSpace(ContinuePromptBox.Text))
        {
            ContinuePromptBox.Text = "延续上一段的主体与镜头运动，动作自然衔接，画面稳定。";
        }

        // duration 是输出总时长：首片段 5s 时若还停在 5，就是零续写。
        // 自动抬到「首片段 + 5s」，并夹在 2–15。
        var clip = row.DurationSeconds;
        if (clip > 0)
        {
            var wanted = Math.Clamp(Math.Ceiling(clip) + 5, 2, 15);
            if (ReadNumber(ContinueDurationBox, 0) <= clip) ContinueDurationBox.Value = wanted;
        }

        RefreshContinueState();
        ShowInfo("已选首片段", $"{row.Title} 将作为 first_clip 提交续写。", InfoBarSeverity.Informational);
    }

    // ---------------------------------------------------------------- continue

    private void ContinueInput_Changed(object sender, SelectionChangedEventArgs e) => RefreshContinueState();

    private void ContinueInput_TextChanged(object sender, TextChangedEventArgs e) => RefreshContinueState();

    private void ContinueNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => RefreshContinueState();

    private void RefreshContinueState()
    {
        var model = ContinueModelCombo.SelectedItem as string;
        var total = ContinueModelCombo.Items.Count;

        // duration 是「输出总时长」而不是「续写增量」（官方口径）：
        // 输入 3s + duration=15 -> 续写 12s、总长 15s、按 15s 计费。
        // 所以输出总时长必须大于首片段时长，否则等于零续写还照扣费。
        var clipSeconds = _firstClipRow?.DurationSeconds ?? 0;
        var rawOut = ReadNumber(ContinueDurationBox, 5);
        var outSeconds = Math.Clamp(rawOut <= 0 ? 5 : rawOut, 2, 15);

        string? reason = null;
        if (total == 0) reason = "模型列表里没有支持 first_clip 续写的 i2v 模型，续接入口已禁用。";
        else if (string.IsNullOrEmpty(model)) reason = "请选择续写模型。";
        else if (_firstClipRow is null) reason = "先在池里选一个片段「设为首片段」。";
        else if (!_firstClipRow.Item.CanContinue) reason = _firstClipRow.Item.ContinueBlockReason;
        else if (string.IsNullOrWhiteSpace(ContinuePromptBox.Text)) reason = "请填写续接提示词。";
        else if (clipSeconds > 0 && outSeconds <= clipSeconds)
            reason = $"输出总时长要大于首片段（{clipSeconds:0.#}s），否则等于没有续写。请调到 {Math.Min(15, Math.Ceiling(clipSeconds) + 1):0}s 以上。";

        ContinueButton.IsEnabled = reason is null;

        var gain = clipSeconds > 0 ? Math.Max(0, outSeconds - clipSeconds) : outSeconds;
        ContinueHint.Text = reason ??
            $"将用 {model} 提交：media[0].type=first_clip。输出总时长 {outSeconds:0}s" +
            (clipSeconds > 0 ? $"（首片段 {clipSeconds:0.#}s → 新增约 {gain:0.#}s）" : "") +
            "。官方口径 duration 是输出总时长，按它计费。";

        ContinueTarget.Text = _firstClipRow is null
            ? "首片段：未选择"
            : $"首片段：{_firstClipRow.Title} · {_firstClipRow.SourceLabel}\nURL：{(_firstClipRow.Item.RemoteUrl ?? "（无公网 URL）")}";
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        RefreshContinueState();
        if (!ContinueButton.IsEnabled || _firstClipRow is null) return;

        var url = _firstClipRow.Item.RemoteUrl;
        if (string.IsNullOrWhiteSpace(url) || !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            ShowInfo("无法续接", "first_clip 需要公网可访问的 http(s) URL；本地文件无法直接提交。", InfoBarSeverity.Error);
            return;
        }

        var model = ContinueModelCombo.SelectedItem as string ?? ModelCatalog.DefaultContinueModelId;
        // duration 是输出总时长（官方口径），不是续写增量。
        var contModel = ContinueModelCombo.SelectedItem as string ?? ModelCatalog.DefaultContinueModelId;
        var contUpper = Math.Max(2, ModelCatalog.Find(contModel)?.MaxDurationWithoutVideo ?? 15);
        var duration = (int)Math.Clamp(Math.Round(ReadNumber(ContinueDurationBox, 5)), 2, contUpper);

        var item = new VideoTaskItem
        {
            Mode = GenerationMode.ContinueFromClip,
            Prompt = ContinuePromptBox.Text.Trim(),
            Model = model,
            Resolution = "480P",
            Ratio = "adaptive",
            Duration = duration,
            FirstClipUrl = url,
            SegmentLabel = _firstClipRow.Item.SegmentLabel
        };

        VideoTaskStore.Add(item);
        _ = VideoJobRunner.RunAsync(item, DispatcherQueue);
        LongVideoWorkspace.ApiSubmitCount++;

        ShowInfo("已提交续接",
            $"官方续接任务已提交（{model}，first_clip）。结果会进「任务」并在成功后入池。",
            InfoBarSeverity.Success);
        RefreshGenerateState();
    }

    // ---------------------------------------------------------------- local stitch

    private void StitchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        LongVideoWorkspace.StitchEnabled = StitchToggle.IsOn;
        foreach (var row in _poolRows) row.ShowSelect = StitchToggle.IsOn;
        if (!StitchToggle.IsOn)
        {
            foreach (var row in _poolRows) row.IsSelected = false;
        }
        RefreshStitchState();
    }

    private void ApplyStitchVisibility()
    {
        foreach (var row in _poolRows) row.ShowSelect = StitchToggle.IsOn;
    }

    private void RefreshStitchState()
    {
        var selected = _poolRows.Where(r => r.IsSelected).ToList();
        string? reason = null;

        if (!StitchToggle.IsOn) reason = "默认关闭；开启后才会出现池内勾选与拼接。";
        else if (selected.Count == 0) reason = "已选 0 段：先在池里勾选至少 2 段。";
        else if (selected.Count == 1) reason = "至少选 2 段才能拼接。";
        else if (!_engine.SupportsConcat) reason = "当前引擎不支持本地拼接（缺 ffmpeg）。";

        StitchButton.IsEnabled = reason is null;
        StitchHint.Text = reason ?? $"将按池中顺序硬拼 {selected.Count} 段，产生 {selected.Count - 1} 处接缝（本地 ffmpeg，非官方续接）。";
    }

    private async void Stitch_Click(object sender, RoutedEventArgs e)
    {
        var selected = _poolRows.Where(r => r.IsSelected).ToList();
        if (selected.Count < 2 || !StitchButton.IsEnabled) return;

        try
        {
            StitchButton.IsEnabled = false;
            StitchResult.Text = "拼接中…";

            var clips = selected.Select(r => r.Item.LocalPath).ToList();
            var seams = new List<double>();
            var acc = 0.0;
            foreach (var clip in clips)
            {
                var info = await _engine.ProbeAsync(clip);
                acc += info.DurationSeconds;
                seams.Add(Math.Round(acc, 2));
            }
            seams.RemoveAt(seams.Count - 1);   // last end is not a seam

            var dir = AppPaths.OutputDir(AppSettings.OutputDirectory);
            var outPath = Path.Combine(dir, $"stitch-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
            await _engine.ConcatAsync(clips, outPath);

            LongVideoWorkspace.LastStitchPath = outPath;
            LongVideoWorkspace.LastStitchSeams = seams;

            var seamText = seams.Count == 0
                ? "无接缝"
                : $"接缝 {seams.Count} 处（{string.Join(" / ", seams.Select(s => LongVideoSegment.FormatTime(s)))}）";
            StitchResult.Text = $"已生成：{outPath}\n{seamText}。这是本地硬拼，不是官方续接，接缝处不保证连贯。";
            ShowInfo("拼接完成", $"{clips.Count} 段 → 1 个文件，{seamText}。本地硬拼，非官方续接。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            StitchResult.Text = $"拼接失败：{ex.Message}";
            ShowInfo("拼接失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            RefreshStitchState();
        }
    }

    // ---------------------------------------------------------------- helpers

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
