using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WanStudio.Services.Media;

namespace WanStudio.Services;

public static class VideoJobRunner
{
    /// <summary>
    /// 把一个续接锚点变成可提交的 first_clip URL：
    /// 来源在 2–10s 内直接上传；超长则先本地裁出尾部窗口再上传。
    /// 上传拿 oss:// 临时地址（48h 有效，且与 model 绑定）。
    /// </summary>
    private static async Task<string> PrepareAnchorAsync(
        ClipAnchorSpec anchor,
        string model,
        string apiKey,
        CancellationToken ct)
    {
        if (!File.Exists(anchor.SourceLocalPath))
            throw new FileNotFoundException("续接锚点的来源片段不存在，可能已被清理。", anchor.SourceLocalPath);

        var engine = MediaEngines.CreateDefault();
        var info = await engine.ProbeAsync(anchor.SourceLocalPath, ct).ConfigureAwait(false);
        var total = info.DurationSeconds;

        if (double.IsNaN(total) || total < ClipAnchor.MinAnchorSeconds)
            throw new InvalidOperationException(
                $"续接锚点的来源片段只有 {(double.IsNaN(total) ? 0 : total):0.#}s，first_clip 至少需要 {ClipAnchor.MinAnchorSeconds:0}s。");

        string uploadPath;
        var isTemp = false;
        if (ClipAnchor.CanBeUsedWhole(total))
        {
            // 整段就在 first_clip 窗口内，不用裁。
            uploadPath = anchor.SourceLocalPath;
        }
        else
        {
            if (!engine.SupportsCut)
                throw new InvalidOperationException(
                    $"来源片段 {total:0.#}s 超过 first_clip 上限 10s，需要本地裁出尾部，但当前引擎不支持切段（缺 ffmpeg）。");

            var take = ClipAnchor.TailSecondsFor(total, anchor.TailSeconds);
            var start = Math.Max(0, total - take);

            var dir = AppPaths.CacheDir;
            uploadPath = Path.Combine(dir, $"anchor-{Guid.NewGuid():N}.mp4");
            isTemp = true;
            await engine.CutAsync(anchor.SourceLocalPath, start, take, uploadPath, ct).ConfigureAwait(false);
        }

        try
        {
            using var uploader = new DashScopeClipUploader(apiKey, AppSettings.Endpoint);
            return await uploader.UploadAsync(uploadPath, model, ct).ConfigureAwait(false);
        }
        finally
        {
            // 裁出来的尾段只用于这一次上传，用完删掉，否则缓存目录会一直涨。
            if (isTemp)
            {
                try { File.Delete(uploadPath); } catch { /* best effort */ }
            }
        }
    }

