using System;
using System.Collections.Generic;
using System.Linq;

namespace WanStudio.Services.Media;

/// <summary>
/// 切段纯函数。不碰磁盘、不碰 UI，方便直接单测。
/// 所有输出保证：Start 严格递增、段与段不重叠、末段 End &lt;= duration + 0.001。
/// </summary>
public static class SegmentPlanner
{
    /// <summary>防止 UI 列表被异常输入撑爆。</summary>
    public const int MaxSegments = 400;

    private const double Epsilon = 0.001;

    /// <summary>
    /// 固定长度切段：从 0 起切，末段不足 <see cref="MediaEngineDefaults.MinUsableSegmentSeconds"/>
    /// 时并进前一段。segmentSeconds 会被夹到 [1, 15]。
    /// </summary>
    public static IReadOnlyList<SegmentSpec> PlanFixed(double durationSeconds, double segmentSeconds)
    {
        if (!IsFinite(durationSeconds) || durationSeconds <= 0)
            return Array.Empty<SegmentSpec>();

        var size = ClampSegmentSeconds(segmentSeconds);

        // 短视频：一段就够。
        if (durationSeconds <= size + Epsilon)
            return new[] { new SegmentSpec(0, durationSeconds) };

        var rawCount = (int)Math.Ceiling(durationSeconds / size - 1e-9);
        if (rawCount > MaxSegments)
            return TruncateByFixedSize(durationSeconds, size);

        var segments = new List<SegmentSpec>(rawCount);
        var start = 0d;
        while (durationSeconds - start > size + Epsilon)
        {
            segments.Add(new SegmentSpec(start, size));
            start += size;
        }

        var rest = durationSeconds - start;
        if (rest > Epsilon)
        {
            if (rest < MediaEngineDefaults.MinUsableSegmentSeconds && segments.Count > 0)
            {
                var last = segments[^1];
                var combined = durationSeconds - last.StartSeconds;
                if (combined <= MediaEngineDefaults.MaxSegmentSeconds + Epsilon)
                {
                    segments[^1] = new SegmentSpec(last.StartSeconds, combined);
                }
                else
                {
                    // 并进去会超过单次 15s 上限（例如 15.5s / 15s）：把最后两段对半均分。
                    var half = combined / 2;
                    segments[^1] = new SegmentSpec(last.StartSeconds, half);
                    segments.Add(new SegmentSpec(last.StartSeconds + half, combined - half));
                }
            }
            else
            {
                segments.Add(new SegmentSpec(start, rest));
            }
        }

        return Normalize(segments, durationSeconds);
    }

    /// <summary>
    /// 按切镜点切段：以 cuts 为界，段 &gt; 15s 按 10s 均分，段 &lt; 1s 并入前段，首段从 0 起。
    /// cuts 里的非法值（NaN/0/负数/超出时长）会被丢弃。
    /// </summary>
    public static IReadOnlyList<SegmentSpec> PlanFromSceneCuts(double durationSeconds, IReadOnlyList<double>? cuts)
    {
        if (!IsFinite(durationSeconds) || durationSeconds <= 0)
            return Array.Empty<SegmentSpec>();

        var bounds = new List<double> { 0 };
        if (cuts is not null)
        {
            foreach (var cut in cuts.Where(IsFinite).OrderBy(c => c))
            {
                if (cut <= Epsilon || cut >= durationSeconds - Epsilon)
                    continue;
                if (cut - bounds[^1] <= Epsilon)
                    continue;
                bounds.Add(cut);
            }
        }

        if (durationSeconds - bounds[^1] > Epsilon)
            bounds.Add(durationSeconds);

        if (bounds.Count < 2)
            return new[] { new SegmentSpec(0, durationSeconds) };

        var pieces = new List<SegmentSpec>(bounds.Count - 1);
        for (var i = 0; i < bounds.Count - 1; i++)
            pieces.Add(new SegmentSpec(bounds[i], bounds[i + 1] - bounds[i]));

        var merged = MergeShortSegments(pieces);
        var split = SplitLongSegments(merged);
        var capped = CapCount(split);
        return Normalize(capped, durationSeconds);
    }

