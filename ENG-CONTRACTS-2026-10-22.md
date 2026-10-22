# 工程契约 — Cut B 长视频（2026-10-22 第二轮开工）

**Owner:** Tiny  
**Lead（主理人）:** 本 AI  
**SoT:** `CUT-2026-10-22-Wan3.0-BINDING.md`（B，AC-L01…L11）+ `CUT-2026-10-22-Wan3.0-CUT3-MODES.md`（additive，AC-M*）  
**工作目录:** `C:\Users\<user>\Desktop\Wan3.0`（禁止越界）

---

## 0. 铁律

1. **文件归属制**：只改自己名下的文件。要改别人的文件，先发消息给 Lead，不许直接动手（上次并发改文件把 Home/Settings 打成 `OK` 占位）。
2. **不许 soft-pass**：绿 build ≠ AC PASS；没有真 Key 的联网项标 `BLOCKED`，不标 PASS。
3. **禁止**用本地 ffmpeg 拼接冒充「官方续接」，禁止「无缝」话术。
4. **构建验证不许抢主目录的 obj/bin**：主目录 build 只由 Lead 执行。其他人验证代码用「复制到临时目录再 build」：

```bash
mkdir -p /tmp/verify-<role> && cd /c/Users/<user>/Desktop/Wan3.0 && \
  (tar cf - --exclude=bin --exclude=obj WanStudio | tar xf - -C /tmp/verify-<role>) && \
  cd /tmp/verify-<role>/WanStudio && dotnet build -c Debug -p:Platform=x64 -v:q
```

5. 每项完成必须附**原始输出**（命令 + stdout，或 JSON），不接受「已完成」口头。
6. Key 永不进仓库/日志/报告明文。

---

## 1. 文件归属表

| 文件 | Owner | 说明 |
|------|-------|------|
| `WanStudio/Services/Media/MediaContracts.cs` | **Lead（已冻结）** | 接口/常量，任何人不得改签名 |
| `WanStudio/Services/Media/FfmpegMediaEngine.cs` | eng-media | ffmpeg 实现 |
| `WanStudio/Services/Media/WinRtMediaEngine.cs` | eng-media | Windows.Media 兜底 |
| `WanStudio/Services/Media/MediaEngines.cs` | eng-media | 定位器 `CreateDefault()` |
| `WanStudio/Services/Media/SegmentPlanner.cs` | eng-media | 纯函数切段算法 |
| `tools/media-check/` | eng-media | 控制台验证程序 |
| `WanStudio/Services/ModelCatalog.cs` | eng-api | 模型与能力位 |
| `WanStudio/Services/VideoPool.cs` | eng-api | 已生成视频池 |
| `WanStudio/Services/DashScopeClient.cs` | eng-api | 追加续接方法 |
| `WanStudio/Services/VideoTaskStore.cs` | eng-api | 追加 ContinueFromClip 等 |
| `WanStudio/Services/VideoJobRunner.cs` | eng-api | 续接分支 + 入池 |
| `WanStudio/Pages/LongVideoPage.xaml(.cs)` | **Lead** | 长视频页 |
| `WanStudio/Pages/TasksPage.xaml(.cs)` | **Lead** | 播放/另存 |
| `WanStudio/Services/LongVideoProject.cs` | **Lead** | 页面状态模型 |
| `tools/mock-dashscope/`, `tools/capture-window.ps1` | qa | 测试替身 |
| `qa/run-*.md`, `qa/*.png` | qa | 证据 |

---

## 2. Media 层契约（eng-media 实现，Lead 已冻结）

见 `WanStudio/Services/Media/MediaContracts.cs`，签名不得改。额外要求：

### 2.1 `MediaEngines.CreateDefault()`

```csharp
public static class MediaEngines
{
    /// 优先 ffmpeg，找不到时回退 WinRT；两者都不可用时返回 WinRtMediaEngine（SupportsCut=false）。
    public static IMediaEngine CreateDefault();
    public static bool TryLocateFfmpeg(out string exePath);
}
```

ffmpeg 查找顺序：
1. 环境变量 `WANSTUDIO_FFMPEG`
2. `AppContext.BaseDirectory\tools\ffmpeg.exe`
3. 从 `AppContext.BaseDirectory` 往上最多 6 层找 `tools\ffmpeg.exe`（覆盖 `bin\x64\Debug\...\win-x64` 跑起来的情况）
4. `PATH`
5. `%LOCALAPPDATA%\oopz\ffmpeg.exe`（本机兜底，可留注释说明）

**不要**打包/拷贝 ffmpeg 到 bin。

### 2.2 `FfmpegMediaEngine`

