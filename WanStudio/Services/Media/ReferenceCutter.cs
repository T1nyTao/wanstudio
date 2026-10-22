using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WanStudio.Services.Media;

/// <summary>
/// 一段本地切出来的参考视频。
/// <para>
/// 重要：这是**本地剪辑**产物——按时间轴把源文件硬切成一段，<b>不是 AI 生成结果</b>，
/// 也不属于「续接」。切出来的片段只用于填 <c>wan2.7-r2v</c> 的 <c>reference_video</c> 输入槽，
/// 提交后的生成质量与这段剪辑本身无关。
/// </para>
/// </summary>
public sealed record CutClip(string LocalPath, double StartSeconds, double DurationSeconds)
{
    /// <summary>区间结束时间（秒）。</summary>
    public double EndSeconds => StartSeconds + DurationSeconds;

    /// <summary>
    /// true 表示这一「段」其实没有真正切，直接引用了用户源文件本身
    /// （源视频时长本来就落在 [1, 30] 窗口内，见 <see cref="ReferenceCutter.CutAsync"/> 的短路分支）。
    /// 这种条目的 <see cref="LocalPath"/> 是用户的原始文件，素材库做删除/清理时必须跳过，
    /// 不能把源文件删掉。
    /// </summary>
    public bool IsSourceCopy { get; init; }

    /// <summary>"00:00–00:30" 形式的区间标签，给素材卡片/下拉框展示用（按秒四舍五入）。</summary>
    public string RangeLabel =>
        $"{ReferenceCutter.FormatTimecode(StartSeconds)}–{ReferenceCutter.FormatTimecode(EndSeconds)}";

    /// <summary>日志/用例输出用的一行描述。</summary>
    public override string ToString()
        => $"{RangeLabel} ({DurationSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}s) -> {LocalPath}";
}

/// <summary>
/// 参考视频自动剪切：把一段很长的源视频规划成若干段，每段都落进
/// <c>reference_video</c> 的官方窗口 <b>[1s, 30s]</b>，再交给媒体引擎逐段本地裁剪。
///
/// <para>
/// 分工：<b>规划是纯函数</b>（<see cref="Plan"/>，不碰磁盘、不碰 ffmpeg、可单测）；
/// <b>执行只调 <see cref="IMediaEngine.CutAsync"/></b>（本类自己绝不启动 ffmpeg 进程、不碰网络、不碰 API）。
/// </para>
///
/// <para>
/// 边界策略（30.5s + chunk 30 怎么切）——选定「末段并入前段，并入后超上限则两段均分」：
/// <list type="number">
/// <item>先按 chunk 贪心切，得到 30 + 0.5。</item>
/// <item>末段 0.5s &lt; 1s，不能单独成段 → 先尝试并入前段，得到一段 30.5s。</item>
/// <item>30.5s &gt; 30s 硬上限，这个「并」不成立 → 把「前一段 + 末段」这块 30.5s
///       在两段之间均分，得到 15.25 + 15.25。</item>
/// </list>
/// 为什么不选 30 + 1（把末段起点往回挪）：那样两段会重叠 0.5s，同一帧内容被算两遍，
/// 且第一段顶在 30s 上限上，ffmpeg 重编码后哪怕多出一帧就越界；均分让每段离上下限都远，
/// 重编码误差吃不掉合法性。为什么不选 29 + 1.5：那只是把问题挪了个位置，
/// 代码里没有「为什么是 1.5 不是 1.25」的依据；均分是这条链路上唯一有确定规则的做法
/// （与 <see cref="SegmentPlanner"/> 里 <c>SplitLongSegments</c> 的均分规则一致）。
/// 另外 15.25 + 15.25 对参考生视频比 29.5 + 1.0 更有用：两段都是能当参考用的长度，
/// 1s 的片段几乎喂不出内容，等于白切一刀。
/// </para>
///
/// <para>
/// 另一个边界（61s + chunk 30）：官方窗口是 30s，2 段最多只能盖 60s，
/// 所以 61s <b>不可能</b>用 2 段覆盖，只能切成 30 + 30 + 1（末段恰好卡在 1s 下限，仍然合法）。
/// 如果调用方不想要 1s 碎段，应该把 chunk 调到能整除得开的位置（实测：chunk 21 → 21 + 21 + 19，
/// 末段 19s；而 chunk 20 仍然会剩 1s 尾部，因为 61 = 20×3 + 1），而不是放宽 30s 上限。
/// </para>
///
/// <para>
/// 复用说明：没有直接调用 <see cref="SegmentPlanner.PlanFixed"/>——它的段长被
/// <c>ClampSegmentSeconds</c> 硬夹在 [1, <see cref="MediaEngineDefaults.MaxSegmentSeconds"/>] = [1, 15]，
/// 那是**生成窗口**（单次请求 15s），对 30s 的参考窗口切 30.5s 会得到 15 + 15.5，语义不对。
/// 这里复用它的三样东西：<see cref="SegmentPlanner.MaxSegments"/> 作为段数上限、
/// <see cref="MediaEngineDefaults.MinUsableSegmentSeconds"/> 作为末段可用下限、
/// 以及「末段不足下限就并入前段 / 超上限就均分」这条规则本身。
/// </para>
/// </summary>
public static class ReferenceCutter
{
    /// <summary>官方 <c>reference_video</c> 时长上限：30s。</summary>
    public const double MaxReferenceSeconds = 30.0;

