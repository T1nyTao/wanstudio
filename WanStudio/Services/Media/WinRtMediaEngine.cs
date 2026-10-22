using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WanStudio.Services.Media;

/// <summary>
/// 没有 ffmpeg 时的兜底引擎，全部走 Windows.Media（MediaComposition / BitmapEncoder）。
/// 能读元数据、能抽帧、能用缩略图采样做粗糙的切镜检测；
/// <b>不能</b>切段、<b>不能</b>拼接 —— 这两项直接抛 <see cref="NotSupportedException"/>，不假装支持。
/// </summary>
public sealed class WinRtMediaEngine : IMediaEngine
{
    /// <summary>缩略图采样尺寸（契约：108×60）。</summary>
    private const int SampleWidth = 108;
    private const int SampleHeight = 60;

    /// <summary>比较尺寸（契约：缩到 16×9）。</summary>
    private const int CompareWidth = 16;
    private const int CompareHeight = 9;

    /// <summary>实测阈值。WinRT 缩略图采样对硬切不如 ffmpeg 的 scene 分数敏感，0.12 是折中值。</summary>
    public const double MeasuredSceneThreshold = 0.12;

    /// <summary>采样点数上限（契约：≤240 个点）。</summary>
    private const int MaxSceneSamples = 240;

    /// <summary>采样步长下限（契约：≥0.25s）。</summary>
    private const double MinSampleStep = 0.25;

    private const double SceneDedupeSeconds = 0.5;
    private const double SceneEdgeIgnoreSeconds = 0.5;

    /// <summary>Shell 属性键：有音轨时可取到声道数。</summary>
    private const string AudioChannelCountProperty = "System.Audio.ChannelCount";

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private MediaComposition? _composition;
    private string? _compositionPath;

    public string Name => "windows-media";

    public string Detail => SupportsCut
        ? "Windows.Media"
        : "Windows.Media（未找到 ffmpeg：可读元数据 / 抽帧 / 缩略图切镜，不支持切段与拼接）";

    public bool SupportsSceneDetect => true;

    /// <summary>没有 ffmpeg 就没有本地切段。UI 据此禁用预览与拼接。</summary>
    public bool SupportsCut => false;

    public bool SupportsConcat => false;

    public async Task<MediaInfo> ProbeAsync(string sourcePath, CancellationToken ct = default)
    {
        var full = RequireFile(sourcePath);
        ct.ThrowIfCancellationRequested();

        StorageFile file;
        Windows.Storage.FileProperties.VideoProperties properties;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(full).AsTask(ct).ConfigureAwait(false);
            properties = await file.Properties.GetVideoPropertiesAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw TranslateFailure("读取视频元数据", full, ex);
        }

        var duration = properties.Duration.TotalSeconds;
        var width = (int)properties.Width;
        var height = (int)properties.Height;
        var hasAudio = await TryHasAudioTrackAsync(file, ct).ConfigureAwait(false);

        return new MediaInfo(
            double.IsFinite(duration) && duration > 0 ? duration : 0,
            width,
            height,
            hasAudio);
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
        if (atSeconds < 0 || !double.IsFinite(atSeconds))
            throw new ArgumentOutOfRangeException(nameof(atSeconds));

        var output = Path.GetFullPath(outputImagePath);
        var directory = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException($"输出路径没有目录部分：{output}", nameof(outputImagePath));
        Directory.CreateDirectory(directory);

        var info = await ProbeAsync(full, ct).ConfigureAwait(false);
        var width = info.Width > 0 ? info.Width : 640;
        var height = info.Height > 0 ? info.Height : 360;

