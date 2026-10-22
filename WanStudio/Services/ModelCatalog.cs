using System;
using System.Collections.Generic;

namespace WanStudio.Services;

/// <summary>
/// 一个生成模型 + UI 允许提供的各项能力。
///
/// 能力全部来自官方文档（万相3.0-视频生成、万相2.7 参考生视频），不是猜的：
///
/// <b>wan3.0-video / wan3.0-video-prime</b> —— All-in-One，不需要换模型名，
/// 服务端按 input.media 里的 type 与提示词意图自动路由：
///   - 只传 prompt              → 文生视频
///   - first_frame              → 首帧生视频
///   - first_frame + last_frame → 首尾帧生视频
///   - reference_image / reference_video / reference_audio / file / link → 全模态参考
///   - reference_video + 编辑/延长意图词 → 视频编辑 / 视频延长
///   素材上限：图片 ≤10 张（单张 ≤20MB）、视频 ≤5 段（总时长 ≤15s、单个 ≤100MB）、
///   音频 ≤5 段（总时长 ≤15s、单个 ≤15MB）。音频字段名是 reference_audio。
///   时长 2–30s 或 -1；有视频输入时「输入 + 输出 ≤30s」。
///   注意 reference_* 与 first_frame/last_frame 互斥，不能混用。
///
/// <b>wan2.7-i2v</b> —— 图生 + 首片段续写（first_clip，2–10s）。
///
/// <b>wan2.7-r2v</b> —— 参考生视频，参考素材 ≤5（图像+视频合计），
/// 音频字段名是 reference_voice（挂在 reference_image / reference_video 内部）。
/// </summary>
public sealed record ModelInfo(
    string Id,
    string Label,
    bool TextToVideo,
    bool ImageToVideo,
    bool FirstClipContinue,
    bool ReferenceToVideo = false,
    int MaxReferenceImages = 0,
    int MaxReferenceVideos = 0,
    int MaxReferenceAudios = 0,
    bool AcceptsReferenceVideo = false,
    int MaxDurationWithVideo = 10,
    int MaxDurationWithoutVideo = 15,
    bool SupportsLastFrame = false,
    /// <summary>参考素材合计上限（图像+视频）。0 = 不额外限制，按各类型上限判。</summary>
    int MaxReferenceTotal = 0,
    /// <summary>
    /// 支持「通用视频编辑」式的人物替换（wan2.7-videoedit）。
    /// 与 ReferenceToVideo 的区别：它用 media[].type="video" 传源视频，
    /// 配最多 4 张 reference_image，且 duration=0 表示保持原视频完整时长。
    /// </summary>
    bool VideoEditing = false,
    /// <summary>视频编辑的参考图上限（官方 4 张）。</summary>
    int MaxEditReferenceImages = 0,
    /// <summary>视频编辑的最长输出时长（官方 2–10s；0 表示跟随原视频）。</summary>
    int MaxEditDurationSeconds = 10,
    /// <summary>参考音频的字段名：wan3.0 用 reference_audio，wan2.7-r2v 用 reference_voice。</summary>
    string ReferenceAudioField = "")
{
    /// <summary>参考素材总上限（图像 + 视频 + 音频）。老代码按「合计 ≤N」校验的入口。</summary>
    public int MaxReferenceMaterials => MaxReferenceTotal > 0
        ? MaxReferenceTotal
        : MaxReferenceImages + MaxReferenceVideos;

    /// <summary>x:xx 比例是否受支持。</summary>
    public bool SupportsRatio(string ratio) => ratio switch
    {
        "adaptive" => true,
        "16:9" or "9:16" or "1:1" => true,
        // 4:3 / 3:4 是 wan3.0 才有；wan2.7 系列只认前四档。
        "4:3" or "3:4" => Id.StartsWith("wan3.0", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}

/// <summary>
/// Model / capability table。
/// 改动这里等于改 UI 可选项。
/// </summary>
public static class ModelCatalog
{
    public static IReadOnlyList<ModelInfo> All { get; } = new List<ModelInfo>
    {
        // All-in-One：文生 / 图生 / 首尾帧 / 全模态参考 / 视频编辑 / 视频延长 全都支持。
        new("wan3.0-video-prime", "Wan 3.0 视频 Prime",
            TextToVideo: true, ImageToVideo: true, FirstClipContinue: false,
            ReferenceToVideo: true,
            MaxReferenceImages: 10, MaxReferenceVideos: 5, MaxReferenceAudios: 5,
            AcceptsReferenceVideo: true,
            MaxDurationWithVideo: 30, MaxDurationWithoutVideo: 30,
            SupportsLastFrame: true,
            ReferenceAudioField: "reference_audio"),

        new("wan3.0-video", "Wan 3.0 视频",
            TextToVideo: true, ImageToVideo: true, FirstClipContinue: false,
            ReferenceToVideo: true,
            MaxReferenceImages: 10, MaxReferenceVideos: 5, MaxReferenceAudios: 5,
            AcceptsReferenceVideo: true,
            MaxDurationWithVideo: 30, MaxDurationWithoutVideo: 30,
            SupportsLastFrame: true,
            ReferenceAudioField: "reference_audio"),

        // 人物替换的正解：官方「通用视频编辑」。用 type="video" 传源视频，
        // 配参考图做物体/人物替换，duration=0 保持原视频时长，audio_setting=origin 保留原声。
        new("wan2.7-videoedit", "Wan 2.7 视频编辑（人物替换）",
            TextToVideo: false, ImageToVideo: false, FirstClipContinue: false,
            VideoEditing: true, MaxEditReferenceImages: 4, MaxEditDurationSeconds: 10),

        new("wan2.7-i2v", "Wan 2.7 图生视频",
            TextToVideo: false, ImageToVideo: true, FirstClipContinue: true,
            MaxDurationWithVideo: 10, MaxDurationWithoutVideo: 15),

        // 参考生视频：参考素材 ≤5，音频用 reference_voice 挂在参考素材内部。
        new("wan2.7-r2v", "Wan 2.7 参考生视频（多角色）",
            TextToVideo: false, ImageToVideo: false, FirstClipContinue: false,
            ReferenceToVideo: true,
            MaxReferenceImages: 5, MaxReferenceVideos: 5, MaxReferenceAudios: 5,
            AcceptsReferenceVideo: true,
            MaxDurationWithVideo: 10, MaxDurationWithoutVideo: 15,
            MaxReferenceTotal: 5,
            ReferenceAudioField: "reference_voice")
    };

    /// <summary>默认文生 / 图生模型。</summary>
    public static string DefaultModelId { get; } = "wan3.0-video-prime";

    /// <summary>默认首片段续写模型（目前只有它 FirstClipContinue=true）。</summary>
    public static string DefaultContinueModelId { get; } = "wan2.7-i2v";

    /// <summary>默认参考生视频模型。产品主力是 wan3.0，所以默认也用 3.0。</summary>
    public static string DefaultReferenceModelId { get; } = "wan3.0-video";

    public static ModelInfo? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var m in All)
        {
            if (string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) return m;
        }
        return null;
    }

    public static bool SupportsFirstClip(string? id) => Find(id)?.FirstClipContinue == true;

    public static bool SupportsReference(string? id) => Find(id)?.ReferenceToVideo == true;

    /// <summary>该模型能否做人物替换 / 视频编辑（两种路子任一即可）。</summary>
    public static bool SupportsCharacterReplace(string? id)
    {
        var m = Find(id);
        return m is { VideoEditing: true } || m is { ReferenceToVideo: true };
    }

    /// <summary>参考素材上限（图像 + 视频）。保留旧入口，内部按新字段算。</summary>
    public static int MaxReferenceMaterials(string? id) => Find(id)?.MaxReferenceMaterials ?? 0;

    /// <summary>该模型参考音频用的字段名（空 = 不支持）。</summary>
    public static string ReferenceAudioField(string? id) => Find(id)?.ReferenceAudioField ?? "";
}