- `ProbeAsync`：用 `ffmpeg -hide_banner -i <file>`，从 stderr 解析 `Duration: HH:MM:SS.ss`，宽高取第一条 `Stream #... Video:` 的 `WxH`，音频看是否有 `Audio:`。**不依赖 ffprobe**。
- `DetectSceneCutsAsync`：`-filter:v "select='gt(scene,<th>)',showinfo" -f null -`，解析 `pts_time:`，去重、升序、丢弃 <0.5s 的（开场）与 ≥duration 的。
- `ExtractFrameAsync`：`-ss <t> -i <src> -frames:v 1 -q:v 2 -y <out>`，输出目录不存在则创建。
- `CutAsync`：优先 `-ss <start> -i <src> -t <dur> -c:v libx264 -preset veryfast -crf 20 -c:a aac -movflags +faststart -y`（精确、可播）。**不要**用 `-c copy`（关键帧偏移会让起止点漂移，AC-L02 要起止秒可核）。
- `ConcatAsync`：写 UTF-8 无 BOM 的 concat 列表文件（路径用 `file '<abs>'`，单引号转义 `'` → `'\''`），`-f concat -safe 0 -i list -c:v libx264 -preset veryfast -crf 20 -c:a aac -y`。失败抛 `InvalidOperationException` 并带 stderr 尾部。
- 所有进程：`RedirectStandardError/Output = true`、`CreateNoWindow = true`、支持 `CancellationToken`（取消时 kill 整棵树），超时保护（单次 ≤ 10 分钟）。

### 2.3 `WinRtMediaEngine`（无 ffmpeg 兜底）

- `ProbeAsync`：`StorageFile.GetFileFromPathAsync` + `Properties.GetVideoPropertiesAsync()`。
- `ExtractFrameAsync`：`MediaComposition.GetThumbnailAsync` + `BitmapEncoder` 存 png。
- `DetectSceneCutsAsync`：`GetThumbnailAsync` 逐点采样（108×60），缩到 16×9 比较平均绝对差；采样步长自适应（目标 ≤ 240 个采样点，最小 0.25s），阈值 0.12。
- `CutAsync` / `ConcatAsync`：`SupportsCut=false` / `SupportsConcat`（用 `NotSupportedException`），**不要**假装支持。

### 2.4 `SegmentPlanner`（纯函数，必须有自测）

```csharp
public static class SegmentPlanner
{
    // 固定秒：从 0 起切，最后一段可短于 segmentSeconds 但不小于 1s（短了就并进前一段）。
    public static IReadOnlyList<SegmentSpec> PlanFixed(double durationSeconds, double segmentSeconds);

    // 切镜：以 cuts 为界，段 >15s 按 FallbackSplitSeconds 均分，段 <1s 并入前段；首段从 0 起。
    public static IReadOnlyList<SegmentSpec> PlanFromSceneCuts(double durationSeconds, IReadOnlyList<double> cuts);
}
```

边界：`durationSeconds<=0` 返回空；`segmentSeconds` 夹到 [1,15]；总段数上限 400（超出截断并保持覆盖到结尾的最后一刀）；输出必须严格递增且不重叠、`EndSeconds<=durationSeconds+0.001`。

### 2.5 验收（eng-media 交付证据）

`tools/media-check` 控制台程序跑这 4 条，原始输出贴进 `qa/run-2026-10-22-media-engine.md`：

```
1) probe fixtures\long-20s.mp4          → 20s / 640x360
2) PlanFixed(20, 6)                     → 4 段，起止正确，末段 2s
3) DetectSceneCuts(fixtures\scenes-4x5s.mp4) → 约 [5,10,15]
4) Cut(scenes-4x5s, 10, 5) + Concat 两段 → 产物存在、可 probe、时长 5±0.2 / 10±0.3
```

---

## 3. 续接 API 契约（eng-api 实现）

### 3.1 `ModelCatalog.cs`（新增）

```csharp
public sealed record ModelInfo(string Id, string Label, bool TextToVideo, bool ImageToVideo, bool FirstClipContinue);

public static class ModelCatalog
{
    public static IReadOnlyList<ModelInfo> All { get; }          // wan3.0-video-prime, wan3.0-video, wan2.7-i2v
    public static string DefaultModelId { get; }                 // wan3.0-video-prime
    public static string DefaultContinueModelId { get; }         // wan2.7-i2v
    public static ModelInfo? Find(string id);
    public static bool SupportsFirstClip(string id);
}
```

`wan2.7-i2v` 的 `FirstClipContinue=true`，两个 `wan3.0-*` 为 `false`。**注释写明**：能力位来自 SoT 文档，尚未用真 Key 验证（QA 会标 BLOCKED）。