        try
        {
            var composition = await LoadCompositionAsync(full, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            using var thumbnail = await composition
                .GetThumbnailAsync(TimeSpan.FromSeconds(atSeconds), width, height, VideoFramePrecision.NearestFrame)
                .AsTask(ct)
                .ConfigureAwait(false);

            var decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask(ct).ConfigureAwait(false);
            using var bitmap = await decoder
                .GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore)
                .AsTask(ct)
                .ConfigureAwait(false);

            var folder = await StorageFolder.GetFolderFromPathAsync(directory).AsTask(ct).ConfigureAwait(false);
            var outFile = await folder
                .CreateFileAsync(Path.GetFileName(output), CreationCollisionOption.ReplaceExisting)
                .AsTask(ct)
                .ConfigureAwait(false);

            using var stream = await outFile.OpenAsync(FileAccessMode.ReadWrite).AsTask(ct).ConfigureAwait(false);
            var encoderId = IsPng(output) ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId;
            var encoder = await BitmapEncoder.CreateAsync(encoderId, stream).AsTask(ct).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw TranslateFailure("抽帧", full, ex);
        }

        if (!File.Exists(output))
            throw new InvalidOperationException($"抽帧失败，未生成文件：{output}");

        return output;
    }

    public async Task<IReadOnlyList<double>> DetectSceneCutsAsync(
        string sourcePath,
        double threshold = MediaEngineDefaults.DefaultSceneThreshold,
        CancellationToken ct = default)
    {
        var full = RequireFile(sourcePath);
        var info = await ProbeAsync(full, ct).ConfigureAwait(false);
        if (info.DurationSeconds <= 0)
            return Array.Empty<double>();

        var effective = ResolveThreshold(threshold);
        var step = Math.Max(MinSampleStep, info.DurationSeconds / MaxSceneSamples);

        var cuts = new List<double>();
        byte[]? previous = null;

        try
        {
            var composition = await LoadCompositionAsync(full, ct).ConfigureAwait(false);

            var sampleCount = (int)Math.Floor(info.DurationSeconds / step);
            for (var i = 1; i <= sampleCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var at = i * step;
                if (at >= info.DurationSeconds)
                    break;

                var current = await SampleAsync(composition, at, ct).ConfigureAwait(false);
                if (previous is not null)
                {
                    var score = MeanAbsDiff(previous, current);
                    if (score >= effective &&
                        at >= SceneEdgeIgnoreSeconds &&
                        at < info.DurationSeconds &&
                        (cuts.Count == 0 || at - cuts[^1] >= SceneDedupeSeconds))
                    {
                        cuts.Add(at);
                    }
                }

                previous = current;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw TranslateFailure("缩略图切镜检测", full, ex);
        }

        return cuts;
    }

    public Task<string> CutAsync(
        string sourcePath,
        double startSeconds,
        double durationSeconds,
        string outputVideoPath,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "当前使用 Windows.Media 兜底引擎（未找到 ffmpeg），不支持本地切段。装好 ffmpeg 或设置 WANSTUDIO_FFMPEG 后重试。");

    public Task<string> ConcatAsync(
        IReadOnlyList<string> clipPaths,
        string outputVideoPath,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "当前使用 Windows.Media 兜底引擎（未找到 ffmpeg），不支持本地拼接。装好 ffmpeg 或设置 WANSTUDIO_FFMPEG 后重试。");

    // ------------------------------------------------------------------ internals

    private async Task<MediaComposition> LoadCompositionAsync(string fullPath, CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_composition is not null &&
                string.Equals(_compositionPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return _composition;
            }

            var file = await StorageFile.GetFileFromPathAsync(fullPath).AsTask(ct).ConfigureAwait(false);

            // 注意：MediaComposition.LoadAsync 读的是「已保存的合成工程」，不是「把视频读进来」，
            // 直接拿 mp4 调它必失败（实测 0xC00CE508）。正确姿势是建 MediaClip 再加进新合成。
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(ct).ConfigureAwait(false);

            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            _composition = composition;
            _compositionPath = fullPath;
            return composition;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw TranslateFailure("打开视频", fullPath, ex);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>WinRT 的 COMException 往往没有 message，这里补成人能看懂的说明。</summary>
    private static Exception TranslateFailure(string action, string path, Exception ex)
    {
        var name = Path.GetFileName(path);

        if (ex is COMException && ex.HResult == unchecked((int)0xC00CE508))
        {
            return new InvalidOperationException(
                $"{action}失败：Windows.Media 无法解码 {name}（0xC00CE508）。" +
                "Media Foundation 不支持该容器/编码，或系统缺少媒体解码组件。" +
                "这是无 ffmpeg 时兜底引擎的限制，装好 ffmpeg 或设置 WANSTUDIO_FFMPEG 后即可恢复。", ex);
        }

        return new InvalidOperationException($"{action}失败（{name}）：{ex.Message}（0x{ex.HResult:X8}）", ex);
    }

    /// <summary>取样一帧并缩到 16×9，返回 Bgra8 原始像素。</summary>
    private static async Task<byte[]> SampleAsync(MediaComposition composition, double atSeconds, CancellationToken ct)
    {
        using var thumbnail = await composition
            .GetThumbnailAsync(TimeSpan.FromSeconds(atSeconds), SampleWidth, SampleHeight, VideoFramePrecision.NearestFrame)
            .AsTask(ct)
            .ConfigureAwait(false);

        var decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask(ct).ConfigureAwait(false);

        var transform = new BitmapTransform
        {
            ScaledWidth = CompareWidth,
            ScaledHeight = CompareHeight,
            InterpolationMode = BitmapInterpolationMode.Linear,
        };

        var pixels = await decoder
            .GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)
            .AsTask(ct)
            .ConfigureAwait(false);

        return pixels.DetachPixelData();
    }

    /// <summary>
    /// 两个 16×9 采样的平均绝对差，0..1，按 B/G/R 三通道各自算再平均。
    /// <para>
    /// 只用亮度会漏掉「红→绿」这类等亮度换色：实测 fixtures\scenes-4x5s.mp4 的
    /// 三个切点，亮度差只有 0.0400 / 0.0228 / 0.2165，0.12 阈值会漏前两刀；
    /// 同一次采样换成三通道平均后是 0.3223 / 0.3667 / 0.4661，而片内抖动最高 0.0982，
    /// 所以 0.12 正好落在窗外。契约的 0.12 阈值配三通道度量才成立。
    /// </para>
    /// </summary>
    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        var pixels = length / 4;
        if (pixels == 0)
            return 0;

        long sum = 0;
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * 4;
            sum += Math.Abs(a[offset] - b[offset]);             // B
            sum += Math.Abs(a[offset + 1] - b[offset + 1]);     // G
            sum += Math.Abs(a[offset + 2] - b[offset + 2]);     // R
        }

        return (double)sum / (pixels * 3 * 255.0);
    }

    /// <summary>
    /// WinRT 没有「有没有音轨」的直接 API，走 Shell 属性系统问 System.Audio.ChannelCount：
    /// 有音轨会返回声道数，没音轨返回 null。失败一律按无音轨处理（只影响 UI 文案，不外抛）。
    /// </summary>
    private static async Task<bool> TryHasAudioTrackAsync(StorageFile file, CancellationToken ct)
    {
        try
        {
            var properties = await file.Properties
                .RetrievePropertiesAsync(new[] { AudioChannelCountProperty })
                .AsTask(ct)
                .ConfigureAwait(false);

            return properties.TryGetValue(AudioChannelCountProperty, out var value) && value is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static double ResolveThreshold(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold <= 0 ||
            Math.Abs(threshold - MediaEngineDefaults.DefaultSceneThreshold) < 1e-9)
        {
            return MeasuredSceneThreshold;
        }

        return threshold;
    }

    private static bool IsPng(string path)
        => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    private static string RequireFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("源文件路径不能为空", nameof(path));

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"找不到文件：{full}", full);

        return full;
    }
}
