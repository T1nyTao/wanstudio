using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WanStudio.Services;

public enum VideoTaskPhase
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled
}

public enum GenerationMode
{
    TextToVideo,
    ImageToVideo,
    CharacterReplace,
    MotionReference,

    /// <summary>官方首片段续写（media[0].type=first_clip），不是本地拼接。</summary>
    ContinueFromClip
}

public sealed class VideoTaskItem : INotifyPropertyChanged
{
    private string _statusText = "排队中";
    private VideoTaskPhase _phase = VideoTaskPhase.Queued;
    private double _progress;
    private string? _localPath;
    private string? _error;
    private string? _videoUrl;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public GenerationMode Mode { get; init; } = GenerationMode.TextToVideo;
    public string Prompt { get; init; } = "";
    public string Model { get; init; } = "";
    public string Resolution { get; init; } = "480P";
    public string Ratio { get; init; } = "adaptive";
    public int Duration { get; init; } = 5;
    public List<string> MediaPaths { get; init; } = new();

    // ---- 参考生视频（wan2.7-r2v）增量 ----
    // 官方：参考图像 + 参考视频 ≤ 5；每份素材只含单一角色，所以「多人」= 多张 reference_image。
    // prompt 里用「图1、图2、视频1」指代，顺序与这里一致（图像和视频分别计数）。

    /// <summary>参考图像（人物 / 主体 / 场景），对应 media[].type=reference_image。</summary>
    public List<string> ReferenceImages { get; init; } = new();

    /// <summary>参考视频，对应 media[].type=reference_video。官方限制 1–30s。</summary>
    public List<string> ReferenceVideos { get; init; } = new();

    /// <summary>
    /// 参考音色（reference_voice），与参考素材按顺序一一配对：
    /// 第 1 个音色挂到第 1 个参考素材（图像或视频）上，第 2 个挂到第 2 个，以此类推。
    /// 官方：wav/mp3、1–10s、≤15MB；只参考音色不看内容；与 reference_video 同时传时优先用它。
    /// 只有 wan2.7-r2v 支持。
    /// </summary>
    public List<string> ReferenceVoices { get; init; } = new();

    /// <summary>参考素材合计条数（图像 + 视频）。</summary>
    public int ReferenceCount => ReferenceImages.Count + ReferenceVideos.Count;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string? RemoteTaskId { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public bool IsDemo { get; init; }

    // ---- 续接（first_clip）增量 ----

    /// <summary>续接用：首片段的公网 http(s) URL，或 oss:// 临时地址。</summary>
    public string? FirstClipUrl { get; init; }

    /// <summary>可选尾帧（last_frame）。</summary>
    public string? LastFrameUrl { get; init; }

    /// <summary>
    /// 续接锚点：生成前用「上一段生成结果」的尾巴拼 first_clip。
    /// 为空表示不续接（独立生成）。
    /// </summary>
    public ClipAnchorSpec? Anchor { get; init; }

    /// <summary>例如 "段 03 · 00:10 – 00:15"，入池后带到池条目上。</summary>
    public string? SegmentLabel { get; init; }

    /// <summary>入池后回写的池条目 Id（成功后由 runner 在 UI 线程写）。</summary>
    public string? PoolItemId { get; set; }

    public string ModeLabel => Mode switch
    {
        GenerationMode.ImageToVideo => "图生",
        GenerationMode.CharacterReplace => "人物替换",
        GenerationMode.MotionReference => "动作参考",
        GenerationMode.ContinueFromClip => "续接",
        _ => "文生"
    };

    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(HasVideo)); } }
    }

    public VideoTaskPhase Phase
    {
        get => _phase;
        set { if (_phase != value) { _phase = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanRetry)); OnPropertyChanged(nameof(HasVideo)); } }
    }

    public double Progress
    {
        get => _progress;
        set { if (Math.Abs(_progress - value) > 0.001) { _progress = value; OnPropertyChanged(); } }
    }

    public string? LocalPath
    {
        get => _localPath;
        set { if (_localPath != value) { _localPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasVideo)); } }
    }

    public string? Error
    {
        get => _error;
        set { if (_error != value) { _error = value; OnPropertyChanged(); } }
    }

    public string? VideoUrl
    {
        get => _videoUrl;
        set { if (_videoUrl != value) { _videoUrl = value; OnPropertyChanged(); } }
    }

    public bool CanCancel => Phase is VideoTaskPhase.Queued or VideoTaskPhase.Running;
    public bool CanRetry => Phase is VideoTaskPhase.Failed or VideoTaskPhase.Canceled;
    public bool HasVideo => Phase == VideoTaskPhase.Succeeded && !string.IsNullOrEmpty(LocalPath);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 续接锚点：从一段已生成的视频里取「尾部若干秒」当 first_clip。