### 3.2 `DashScopeClient.SubmitContinueAsync`

```csharp
public async Task<string> SubmitContinueAsync(
    string firstClipUrl,
    string prompt,
    string model,
    string? lastFrameUrl = null,
    string resolution = "480P",
    string ratio = "adaptive",
    int duration = 5,
    CancellationToken ct = default)
```

请求体（顺序不重要，字段名必须一致）：

```json
{
  "model": "wan2.7-i2v",
  "input": {
    "prompt": "…",
    "media": [
      { "type": "first_clip", "url": "https://…/clip.mp4" }
    ]
  },
  "parameters": { "resolution": "480P", "ratio": "adaptive", "duration": 5 }
}
```

- `lastFrameUrl` 非空时追加 `{ "type": "last_frame", "url": … }`。
- 复用现有 `X-DashScope-Async: enable`、Bearer、task_id 解析逻辑（抽私有 `SubmitAsync(object body)` 也可以，但不要改 `SubmitTextToVideoAsync` / `SubmitVideoJobAsync` 的对外签名）。
- 本地路径（`file://`、`C:\`）**不允许**当 first_clip 传入：直接抛 `InvalidOperationException("first_clip 需要公网可访问 URL（http/https）…")`。

### 3.3 `VideoTaskStore` 增量

```csharp
public enum GenerationMode { TextToVideo, ImageToVideo, CharacterReplace, MotionReference, ContinueFromClip }

// VideoTaskItem 追加（全部 init-only，除标注）：
public string? FirstClipUrl { get; init; }
public string? LastFrameUrl { get; init; }
public string? SegmentLabel { get; init; }     // 例如 "段 03 · 00:10–00:15"
public string? PoolItemId { get; set; }
```

`ModeLabel` 增加 `ContinueFromClip => "续接"`。

### 3.4 `VideoJobRunner` 增量

- 提交段：`item.Mode == GenerationMode.ContinueFromClip` → 走 `SubmitContinueAsync(item.FirstClipUrl!, …)`，其余保持原样（现有 4 模式不改行为）。
- 成功后（下载完成）：`VideoPool.AddFromTask(item)` 入池，把返回的 `PoolItemId` 写回 `item.PoolItemId`。
- 失败信息保持可读（HTTP 状态码 + 响应体前 500 字符）。

### 3.5 `VideoPool.cs`（新增）

```csharp
public sealed class VideoPoolItem : INotifyPropertyChanged
{
    public string Id { get; init; }
    public string Title { get; init; }            // 任务提示词前 24 字 或 SegmentLabel
    public string LocalPath { get; init; }
    public string? RemoteUrl { get; init; }       // video_url（续接必须要它）
    public string? ThumbPath { get; set; }        // 懒加载，Lead 的 UI 填
    public string Source { get; init; }           // "文生" / "图生" / "人物替换" / "动作参考" / "续接" / "导入"
    public string? SegmentLabel { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsImported { get; init; }
    public bool CanContinue => !IsImported && !string.IsNullOrWhiteSpace(RemoteUrl);
    public string ContinueBlockReason => …          // 无 URL / 导入片段 → 中文原因
}

public static class VideoPool
{
    public static ObservableCollection<VideoPoolItem> Items { get; }
    public static VideoPoolItem AddFromTask(VideoTaskItem task);   // 幂等：同 task.Id 只加一次
    public static VideoPoolItem AddImported(string localPath, string? title = null);
    public static VideoPoolItem? Find(string id);
}
```

池不做持久化（本轮范围），但要在报告里注明「重启清空」是已知限制。

### 3.6 交付证据（eng-api）

`qa/run-2026-10-22-continue-api.md` 里贴：

1. `dotnet build` 原始输出（临时目录那份）。
2. 用 `tools/mock-dashscope` 起的本地 mock，POST 请求体（Authorization 打码）+ 返回 task_id 的原始 JSON。
3. `grep -n "first_clip" Services/DashScopeClient.cs` 原始输出。
4. 说明：真 Key 未验证 → AC-L06/L07 仍 BLOCKED。

---

## 4. 联调约定（Lead ↔ 所有人）

- Lead 会在 `Pages/LongVideoPage.xaml.cs` 里按上面的签名调用：`MediaEngines.CreateDefault()`、`SegmentPlanner.*`、`ModelCatalog.*`、`VideoPool.*`、`VideoTaskItem { Mode = ContinueFromClip, FirstClipUrl = … }`。
- 任何签名要变，先发消息，禁止单方面改。
- 状态同步：完成一项就把 TaskList 里自己的任务标 completed，并在消息里贴原始证据摘要。
