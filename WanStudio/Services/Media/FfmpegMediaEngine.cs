using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WanStudio.Services.Media;

/// <summary>
/// 纯本地的 ffmpeg 实现。不联网，不依赖 ffprobe（本机没有），所有信息都从
/// <c>ffmpeg -hide_banner -i</c> 的 stderr 里解析。
/// </summary>
public sealed class FfmpegMediaEngine : IMediaEngine
{
    /// <summary>
    /// 实测得到的场景阈值（见 qa/run-2026-10-22-media-engine.md 的阈值表）。
    /// 契约里的 <see cref="MediaEngineDefaults.DefaultSceneThreshold"/> 是 0.35，但本机
    /// fixtures\scenes-4x5s.mp4 的红→绿那一刀 scene 分数只有 0.030，0.35 会漏。
    /// 实测分数分布：切点 0.5437 / 0.2700 / 0.0300，片内 testsrc2 抖动最高 0.0157，
    /// 所以可用的窗口是 (0.0157, 0.0300)，取 0.02。
    /// </summary>
    public const double MeasuredSceneThreshold = 0.02;

    /// <summary>两次切点间隔小于该值视为同一刀（同一帧附近 ffmpeg 可能连续报两帧）。</summary>
    private const double SceneDedupeSeconds = 0.5;

    /// <summary>片头这么短的位置不当作切点。</summary>
    private const double SceneEdgeIgnoreSeconds = 0.5;

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(10);

