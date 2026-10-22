using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WanStudio.Services.Media;

namespace WanStudio.Services;

/// <summary>
/// 素材种类。统一素材库（Clip Bin）里三类东西同池共存，卡片网格靠这个区分。
/// </summary>
public enum ClipKind
{
    /// <summary>生成出来的视频：有公网 URL，可以当续接锚点 / 首片段。</summary>
    Generated,

    /// <summary>从长视频里切出来的参考片段：只有本地文件，没有公网 URL，不能当 first_clip。</summary>
    CutReference,

    /// <summary>用户本地导入的片段：只有本地文件，没有公网 URL，不能当 first_clip。</summary>
    Imported,

    /// <summary>
    /// 刚导入、还没裁切的原始视频。
    /// 与 <see cref="Imported"/> 的区别：这是「待处理」状态——用户还没决定要不要切、
    /// 切成多少秒。裁切后才转成 <see cref="CutReference"/> 进素材片段区。
    /// 它不进「素材片段」，单独显示在「原视频」区。
    /// </summary>
    RawVideo
}

/// <summary>
/// 统一素材库（Clip Bin）里的一条：剪切出来的参考片段和生成出来的视频同池。
/// 池会落盘（见 <see cref="VideoPool"/>），并且绑定到 UI（ObservableCollection），
/// 所以只能在 UI 线程增删。
/// </summary>
public sealed class VideoPoolItem : INotifyPropertyChanged
{
    private string? _thumbPath;
    private double _durationSeconds;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>任务提示词前 24 字，取不到时用 SegmentLabel / 文件名。</summary>
    public string Title { get; init; } = "";

    public string LocalPath { get; init; } = "";

    /// <summary>
    /// 接口返回的 video_url，<b>DashScope 的临时地址，只有 24 小时有效期</b>。
    /// 过期后这个字段还在（池会持久化它），但地址已经取不到内容：
    ///  - 「直接拿它当 first_clip 提交」那条路（LongVideoPage 的「首片段」选择）会失败；
    ///  - 链式续接（<see cref="ClipAnchorSpec"/>）不受影响——runner 是从
    ///    <see cref="LocalPath"/> 现裁尾段再重新上传换新的 oss:// 地址。
    /// 所以这里只做注释提示，不按时间把 <see cref="CanContinue"/> 关掉：
    /// 关掉会连累续接路径，而那条路明明还能用。
    /// 剪切片段与本地导入的条目没有公网 URL，这个字段恒为 null。
    /// </summary>
    public string? RemoteUrl { get; init; }

    /// <summary>缩略图路径，懒加载，由页面回填；不持久化（重启后重新生成）。</summary>
    public string? ThumbPath
    {
        get => _thumbPath;
        set
        {
            if (_thumbPath == value) return;
            _thumbPath = value;
            OnPropertyChanged();
        }
    }

    /// <summary>文生 / 图生 / 人物替换 / 动作参考 / 续接 / 剪切 / 导入。</summary>
    public string Source { get; init; } = "导入";

    /// <summary>例如 "段 03 · 00:10 – 00:15"，或剪切来源 "参考视频 A · 00:00–00:30"。</summary>
    public string? SegmentLabel { get; init; }

