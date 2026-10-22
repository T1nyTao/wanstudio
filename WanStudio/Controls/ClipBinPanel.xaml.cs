using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Text;
using WanStudio.Services;
using WanStudio.Services.Media;

namespace WanStudio.Controls;

/// <summary>
/// 双栏素材库。
/// 左栏 = 已经裁好、还没生成过的片段（素材候选，可反复用）；
/// 右栏 = 已生成的视频，可单选一段作为续接源。
///
/// 数据全部来自 <see cref="VideoPool"/>，本控件不维护第二份真值。
/// </summary>
public sealed partial class ClipBinPanel : UserControl
{
    private readonly ObservableCollection<ClipBinRow> _rawRows = new();
    private readonly ObservableCollection<ClipBinRow> _pendingRows = new();
    private readonly ObservableCollection<ClipBinRow> _generatedRows = new();
    private readonly IMediaEngine _engine;
    private bool _ready;

    public ClipBinPanel()
    {
        InitializeComponent();
        _engine = MediaEngines.CreateDefault();

        RawGrid.ItemsSource = _rawRows;
        PendingGrid.ItemsSource = _pendingRows;
        GeneratedGrid.ItemsSource = _generatedRows;
        RawGrid.ItemTemplate = (DataTemplate)Resources["ClipCardTemplate"];
        PendingGrid.ItemTemplate = (DataTemplate)Resources["ClipCardTemplate"];
        GeneratedGrid.ItemTemplate = (DataTemplate)Resources["ClipCardTemplate"];

        Loaded += ClipBinPanel_Loaded;
        Unloaded += ClipBinPanel_Unloaded;
    }

    /// <summary>标题前缀，控件自己拼条数。</summary>
    public string HeaderText
    {
        get => HeaderBlock.Text;
        set => HeaderBlock.Text = value;
    }

    /// <summary>裁切秒数（与 VideoPool 持久化同步）。</summary>
    public double CutSeconds
    {
        get => VideoPool.ClipCutSeconds;
        set
        {
            if (Math.Abs(VideoPool.ClipCutSeconds - value) < 0.01) return;
            VideoPool.ClipCutSeconds = value;
            CutSecondsChanged?.Invoke(this, VideoPool.ClipCutSeconds);
        }
    }

    /// <summary>右栏当前选中的续接源（没选为 null）。</summary>
    public VideoPoolItem? ContinueSource => VideoPool.SelectedContinueClip;

    /// <summary>
    /// 左栏勾选的素材（多选）。旧调用点（HomePage 挑选参考素材）用这个名字，
    /// 语义与新双栏一致：只返回「待生成素材」里被勾选的条目。
    /// </summary>
    public IReadOnlyList<VideoPoolItem> SelectedItems =>
        _pendingRows.Where(r => r.IsSelected).Select(r => r.Item).ToList();

    public event EventHandler<double>? CutSecondsChanged;
    public event EventHandler? ContinueSourceChanged;
    public event EventHandler? SelectionChanged;

    private void ClipBinPanel_Loaded(object sender, RoutedEventArgs e)
    {
        _ready = false;

        // NumberBox 不能在 XAML 里写 Value（会抛 Failed to assign），必须在这里赋 double。
        CutSecondsBox.Value = VideoPool.ClipCutSeconds;

        VideoPool.Items.CollectionChanged += Pool_CollectionChanged;
        SizeChanged += ClipBinPanel_SizeChanged;
        _ready = true;
        RefreshCategoryFilters();
        Refresh();
        ApplyResponsiveLayout(ActualWidth);

        // 页面导航动画期间 XamlRoot.Size 会短暂返回很小的值（实测 640），
        // 直接采信会让布局在宽/窄之间来回抖。这里只在稳定后补一次，
        // 且用控件自身的 ActualWidth（它跟随真实布局），不用 XamlRoot.Size。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ActualWidth > 0) ApplyResponsiveLayout(ActualWidth);
        });
    }

    /// <summary>
    /// 响应式：窄于 860px 时双栏改上下堆叠。
    /// 桌面窗口可以从 800 拖到 4K，两栏各挤到 180px 会难用，所以要有降级而不是硬撑。
    /// </summary>
    private void ClipBinPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout(e.NewSize.Width);

    private bool _stacked;
    private bool _reported;
    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0) return;

        var shouldStack = width < 860;
        // 首次调用（_reported==false）无条件走一遍，避免因为初始 _stacked 默认值与
        // 计算结果相同就跳过，导致布局从未真正应用。
        if (_reported && shouldStack == _stacked) return;
        _stacked = shouldStack;
        _reported = true;

        if (shouldStack)
        {
            // 上下堆叠：右栏落到第二行，分隔线改成横向。
            Grid.SetRow(RightPane, 1);
            Grid.SetColumn(RightPane, 0);
            Grid.SetColumnSpan(RightPane, 3);
            Grid.SetRow(LeftPane, 0);
            Grid.SetColumn(LeftPane, 0);
            Grid.SetColumnSpan(LeftPane, 3);

            RightCol.Width = new GridLength(0);
            DividerCol.Width = new GridLength(0);
            LeftCol.Width = new GridLength(1, GridUnitType.Star);

            Divider.Visibility = Visibility.Collapsed;
            TopRow.Height = GridLength.Auto;
            BottomRow.Height = GridLength.Auto;
        }
        else
        {
            Grid.SetRow(RightPane, 0);
            Grid.SetColumn(RightPane, 2);
            Grid.SetColumnSpan(RightPane, 1);
            Grid.SetRow(LeftPane, 0);
            Grid.SetColumn(LeftPane, 0);
            Grid.SetColumnSpan(LeftPane, 1);

            LeftCol.Width = new GridLength(1, GridUnitType.Star);
            DividerCol.Width = new GridLength(1);
            RightCol.Width = new GridLength(1, GridUnitType.Star);

            Divider.Visibility = Visibility.Visible;
        }

        // 诊断日志只在调试构建里留，发布版不写盘。
