using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WanStudio.Services.Media;

namespace WanStudio.Services;

public sealed class DashScopeClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    public DashScopeClient(string apiKey, string? baseUrl = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _baseUrl = (baseUrl ?? AppSettings.DefaultEndpoint).TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public async Task<string> SubmitTextToVideoAsync(
        string prompt,
        string model,
        string resolution = "480P",
        string ratio = "adaptive",
        int duration = 5,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/services/aigc/video-generation/video-synthesis";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
        var body = new
        {
            model,
            input = new { prompt },
            parameters = new { resolution, ratio, duration }
        };
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Submit failed {(int)resp.StatusCode}: {json}");
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("output", out var output) &&
            output.TryGetProperty("task_id", out var taskId))
        {
            return taskId.GetString() ?? throw new InvalidOperationException("Empty task_id");
        }

        if (doc.RootElement.TryGetProperty("task_id", out var topTask))
        {
            return topTask.GetString() ?? throw new InvalidOperationException("Empty task_id");
        }

        throw new InvalidOperationException($"No task_id in response: {json}");
    }


    public async Task<string> SubmitVideoJobAsync(
        GenerationMode mode,
        string prompt,
        string model,
        IReadOnlyList<string> mediaPaths,
        string resolution = "480P",
        string ratio = "adaptive",
        int duration = 5,
        CancellationToken ct = default)
    {
        if (mode == GenerationMode.TextToVideo)
        {
            return await SubmitTextToVideoAsync(prompt, model, resolution, ratio, duration, ct).ConfigureAwait(false);
        }

        // 非文生模式却没有素材：早先会悄悄退化成文生，用户得到一段与所选素材无关的视频还照扣费。
        if (mediaPaths is null || mediaPaths.Count == 0)
        {
            throw new InvalidOperationException("当前模式需要素材，但没有可用的素材文件。请先添加素材再生成。");
        }

        // 本地素材必须先上传成 oss://，否则服务端取不到（早期实现直接发 file://）。
        // 上传与模型绑定，所以这里必须用本次调用的 model。
        async Task<string> ReachableAsync(string localPath)
        {
            var url = await UploadLocalAsync(localPath, model, ct).ConfigureAwait(false);
            RequireServerReachableUrl(url, "media");
            return url;
        }

        // first_frame 只接受图片。把 mp4 当 first_frame 发给服务端，
        // 会得到「Error validating image, can not read image from ...mp4」——
        // 服务端按图片去解码视频，自然读不出来。所以这里先挡住，给一句能看懂的错。
        static bool IsImage(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
        }

        var media = new List<object>();
        switch (mode)
        {
            case GenerationMode.ImageToVideo:
                if (!IsImage(mediaPaths[0]))
                    throw new InvalidOperationException(
                        $"图生视频的首帧必须是图片，当前素材是 {Path.GetExtension(mediaPaths[0])}。");
                media.Add(new { type = "first_frame", url = await ReachableAsync(mediaPaths[0]).ConfigureAwait(false) });
                break;

            // wan2.7-i2v 的合法 media type 只有 first_frame / last_frame /
            // driving_audio / first_clip，且组合受限（见官方图生视频 API）。
            // 参考图/参考视频不是 i2v 的合法 type —— 它们属于 wan2.7-r2v
            // （参考生视频），所以这两个模式会由调用方路由到 r2v，
            // 只有误配到 i2v 时才退化成下面这种近似写法。
            case GenerationMode.CharacterReplace:
                if (mediaPaths.Count < 1)
                    throw new InvalidOperationException("人物替换需要人物参考素材。");
                if (!IsImage(mediaPaths[0]))
                    throw new InvalidOperationException(
                        $"人物替换的参考素材必须是图片，当前是 {Path.GetExtension(mediaPaths[0])}。" +
                        "要把视频当参考，请把模型换成 wan2.7-r2v（参考生视频）。");
                media.Add(new { type = "first_frame", url = await ReachableAsync(mediaPaths[0]).ConfigureAwait(false) });
                break;

            case GenerationMode.MotionReference:
                if (mediaPaths.Count < 1)
                    throw new InvalidOperationException("动作参考需要动作视频。");
                media.Add(new { type = "first_clip", url = await ReachableAsync(mediaPaths[0]).ConfigureAwait(false) });
                break;
        }

        // 参数归一：wan2.7 系列不认 4:3/3:4/adaptive 之外的组合时会被判非法，
        // wan3.0 全档都认（含 adaptive）。
        var isWan3 = model.StartsWith("wan3.0", StringComparison.OrdinalIgnoreCase);
        var safeResolution = resolution?.Trim().ToUpperInvariant() switch
        {
            "480P" or "720P" or "1080P" => resolution!.Trim().ToUpperInvariant(),
            _ => isWan3 ? "1080P" : "720P"
        };
        var safeRatio = ratio?.Trim().ToLowerInvariant() switch
        {
            "16:9" or "9:16" or "1:1" => ratio!.Trim().ToLowerInvariant(),
            "4:3" or "3:4" => isWan3 ? ratio!.Trim().ToLowerInvariant() : "16:9",
            _ => isWan3 ? "adaptive" : "16:9"
        };
        var mi2 = ModelCatalog.Find(model);
        var safeDuration = Math.Clamp(duration, 2, Math.Max(2, mi2?.MaxDurationWithoutVideo ?? 15));

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = new Dictionary<string, object?>
            {
                ["prompt"] = prompt,
                ["media"] = media
            },
            ["parameters"] = new Dictionary<string, object?>
            {
                ["resolution"] = safeResolution,
                ["ratio"] = safeRatio,
                ["duration"] = safeDuration,
                // 显式关掉出片标识：默认值在不同模型上不一致，写死 false 最可控。
                ["watermark"] = false
            }
        };

        return await SubmitAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 参考生视频（wan2.7-r2v）：多张 reference_image + 若干 reference_video，
    /// 可给参考素材指定音色（reference_voice）。
    /// 官方约束：图像+视频合计 ≤5；首帧最多 1 张；每份素材仅含单一角色；
    /// reference_voice 为 wav/mp3、1–10s、≤15MB，只参考音色，与视频原声同时存在时优先它。
    /// prompt 用「图1/图2/视频1」指代，顺序与 media 数组一致（图像、视频分别计数）。
    /// </summary>
    /// <summary>
    /// 参考生视频（wan3.0-video / wan3.0-video-prime / wan2.7-r2v）。
    ///
    /// 官方文档要点：
    ///  - wan3.0 是 All-in-One：reference_image / reference_video / reference_audio 平铺在
    ///    input.media 里，音频是**独立条目**（提示词里叫「音频1」）。
    ///    上限：图片 ≤10、视频 ≤5（总时长 ≤15s）、音频 ≤5（总时长 ≤15s）。
    ///  - wan2.7-r2v 的音频是 reference_voice，**挂在** reference_image / reference_video
    ///    条目内部，不是独立条目；参考素材合计 ≤5。
    ///  - reference_* 与 first_frame / last_frame 互斥，不能混用。
    ///  - 提示词用「图1/图片1」「视频1」「音频1」指代，三者的序号各自独立计数。
    /// </summary>
    public async Task<string> SubmitReferenceAsync(
        IReadOnlyList<string> referenceImages,
        IReadOnlyList<string> referenceVideos,
        string prompt,
        string model,
        string resolution = "720P",
        string ratio = "adaptive",
        int duration = 5,
        string? firstFramePath = null,
        IReadOnlyList<string>? referenceVoices = null,
        CancellationToken ct = default)
    {
        var info = ModelCatalog.Find(model)
            ?? throw new InvalidOperationException($"未知模型 {model}。");
        if (!info.ReferenceToVideo)
            throw new InvalidOperationException($"模型 {model} 不支持参考素材（reference_image / reference_video）。");

        var images = referenceImages.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var videos = referenceVideos.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var voices = (referenceVoices ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        if (images.Count + videos.Count == 0)
            throw new InvalidOperationException("参考生视频至少需要 1 份参考素材（参考图或参考视频）。");

        // 上限按类型分别判：官方图片和视频是两套配额，不该简单相加。
        if (images.Count > info.MaxReferenceImages)
            throw new InvalidOperationException(
                $"参考图过多：{images.Count} 张，{model} 上限 {info.MaxReferenceImages} 张。");
        if (videos.Count > info.MaxReferenceVideos)
            throw new InvalidOperationException(
                $"参考视频过多：{videos.Count} 段，{model} 上限 {info.MaxReferenceVideos} 段。");

        // r2v 还有一条「参考素材合计 ≤5」的总配额。
        if (model.StartsWith("wan2.7", StringComparison.OrdinalIgnoreCase)
            && images.Count + videos.Count > info.MaxReferenceMaterials)
        {
            throw new InvalidOperationException(
                $"参考素材过多：{images.Count + videos.Count} 份，{model} 合计上限 {info.MaxReferenceMaterials} 份。");
        }

        var media = new List<object>();

        // 首帧：官方与 reference_* 互斥。这里只在没有任何参考素材时才允许带。
        if (!string.IsNullOrWhiteSpace(firstFramePath) && images.Count == 0 && videos.Count == 0)
        {
            var url = await UploadLocalAsync(firstFramePath!, model, ct).ConfigureAwait(false);
            RequireServerReachableUrl(url, "first_frame");
            media.Add(new { type = "first_frame", url });
        }

        // 先把图片/视频都上传好，拿到 URL —— 后面音频配对要按槽位重建条目，
        // 所以不能一边上传一边往 media 里塞（会重复插入同一份素材）。
        var imageUrls = new List<string>();
        foreach (var img in images)
        {
            var url = await UploadLocalAsync(img, model, ct).ConfigureAwait(false);
            RequireServerReachableUrl(url, "reference_image");
            imageUrls.Add(url);
        }

        var videoUrls = new List<string>();
        foreach (var vid in videos)
        {
            var url = await UploadLocalAsync(vid, model, ct).ConfigureAwait(false);
            RequireServerReachableUrl(url, "reference_video");
            videoUrls.Add(url);
        }

        // 音频语义两代模型不同：
        //   wan3.0     → 独立 media 条目 type=reference_audio（提示词叫「音频1」）
        //   wan2.7-r2v → reference_voice 字段挂在每条 reference_image / reference_video 内部
        var isWan30 = model.StartsWith("wan3.0", StringComparison.OrdinalIgnoreCase);
        var voiceUrls = new List<string>();

        if (voices.Count > 0 && isWan30)
        {
            if (voices.Count > info.MaxReferenceAudios)
                throw new InvalidOperationException(
                    $"参考音频过多：{voices.Count} 段，{model} 上限 {info.MaxReferenceAudios} 段。");

            foreach (var v in voices)
            {
                var url = await UploadLocalAsync(v, model, ct).ConfigureAwait(false);
                RequireServerReachableUrl(url, "reference_audio");
                voiceUrls.Add(url);
            }
        }
        else if (voices.Count > 0)
        {
            // reference_voice 按顺序挂到参考素材上：第 1 个音色 → 第 1 份参考素材。
            var slots = images.Count + videos.Count;
            if (voices.Count > slots)
                throw new InvalidOperationException(
                    $"音色数量（{voices.Count}）多于参考素材（{slots}）：音色要按顺序对应到参考图/参考视频上。");

            foreach (var v in voices)
            {
                var url = await UploadLocalAsync(v, model, ct).ConfigureAwait(false);
                RequireServerReachableUrl(url, "reference_voice");
                voiceUrls.Add(url);
            }
        }

        // ---- 组装 media（每条素材只出现一次）----
        var slot = 0;
        foreach (var url in imageUrls)
        {
            if (!isWan30 && slot < voiceUrls.Count)
                media.Add(new { type = "reference_image", url, reference_voice = voiceUrls[slot] });
            else
                media.Add(new { type = "reference_image", url });
            slot++;
        }

        foreach (var url in videoUrls)
        {
            if (!isWan30 && slot < voiceUrls.Count)
                media.Add(new { type = "reference_video", url, reference_voice = voiceUrls[slot] });
            else
                media.Add(new { type = "reference_video", url });
            slot++;
        }

        // wan3.0 的音频是独立条目，放在图片/视频之后。
        foreach (var url in voiceUrls)
        {
            if (isWan30) media.Add(new { type = "reference_audio", url });
        }

        // ---- 参数 ----
        // wan3.0 支持 480P/720P/1080P + adaptive；wan2.7-r2v 只有 720P/1080P 且 ratio 固定档。
        var isWan3 = model.StartsWith("wan3.0", StringComparison.OrdinalIgnoreCase);

        var safeResolution = resolution?.Trim().ToUpperInvariant() switch
        {
            "480P" => isWan3 ? "480P" : "720P",
            "720P" => "720P",
            "1080P" => "1080P",
            _ => isWan3 ? "1080P" : "720P"
        };

        var safeRatio = isWan3
            ? (ratio?.Trim().ToLowerInvariant() switch
            {
                "16:9" or "9:16" or "1:1" or "4:3" or "3:4" => ratio!.Trim().ToLowerInvariant(),
                _ => "adaptive"
            })
            : (ratio?.Trim().ToLowerInvariant() switch
            {
                "16:9" or "9:16" or "1:1" or "4:3" or "3:4" => ratio!.Trim().ToLowerInvariant(),
                _ => "16:9"
            });

        var upper = videos.Count > 0 ? info.MaxDurationWithVideo : info.MaxDurationWithoutVideo;
        var clamped = Math.Clamp(duration, 2, upper);

        var parameters = new Dictionary<string, object?>
        {
            ["resolution"] = safeResolution,
            ["ratio"] = safeRatio,
            ["duration"] = clamped
        };
        // wan3.0 官方示例都带 prompt_extend，靠它把简短提示词扩写成可用的分镜描述。
        if (isWan3) parameters["prompt_extend"] = true;
        parameters["watermark"] = false;

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = new Dictionary<string, object?>
            {
                ["prompt"] = prompt,
                ["media"] = media
            },
            ["parameters"] = parameters
        };

        return await SubmitAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把本地文件变成服务端可取的 URL：已是 http(s)/oss:// 的原样返回，
    /// 本地路径走 DashScope 临时存储上传。
    /// </summary>
    private async Task<string> UploadLocalAsync(string pathOrUrl, string model, CancellationToken ct)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("oss://", StringComparison.OrdinalIgnoreCase))
        {
            return pathOrUrl;
        }

        using var uploader = new DashScopeClipUploader(_apiKey, _baseUrl);
        return await uploader.UploadAsync(pathOrUrl, model, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 通用视频编辑（wan2.7-videoedit）——人物替换的正解。
    ///
    /// 官方口径（千问AI平台「通用视频编辑」）：
    ///  - input.media 必须含**一个** <c>type="video"</c> 项（注意不是 reference_video），
    ///    外加最多 4 个 <c>reference_image</c>；用参考图替换视频里的人/物。
    ///  - parameters.duration：<c>0</c> = 保持输入视频完整时长（默认）；
    ///    2–10 = 只截取前 N 秒。人物替换按原片长度走，所以传 0。
    ///  - parameters.audio_setting："origin" 保留原声，"auto" 由模型决定。
    ///  - parameters.ratio：不传则自动跟输入视频比例一致。
    ///  - parameters.watermark：默认 false（显式传 false，避免出片带标识）。
    /// </summary>
    public async Task<string> SubmitVideoEditAsync(
        string sourceVideoPath,
        IReadOnlyList<string> referenceImages,
        string prompt,
        string model = "wan2.7-videoedit",
        string resolution = "1080P",
        string? ratio = null,
        int duration = 0,
        string audioSetting = "origin",
        bool watermark = false,
        string? negativePrompt = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceVideoPath))
            throw new InvalidOperationException("视频编辑必须提供源视频。");

        var info = ModelCatalog.Find(model);
        if (info is not { VideoEditing: true })
            throw new InvalidOperationException($"模型 {model} 不支持视频编辑（wan2.7-videoedit）。");

        var images = referenceImages.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (images.Count > info.MaxEditReferenceImages)
            throw new InvalidOperationException(
                $"参考图过多：{images.Count} 张，{model} 上限 {info.MaxEditReferenceImages} 张。");

        var media = new List<object>();

        // 源视频：type 就是 "video"（不是 reference_video，这两者在这个模型上不等价）。
        var videoUrl = await UploadLocalAsync(sourceVideoPath, model, ct).ConfigureAwait(false);
        RequireServerReachableUrl(videoUrl, "video");
        media.Add(new { type = "video", url = videoUrl });

        foreach (var img in images)
        {
            var url = await UploadLocalAsync(img, model, ct).ConfigureAwait(false);
            RequireServerReachableUrl(url, "reference_image");
            media.Add(new { type = "reference_image", url });
        }

        // duration：0 = 原时长；否则夹到 2–10。
        var safeDuration = duration <= 0
            ? 0
            : Math.Clamp(duration, 2, Math.Max(2, info.MaxEditDurationSeconds));

        var safeResolution = string.Equals(resolution?.Trim(), "720P", StringComparison.OrdinalIgnoreCase)
            ? "720P" : "1080P";

        var parameters = new Dictionary<string, object?>
        {
            ["resolution"] = safeResolution,
            ["duration"] = safeDuration,
            ["audio_setting"] = string.Equals(audioSetting, "auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "origin",
            ["prompt_extend"] = true,
            ["watermark"] = watermark
        };

        // ratio 留空 = 跟随输入视频比例（官方默认行为），比硬塞一个值更稳。
        if (!string.IsNullOrWhiteSpace(ratio)
            && ratio.Trim().ToLowerInvariant() is "16:9" or "9:16" or "1:1" or "4:3" or "3:4")
        {
            parameters["ratio"] = ratio.Trim().ToLowerInvariant();
        }

        var input = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["media"] = media
        };
        if (!string.IsNullOrWhiteSpace(negativePrompt)) input["negative_prompt"] = negativePrompt;

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = input,
            ["parameters"] = parameters
        };

        return await SubmitAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 首片段续写（first_clip）。first_clipUrl 必须是公网可访问的 http/https 地址，
    /// 本地文件（file:// 或盘符路径）一律拒绝——服务端取不到本地文件。
    /// </summary>
    public async Task<string> SubmitContinueAsync(
        string firstClipUrl,
        string prompt,
        string model,
        string? lastFrameUrl = null,
        string resolution = "480P",
        string ratio = "adaptive",
        int duration = 5,
        CancellationToken ct = default)
    {
        RequireServerReachableUrl(firstClipUrl, "续接片段");

        var info = ModelCatalog.Find(model);
        var isWan3 = model.StartsWith("wan3.0", StringComparison.OrdinalIgnoreCase);

        // ---- 参数归一（两代模型口径不同）----
        var safeResolution = resolution?.Trim().ToUpperInvariant() switch
        {
            "480P" => isWan3 ? "480P" : "480P",
            "720P" => "720P",
            "1080P" => "1080P",
            _ => isWan3 ? "1080P" : "720P"
        };
        var safeRatio = ratio?.Trim().ToLowerInvariant() switch
        {
            "16:9" or "9:16" or "1:1" => ratio!.Trim().ToLowerInvariant(),
            "4:3" or "3:4" => isWan3 ? ratio!.Trim().ToLowerInvariant() : "16:9",
            _ => "adaptive"
        };

        List<object> media;
        string finalPrompt = prompt;
        Dictionary<string, object?> parameters;

        if (info?.FirstClipContinue == true)
        {
            // wan2.7-i2v：first_clip（2–10s），可再带一张 last_frame 控制结尾。
            media = new List<object> { new { type = "first_clip", url = firstClipUrl } };
            if (!string.IsNullOrWhiteSpace(lastFrameUrl))
            {
                RequireServerReachableUrl(lastFrameUrl!, "last_frame");
                media.Add(new { type = "last_frame", url = lastFrameUrl });
            }

            var upper = info.MaxDurationWithoutVideo;
            parameters = new Dictionary<string, object?>
            {
                ["resolution"] = safeResolution,
                ["ratio"] = safeRatio == "adaptive" ? "16:9" : safeRatio,
                ["duration"] = Math.Clamp(duration, 2, upper),
                ["watermark"] = false
            };
        }
        else
        {
            // wan3.0 没有 first_clip 这个 type！官方「视频延长」= reference_video +
            // 提示词带延长意图（"延长/延续/续写/向前/向后延长"）。
            // 另外 reference_* 与 first_frame/last_frame 互斥，所以 lastFrame 在这里必须丢掉。
            media = new List<object> { new { type = "reference_video", url = firstClipUrl } };

            if (!HasExtendIntent(prompt))
            {
                finalPrompt = "将视频1向后延长，保持画面风格、角色与镜头连贯。" + prompt;
            }

            // 延长时 duration = 输入+输出的总时长，上限 30。
            parameters = new Dictionary<string, object?>
            {
                ["resolution"] = safeResolution,
                ["ratio"] = safeRatio,
                ["duration"] = Math.Clamp(duration, 2, 30),
                ["prompt_extend"] = true,
                ["watermark"] = false
            };
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = new Dictionary<string, object?>
            {
                ["prompt"] = finalPrompt,
                ["media"] = media
            },
            ["parameters"] = parameters
        };

        return await SubmitAsync(body, ct).ConfigureAwait(false);
    }

    /// <summary>提示词里是否已经带了官方要求的「延长意图」关键词。</summary>
    private static bool HasExtendIntent(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        foreach (var kw in new[] { "延长", "延续", "续写", "接着", "继续" })
        {
            if (prompt.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Media URLs must be reachable by the service. Accepted: public http(s),
    /// and oss:// URLs produced by DashScope's temporary-storage upload.
    /// Local file:// / drive-letter / UNC paths are always rejected.
    /// </summary>
    private static void RequireServerReachableUrl(string url, string role)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"{role} 需要服务端可取到的 URL（http/https 或 oss://），当前为空。");

        var looksLocal = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                         || url.StartsWith(@"\\", StringComparison.Ordinal)
                         || (url.Length >= 2 && char.IsLetter(url[0]) && url[1] == ':');

        var isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var isOss = url.StartsWith("oss://", StringComparison.OrdinalIgnoreCase);

        if (looksLocal || !(isHttp || isOss))
        {
            throw new InvalidOperationException(
                $"{role} 需要服务端可取到的 URL（http/https 或 oss://），本地文件（file:// 或盘符路径）不能直接提交，请先上传到可公网访问的地址。当前值：{url}");
        }
    }

    /// <summary>POST 到 video-synthesis，开 X-DashScope-Async，解析 task_id。</summary>
    private async Task<string> SubmitAsync(Dictionary<string, object?> body, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/v1/services/aigc/video-generation/video-synthesis";
        var payload = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");
        // oss:// URLs from the temporary-storage upload are only resolvable when
        // this header is present; without it the service cannot read the file.
        if (payload.Contains("oss://", StringComparison.Ordinal))
        {
            req.Headers.TryAddWithoutValidation("X-DashScope-OssResourceResolve", "enable");
        }
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var head = json.Length > 500 ? json[..500] : json;
            throw new InvalidOperationException($"Submit failed {(int)resp.StatusCode}: {head}");
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("output", out var output) &&
            output.TryGetProperty("task_id", out var taskId))
        {
            return taskId.GetString() ?? throw new InvalidOperationException("Empty task_id");
        }

        if (doc.RootElement.TryGetProperty("task_id", out var topTask))
        {
            return topTask.GetString() ?? throw new InvalidOperationException("Empty task_id");
        }

        throw new InvalidOperationException($"No task_id in response: {json}");
    }

    public async Task<DashScopeTaskStatus> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/tasks/{taskId}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Poll failed {(int)resp.StatusCode}: {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return new DashScopeTaskStatus(taskId, "PENDING", null, null, json);

        var output = root.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.Object ? o : root;
        // 响应里没有 task_status：当作暂态继续轮询（交给调用方的失败预算/总时限兜底），
        // 不再记成 UNKNOWN —— UNKNOWN 会被当成终态，一次畸形响应就会杀掉一个仍在跑的任务。
        var status = output.TryGetProperty("task_status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? "PENDING"
            : "PENDING";
        string? videoUrl = null;
        if (output.TryGetProperty("video_url", out var vu))
        {
            videoUrl = vu.GetString();
        }
        else if (output.TryGetProperty("results", out var results) &&
                 results.ValueKind == JsonValueKind.Array &&
                 results.GetArrayLength() > 0 &&
                 results[0].TryGetProperty("url", out var u))
        {
            videoUrl = u.GetString();
        }

        string? message = null;
        if (output.TryGetProperty("message", out var msg))
        {
            message = msg.GetString();
        }
        else if (root.TryGetProperty("message", out var rootMsg))
        {
            message = rootMsg.GetString();
        }

        return new DashScopeTaskStatus(taskId, status, videoUrl, message, json);
    }

    public async Task<DashScopeTaskStatus> WaitUntilDoneAsync(
        string taskId,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        var delay = pollInterval ?? TimeSpan.FromSeconds(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = await GetTaskAsync(taskId, ct).ConfigureAwait(false);
            if (status.IsTerminal)
            {
                return status;
            }

            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    public async Task<string> DownloadVideoAsync(string videoUrl, string destinationPath, CancellationToken ct = default)
    {
        if (AppPaths.TryEnsureParentOf(destinationPath) is null)
            throw new IOException($"下载目录不可写：{Path.GetDirectoryName(destinationPath)}");

        // video_url 是别的主机上的预签名 OSS 链接：不能带 API Key 的 Authorization 头
        // （既泄露密钥，OSS 也会因「同时存在两种鉴权」返回 400），所以用一个干净的客户端。
        // Timeout 设为无限，由调用方的 ct 控制取消；大文件下载不能被 2 分钟总超时打断。
        using var plain = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var resp = await plain.GetAsync(videoUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        // 先写 .part，完整落盘后再改名：取消/断网不会在输出目录留下半截 .mp4。
        var part = destinationPath + ".part";
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            File.Move(part, destinationPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { /* best effort */ }
            throw;
        }

        return destinationPath;
    }

    public void Dispose() => _http.Dispose();
}

public sealed record DashScopeTaskStatus(
    string TaskId,
    string Status,
    string? VideoUrl,
    string? Message,
    string RawJson)
{
    public bool IsSucceeded => string.Equals(Status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => string.Equals(Status, "FAILED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "CANCELED", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "UNKNOWN", StringComparison.OrdinalIgnoreCase);
    public bool IsTerminal => IsSucceeded || IsFailed
        || string.Equals(Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
}
