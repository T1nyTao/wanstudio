using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WanStudio.Services.Media;

/// <summary>Basic container metadata. Duration is what the segment planner relies on.</summary>
public sealed record MediaInfo(double DurationSeconds, int Width, int Height, bool HasAudio);

/// <summary>A segment window inside the source video. Always 1..15s for API compatibility.</summary>
public sealed record SegmentSpec(double StartSeconds, double DurationSeconds)
{
    public double EndSeconds => StartSeconds + DurationSeconds;
}

/// <summary>
/// Local file media operations. Implementations must never call the network.
/// Cutting/concatenating is local-only and must not be presented as official continuation.
/// </summary>
public interface IMediaEngine
{
    /// <summary>"ffmpeg" or "windows-media".</summary>
    string Name { get; }

    /// <summary>Human readable detail (exe path or API name) shown in the UI.</summary>
    string Detail { get; }

    bool SupportsSceneDetect { get; }

    /// <summary>True when CutAsync can actually produce a trimmed clip.</summary>
    bool SupportsCut { get; }

    /// <summary>True when ConcatAsync can produce a local hard-stitch.</summary>
    bool SupportsConcat { get; }

    Task<MediaInfo> ProbeAsync(string sourcePath, CancellationToken ct = default);

    /// <summary>Returns absolute cut times (seconds, ascending), excluding 0.</summary>
    Task<IReadOnlyList<double>> DetectSceneCutsAsync(
        string sourcePath,
        double threshold = MediaEngineDefaults.DefaultSceneThreshold,
        CancellationToken ct = default);

    /// <summary>Writes a single frame as png/jpg and returns the output path.</summary>
    Task<string> ExtractFrameAsync(
        string sourcePath,
        double atSeconds,
        string outputImagePath,
        CancellationToken ct = default);

    /// <summary>Writes one trimmed clip and returns the output path. Requires SupportsCut.</summary>
    Task<string> CutAsync(
        string sourcePath,
        double startSeconds,
        double durationSeconds,
        string outputVideoPath,
        CancellationToken ct = default);

    /// <summary>Concatenates clips in the given order (local hard-stitch).</summary>
    Task<string> ConcatAsync(
        IReadOnlyList<string> clipPaths,
        string outputVideoPath,
        CancellationToken ct = default);
}

public static class MediaEngineDefaults
{
    /// <summary>Single request must stay inside the Wan API window.</summary>
    public const double MinSegmentSeconds = 1.0;
    public const double MaxSegmentSeconds = 15.0;

    public const double DefaultSceneThreshold = 0.35;

    /// <summary>Fallback split length for scene segments that are too long for one request.</summary>
    public const double FallbackSplitSeconds = 10.0;

    /// <summary>Segments shorter than this are merged into a neighbour instead of dropped.</summary>
    public const double MinUsableSegmentSeconds = 1.0;
}