#if DEBUG
        ReportLayout(width, shouldStack);
#endif
    }

#if DEBUG
    /// <summary>
    /// 把当前布局状态写到 %TEMP%，仅用于自动化验收核对布局分支是否真的生效
    /// （UIA 读不到 Grid 行列位置）。不含任何敏感信息，发布版不编译。
    /// </summary>
    private void ReportLayout(double width, bool stacked)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} width={width:0} stacked={stacked} " +
                       $"rightRow={Grid.GetRow(RightPane)} rightCol={Grid.GetColumn(RightPane)} " +
                       $"colSpan={Grid.GetColumnSpan(RightPane)} divider={(Divider.Visibility == Visibility.Visible ? "on" : "off")}";
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wanstudio-layout.log"),
                line + Environment.NewLine);
        }
        catch
        {
            // 诊断用，失败不影响功能
        }
    }
#endif

    private void ClipBinPanel_Unloaded(object sender, RoutedEventArgs e)
    {
        VideoPool.Items.CollectionChanged -= Pool_CollectionChanged;
        // 控件卸载时停播并释放，否则 MediaPlayer 会一直占着文件句柄。
        ClosePreview();
    }

    private void Pool_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(Refresh);
            return;
        }
        Refresh();
    }

    /// <summary>重建两栏卡片并回填缩略图/时长。</summary>
    public void Refresh()
    {
        if (!_ready) return;

        // 原视频区：导入后待在这里，裁不裁由用户决定。
        Rebuild(_rawRows, VideoPool.RawVideos);
        Rebuild(_pendingRows, Filter(VideoPool.PendingClips, MaterialFilter));
        Rebuild(_generatedRows, Filter(VideoPool.GeneratedClips, GeneratedFilter));

        RawHeader.Text = $"原视频（{_rawRows.Count}）";
        LeftHeader.Text = $"素材片段（{_pendingRows.Count}）";
        RightHeader.Text = $"已生成（{_generatedRows.Count}）";
        HeaderBlock.Text = $"素材库（{_rawRows.Count + _pendingRows.Count + _generatedRows.Count}）";

        RawEmpty.Visibility = _rawRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RawGrid.Visibility = _rawRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CutSelectedButton.IsEnabled = _rawRows.Any(r => r.IsSelected);
        PendingEmpty.Visibility = _pendingRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PendingGrid.Visibility = _pendingRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        GeneratedEmpty.Visibility = _generatedRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GeneratedGrid.Visibility = _generatedRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        UpdateSelectionText();
        UpdateContinueHint();
    }

    /// <summary>按指定筛选框过滤（「全部」不筛）。两侧各用各的。</summary>
    private static IReadOnlyList<VideoPoolItem> Filter(
        IReadOnlyList<VideoPoolItem> source, ComboBox? filter)
    {
        var sel = filter?.SelectedItem as string;
        if (string.IsNullOrEmpty(sel) || sel == AllCategoriesLabel) return source;

        var wantUncategorized = sel == VideoPool.UncategorizedLabel;
        var list = new List<VideoPoolItem>();
        foreach (var item in source)
        {
            var isUncat = string.IsNullOrWhiteSpace(item.Category);
            if (wantUncategorized)
            {
                if (isUncat) list.Add(item);
            }
            else if (!isUncat && string.Equals(item.Category, sel, StringComparison.CurrentCultureIgnoreCase))
            {
                list.Add(item);
            }
        }
        return list;
    }

    private void Rebuild(ObservableCollection<ClipBinRow> target, IReadOnlyList<VideoPoolItem> source)
    {
        var picked = VideoPool.SelectedContinueClipId;
        target.Clear();
        foreach (var item in source)
        {
            var row = new ClipBinRow(item)
            {
                CardWidth = CurrentCardWidth,
                IsContinueSource = string.Equals(item.Id, picked, StringComparison.Ordinal)
            };
            target.Add(row);
            _ = LoadExtrasAsync(row);
        }
    }

    private double CurrentCardWidth
    {
        get
        {
            // Slider 未赋值是 NaN，NaN<=0 为 false，不能只判大小。
            var v = SizeSlider?.Value ?? double.NaN;
            return double.IsNaN(v) || v <= 0 ? 150 : v;
        }
    }

    private void SizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready) return;
        var w = CurrentCardWidth;
        foreach (var r in _pendingRows) r.CardWidth = w;
        foreach (var r in _generatedRows) r.CardWidth = w;
        ApplyPanelSize(w);
    }

    private void ApplyPanelSize(double w)
    {
        void Size(GridView g)
        {
            if (g.ItemsPanelRoot is ItemsWrapGrid wrap)
            {
                wrap.ItemWidth = w + 10;
                wrap.ItemHeight = 196;
            }
        }
        Size(PendingGrid);
        Size(GeneratedGrid);
    }

    private async Task LoadExtrasAsync(ClipBinRow row)
    {
        try
        {
            if (!string.IsNullOrEmpty(row.Item.ThumbPath) && File.Exists(row.Item.ThumbPath))
            {
                row.Thumb = new BitmapImage(new Uri(row.Item.ThumbPath!));
            }
            else if (File.Exists(row.Item.LocalPath))
            {
                // 缩略图目录也走统一解析：目录写不进去时最多没缩略图，不能让整个素材库崩。
                var dir = AppPaths.CacheDir;
                var thumb = Path.Combine(dir, $"bin-{row.Item.Id}.jpg");
                if (!File.Exists(thumb))
                {
                    await _engine.ExtractFrameAsync(row.Item.LocalPath, 0.2, thumb);
                }
                row.Thumb = new BitmapImage(new Uri(thumb));
                row.Item.ThumbPath = thumb;
            }
        }
        catch
        {
            // 缩略图是装饰，失败不打断
        }

        try
        {
            if (row.Item.DurationSeconds <= 0 && File.Exists(row.Item.LocalPath))
            {
                var info = await _engine.ProbeAsync(row.Item.LocalPath);
                row.Item.DurationSeconds = info.DurationSeconds;
                row.RefreshComputed();
            }
        }
        catch
        {
            // 探测失败就显示未知时长
        }
    }

    private void PendingGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        // 左栏只做多选（挑素材），不参与续接选中。
        if (e.ClickedItem is not ClipBinRow row) return;
        row.IsSelected = !row.IsSelected;
        UpdateSelectionText();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void GeneratedGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ClipBinRow row) return;

        // 右栏是单选：点一次设为续接源，再点一次取消。
        var already = string.Equals(VideoPool.SelectedContinueClipId, row.Item.Id, StringComparison.Ordinal);
        VideoPool.SelectedContinueClipId = already ? null : row.Item.Id;

        foreach (var r in _generatedRows)
        {
            r.IsContinueSource = string.Equals(VideoPool.SelectedContinueClipId, r.Item.Id, StringComparison.Ordinal);
        }

        UpdateContinueHint();
        ContinueSourceChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CutSecondsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready) return;

        // NaN 兜底：NumberBox 未赋值/清空时是 NaN，clamp 前必须先判。
        var raw = CutSecondsBox.Value;
        if (double.IsNaN(raw) || raw <= 0) raw = VideoPool.DefaultClipCutSeconds;

        var v = Math.Clamp(raw, VideoPool.MinClipCutSeconds, VideoPool.MaxClipCutSeconds);
        if (Math.Abs(CutSecondsBox.Value - v) > 0.01) CutSecondsBox.Value = v;

        CutSeconds = v;
        UpdateContinueHint();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>把操作结果写进素材库内的 InfoBar（不用 Visibility，用 IsOpen）。</summary>
    private void ShowResult(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        ImportResult.Severity = severity;
        ImportResult.Title = severity == InfoBarSeverity.Error ? "出错了" : "素材库";
        ImportResult.Message = message;
        ImportResult.IsOpen = true;
    }

    // ---------------------------------------------------------------- 导入 / 裁切

    /// <summary>
    /// 导入素材到素材库。视频超过单段上限（官方 reference_video 是 30s）会自动裁切，
    /// 图片原样入库。这是本地处理，不调用任何生成接口。
    /// </summary>
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        ImportTrace("=== Import_Click 开始 ===");
        try
        {
            // 用 Win32 IFileOpenDialog，不用 WinRT FileOpenPicker。
            // 后者在未打包 WinUI 3 应用里会在 PickMultipleFilesAsync 直接抛
            // COMException E_FAIL(0x80004005)，对话框根本不弹（实测复现）。
            ImportTrace("取窗口句柄…");
            var hwnd = PickerHost.BringToFrontAndGetHandle();
            ImportTrace($"hwnd={hwnd}");

            ImportTrace("调 PickFilesAsync…");
            var picked = await Win32FilePicker.PickFilesAsync(
                hwnd,
                "选择要导入的视频",
                new[] { "mp4", "mov", "webm", "mkv", "avi", "m4v" });
            ImportTrace($"选择器返回：count={picked.Count}");
            if (picked.Count == 0) return;

            var added = 0;
            var skipped = new List<string>();

            foreach (var path in picked)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                ImportTrace($"处理：{path}");

                if (AlreadyImported(path))
                {
                    skipped.Add($"{Path.GetFileName(path)}（已在素材库）");
                    continue;
                }

                if (ext is not (".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" or ".m4v" or ".wmv" or ".flv" or ".ts" or ".mpg" or ".mpeg"))
                {
                    skipped.Add($"{Path.GetFileName(path)}（不支持的格式）");
                    continue;
                }

                MediaInfo info;
                try
                {
                    info = await _engine.ProbeAsync(path);
                }
                catch (Exception ex)
                {
                    skipped.Add($"{Path.GetFileName(path)}（读不出时长：{ex.Message}）");
                    continue;
                }

                if (info.DurationSeconds < 1.0)
                {
                    skipped.Add($"{Path.GetFileName(path)}（{info.DurationSeconds:0.#}s，太短）");
                    continue;
                }

                // 关键：导入只入「原视频」，不裁切。
                // 剪不剪、剪成多少秒，由用户在下面自己决定。
                VideoPool.AddRawVideo(path, info.DurationSeconds);
                added++;
                ImportTrace($"  已作为原视频入库（{info.DurationSeconds:0.#}s）");
            }

            Refresh();
            ShowResult(added == 0
                ? "没有导入任何视频。" + (skipped.Count > 0 ? " 跳过：" + string.Join("；", skipped.Take(3)) : "")
                : $"已导入 {added} 个原视频。要不要裁成片段、裁成几秒，由你在下面决定。");
            ImportTrace($"=== 完成：added={added} ===");
        }
        catch (Exception ex)
        {
            ImportTrace($"!!! 异常：{ex.GetType().Name}: {ex.Message}");
            // COMException 从 Message 看不出问题，必须把 HRESULT 和出错位置记下来。
            var detail = ex is System.Runtime.InteropServices.COMException com
                ? $"HRESULT=0x{com.HResult:X8} {ex.Message}"
                : $"{ex.GetType().Name} — {ex.Message}";
            ImportTrace($"!!! {detail}");
            ImportTrace($"HResult=0x{ex.HResult:X8}");
            ImportTrace(ex.StackTrace ?? "(no stack)");
            if (ex.InnerException is not null)
                ImportTrace($"Inner: {ex.InnerException.GetType().Name} 0x{ex.InnerException.HResult:X8} {ex.InnerException.Message}");
            ShowResult($"导入失败：{detail}", InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// 导入流程诊断日志，写 %TEMP%。
    /// 之前失败信息只进 InfoBar，而 InfoBar 在页面底部、滚出视口，
    /// 用户看不见、我也拿不到，「导入没反应」就无法定位。
    /// </summary>
    private static void ImportTrace(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wanstudio-import.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// 该源文件是否已经导入过（整段或已切段）。
    ///
    /// 为什么必须按「源文件」而不是产物路径判断：切段产物文件名带 guid，
    /// 每次切都是全新文件，靠 LocalPath 去重永远撞不上——同一个视频导入两次
    /// 就会在库里堆出重复条目、磁盘上还留一堆孤儿片段。
    /// </summary>
    private static bool AlreadyImported(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        var stem = Path.GetFileNameWithoutExtension(full);

        foreach (var item in VideoPool.Items)
        {
            if (item.Kind != ClipKind.CutReference && item.Kind != ClipKind.Imported) continue;

            // 整段导入：LocalPath 就是源文件本身。
            if (string.Equals(item.LocalPath, full, StringComparison.OrdinalIgnoreCase)) return true;

            // 切段导入：SegmentLabel 形如 "<源文件名> · 00:00–00:25"。
            if (!string.IsNullOrWhiteSpace(item.SegmentLabel) &&
                item.SegmentLabel!.StartsWith(stem + " ·", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------- 原视频：选择与裁切

    private void RawGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        // 原视频区支持多选：用户可以一次勾多个一起裁。
        if (e.ClickedItem is not ClipBinRow row) return;
        row.IsSelected = !row.IsSelected;
        CutSelectedButton.IsEnabled = _rawRows.Any(r => r.IsSelected);
        UpdateSelectionText();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 把勾选的原视频按当前「裁成每段 N 秒」切片，切好的进入素材片段。
    ///
    /// 这是用户主动触发的，导入时绝不自动裁——用户要求：
    /// 导入只得到一个原视频，要不要切、切几秒，由他自己决定。
    /// </summary>
    private async void CutSelected_Click(object sender, RoutedEventArgs e)
    {
        var picked = _rawRows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        if (picked.Count == 0)
        {
            ShowResult("先在「原视频」里勾选要裁切的视频。", InfoBarSeverity.Warning);
            return;
        }

        var chunk = VideoPool.ClipCutSeconds;
        CutSelectedButton.IsEnabled = false;
        ShowResult($"正在裁切 {picked.Count} 个原视频（每段 {chunk:0.#}s）…");

        var totalClips = 0;
        var failed = new List<string>();

        try
        {
            foreach (var item in picked)
            {
                if (!File.Exists(item.LocalPath))
                {
                    failed.Add($"{item.Title}（文件不见了）");
                    continue;
                }

                try
                {
                    ImportTrace($"裁切原视频：{item.LocalPath} 每段 {chunk:0.#}s");
                    var dir = CutOutputDir;
                    var clips = await ReferenceCutter.CutAsync(_engine, item.LocalPath, dir, chunk);
                    ImportTrace($"  裁出 {clips.Count} 段");

                    var source = Path.GetFileNameWithoutExtension(item.LocalPath);
                    foreach (var clip in clips)
                    {
                        VideoPool.AddCutReference(
                            clip.LocalPath,
                            $"{source} · {clip.RangeLabel}",
                            clip.DurationSeconds,
                            $"{source} 片段");
                        totalClips++;
                    }

                    // 原视频已经切成片段了，从「原视频」区移除（文件本身不动）。
                    VideoPool.Remove(item.Id);
                }
                catch (Exception ex)
                {
                    ImportTrace($"  裁切失败：{ex.Message}");
                    failed.Add($"{item.Title}（{ex.Message}）");
                }
            }

            Refresh();
            ShowResult(totalClips == 0
                ? "没有裁出片段。" + (failed.Count > 0 ? " 失败：" + string.Join("；", failed.Take(3)) : "")
                : $"已裁出 {totalClips} 段，进入下方「素材片段」。" +
                  (failed.Count > 0 ? $" 失败 {failed.Count} 个。" : ""));
        }
        finally
        {
            CutSelectedButton.IsEnabled = _rawRows.Any(r => r.IsSelected);
        }
    }

    // ---------------------------------------------------------------- 预览

    /// <summary>
    /// 预览一个片段。用 MediaPlayerElement 在本控件里播放，不跳页、不调 API。
    /// 素材可能只是被移除卡片（文件还在），也可能文件已被外部删掉——两种都要给明确提示。
    /// </summary>
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;
        ShowPreview(id);
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();

    private void ShowPreview(string id)
    {
        var item = VideoPool.Find(id);
        if (item is null) { ShowResult("找不到这个素材，可能已被移除。", InfoBarSeverity.Warning); return; }

        if (string.IsNullOrWhiteSpace(item.LocalPath) || !File.Exists(item.LocalPath))
        {
            ShowResult($"文件不在了：{item.LocalPath}", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            PreviewPanel.Visibility = Visibility.Visible;
            PreviewTitle.Text = string.IsNullOrWhiteSpace(item.Title) ? "(无标题)" : item.Title;

            var duration = item.DurationSeconds > 0 ? $"{item.DurationSeconds:0.#}s" : "时长未知";
            PreviewMeta.Text = $"{item.Source} · {duration} · {item.LocalPath}";

            PreviewPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(item.LocalPath));
            PreviewPlayer.MediaPlayer?.Play();
        }
        catch (Exception ex)
        {
            PreviewPanel.Visibility = Visibility.Collapsed;
            ShowResult($"预览失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void ClosePreview()
    {
        try { PreviewPlayer.MediaPlayer?.Pause(); } catch { }
        PreviewPlayer.Source = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
    }

    private static string BuildImportMessage(int added, int cut, IReadOnlyList<string> skipped)
    {
        var msg = added == 0 ? "没有导入任何素材。" : $"已导入 {added} 条素材";
        if (cut > 0) msg += $"（其中 {cut} 个长视频按 {VideoPool.ClipCutSeconds:0.#}s 自动裁切）";
        msg += "。";
        if (skipped.Count > 0) msg += " 跳过：" + string.Join("；", skipped.Take(3));
        return msg;
    }

    /// <summary>按当前「每段裁成」重裁勾选的素材（原始文件必须还在）。</summary>

    /// <summary>
    /// 裁切输出目录。统一走 <see cref="AppPaths"/>：首选位置不可写会自动回退
    /// （LOCALAPPDATA → USERPROFILE/.wanstudio → TEMP），不再因为一个目录失败就整条导入废掉。
    /// </summary>
    private static string CutOutputDir => AppPaths.ReferenceCutDir;

    // ---------------------------------------------------------------- 分类

    private void Categorize_Click(object sender, RoutedEventArgs e)
    {
        var picked = _pendingRows.Where(r => r.IsSelected).Select(r => r.Item.Id).ToList();
        if (picked.Count == 0)
        {
            ShowResult("先在左栏勾选素材，再设置分类。");
            return;
        }
        _ = ShowCategoryDialogAsync(picked, ClipKind.CutReference);
    }

    private async Task ShowCategoryDialogAsync(IReadOnlyList<string> ids, ClipKind sideKind)
    {
        var existing = VideoPool.Categories(sideKind);
        var box = new TextBox
        {
            PlaceholderText = existing.Count == 0
                ? "输入分类名，例如：人物 / 场景 / 参考动作"
                : "输入分类名，或从下面选一个已有的",
            Text = ""
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"将 {ids.Count} 条素材归到分类：",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(box);

        if (existing.Count > 0)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var c in existing.Take(6))
            {
                var chip = new Button { Content = c, FontSize = 12 };
                // 点已有分类 = 直接填进输入框，确认后统一提交。
                chip.Click += (_, _) => box.Text = c;
                chips.Children.Add(chip);
            }
            panel.Children.Add(chips);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "设置分类",
            Content = panel,
            PrimaryButtonText = "确定",
            SecondaryButtonText = "移出分类",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        var changed = result == ContentDialogResult.Secondary
            ? VideoPool.SetCategory(ids, null)
            : VideoPool.SetCategory(ids, box.Text);

        Refresh();
        RefreshCategoryFilters();
        ShowResult(changed == 0
            ? "分类没有变化。"
            : result == ContentDialogResult.Secondary
                ? $"已把 {changed} 条素材移出分类。"
                : $"已把 {changed} 条素材归到「{box.Text.Trim()}」。");
    }

    private void MaterialFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Refresh();
    }

    private void GeneratedFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Refresh();
    }

    /// <summary>刷新某一侧的筛选下拉：全部 + 该侧分类 + 未分类。</summary>
    private void RefreshFilter(ComboBox box, ClipKind sideKind)
    {
        var keep = box.SelectedItem as string;
        var items = new List<string> { AllCategoriesLabel };
        items.AddRange(VideoPool.Categories(sideKind));
        items.Add(VideoPool.UncategorizedLabel);

        box.ItemsSource = items;
        box.SelectedItem = keep is not null && items.Contains(keep) ? keep : AllCategoriesLabel;
    }

    /// <summary>两侧筛选一起刷新。</summary>
    private void RefreshCategoryFilters()
    {
        RefreshFilter(MaterialFilter, ClipKind.CutReference);
        RefreshFilter(GeneratedFilter, ClipKind.Generated);
    }

    private const string AllCategoriesLabel = "全部";

    // ---------------------------------------------------------------- 分类管理

    /// <summary>
    /// 分类管理：新建 / 改名 / 删除。
    /// 删除时先问清该分类下的素材去哪（移出到未分类，或合并到另一个分类），不静默丢数据。
    /// </summary>
    private async void ManageGeneratedCategories_Click(object sender, RoutedEventArgs e)
        => await ManageCategoriesForAsync(ClipKind.Generated);

    /// <summary>素材侧（左栏）的分类管理入口。</summary>
    private async void ManageMaterialCategories_Click(object sender, RoutedEventArgs e)
        => await ManageCategoriesForAsync(ClipKind.CutReference);

    private async Task ManageCategoriesForAsync(ClipKind sideKind)
    {
        var sideName = sideKind == ClipKind.Generated ? "已生成" : "素材";
        var list = VideoPool.Categories(sideKind);
        var panel = new StackPanel { Spacing = 10 };

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var nameBox = new TextBox { PlaceholderText = "新分类名", Width = 220 };
        var addBtn = new Button { Content = "新建" };
        addRow.Children.Add(nameBox);
        addRow.Children.Add(addBtn);

        panel.Children.Add(new TextBlock { Text = "新建分类", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(addRow);

        panel.Children.Add(new TextBlock
        {
            Text = list.Count == 0
                ? $"还没有{sideName}分类，先在上面新建一个。"
                : $"{sideName}分类（可改名或删除）",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        });

        var host = new StackPanel { Spacing = 6 };
        foreach (var c in list)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var editor = new TextBox { Text = c, Width = 200 };
            var save = new Button { Content = "改名" };
            var del = new Button { Content = "删除" };

            var captured = c;
            save.Click += (_, _) =>
            {
                var to = editor.Text.Trim();
                if (to.Length == 0) return;
                var n = VideoPool.RenameCategory(sideKind, captured, to);
                ShowResult(n == 0
                    ? "改名没有变化（名字为空或与原分类相同）。"
                    : $"「{captured}」已改名为「{to}」，影响 {n} 条素材。");
                Refresh();
                RefreshCategoryFilters();
            };

            del.Click += (_, _) =>
            {
                _ = DeleteCategoryFlowAsync(captured, sideKind);
            };

            row.Children.Add(editor);
            row.Children.Add(save);
            row.Children.Add(del);
            host.Children.Add(row);
        }
        panel.Children.Add(host);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"管理分类 · {sideName}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary
        };

        addBtn.Click += (_, _) =>
        {
            var raw = nameBox.Text;
            if (VideoPool.AddCategory(sideKind, raw))
            {
                nameBox.Text = "";
                ShowResult($"已新建分类「{raw.Trim()}」。");
                list = VideoPool.Categories(sideKind);
                Refresh();
                RefreshCategoryFilters();
                // 分类列表是快照，新建后要刷新出来：关掉再打开这个对话框。
                dialog.Hide();
                DispatcherQueue.TryEnqueue(() => _ = ManageCategoriesForAsync(sideKind));
            }
            else
            {
                ShowResult($"「{raw.Trim()}」建不了：名字为空、与「未分类」冲突，或已经有同名分类。",
                    InfoBarSeverity.Warning);
            }
        };

        await dialog.ShowAsync();
        Refresh();
        RefreshCategoryFilters();
    }

    /// <summary>删除分类：先问素材去哪，再删。</summary>
    private async Task DeleteCategoryFlowAsync(string name, ClipKind sideKind)
    {
        var count = 0;
        foreach (var item in VideoPool.Items)
        {
            if (string.Equals(item.Category, name, StringComparison.CurrentCultureIgnoreCase)) count++;
        }

        var others = VideoPool.Categories(sideKind)
            .Where(c => !string.Equals(c, name, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        var choices = new List<string> { "移出到未分类" };
        choices.AddRange(others.Select(o => $"合并到「{o}」"));

        var pick = new ComboBox
        {
            Header = count == 0 ? "这个分类下没有素材" : "这个分类下的素材怎么处理",
            ItemsSource = choices,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = count == 0
                ? $"删除空分类「{name}」。"
                : $"删除分类「{name}」，它下面有 {count} 条素材。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(pick);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除分类",
            Content = panel,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var target = "";
        if (count > 0 && pick.SelectedIndex > 0 && pick.SelectedItem is string label)
        {
            var open = label.IndexOf('「');
            var close = label.LastIndexOf('」');
            if (open >= 0 && close > open) target = label.Substring(open + 1, close - open - 1);
        }

        var moved = VideoPool.DeleteCategory(sideKind, name, target);
        Refresh();
        RefreshCategoryFilters();

        ShowResult(target.Length > 0
            ? $"已删除分类「{name}」，{moved} 条素材合并到「{target}」。"
            : moved == 0
                ? $"已删除空分类「{name}」。"
                : $"已删除分类「{name}」，{moved} 条素材移到未分类。");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id) return;

        // 只移除库条目，不删本地文件（素材可能还要用）。
        // VideoPool.Remove 内部会连带清理悬空的续接源选择。
        VideoPool.Remove(id);
        Refresh();
        ContinueSourceChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelectionText()
    {
        var picked = _pendingRows.Where(r => r.IsSelected).Select(r => r.Item).ToList();
        if (picked.Count == 0)
        {
            SelectionBlock.Text = "";
            return;
        }
        var names = string.Join("、", picked.Take(3).Select(i => ShortTitle(i.Title)));
        var more = picked.Count > 3 ? $" 等 {picked.Count} 段" : "";
        SelectionBlock.Text = $"已选素材：{names}{more}";
    }

    private void UpdateContinueHint()
    {
        var src = VideoPool.SelectedContinueClip;
        if (src is null)
        {
            ContinueHint.Text = "右栏点一段可设为续接源；续接时用它尾部当首片段（first_clip）。";
            return;
        }

        var dur = src.DurationSeconds > 0 ? $"{src.DurationSeconds:0.#}s" : "时长未知";
        ContinueHint.Text = $"续接源：{ShortTitle(src.Title)}（{dur}）。" +
            "生成时会自动取它尾部再上传，不需要手动导出视频。";
    }

    private static string ShortTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "(无标题)";
        var t = title.Trim();
        return t.Length <= 12 ? t : t[..12] + "…";
    }
}

/// <summary>
/// 卡片行模型。刻意用 {Binding}（x:Bind 在本项目会打崩 XAML 编译器）。
/// </summary>
public sealed class ClipBinRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isContinueSource;
    private ImageSource? _thumb;
    private double _cardWidth = 150;

    public ClipBinRow(VideoPoolItem item) => Item = item;

    public VideoPoolItem Item { get; }
    public string ItemId => Item.Id;

    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? "(无标题)" : Item.Title;

    public string SourceLabel => Item.Kind switch
    {
        ClipKind.CutReference => string.IsNullOrWhiteSpace(Item.SegmentLabel)
            ? "裁切片段"
            : $"裁切 · {Item.SegmentLabel}",
        ClipKind.Imported => "原始导入",
        _ => string.IsNullOrWhiteSpace(Item.SegmentLabel)
                ? (string.IsNullOrWhiteSpace(Item.Source) ? "生成" : Item.Source)
                : $"{Item.Source} · {Item.SegmentLabel}"
    };

    public string DurationLabel => Item.DurationSeconds <= 0 ? "时长未知" : $"{Item.DurationSeconds:0.#}s";

    public string StatusLabel => Item.Kind switch
    {
        ClipKind.CutReference => "裁切片段",
        ClipKind.Imported => "原始导入",
        _ => Item.CanContinue ? "可续接" : "生成结果"
    };

    /// <summary>分类标签：未分类时不显示。</summary>
    public string CategoryLabel => string.IsNullOrWhiteSpace(Item.Category)
        ? VideoPool.UncategorizedLabel
        : Item.Category;

    public Visibility CategoryVisibility => string.IsNullOrWhiteSpace(Item.Category)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public ImageSource? Thumb
    {
        get => _thumb;
        set { if (!ReferenceEquals(_thumb, value)) { _thumb = value; OnPropertyChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardBorderBrush));
            OnPropertyChanged(nameof(CardBorderThickness));
        }
    }

    /// <summary>是否为右栏当前续接源（选中态要明显：换边框色 + 角标）。</summary>
    public bool IsContinueSource
    {
        get => _isContinueSource;
        set
        {
            if (_isContinueSource == value) return;
            _isContinueSource = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardBorderBrush));
            OnPropertyChanged(nameof(CardBorderThickness));
            OnPropertyChanged(nameof(ContinueBadgeVisibility));
        }
    }

    public Visibility ContinueBadgeVisibility => IsContinueSource ? Visibility.Visible : Visibility.Collapsed;

    public double CardWidth
    {
        get => _cardWidth;
        set
        {
            if (Math.Abs(_cardWidth - value) < 0.5) return;
            _cardWidth = value;
            OnPropertyChanged();
        }
    }

    public Brush CardBorderBrush =>
        IsContinueSource ? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
        : IsSelected ? new SolidColorBrush(Microsoft.UI.Colors.MediumSeaGreen)
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public Thickness CardBorderThickness =>
        IsContinueSource || IsSelected ? new Thickness(2) : new Thickness(1);

    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(CategoryLabel));
        OnPropertyChanged(nameof(CategoryVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