    /// <summary>
    /// 分类（用户可自建）。空串表示「未分类」。
    /// 只用于素材库组织，不参与生成逻辑。
    /// </summary>
    public string Category { get; set; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// 旧字段，保留兼容老 pool.json 与老调用点。
    /// <b>新逻辑一律以 <see cref="Kind"/> 为准</b>：<c>IsImported == true</c> 就等价于
    /// <see cref="ClipKind.Imported"/>（恢复时会双向归一，见 <see cref="VideoPool"/>）。
    /// </summary>
    public bool IsImported { get; init; }

    /// <summary>
    /// 素材种类：生成 / 剪切参考 / 本地导入。
    /// 默认 <see cref="ClipKind.Generated"/>，剪切与导入的入口会显式给值。
    /// </summary>
    public ClipKind Kind { get; init; } = ClipKind.Generated;

    /// <summary>
    /// 探测到的时长（秒），用于卡片上显示、以及 r2v 的 30s 上限校验。
    /// 恢复自老 pool.json 时为 0，UI 侧需要时探测回填。
    /// 故意做成可写（而不是 init-only）：回填发生在条目已经进池之后，
    /// 并且要触发 PropertyChanged 让卡片上的时长文字跟着更新。
    /// </summary>
    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            var v = double.IsFinite(value) && value > 0 ? value : 0;
            if (Math.Abs(_durationSeconds - v) < 0.001) return;
            _durationSeconds = v;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 能不能当续接锚点 / 首片段。语义未改：
    /// 只有「有公网 URL 且不是本地导入」的条目可以。
    /// 剪切片段 <see cref="RemoteUrl"/> 恒为 null，所以自动落进不可续接那一类。
    /// </summary>
    public bool CanContinue => !IsImported && !string.IsNullOrWhiteSpace(RemoteUrl);

    /// <summary>不能续接时的中文原因（能续接时为空串）。</summary>
    public string ContinueBlockReason
    {
        get
        {
            if (CanContinue) return "";
            if (Kind == ClipKind.CutReference)
                return "剪切出来的参考片段只有本地文件，没有公网 URL，不能作为首片段（first_clip）提交。";
            if (Kind == ClipKind.Imported || IsImported)
                return "本地导入的片段没有公网 URL，不能作为首片段（first_clip）提交。";
            return "这个片段没有拿回公网 video_url（例如只存在本地或接口未返回地址），不能作为首片段（first_clip）提交。";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 统一素材库（Clip Bin）：剪切出来的参考片段 + 生成出来的视频 + 本地导入，同一个池。
/// <b>会持久化</b>：
/// <list type="bullet">
/// <item>落盘路径：<c>%LOCALAPPDATA%\WanStudio\pool.json</c>（与 settings.json 同目录）。</item>
/// <item>只存可恢复字段：Id / Title / LocalPath / RemoteUrl / Source / SegmentLabel / CreatedAt /
///     IsImported / Kind / DurationSeconds。ThumbPath 不存，重启后由页面懒加载回填。</item>
/// <item>启动恢复：文件不存在、为空或解析失败 → 静默空池，绝不因为池文件崩掉应用。</item>
/// <item>老 pool.json 兼容：没有 <c>Kind</c> 键时按 <c>IsImported</c> 推断
///     （true→Imported，false→Generated），没有 <c>DurationSeconds</c> 键时取 0。
///     两个字段都允许缺省，所以老文件不会解析失败。</item>
/// <item>恢复时<b>跳过 LocalPath 已经不存在的条目</b>：文件被删/被挪走的条目直接丢弃
///     （留着它只会在续接时抛 FileNotFoundException），并把清掉的结果回写一次。</item>
/// <item>顺序：落盘按内存顺序写，恢复按文件顺序 Append，所以重启后仍是「最近的在最前」。</item>
/// </list>
///
/// 为什么值得持久化：链式续接的锚点来源就是池里的片段。池一重启就空，
/// 昨天生成的片子今天就接不上了。
///
/// <b>24 小时限制</b>：池里的 RemoteUrl 是 DashScope 的临时地址，24h 后失效。
/// 所以「直接拿池条目的 URL 当 first_clip 提交」隔天可能失败；但链式续接是从
/// LocalPath 现裁尾段重新上传，不受影响。详见 <see cref="VideoPoolItem.RemoteUrl"/>。
///
/// 线程约定（不能破）：Items 直接绑 UI，所有增删必须在 UI 线程调用；
/// VideoJobRunner 用 DispatcherQueue 的 Ui(...) 包住 AddFromTask。
/// 落盘就在这条 UI 线程上同步写（几十条数据，量很小），
/// <b>不引入后台线程去动 Items</b>，也不做异步改集合。
/// </summary>
public static class VideoPool
{
    /// <summary>落盘串行化用。UI 线程同步写，加锁只是防止重入/并发写坏文件。</summary>
    private static readonly object Gate = new();

    /// <summary>pool.json 的结构版本号，将来改字段时用来兼容旧文件。</summary>
    private const int SchemaVersion = 4;

    // ---------------------------------------------------------- 裁切秒数（设置项）

    /// <summary>裁切秒数下限，与 <see cref="ReferenceCutter.MinReferenceSeconds"/> 一致（1s）。</summary>
    public const double MinClipCutSeconds = ReferenceCutter.MinReferenceSeconds;

    /// <summary>裁切秒数上限，与 <see cref="ReferenceCutter.MaxReferenceSeconds"/> 一致（30s）。</summary>
    public const double MaxClipCutSeconds = ReferenceCutter.MaxReferenceSeconds;

    /// <summary>
    /// 默认裁切秒数（产品定 13s）。
    /// 不跟随 ReferenceCutter 的 25s：25s 会一上来就顶到 30s 上限，
    /// 每段太长不好挑；13s 更接近「一段完整动作」的长度。
    /// </summary>
    public const double DefaultClipCutSeconds = 13.0;

    /// <summary>素材库顶部「裁切成每段多少秒」。持久化在 pool.json 根上。</summary>
    private static double _clipCutSeconds = DefaultClipCutSeconds;

    /// <summary>当前选中的续接源条目 Id（只内存，不持久化）。</summary>
    private static string? _selectedContinueClipId;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文不转义成 \uXXXX：pool.json 是给人看得懂的本地数据，出问题时要能直接打开看。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 枚举写成 "CutReference" 而不是 1：同上，文件要能直接读。
        Converters = { new JsonStringEnumConverter() }
    };

    public static ObservableCollection<VideoPoolItem> Items { get; } = new();

    /// <summary>
    /// 池落盘路径。跟着 AppPaths 走：LOCALAPPDATA 写不进去时自动回退，
    /// 保证素材库不会因为目录权限问题整个丢失。
    /// </summary>
    private static string PoolPath
    {
        get
        {
            try
            {
                return Path.Combine(AppPaths.WanStudioRoot, "pool.json");
            }
            catch
            {
                // 所有候选都不可写：退回 TEMP，至少让本次会话能用。
                return Path.Combine(Path.GetTempPath(), "WanStudio", "pool.json");
            }
        }
    }

    // ---------------------------------------------------------- 左半 / 右半分组

    /// <summary>
    /// <b>左半</b>：已经裁切完、还没生成过的片段（<see cref="ClipKind.CutReference"/>）。
    /// 这些是「素材候选」，只有本地文件、没有公网 URL，所以不能当 first_clip。
    ///
    /// <para>
    /// <b>语义（别改成「用一次就删」）</b>：一条裁切片被拿去生成之后，
    /// 它<b>不会</b>变成 Generated，也<b>不会</b>离开左半——生成产物是一条<b>新的</b>
    /// <see cref="ClipKind.Generated"/> 条目进右半。左半条目是素材，可以反复用，
    /// 只有用户自己点删除才会消失。
    /// </para>
    ///
    /// 返回快照列表，UI 可以直接绑；每次访问重新算，别在循环里反复取。
    /// </summary>
    public static IReadOnlyList<VideoPoolItem> PendingClips => Snapshot(ClipKind.CutReference);

    /// <summary>
    /// <b>右半</b>：已经生成好的视频（<see cref="ClipKind.Generated"/>）。
    /// 只有这一半能当续接源（有公网 URL），配合 <see cref="SelectedContinueClipId"/> 选择。
    /// 返回快照列表，UI 可以直接绑；每次访问重新算，别在循环里反复取。
    /// </summary>
    public static IReadOnlyList<VideoPoolItem> GeneratedClips => Snapshot(ClipKind.Generated);

    /// <summary>
    /// <see cref="PendingClips"/> 的旧名，保留兼容老调用点。新代码请用 <see cref="PendingClips"/>。
    /// </summary>
    public static IReadOnlyList<VideoPoolItem> CutReferences => Snapshot(ClipKind.CutReference);

    /// <summary>
    /// <see cref="GeneratedClips"/> 的旧名，保留兼容老调用点。新代码请用 <see cref="GeneratedClips"/>。
    /// </summary>
    public static IReadOnlyList<VideoPoolItem> Generated => Snapshot(ClipKind.Generated);

    // ---------------------------------------------------------- 裁切秒数 / 续接源选择

    /// <summary>
    /// 素材库顶部「裁切成每段多少秒」。默认 25（= <see cref="ReferenceCutter.DefaultChunkSeconds"/>），
    /// 写入时夹到 [1, 30]（= 官方 reference_video 窗口），非法值回落默认。
    /// <b>持久化在 pool.json 根上</b>（不是 AppSettings），赋值即落盘。
    /// 必须在 UI 线程读写（落盘在 UI 线程同步写）。
    /// </summary>
    public static double ClipCutSeconds
    {
        get => _clipCutSeconds;
        set
        {
            var v = double.IsFinite(value)
                ? Math.Clamp(value, MinClipCutSeconds, MaxClipCutSeconds)
                : DefaultClipCutSeconds;
            if (Math.Abs(_clipCutSeconds - v) < 0.001) return;
            _clipCutSeconds = v;
            SaveToDisk();   // 设置项自己也要落盘（不经过 Items，所以不会触发 CollectionChanged）
        }
    }

    /// <summary>
    /// 右半里当前选中的续接源 Id；null = 没选。
    /// 赋值时校验：必须是 <see cref="ClipKind.Generated"/> 且 <c>LocalPath</c> 真实存在，
    /// 不合法就落成 null（不抛异常）——UI 传了过期/错的 Id 不会把界面搞崩。
    /// <b>只在内存里，不持久化</b>：重启后由 UI 重新选，避免恢复出一个用户没见过的选中态。
    /// 必须在 UI 线程读写。
    /// </summary>
    public static string? SelectedContinueClipId
    {
        get => _selectedContinueClipId;
        set
        {
            var id = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
            _selectedContinueClipId = IsUsableContinueSource(id) ? id : null;
        }
    }

    /// <summary>
    /// 当前选中的续接源条目；没选、或条目已失效时返回 null。
    /// 读取时顺手清理悬空 Id（条目被删/文件被外部删掉），避免 UI 拿着一个指向不存在条目的 Id。
    /// 必须在 UI 线程读。
    /// </summary>
    public static VideoPoolItem? SelectedContinueClip
    {
        get
        {
            var id = _selectedContinueClipId;
            if (id is null) return null;

            if (IsUsableContinueSource(id)) return Find(id);

            _selectedContinueClipId = null;   // 悬空 → 清掉
            return null;
        }
    }

    /// <summary>
    /// 能否当续接源：必须是生成出来的条目、且本地文件还在。
    /// （续接要拿它的尾巴去裁+上传，本地文件没了就没法做。）
    /// </summary>
    private static bool IsUsableContinueSource(string? id)
    {
        if (id is null) return false;

        var item = Find(id);
        return item is not null
            && item.Kind == ClipKind.Generated
            && !string.IsNullOrWhiteSpace(item.LocalPath)
            && File.Exists(item.LocalPath);
    }

    /// <summary>条目被删 / 池被清空后，把指向它的续接源选择清掉。</summary>
    private static void PruneSelection()
    {
        var id = _selectedContinueClipId;
        if (id is null) return;
        if (!IsUsableContinueSource(id)) _selectedContinueClipId = null;
    }

    /// <summary>
    /// 第一次访问 VideoPool 时恢复一次。首次访问发生在页面构造 / runner 的 Ui(...)，
    /// 都是 UI 线程，此时还没有任何控件绑到 Items，所以恢复期填充集合是安全的。
    /// 整个恢复过程吞掉所有异常：池文件坏了只是回到空池。
    /// </summary>
    static VideoPool()
    {
        try
        {
            RestoreFromDisk();
        }
        catch
        {
            // pool.json 损坏 / 无权限 / 磁盘异常 → 静默空池，不崩。
        }

        // 挂上落盘：Insert / RemoveAt / Clear 都会走到这里，所以「清空池也要落盘」自动满足。
        // 必须在恢复之后挂，否则恢复过程会立刻反向写一次文件。
        // 顺便清理悬空的续接源选择（条目被删/池被清空时 SelectedContinueClipId 不该留着）。
        Items.CollectionChanged += (_, _) =>
        {
            PruneSelection();
            SaveToDisk();
        };
    }

    /// <summary>
    /// 任务成功后入池（Kind = Generated）。幂等：同一个 task.Id 只入池一次，重复调用返回已有条目。
    /// 必须在 UI 线程调用（Items 绑定 UI）。入池会触发一次落盘。
    /// </summary>
    public static VideoPoolItem AddFromTask(VideoTaskItem task)
    {
        ArgumentNullException.ThrowIfNull(task);

        foreach (var existing in Items)
        {
            if (string.Equals(existing.Id, task.Id, StringComparison.Ordinal)) return existing;
        }

        var item = new VideoPoolItem
        {
            Id = task.Id,
            Title = BuildTitle(task),
            LocalPath = task.LocalPath ?? "",
            RemoteUrl = task.VideoUrl,
            Source = task.ModeLabel,
            SegmentLabel = task.SegmentLabel,
            CreatedAt = task.CreatedAt,
            IsImported = false,
            Kind = ClipKind.Generated
        };
        Items.Insert(0, item);   // CollectionChanged → SaveToDisk()
        return item;
    }

    /// <summary>
    /// 本地导入一个已存在的视频文件（Kind = Imported，不可续接）。
    /// 入池会触发一次落盘。
    /// </summary>
    public static VideoPoolItem AddImported(string localPath, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new InvalidOperationException("导入片段需要本地文件路径。");

        var full = Path.GetFullPath(localPath);
        foreach (var existing in Items)
        {
            if (existing.IsImported && string.Equals(existing.LocalPath, full, StringComparison.OrdinalIgnoreCase))
                return existing;
        }

        var item = new VideoPoolItem
        {
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(full) : title!.Trim(),
            LocalPath = full,
            RemoteUrl = null,
            Source = "导入",
            IsImported = true,
            Kind = ClipKind.Imported
        };
        Items.Insert(0, item);   // CollectionChanged → SaveToDisk()
        return item;
    }

    /// <summary>
    /// 剪切出来的参考片段入池（Kind = CutReference，没有公网 URL，不能当 first_clip）。
    /// 幂等：同一个 LocalPath（忽略大小写）已经在池里就直接返回已有条目，不会多出一张卡片。
    /// 入池会触发一次落盘。必须在 UI 线程调用。
    /// </summary>
    /// <param name="localPath">剪切产物文件（本地路径）。</param>
    /// <param name="sourceLabel">来源说明，例如 "参考视频 A · 00:00–00:30"。</param>
    /// <param name="durationSeconds">片段时长（秒），卡片显示与 30s 上限校验用；未知传 0。</param>
    /// <param name="title">卡片标题；不传则用 sourceLabel，再不行用文件名。</param>
    /// <summary>
    /// 导入一个原始视频（未裁切）。只入池、不切段——用户自己决定要不要切。
    /// 幂等：同一路径已存在就直接返回旧条目。
    /// </summary>
    public static VideoPoolItem AddRawVideo(string localPath, double durationSeconds, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new InvalidOperationException("导入视频需要本地文件路径。");

        var full = Path.GetFullPath(localPath);
        foreach (var existing in Items)
        {
            if (string.Equals(existing.LocalPath, full, StringComparison.OrdinalIgnoreCase)) return existing;
        }

        var item = new VideoPoolItem
        {
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(full) : title!.Trim(),
            LocalPath = full,
            RemoteUrl = null,
            Source = "原视频",
            SegmentLabel = null,
            IsImported = false,
            Kind = ClipKind.RawVideo,
            DurationSeconds = durationSeconds
        };
        Items.Insert(0, item);
        return item;
    }

    /// <summary>还没裁切的原始视频（用户可决定是否切、切多少秒）。</summary>
    public static IReadOnlyList<VideoPoolItem> RawVideos => Snapshot(ClipKind.RawVideo);

    public static VideoPoolItem AddCutReference(
        string localPath,
        string sourceLabel,
        double durationSeconds,
        string? title = null)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            throw new InvalidOperationException("剪切片段需要本地文件路径。");

        var full = Path.GetFullPath(localPath);

        foreach (var existing in Items)
        {
            if (string.Equals(existing.LocalPath, full, StringComparison.OrdinalIgnoreCase))
                return existing;
        }

        var label = string.IsNullOrWhiteSpace(sourceLabel) ? null : sourceLabel.Trim();

        var item = new VideoPoolItem
        {
            Title = string.IsNullOrWhiteSpace(title)
                ? (label ?? Path.GetFileNameWithoutExtension(full))
                : title!.Trim(),
            LocalPath = full,
            RemoteUrl = null,
            Source = "剪切",
            SegmentLabel = label,
            IsImported = false,
            Kind = ClipKind.CutReference,
            DurationSeconds = durationSeconds
        };
        Items.Insert(0, item);   // CollectionChanged → SaveToDisk()
        return item;
    }

    /// <summary>
    /// 卡片上的删除：把条目从池里拿掉并落盘（RemoveAt → CollectionChanged → SaveToDisk）。
    /// <b>只删池条目，不删本地文件</b>——用户删的是一张卡片，不是硬盘上的素材；
    /// 真要清文件请走「打开文件夹」。找不到该 id 时静默返回。
    /// 必须在 UI 线程调用。
    /// </summary>
    public static void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        for (var i = 0; i < Items.Count; i++)
        {
            if (!string.Equals(Items[i].Id, id, StringComparison.Ordinal)) continue;
            Items.RemoveAt(i);   // CollectionChanged → SaveToDisk()
            return;
        }
    }

    public static VideoPoolItem? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var item in Items)
        {
            if (string.Equals(item.Id, id, StringComparison.Ordinal)) return item;
        }
        return null;
    }

    // ---------------------------------------------------------------- 分类

    /// <summary>「未分类」的显示名与内部值（内部用空串，显示出来要有字）。</summary>
    public const string UncategorizedLabel = "未分类";

    /// <summary>
    /// 左栏（素材）与右栏（已生成）是<b>两套独立的分类体系</b>：
    /// 给素材建的分类不会出现在已生成的筛选里，反之亦然。
    ///
    /// 为什么必须分开：两侧装的是不同性质的东西——左边是待生成的原料，
    /// 右边是成品（续接源）。共用一个分类列表会让「按分类找成品」被原料分类污染。
    ///
    /// 内部按 <see cref="ClipKind"/> 分组存；Imported 归到素材侧（它也是原料）。
    /// </summary>
    private static readonly Dictionary<ClipKind, List<string>> _declaredCategories = new()
    {
        [ClipKind.CutReference] = new List<string>(),
        [ClipKind.Generated] = new List<string>()
    };

    /// <summary>把 Imported 归到素材侧（原料），保证两侧语义只有「原料 / 成品」。</summary>
    private static ClipKind CategorySide(ClipKind kind)
        => kind == ClipKind.Generated ? ClipKind.Generated : ClipKind.CutReference;

    /// <summary>某一侧的全部可用分类：显式创建的 + 该侧条目实际用到的，去重排序。</summary>
    public static IReadOnlyList<string> Categories(ClipKind kind)
    {
        var side = CategorySide(kind);
        var set = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var c in _declaredCategories[side])
        {
            if (!string.IsNullOrWhiteSpace(c)) set.Add(c.Trim());
        }
        foreach (var item in Items)
        {
            if (CategorySide(item.Kind) != side) continue;
            if (!string.IsNullOrWhiteSpace(item.Category)) set.Add(item.Category.Trim());
        }
        return set.ToList();
    }

    /// <summary>在某一侧新建分类。名字为空、重名（忽略大小写）时返回 false。</summary>
    public static bool AddCategory(ClipKind kind, string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return false;
        if (n == UncategorizedLabel) return false;

        var side = CategorySide(kind);
        foreach (var c in Categories(side))
        {
            if (string.Equals(c, n, StringComparison.CurrentCultureIgnoreCase)) return false;
        }

        _declaredCategories[side].Add(n);
        SaveToDisk();
        return true;
    }

    /// <summary>
    /// 在某一侧删除一个分类。<paramref name="moveItemsTo"/> 指定该分类下的条目怎么处理：
    /// 传 null → 移到未分类；传另一个分类名 → 合并过去。返回被处理的条目数。
    /// </summary>
    public static int DeleteCategory(ClipKind kind, string? name, string? moveItemsTo = null)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return 0;

        var side = CategorySide(kind);
        var target = (moveItemsTo ?? "").Trim();
        if (string.Equals(target, n, StringComparison.CurrentCultureIgnoreCase)) target = "";

        var affected = 0;
        foreach (var item in Items)
        {
            if (CategorySide(item.Kind) != side) continue;
            if (!string.Equals(item.Category, n, StringComparison.CurrentCultureIgnoreCase)) continue;
            item.Category = target;
            affected++;
        }

        _declaredCategories[side].RemoveAll(c => string.Equals(c, n, StringComparison.CurrentCultureIgnoreCase));
        SaveToDisk();
        return affected;
    }

    /// <summary>在某一侧重命名分类；<paramref name="to"/> 为空表示并入未分类。</summary>
    public static int RenameCategory(ClipKind kind, string from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from)) return 0;
        var target = (to ?? "").Trim();
        if (string.Equals(from.Trim(), target, StringComparison.CurrentCultureIgnoreCase)) return 0;

        var side = CategorySide(kind);
        var changed = 0;
        foreach (var item in Items)
        {
            if (CategorySide(item.Kind) != side) continue;
            if (!string.Equals(item.Category, from.Trim(), StringComparison.CurrentCultureIgnoreCase)) continue;
            item.Category = target;
            changed++;
        }

        var declared = _declaredCategories[side];
        for (var i = 0; i < declared.Count; i++)
        {
            if (!string.Equals(declared[i], from.Trim(), StringComparison.CurrentCultureIgnoreCase)) continue;
            declared[i] = target;
        }
        declared.RemoveAll(c => string.IsNullOrWhiteSpace(c));

        if (changed > 0 || declared.Count >= 0) SaveToDisk();
        return changed;
    }

    /// <summary>给一个条目改分类（空串 = 未分类），改完落盘。</summary>
    public static bool SetCategory(string id, string? category)
    {
        var item = Find(id);
        if (item is null) return false;

        var next = (category ?? "").Trim();
        if (string.Equals(item.Category, next, StringComparison.Ordinal)) return false;

        item.Category = next;

        // 用到的分类自动登记到它所属那一侧，避免改名时漏掉「只因为有条目用而存在」的分类。
        var side = CategorySide(item.Kind);
        if (next.Length > 0 && next != UncategorizedLabel &&
            !_declaredCategories[side].Exists(c => string.Equals(c, next, StringComparison.CurrentCultureIgnoreCase)))
        {
            _declaredCategories[side].Add(next);
        }

        // Category 是普通属性，不触发 CollectionChanged，这里显式落盘一次。
        SaveToDisk();
        return true;
    }

    /// <summary>把一批条目移到某个分类下。</summary>
    public static int SetCategory(IEnumerable<string> ids, string? category)
    {
        var changed = 0;
        foreach (var id in ids)
        {
            if (SetCategory(id, category)) changed++;
        }
        return changed;
    }

    /// <summary>按分类筛选；<paramref name="category"/> 为 null 表示不筛选。</summary>
    public static IReadOnlyList<VideoPoolItem> InCategory(string? category, ClipKind? kind = null)
    {
        var list = new List<VideoPoolItem>();
        foreach (var item in Items)
        {
            if (kind is not null && item.Kind != kind) continue;
            if (category is not null &&
                !string.Equals(item.Category, category.Trim(), StringComparison.CurrentCultureIgnoreCase)) continue;
            list.Add(item);
        }
        return list;
    }

    private static IReadOnlyList<VideoPoolItem> Snapshot(ClipKind kind)
    {
        var list = new List<VideoPoolItem>();
        foreach (var item in Items)
        {
            if (item.Kind == kind) list.Add(item);
        }
        return list;
    }

    private static string BuildTitle(VideoTaskItem task)
    {
        var prompt = (task.Prompt ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (prompt.Length > 24) prompt = prompt[..24] + "…";
        if (!string.IsNullOrWhiteSpace(prompt)) return prompt;
        if (!string.IsNullOrWhiteSpace(task.SegmentLabel)) return task.SegmentLabel!;
        return "(无提示词)";
    }

    // ------------------------------------------------------------ 落盘 / 恢复

    /// <summary>
    /// 从 pool.json 恢复。只恢复文件仍存在的条目；顺序按文件顺序（最近的在最前）。
    /// 有任何条目被丢弃就回写一次，避免每次启动都重扫死条目。
    /// 老文件缺 Kind / DurationSeconds 时按规则补默认值，不会解析失败。
    /// </summary>
    /// <summary>池恢复诊断日志（%TEMP%），用于排查「数据在文件里但界面读不到」。</summary>
    private static void RestoreTrace(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "wanstudio-pool.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private static void RestoreFromDisk()
    {
        var path = PoolPath;
        if (!File.Exists(path)) return;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return;

        PoolFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PoolFile>(json, JsonOpts);
        }
        catch
        {
            // 文件损坏 / 序列化被裁掉（见 csproj 里 PublishTrimmed 的说明）→ 静默空池。
            return;
        }
        if (file?.Items is null || file.Items.Count == 0) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dropped = 0;

        // 先恢复显式创建的分类，再恢复素材。
        // （放在条目循环里会导致：空分类丢失、每条都 Clear 一次、continue 时根本不执行。）
        _declaredCategories[ClipKind.CutReference].Clear();
        _declaredCategories[ClipKind.Generated].Clear();
        if (file.MaterialCategories is not null)
        {
            foreach (var c in file.MaterialCategories)
            {
                if (!string.IsNullOrWhiteSpace(c)) _declaredCategories[ClipKind.CutReference].Add(c.Trim());
            }
        }
        if (file.GeneratedCategories is not null)
        {
            foreach (var c in file.GeneratedCategories)
            {
                if (!string.IsNullOrWhiteSpace(c)) _declaredCategories[ClipKind.Generated].Add(c.Trim());
            }
        }

        foreach (var entry in file.Items)
        {
            if (entry is null)
            {
                dropped++;
                continue;
            }

            var id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id!;
            if (!seen.Add(id))          // 同一个 Id 只恢复一次（文件被手工改坏时的兜底）
            {
                dropped++;
                continue;
            }

            var local = entry.LocalPath ?? "";
            // 文件已经不在了 → 丢弃。容忍用户清理 outputs 目录。
            if (local.Length == 0 || !File.Exists(local))
            {
                dropped++;
                continue;
            }

            // 老 pool.json 没有 Kind：按 IsImported 推断（true→Imported，false→Generated）。
            var kind = entry.Kind ?? (entry.IsImported ? ClipKind.Imported : ClipKind.Generated);
            // 双向归一，保证不变量「IsImported 与 Kind.Imported 一致」：
            // 老文件里 IsImported=true 但（可能被手工改过）Kind=Generated 时，仍按导入处理。
            var isImported = entry.IsImported || kind == ClipKind.Imported;
            if (isImported) kind = ClipKind.Imported;

            var duration = double.IsFinite(entry.DurationSeconds) && entry.DurationSeconds > 0
                ? entry.DurationSeconds
                : 0;

            Items.Add(new VideoPoolItem
            {
                Id = id,
                Title = string.IsNullOrWhiteSpace(entry.Title)
                    ? Path.GetFileNameWithoutExtension(local)
                    : entry.Title!,
                LocalPath = local,
                RemoteUrl = string.IsNullOrWhiteSpace(entry.RemoteUrl) ? null : entry.RemoteUrl,
                Source = string.IsNullOrWhiteSpace(entry.Source) ? "导入" : entry.Source!,
                SegmentLabel = string.IsNullOrWhiteSpace(entry.SegmentLabel) ? null : entry.SegmentLabel,
                CreatedAt = entry.CreatedAt == default ? DateTimeOffset.Now : entry.CreatedAt,
                IsImported = isImported,
                Kind = kind,
                DurationSeconds = duration,
                Category = entry.Category ?? ""
            });
        }

        if (dropped > 0) SaveToDisk();
    }

    /// <summary>
    /// 同步落盘。由 Items 的 CollectionChanged 调用，所以一定在 UI 线程上跑。
    /// 先写 .tmp 再原子替换：进程中途退出不会留下半截 pool.json。
    /// 写失败只吞掉——内存池照常可用，下次增删再试。
    /// </summary>
    private static void SaveToDisk()
    {
        lock (Gate)
        {
            try
            {
                var path = PoolPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var file = new PoolFile { Version = SchemaVersion };
                file.MaterialCategories = new List<string>(_declaredCategories[ClipKind.CutReference]);
                file.GeneratedCategories = new List<string>(_declaredCategories[ClipKind.Generated]);

                foreach (var item in Items)
                {
                    file.Items.Add(new PoolEntry
                    {
                        Id = item.Id,
                        Title = item.Title,
                        LocalPath = item.LocalPath,
                        RemoteUrl = item.RemoteUrl,
                        Source = item.Source,
                        SegmentLabel = item.SegmentLabel,
                        CreatedAt = item.CreatedAt,
                        IsImported = item.IsImported,
                        Kind = item.Kind,
                        DurationSeconds = item.DurationSeconds,
                        Category = item.Category
                    });
                }

                var json = JsonSerializer.Serialize(file, JsonOpts);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // 磁盘满 / 无权限 / 被占用：落盘失败不影响内存池。
            }
        }
    }

    // ------------------------------------------------------------ 落盘 DTO

    /// <summary>pool.json 的根对象。Version 留作将来兼容旧文件。</summary>
    private sealed class PoolFile
    {
        public int Version { get; set; } = SchemaVersion;
        public List<PoolEntry> Items { get; set; } = new();

        /// <summary>v4：素材侧（CutReference/Imported）的分类。</summary>
        public List<string> MaterialCategories { get; set; } = new();

        /// <summary>v4：已生成侧的分类。</summary>
        public List<string> GeneratedCategories { get; set; } = new();
    }

    /// <summary>
    /// pool.json 里的一条。Kind / DurationSeconds 是 v2 新增：
    /// 都写成可缺省，老文件（v1）读进来不会失败。
    /// </summary>
    private sealed class PoolEntry
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? LocalPath { get; set; }
        public string? RemoteUrl { get; set; }
        public string? Source { get; set; }
        public string? SegmentLabel { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsImported { get; set; }

        /// <summary>v2 新增。老文件没有这个键 → null → 按 IsImported 推断。</summary>
        public ClipKind? Kind { get; set; }

        /// <summary>v2 新增。老文件没有这个键 → 0，UI 需要时探测回填。</summary>
        public double DurationSeconds { get; set; }

        /// <summary>v4 新增。老文件没有这个键 → null → 视为未分类。</summary>
        public string? Category { get; set; }
    }
}