    public static async Task RunAsync(VideoTaskItem item, DispatcherQueue dispatcher)
    {
        if (item.IsDemo)
        {
            // Demo rows are UI-only (AC-03 proof).
            return;
        }

        var cts = new CancellationTokenSource();
        item.Cts = cts;
        var ct = cts.Token;

        void Ui(Action a)
        {
            if (dispatcher.HasThreadAccess) a();
            else dispatcher.TryEnqueue(() => a());
        }

        try
        {
            var key = ApiKeyStore.TryGet(AppSettings.KeyProfile);
            if (string.IsNullOrEmpty(key))
            {
                Ui(() =>
                {
                    item.Phase = VideoTaskPhase.Failed;
                    item.StatusText = "失败";
                    item.Error = "未配置 API Key。请到「设置」保存密钥后再试。";
                    item.Progress = 0;
                });
                return;
            }

            Ui(() =>
            {
                item.Phase = VideoTaskPhase.Running;
                item.StatusText = "提交中…";
                item.Progress = 0.05;
                item.Error = null;
            });

            using var client = new DashScopeClient(key, AppSettings.Endpoint);
            string taskId;
            if (item.Anchor is not null)
            {
                // 链式续接：拿上一段生成结果的尾巴当 first_clip。
                // 超过 10s 必须先本地裁到尾部窗口内，再上传换 oss:// 才能提交。
                Ui(() =>
                {
                    item.StatusText = "准备续接锚点…";
                    item.Progress = 0.08;
                });

                var anchorUrl = await PrepareAnchorAsync(item.Anchor, item.Model, key, ct).ConfigureAwait(false);

                taskId = await client.SubmitContinueAsync(
                    anchorUrl,
                    item.Prompt,
                    item.Model,
                    item.LastFrameUrl,
                    item.Resolution,
                    item.Ratio,
                    item.Duration,
                    ct).ConfigureAwait(false);
            }
            else if (item.Mode == GenerationMode.ContinueFromClip)
            {
                var firstClip = item.FirstClipUrl;
                if (string.IsNullOrWhiteSpace(firstClip))
                    throw new InvalidOperationException("续接任务缺少首片段 URL（first_clip），无法提交。");

                taskId = await client.SubmitContinueAsync(
                    firstClip,
                    item.Prompt,
                    item.Model,
                    item.LastFrameUrl,
                    item.Resolution,
                    item.Ratio,
                    item.Duration,
                    ct).ConfigureAwait(false);
            }
            else if (ModelCatalog.Find(item.Model) is { VideoEditing: true })
            {
                // 通用视频编辑（wan2.7-videoedit）：人物替换的正解。
                // 源视频用 media[].type="video"，参考图做替换；
                // duration 传 0 = 保持原视频完整时长（人物替换不按用户选的时长走）。
                var sourceVideo = item.ReferenceVideos.FirstOrDefault()
                                ?? item.MediaPaths.FirstOrDefault(MediaLooksLikeVideo);

                if (string.IsNullOrWhiteSpace(sourceVideo))
                    throw new InvalidOperationException(
                        "视频编辑需要一段源视频：请在素材库里点选要替换的视频。");

                Ui(() =>
                {
                    item.StatusText = "提交视频编辑…";
                    item.Progress = 0.15;
                });

                taskId = await client.SubmitVideoEditAsync(
                    sourceVideo!,
                    item.ReferenceImages,
                    item.Prompt,
                    item.Model,
                    item.Resolution,
                    ratio: null,
                    duration: 0,
                    audioSetting: "origin",
                    watermark: false,
                    negativePrompt: null,
                    ct: ct).ConfigureAwait(false);
            }
            else if (item.ReferenceCount > 0 && ModelCatalog.SupportsReference(item.Model))
            {
                // 参考生视频（wan2.7-r2v）：多张参考图 + 参考视频 + 可选音色参考。
                // 人物替换的「多人」就是多张 reference_image。
                taskId = await client.SubmitReferenceAsync(
                    item.ReferenceImages,
                    item.ReferenceVideos,
                    item.Prompt,
                    item.Model,
                    item.Resolution,
                    item.Ratio,
                    item.Duration,
                    referenceVoices: item.ReferenceVoices,
                    ct: ct).ConfigureAwait(false);
            }
            else
            {
                taskId = await client.SubmitVideoJobAsync(
                    item.Mode,
                    item.Prompt,
                    item.Model,
                    item.MediaPaths,
                    item.Resolution,
                    item.Ratio,
                    item.Duration,
                    ct).ConfigureAwait(false);
            }

            Ui(() =>
            {
                item.RemoteTaskId = taskId;
                item.StatusText = "排队/生成中…";
                item.Progress = 0.15;
            });

            var pollFailures = 0;
            var deadline = DateTime.UtcNow + MaxJobDuration;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"任务超过 {MaxJobDuration.TotalMinutes:0} 分钟仍未结束，已停止等待（远端任务 {taskId} 可能仍在运行）。");

                DashScopeTaskStatus status;
                try
                {
                    status = await client.GetTaskAsync(taskId, ct).ConfigureAwait(false);
                    pollFailures = 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is not TimeoutException)
                {
                    // 单次轮询失败（网络抖动、429、5xx、HttpClient 超时）不等于任务失败：
                    // 远端任务还在跑，直接判失败会让用户重复提交并重复扣费。连续失败超过预算才放弃。
                    if (++pollFailures >= MaxPollFailures) throw;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 5 * pollFailures)), ct).ConfigureAwait(false);
                    continue;
                }

                if (status.IsSucceeded)
                {
                    if (string.IsNullOrEmpty(status.VideoUrl))
                    {
                        Ui(() =>
                        {
                            item.Phase = VideoTaskPhase.Failed;
                            item.StatusText = "失败";
                            item.Error = "任务成功但未返回 video_url。";
                        });
                        return;
                    }

                    Ui(() =>
                    {
                        item.StatusText = "下载中…";
                        item.Progress = 0.85;
                        item.VideoUrl = status.VideoUrl;
                    });

                    var dir = AppPaths.OutputDir(AppSettings.OutputDirectory);
                    var safe = string.Join("_", item.Prompt.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
                    safe = TruncateSafe(safe, 40);
                    if (string.IsNullOrWhiteSpace(safe)) safe = item.ModeLabel;
                    var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}-{taskId[..Math.Min(8, taskId.Length)]}.mp4");
                    await client.DownloadVideoAsync(status.VideoUrl!, path, ct).ConfigureAwait(false);

                    Ui(() =>
                    {
                        item.LocalPath = path;
                        item.Phase = VideoTaskPhase.Succeeded;
                        item.StatusText = "已完成";
                        item.Progress = 1;

                        // VideoPool.Items 直接绑 UI：入池与回写 PoolItemId 必须在 UI 线程做。
                        // 入池失败（写 pool.json 出错等）不能让 UI 线程未处理异常崩应用；
                        // 视频已经下载好了，所以保持成功，只把入池失败告诉用户。
                        try
                        {
                            var pooled = VideoPool.AddFromTask(item);
                            item.PoolItemId = pooled.Id;
                        }
                        catch (Exception poolEx)
                        {
                            AppPaths.AppendCrashLog("[AddFromTask] " + poolEx);
                            item.Error = "视频已下载，但加入素材库失败：" + poolEx.Message;
                        }
                    });
                    return;
                }

                if (status.IsFailed || status.IsTerminal)
                {
                    Ui(() =>
                    {
                        item.Phase = VideoTaskPhase.Failed;
                        item.StatusText = "失败";
                        item.Error = string.IsNullOrWhiteSpace(status.Message)
                            ? $"任务失败：{status.Status}"
                            : status.Message;
                        item.Progress = 0;
                    });
                    return;
                }

                Ui(() =>
                {
                    item.StatusText = $"生成中（{status.Status}）…";
                    if (item.Progress < 0.8) item.Progress = Math.Min(0.8, item.Progress + 0.05);
                });

                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 只有用户真的点了取消才算取消。HttpClient 超时同样抛 TaskCanceledException，
            // 早先会被误判成「已取消」，用户根本没取消。
            Ui(() =>
            {
                item.Phase = VideoTaskPhase.Canceled;
                item.StatusText = "已取消";
                item.Error = null;
                item.Progress = 0;
            });
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "请求超时，请检查网络后重试。" : ex.Message;
            Ui(() =>
            {
                item.Phase = VideoTaskPhase.Failed;
                item.StatusText = "失败";
                item.Error = message;
                item.Progress = 0;
            });
        }
        finally
        {
            // 重试会换上新的 Cts；旧 runner 的 finally 不能把新的清掉，否则新任务无法取消。
            if (ReferenceEquals(item.Cts, cts)) item.Cts = null;
            cts.Dispose();
        }
    }

    /// <summary>单个任务从提交到结束的最长等待。</summary>
    private static readonly TimeSpan MaxJobDuration = TimeSpan.FromMinutes(60);

    /// <summary>连续轮询失败多少次才放弃。</summary>
    private const int MaxPollFailures = 6;

    /// <summary>按「文本元素」截断，不把 emoji 等代理对从中间劈开（会让文件名非法）。</summary>
    private static string TruncateSafe(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut];
    }

    /// <summary>路径看起来是不是视频（用于视频编辑挑源视频）。</summary>
    private static bool MediaLooksLikeVideo(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" or ".m4v";
    }
}