    private static readonly Regex DurationRegex =
        new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static readonly Regex VideoSizeRegex =
        new(@"Stream #\d+:\d+.*?Video:.*?(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);

    private static readonly Regex AudioRegex =
        new(@"Stream #\d+:\d+.*?Audio:", RegexOptions.Compiled);

    private static readonly Regex PtsTimeRegex =
        new(@"pts_time:\s*(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private readonly string _ffmpegPath;

    public FfmpegMediaEngine(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            throw new ArgumentException("ffmpeg 路径不能为空", nameof(ffmpegPath));
        _ffmpegPath = ffmpegPath;
    }

    public string Name => "ffmpeg";

    public string Detail => _ffmpegPath;

    public bool SupportsSceneDetect => true;

    public bool SupportsCut => true;

    public bool SupportsConcat => true;

    /// <summary>ffmpeg 路径可以直接用于 UI 提示/诊断。</summary>
    public string FfmpegPath => _ffmpegPath;

    public async Task<MediaInfo> ProbeAsync(string sourcePath, CancellationToken ct = default)
    {
        var full = RequireFile(sourcePath);

        // 只给 -i 不给输出时 ffmpeg 以 1 退出并把元数据写在 stderr，这是预期行为。
        var result = await RunAsync(new[] { "-hide_banner", "-nostdin", "-i", full }, ct).ConfigureAwait(false);
        var text = result.Stderr;

        var durationMatch = DurationRegex.Match(text);
        if (!durationMatch.Success)
            throw new InvalidOperationException($"无法从 ffmpeg 输出解析时长：{Tail(text, 800)}");

        var duration =
            int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
            int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
            double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);

        var sizeMatch = VideoSizeRegex.Match(text);
        var width = sizeMatch.Success ? int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var height = sizeMatch.Success ? int.Parse(sizeMatch.Groups[2].Value, CultureInfo.InvariantCulture) : 0;

        return new MediaInfo(duration, width, height, AudioRegex.IsMatch(text));
    }

    public async Task<IReadOnlyList<double>> DetectSceneCutsAsync(
        string sourcePath,
        double threshold = MediaEngineDefaults.DefaultSceneThreshold,
        CancellationToken ct = default)
    {
        var full = RequireFile(sourcePath);
        var info = await ProbeAsync(full, ct).ConfigureAwait(false);
        var effective = ResolveThreshold(threshold);

        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"select='gt(scene,{effective.ToString("0.####", CultureInfo.InvariantCulture)})',showinfo");

        var args = new[]
        {
            "-hide_banner", "-nostdin", "-nostats",
            "-i", full,
            "-filter:v", filter,
            "-an",
            "-f", "null", "-",
        };

        var result = await RunAsync(args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg 场景检测失败：{Tail(result.Stderr, 800)}");

        var cuts = new List<double>();
        foreach (Match match in PtsTimeRegex.Matches(result.Stderr))
        {
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                continue;
            if (t < SceneEdgeIgnoreSeconds || t >= info.DurationSeconds)
                continue;
            if (cuts.Count > 0 && t - cuts[cuts.Count - 1] < SceneDedupeSeconds)
                continue;
            cuts.Add(t);
        }

        cuts.Sort();
        return cuts;
    }

    public async Task<string> ExtractFrameAsync(
        string sourcePath,
        double atSeconds,
        string outputImagePath,
        CancellationToken ct = default)
    {
        var full = RequireFile(sourcePath);
        if (string.IsNullOrWhiteSpace(outputImagePath))
            throw new ArgumentException("输出图片路径不能为空", nameof(outputImagePath));
        if (atSeconds < 0 || double.IsNaN(atSeconds) || double.IsInfinity(atSeconds))
            throw new ArgumentOutOfRangeException(nameof(atSeconds));

        var output = Path.GetFullPath(outputImagePath);
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var time = atSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var args = new[]
        {
            "-hide_banner", "-nostdin",
            "-ss", time,
            "-i", full,
            "-frames:v", "1",
            "-q:v", "2",
            "-y", output,
        };

        var result = await RunAsync(args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"ffmpeg 抽帧失败：{Tail(result.Stderr, 800)}");

        return output;
    }

    public async Task<string> CutAsync(
        string sourcePath,
        double startSeconds,
        double durationSeconds,
        string outputVideoPath,
        CancellationToken ct = default)
    {
        var full = RequireFile(sourcePath);
        if (string.IsNullOrWhiteSpace(outputVideoPath))
            throw new ArgumentException("输出视频路径不能为空", nameof(outputVideoPath));
        if (startSeconds < 0 || double.IsNaN(startSeconds) || double.IsInfinity(startSeconds))
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        if (durationSeconds <= 0 || double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));

        var output = Path.GetFullPath(outputVideoPath);
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 重编码而不是 -c copy：copy 会吸附到关键帧，起止秒不可核（AC-L02）。
        var args = new[]
        {
            "-hide_banner", "-nostdin",
            "-ss", startSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", full,
            "-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-c:a", "aac",
            "-movflags", "+faststart",
            "-y", output,
        };

        var result = await RunAsync(args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || !File.Exists(output))
            throw new InvalidOperationException($"ffmpeg 切段失败：{Tail(result.Stderr, 800)}");

        return output;
    }

    public async Task<string> ConcatAsync(
        IReadOnlyList<string> clipPaths,
        string outputVideoPath,
        CancellationToken ct = default)
    {
        if (clipPaths is null || clipPaths.Count == 0)
            throw new ArgumentException("没有可拼接的片段", nameof(clipPaths));
        if (string.IsNullOrWhiteSpace(outputVideoPath))
            throw new ArgumentException("输出视频路径不能为空", nameof(outputVideoPath));

        var output = Path.GetFullPath(outputVideoPath);
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var listPath = Path.Combine(
            Path.GetTempPath(),
            $"wanstudio-concat-{Guid.NewGuid():N}.txt");

        try
        {
            // UTF-8 无 BOM；ffmpeg 的 concat 列表不接受 BOM。
            var builder = new StringBuilder();
            foreach (var clip in clipPaths)
            {
                var full = Path.GetFullPath(RequireFile(clip));
                builder.Append("file '").Append(EscapeConcatPath(full)).Append('\'').Append('\n');
            }
            File.WriteAllText(listPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var args = new[]
            {
                "-hide_banner", "-nostdin",
                "-f", "concat", "-safe", "0",
                "-i", listPath,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-c:a", "aac",
                "-movflags", "+faststart",
                "-y", output,
            };

            var result = await RunAsync(args, ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException($"ffmpeg 拼接失败：{Tail(result.Stderr, 800)}");

            return output;
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    /// <summary>调用方用契约默认值时换成实测阈值；显式传别的值则尊重调用方。</summary>
    private static double ResolveThreshold(double threshold)
    {
        if (double.IsNaN(threshold) || threshold <= 0 ||
            Math.Abs(threshold - MediaEngineDefaults.DefaultSceneThreshold) < 1e-9)
        {
            return MeasuredSceneThreshold;
        }

        return threshold;
    }

    /// <summary>concat 列表里的单引号按 ffmpeg 约定转义成 '\''。</summary>
    private static string EscapeConcatPath(string path)
        => path.Replace("/", "\\").Replace("'", "'\\''");

    private static string RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("源文件路径不能为空", nameof(path));

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"找不到文件：{full}", full);

        return full;
    }

    private static string Tail(string text, int maxChars)
        => string.IsNullOrEmpty(text) || text.Length <= maxChars
            ? text ?? string.Empty
            : text[^maxChars..];

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件删除失败不影响结果。
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// 统一的子进程封装：重定向两个流、不弹窗、支持取消（kill 整棵树）、10 分钟超时。
    /// </summary>
    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout) stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr) stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 ffmpeg 进程");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(ProcessTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);

            if (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException($"ffmpeg 执行超过 {ProcessTimeout.TotalMinutes:0} 分钟，已终止");

            throw;
        }

        // 等异步事件把剩余输出读完。
        process.WaitForExit();

        string outText, errText;
        lock (stdout) outText = stdout.ToString();
        lock (stderr) errText = stderr.ToString();

        return new ProcessResult(process.ExitCode, outText, errText);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能刚好自己退出，忽略。
        }
    }
}