    /// <summary>官方 <c>reference_video</c> 时长下限：1s。</summary>
    public const double MinReferenceSeconds = 1.0;

    /// <summary>默认每段秒数。留 5s 余量，不卡 30s 上限（重编码后时长有几十毫秒抖动）。</summary>
    public const double DefaultChunkSeconds = 25.0;

    /// <summary>浮点比较容差：1ms。所有边界都按这个容差判断，避免 30.0000001 这种误判。</summary>
    private const double Epsilon = 0.001;

    /// <summary>
    /// 规划切点：把 <paramref name="totalSeconds"/> 切成若干段，
    /// <b>每段都在 [1s, 30s] 内</b>，且首尾相接、连续覆盖整段源视频（不重叠、不丢帧）。
    ///
    /// <para>
    /// 纯函数：不碰磁盘、不碰 ffmpeg、不碰 UI，可以直接单测。
    /// 返回项的 Start 严格递增；返回值可能为空（源视频短于 1s，没有任何合法片段）。
    /// </para>
    ///
    /// <para>
    /// <paramref name="chunkSeconds"/> 是**期望**粒度，不是硬约束：它会被夹到 [1, 30]；
    /// 在「末段不足 1s」和「并入后超过 30s」这两处，实际段长会和 chunk 不同（见类型注释的 30.5s 例子）。
    /// </para>
    /// </summary>
    /// <param name="totalSeconds">源视频总时长（秒），一般来自 <see cref="IMediaEngine.ProbeAsync"/>。</param>
    /// <param name="chunkSeconds">期望每段秒数，默认 <see cref="DefaultChunkSeconds"/>。</param>
    public static IReadOnlyList<(double Start, double Duration)> Plan(
        double totalSeconds,
        double chunkSeconds = DefaultChunkSeconds)
    {
        if (!IsFinite(totalSeconds) || totalSeconds <= 0)
            return Array.Empty<(double Start, double Duration)>();

        // 源视频短于 1s：切完也没有任何一段能进 reference_video 窗口，返回空表让上层提示用户换素材。
        if (totalSeconds < MinReferenceSeconds - Epsilon)
            return Array.Empty<(double Start, double Duration)>();

        var chunk = ClampChunk(chunkSeconds);

        // 总时长没超过期望粒度：一段就够，不做多余切割。
        if (totalSeconds <= chunk + Epsilon)
            return new[] { (0d, totalSeconds) };

        // 防御：chunk 被夹到 1s 时超长源会算出几百段，撑爆素材库列表。
        // 段数上限直接沿用 SegmentPlanner 的常量，超了就放粗粒度（最多放到 30s）。
        var wanted = (int)Math.Ceiling(totalSeconds / chunk - 1e-9);
        if (wanted > SegmentPlanner.MaxSegments)
            chunk = Math.Min(MaxReferenceSeconds, totalSeconds / SegmentPlanner.MaxSegments);

        var pieces = BuildGreedy(totalSeconds, chunk);
        ResolveShortTail(pieces, totalSeconds);
        return EnsureWithinMax(pieces);
    }