    /// <summary>&lt; 1s 的段并进前段；首段太短则并进后段（没有前段可并）。</summary>
    private static List<SegmentSpec> MergeShortSegments(List<SegmentSpec> pieces)
    {
        var result = new List<SegmentSpec>(pieces.Count);
        double? pendingStart = null;

        foreach (var piece in pieces)
        {
            if (piece.DurationSeconds >= MediaEngineDefaults.MinUsableSegmentSeconds - 1e-9)
            {
                if (pendingStart.HasValue)
                {
                    // 首段太短：从 pendingStart 一直覆盖到本段结束。
                    result.Add(new SegmentSpec(pendingStart.Value, piece.EndSeconds - pendingStart.Value));
                    pendingStart = null;
                }
                else
                {
                    result.Add(piece);
                }

                continue;
            }

            if (result.Count > 0)
            {
                var prev = result[^1];
                result[^1] = new SegmentSpec(prev.StartSeconds, piece.EndSeconds - prev.StartSeconds);
            }
            else
            {
                // 连续多个过短的开头片段：起点必须停在第一个，否则会丢掉片头。
                pendingStart ??= piece.StartSeconds;
            }
        }

        if (pendingStart.HasValue)
            result.Add(new SegmentSpec(pendingStart.Value, pieces[^1].EndSeconds - pendingStart.Value));

        return result;
    }

    /// <summary>超长段按 FallbackSplitSeconds(10s) 均分，均分后每段都 &lt;= 15s。</summary>
    private static List<SegmentSpec> SplitLongSegments(List<SegmentSpec> pieces)
    {
        var result = new List<SegmentSpec>(pieces.Count);

        foreach (var piece in pieces)
        {
            if (piece.DurationSeconds <= MediaEngineDefaults.MaxSegmentSeconds + Epsilon)
            {
                result.Add(piece);
                continue;
            }

            var parts = (int)Math.Ceiling(piece.DurationSeconds / MediaEngineDefaults.FallbackSplitSeconds);
            if (parts < 2)
                parts = 2;

            var partLength = piece.DurationSeconds / parts;
            for (var i = 0; i < parts; i++)
            {
                var start = piece.StartSeconds + i * partLength;
                var end = i == parts - 1 ? piece.EndSeconds : start + partLength;
                result.Add(new SegmentSpec(start, end - start));
            }
        }

        return result;
    }

    /// <summary>
    /// 段数超过 400 时只保留前 400 段。
    /// 早先把剩余部分合成一个末段，会产生远超 15s 上限的超长段，API 一定拒绝；宁可截断也不产出非法段。
    /// </summary>
    private static List<SegmentSpec> CapCount(List<SegmentSpec> pieces)
        => pieces.Count <= MaxSegments ? pieces : pieces.Take(MaxSegments).ToList();

    /// <summary>
    /// 固定长度切出来会超过 400 段：先把段长抬高到刚好 400 段装得下（上限 15s）；
    /// 仍装不下（视频超过 400×15s）就只覆盖前 400 段，不再产出超长末段。
    /// </summary>
    private static IReadOnlyList<SegmentSpec> TruncateByFixedSize(double durationSeconds, double size)
    {
        var bigger = Math.Ceiling(durationSeconds / MaxSegments * 10) / 10;
        size = Math.Min(MediaEngineDefaults.MaxSegmentSeconds, Math.Max(size, bigger));

        var count = Math.Min(MaxSegments, (int)Math.Ceiling(durationSeconds / size - 1e-9));
        var result = new List<SegmentSpec>(count);
        for (var i = 0; i < count; i++)
        {
            var start = i * size;
            result.Add(new SegmentSpec(start, Math.Min(size, durationSeconds - start)));
        }

        return Normalize(result, durationSeconds);
    }

    /// <summary>最终校正：去零长段、保证递增、末段不超出时长。</summary>
    private static IReadOnlyList<SegmentSpec> Normalize(List<SegmentSpec> pieces, double durationSeconds)
    {
        var result = new List<SegmentSpec>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (!IsFinite(piece.StartSeconds) || !IsFinite(piece.DurationSeconds))
                continue;

            var start = Math.Max(0, Math.Min(piece.StartSeconds, durationSeconds));
            var end = Math.Min(piece.EndSeconds, durationSeconds);
            if (end - start <= Epsilon)
                continue;

            result.Add(new SegmentSpec(start, end - start));
        }

        return result;
    }

    private static double ClampSegmentSeconds(double value)
    {
        if (!IsFinite(value))
            return MediaEngineDefaults.MinSegmentSeconds;

        return Math.Clamp(
            value,
            MediaEngineDefaults.MinSegmentSeconds,
            MediaEngineDefaults.MaxSegmentSeconds);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