/// 这是链式续接的核心——第二段必须锚住第一段的尾巴，接缝才不会跳。
/// </summary>
/// <param name="SourceLocalPath">池里那段已生成视频的本地文件（已下载）。</param>
/// <param name="SourcePoolItemId">池条目 Id，用于 UI 回显与去重。</param>
/// <param name="TailSeconds">要取多少秒尾巴；会被夹到 first_clip 允许的 2–10s。</param>
/// <param name="SourceDurationSeconds">来源时长，用于判断是否需要裁切。</param>
public sealed record ClipAnchorSpec(
    string SourceLocalPath,
    string SourcePoolItemId,
    double TailSeconds,
    double SourceDurationSeconds)
{
    /// <summary>来源本身就在 first_clip 窗口内，可以整段直接上传，不必裁。</summary>
    public bool CanUseWhole => SourceDurationSeconds >= 2.0 && SourceDurationSeconds <= 10.0;

    /// <summary>实际要提交的片段时长。</summary>
    public double EffectiveSeconds => CanUseWhole
        ? SourceDurationSeconds
        : Math.Min(TailSeconds, Math.Max(0, SourceDurationSeconds));
}

/// <summary>
/// 任务列表 + 落盘。
///
/// 为什么必须落盘：任务原先只存在内存里，关掉应用就全没了——用户会以为「任务丢了」。
/// 现在写 %LOCALAPPDATA%\WanStudio\tasks.json，并遵守两条规则：
///  1. 启动时读回来；关应用时还在跑的任务标成「已中断」，不留在「进行中」卡住；
///  2. 防抖写盘（进度会高频更新），先写 .tmp 再替换，避免写一半崩掉毁掉历史。
/// </summary>
public static class VideoTaskStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文不转义，出问题能直接打开看。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly object Gate = new();
    private static System.Threading.Timer? _saveTimer;
    private static bool _loaded;

    /// <summary>
    /// 任何入口（页面绑定 Tasks、Add、Find）第一次碰到这个类型时就把历史读回来。
    /// 放在静态构造里是为了不依赖调用方记得先调 EnsureLoaded。
    /// </summary>
    static VideoTaskStore() => EnsureLoaded();

    /// <summary>最多保留多少条历史，防止文件无限增长。</summary>
    private const int MaxItems = 300;

    public static ObservableCollection<VideoTaskItem> Tasks { get; } = new();

    private static string StorePath
    {
        get
        {
            try
            {
                return Path.Combine(AppPaths.WanStudioRoot, "tasks.json");
            }
            catch
            {
                // 所有候选都不可写：退回 TEMP，至少让本次会话能用。
                return Path.Combine(Path.GetTempPath(), "WanStudio", "tasks.json");
            }
        }
    }

    public static VideoTaskItem Add(VideoTaskItem item)
    {
        EnsureLoaded();
        Tasks.Insert(0, item);
        Hook(item);
        TrimHistory();
        RequestSave();
        return item;
    }

    public static VideoTaskItem? Find(string id)
    {
        EnsureLoaded();
        foreach (var t in Tasks)
        {
            if (t.Id == id) return t;
        }
        return null;
    }

    /// <summary>把上次的任务读回来。幂等，应用启动时调一次即可。</summary>
    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            try { RestoreFromDisk(); }
            catch { /* 文件坏了就当空列表，不阻断启动 */ }
        }
    }

    /// <summary>立刻写盘（退出前可以主动调一次）。</summary>
    public static void SaveNow()
    {
        try
        {
            var dtos = new List<TaskDto>();
            foreach (var t in Tasks) dtos.Add(TaskDto.From(t));

            var json = JsonSerializer.Serialize(dtos, JsonOpts);
            var path = StorePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 写不进去不该影响正在跑的任务。
        }
    }

    private static void Hook(VideoTaskItem item)
        => item.PropertyChanged += (_, _) => RequestSave();

    private static void TrimHistory()
    {
        while (Tasks.Count > MaxItems) Tasks.RemoveAt(Tasks.Count - 1);
    }

    private static void RequestSave()
    {
        lock (Gate)
        {
            _saveTimer ??= new System.Threading.Timer(
                _ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
            // 进度是高频更新，防抖 1.2s。
            _saveTimer.Change(1200, Timeout.Infinite);
        }
    }

    private static void RestoreFromDisk()
    {
        var path = StorePath;
        if (!File.Exists(path)) return;

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json)) return;

        List<TaskDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<TaskDto>>(json, JsonOpts);
        }
        catch
        {
            return;
        }
        if (dtos is null || dtos.Count == 0) return;

        foreach (var dto in dtos)
        {
            var item = dto.ToItem();

            // 关应用时还在排队/运行的任务：标成中断，并可重试。
            // 不这么做它们会永远停在「进行中」，用户既等不到结果也点不了重试。
            if (item.Phase is VideoTaskPhase.Queued or VideoTaskPhase.Running)
            {
                item.Phase = VideoTaskPhase.Failed;
                item.StatusText = "已中断（应用关闭）";
                item.Error = "应用关闭时这个任务还没跑完。点「重试」可以重新提交。";
                item.Progress = 0;
            }

            Hook(item);
            Tasks.Add(item);
        }
    }

    /// <summary>
    /// 落盘用的扁平结构。刻意不用 VideoTaskItem 本身：Cts（取消令牌）不该被序列化，
    /// 而且字段改名时不要让旧文件直接读不出来。
    /// </summary>
    private sealed record TaskDto
    {
        public string Id { get; init; } = "";
        public GenerationMode Mode { get; init; }
        public VideoTaskPhase Phase { get; init; }
        public string StatusText { get; init; } = "";
        public double Progress { get; init; }
        public string Prompt { get; init; } = "";
        public string Model { get; init; } = "";
        public string Resolution { get; init; } = "480P";
        public string Ratio { get; init; } = "adaptive";
        public int Duration { get; init; }
        public List<string> MediaPaths { get; init; } = new();
        public List<string> ReferenceImages { get; init; } = new();
        public List<string> ReferenceVideos { get; init; } = new();
        public List<string> ReferenceVoices { get; init; } = new();
        public string? FirstClipUrl { get; init; }
        public string? LastFrameUrl { get; init; }
        public ClipAnchorSpec? Anchor { get; init; }
        public string? SegmentLabel { get; init; }
        public string? LocalPath { get; init; }
        public string? Error { get; init; }
        public string? VideoUrl { get; init; }
        public string? RemoteTaskId { get; init; }
        public string? PoolItemId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }

        public static TaskDto From(VideoTaskItem t) => new()
        {
            Id = t.Id,
            Mode = t.Mode,
            Phase = t.Phase,
            StatusText = t.StatusText,
            Progress = t.Progress,
            Prompt = t.Prompt,
            Model = t.Model,
            Resolution = t.Resolution,
            Ratio = t.Ratio,
            Duration = t.Duration,
            MediaPaths = new List<string>(t.MediaPaths),
            ReferenceImages = new List<string>(t.ReferenceImages),
            ReferenceVideos = new List<string>(t.ReferenceVideos),
            ReferenceVoices = new List<string>(t.ReferenceVoices),
            FirstClipUrl = t.FirstClipUrl,
            LastFrameUrl = t.LastFrameUrl,
            Anchor = t.Anchor,
            SegmentLabel = t.SegmentLabel,
            LocalPath = t.LocalPath,
            Error = t.Error,
            VideoUrl = t.VideoUrl,
            RemoteTaskId = t.RemoteTaskId,
            PoolItemId = t.PoolItemId,
            CreatedAt = t.CreatedAt
        };

        public VideoTaskItem ToItem()
        {
            var item = new VideoTaskItem
            {
                Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
                Mode = Mode,
                Prompt = Prompt,
                Model = Model,
                Resolution = Resolution,
                Ratio = Ratio,
                Duration = Duration,
                MediaPaths = MediaPaths ?? new List<string>(),
                ReferenceImages = ReferenceImages ?? new List<string>(),
                ReferenceVideos = ReferenceVideos ?? new List<string>(),
                ReferenceVoices = ReferenceVoices ?? new List<string>(),
                FirstClipUrl = FirstClipUrl,
                LastFrameUrl = LastFrameUrl,
                Anchor = Anchor,
                SegmentLabel = SegmentLabel,
                CreatedAt = CreatedAt == default ? DateTimeOffset.Now : CreatedAt
            };

            item.Phase = Phase;
            item.StatusText = StatusText;
            item.Progress = Progress;
            item.LocalPath = LocalPath;
            item.Error = Error;
            item.VideoUrl = VideoUrl;
            item.RemoteTaskId = RemoteTaskId;
            item.PoolItemId = PoolItemId;
            return item;
        }
    }
}