    /// <summary>
    /// 执行切分：探测时长 → <see cref="Plan"/> → 用 <see cref="IMediaEngine.CutAsync"/> 逐段切到
    /// <paramref name="outputDir"/>，返回切好的片段（按时间顺序）。
    ///
    /// <para>
    /// 这是**本地剪辑**，不联网、不调 API、不产生生成任务。
    /// </para>
    ///
    /// <para>
    /// 短路：源视频时长本身就在 [1s, 30s] 内时，直接返回单段并引用**源文件本身**
    /// （不重切，省一次重编码的质量损失和耗时），此时 <see cref="CutClip.IsSourceCopy"/> 为 true，
    /// 调用方清理素材时不要删它。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> 为 null。</exception>
    /// <exception cref="ArgumentException">源路径或输出目录为空。</exception>
    /// <exception cref="InvalidOperationException">当前媒体引擎不支持裁剪（<see cref="IMediaEngine.SupportsCut"/> 为 false）。</exception>
    public static async Task<IReadOnlyList<CutClip>> CutAsync(
        IMediaEngine engine,
        string sourcePath,
        string outputDir,
        double chunkSeconds = DefaultChunkSeconds,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        if (engine is null)
            throw new ArgumentNullException(nameof(engine));
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("参考视频路径不能为空。", nameof(sourcePath));
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new ArgumentException("片段输出目录不能为空。", nameof(outputDir));
        if (!engine.SupportsCut)
        {
            throw new InvalidOperationException(
                $"当前媒体引擎（{engine.Name}）不支持裁剪，无法自动切分参考视频。");
        }

        ct.ThrowIfCancellationRequested();

        // 时长一律以媒体引擎探测结果为准，不看扩展名、不猜。
        var info = await engine.ProbeAsync(sourcePath, ct).ConfigureAwait(false);
        var total = info.DurationSeconds;

        if (!IsFinite(total) || total < MinReferenceSeconds - Epsilon)
            return Array.Empty<CutClip>();

        var plan = Plan(total, chunkSeconds);
        if (plan.Count == 0)
            return Array.Empty<CutClip>();

        // 单段 = 源视频自身就在窗口内：不重切，直接引用源文件（调用方不要删）。
        if (plan.Count == 1)
        {
            progress?.Report((1, 1));
            return new[] { new CutClip(sourcePath, 0d, total) { IsSourceCopy = true } };
        }

        // 调用方负责给出可写目录；这里再兜一次，失败时抛出带路径的明确错误。
        if (!AppPaths.TryEnsureWritable(outputDir, out var writeError))
            throw new IOException($"裁切输出目录不可写：{writeError}");

        var clips = new List<CutClip>(plan.Count);
        for (var i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (start, duration) = plan[i];

            // 文件名：ref-{序号:000}-{guid}.mp4。guid 保证多次剪切、或同一源文件被并发切时互不覆盖。
            var outputPath = Path.Combine(outputDir, $"ref-{i + 1:000}-{Guid.NewGuid():N}.mp4");

            var produced = await engine.CutAsync(sourcePath, start, duration, outputPath, ct)
                .ConfigureAwait(false);

            // 引擎正常应抛异常；这里再兜一层，避免把不存在的路径当成素材写进池子。
            if (string.IsNullOrWhiteSpace(produced) || !File.Exists(produced))
                throw new InvalidOperationException($"切段 {i + 1} 没有产出文件：{outputPath}");

            clips.Add(new CutClip(produced, start, duration));

            // 每完成一段就报一次进度，UI 可以按 (Done/Total) 画进度条。
            progress?.Report((i + 1, plan.Count));
        }

        return clips;
    }

    /// <summary>
    /// "00:00" 形式的时间码（分钟可以超过 60，长片显示成 75:30 而不是 01:15:30）。
    /// 按秒四舍五入，只用于展示；真实时长以 <see cref="CutClip.DurationSeconds"/> 为准。
    /// </summary>
    public static string FormatTimecode(double seconds)
    {
        if (!IsFinite(seconds) || seconds < 0)
            seconds = 0;

        var whole = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        var minutes = whole / 60;
        var rest = whole % 60;
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{minutes:00}:{rest:00}");
    }

    /// <summary>把 chunk 夹到 [1, 30]：小于 1s 的段本身就不合法，大于 30s 会越官方上限。</summary>
    private static double ClampChunk(double chunkSeconds)
    {
        if (!IsFinite(chunkSeconds))
            return DefaultChunkSeconds;

        return Math.Clamp(chunkSeconds, MinReferenceSeconds, MaxReferenceSeconds);
    }

    /// <summary>从 0 起按 chunk 贪心铺段，末段剩下多少就是多少（可能不足 1s，由调用方处理）。</summary>
    private static List<(double Start, double Duration)> BuildGreedy(double totalSeconds, double chunk)
    {
        var pieces = new List<(double Start, double Duration)>();
        var start = 0d;

        while (totalSeconds - start > chunk + Epsilon)
        {
            pieces.Add((start, chunk));
            start += chunk;
        }

        var rest = totalSeconds - start;
        if (rest > Epsilon)
            pieces.Add((start, rest));

        return pieces;
    }

    /// <summary>
    /// 末段 &lt; 1s 时不能单独成段：先试「并入前一段」；并进去后若超过 30s 硬上限
    /// （说明「前一段 + 末段」这块本身就装不进一个合法片段），就改成把这块在当前段数内均分。
    /// </summary>
    private static void ResolveShortTail(List<(double Start, double Duration)> pieces, double totalSeconds)
    {
        if (pieces.Count < 2)
            return;

        var tail = pieces[^1];
        if (tail.Duration >= MinReferenceSeconds - Epsilon)
            return;

        // 前一段起点到结尾这一整块，是「末段并入前段」后的大小。
        var tailStart = pieces[^2].Start;
        var mergedLength = totalSeconds - tailStart;

        pieces.RemoveRange(pieces.Count - 2, 2);

        if (mergedLength <= MaxReferenceSeconds + Epsilon)
        {
            // 装得下：并成一段（与 SegmentPlanner.PlanFixed 的末段合并语义一致）。
            pieces.Add((tailStart, mergedLength));
            return;
        }

        // 装不下：两段均分。30.5s + chunk 30 走的就是这里 → 15.25 + 15.25。
        pieces.AddRange(SplitEvenly(tailStart, mergedLength, 2));
    }

    /// <summary>
    /// 最后一层防御：任何一段超过 30s 硬上限（chunk 被夹、浮点边界、异常输入）都按
    /// <see cref="MaxReferenceSeconds"/> 均分掉。正常路径不会触发。
    /// </summary>
    private static IReadOnlyList<(double Start, double Duration)> EnsureWithinMax(
        List<(double Start, double Duration)> pieces)
    {
        var needsSplit = false;
        foreach (var piece in pieces)
        {
            if (piece.Duration > MaxReferenceSeconds + Epsilon)
            {
                needsSplit = true;
                break;
            }
        }

        if (!needsSplit)
            return pieces;

        var fixedPieces = new List<(double Start, double Duration)>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (piece.Duration <= MaxReferenceSeconds + Epsilon)
            {
                fixedPieces.Add(piece);
                continue;
            }

            var parts = (int)Math.Ceiling(piece.Duration / MaxReferenceSeconds - 1e-9);
            fixedPieces.AddRange(SplitEvenly(piece.Start, piece.Duration, parts));
        }

        return fixedPieces;
    }

    /// <summary>
    /// 把 [start, start+length) 均分成 parts 份，最后一份吃掉浮点余量以保证收尾精确落在末尾。
    /// 与 <see cref="SegmentPlanner"/> 里 <c>SplitLongSegments</c> 的均分算法同构。
    /// </summary>
    private static List<(double Start, double Duration)> SplitEvenly(double start, double length, int parts)
    {
        if (parts < 1)
            parts = 1;

        var result = new List<(double Start, double Duration)>(parts);
        var partLength = length / parts;
        var end = start + length;

        for (var i = 0; i < parts; i++)
        {
            var pieceStart = start + i * partLength;
            var pieceEnd = i == parts - 1 ? end : pieceStart + partLength;
            result.Add((pieceStart, pieceEnd - pieceStart));
        }

        return result;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
