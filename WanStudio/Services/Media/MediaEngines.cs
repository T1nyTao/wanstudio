using System;
using System.Collections.Generic;
using System.IO;

namespace WanStudio.Services.Media;

/// <summary>
/// 切段引擎定位器。优先 ffmpeg（功能全），找不到时回退 <see cref="WinRtMediaEngine"/>（只能 probe / 抽帧 / 缩略图切镜）。
/// 不打包 ffmpeg、不联网、不改 PATH。
/// </summary>
public static class MediaEngines
{
    /// <summary>用户/测试可显式指定 ffmpeg 路径，优先级高于所有自动查找。</summary>
    public const string FfmpegEnvVar = "WANSTUDIO_FFMPEG";

    private const string FfmpegExeName = "ffmpeg.exe";

    /// <summary>从 BaseDirectory 往上找 tools\ffmpeg.exe 的层数（覆盖 bin\x64\Debug\net8.0-windows...\win-x64）。</summary>
    private const int MaxParentProbeDepth = 6;

    private static readonly object Gate = new();

    /// <summary>命中过的 ffmpeg 路径。只缓存成功结果，失败下次重新找（用户可能中途装/删）。</summary>
    private static string? _locatedFfmpeg;

    /// <summary>
    /// 优先 ffmpeg，找不到时回退 WinRT；两者都不可用时仍返回 <see cref="WinRtMediaEngine"/>，
    /// 它的 SupportsCut / SupportsConcat 为 false，由 UI 禁用切段/拼接入口。
    /// </summary>
    public static IMediaEngine CreateDefault()
    {
        if (TryLocateFfmpeg(out var exePath))
            return new FfmpegMediaEngine(exePath);

        return new WinRtMediaEngine();
    }

    /// <summary>
    /// 按契约顺序查找 ffmpeg：
    /// 环境变量 → BaseDirectory\tools → 向上 6 层 → PATH → %LOCALAPPDATA%\oopz。
    /// 只做存在性校验，不试跑（避免启动时多一次进程创建）。
    /// </summary>
    public static bool TryLocateFfmpeg(out string exePath)
    {
        lock (Gate)
        {
            if (_locatedFfmpeg is not null && IsUsableFfmpeg(_locatedFfmpeg))
            {
                exePath = _locatedFfmpeg;
                return true;
            }

            _locatedFfmpeg = null;
        }

        foreach (var candidate in EnumerateCandidates())
        {
            if (!IsUsableFfmpeg(candidate))
                continue;

            lock (Gate) _locatedFfmpeg = candidate;
            exePath = candidate;
            return true;
        }

        exePath = string.Empty;
        return false;
    }

    /// <summary>当前环境能不能做本地切段/拼接（UI 用它决定提示文案，不必构造引擎）。</summary>
    public static bool CanCutLocally() => TryLocateFfmpeg(out _);

    private static IEnumerable<string> EnumerateCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 显式覆盖：环境变量（值可以是 exe 或目录）。
        var fromEnv = Environment.GetEnvironmentVariable(FfmpegEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            foreach (var path in NormalizeEnvCandidate(fromEnv!))
            {
                if (seen.Add(path))
                    yield return path;
            }
        }

        var baseDirectory = AppContext.BaseDirectory;

        // 2) 随程序分发的 tools\ffmpeg.exe。契约明确不打包，这里只是留好位置，不往里拷。
        var bundled = SafeCombine(baseDirectory, "tools", FfmpegExeName);
        if (bundled is not null && seen.Add(bundled))
            yield return bundled;

        // 3) 向上最多 6 层：开发期从 bin\<Platform>\<Config>\<tfm>\<rid> 启动时能找到仓库根的 tools\。
        var directory = new DirectoryInfo(baseDirectory);
        for (var depth = 0; depth < MaxParentProbeDepth && directory?.Parent is not null; depth++)
        {
            directory = directory.Parent;
            var up = SafeCombine(directory.FullName, "tools", FfmpegExeName);
            if (up is not null && seen.Add(up))
                yield return up;
        }

        // 4) PATH。PATH 里可能夹空项、带引号的项，逐个容错。
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var entry in pathVariable!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = entry.Trim().Trim('"');
                if (trimmed.Length == 0)
                    continue;

                var full = SafeCombine(trimmed, FfmpegExeName);
                if (full is not null && seen.Add(full))
                    yield return full;
            }
        }

        // 5) 本机兜底：Oopz 客户端自带一份 ffmpeg。属于「这台机器刚好有」的便利，不是本项目的依赖。
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            var oopz = SafeCombine(localAppData, "oopz", FfmpegExeName);
            if (oopz is not null && seen.Add(oopz))
                yield return oopz;
        }
    }

    /// <summary>环境变量既可能是 exe 也可能是目录，两种都接受。</summary>
    private static IEnumerable<string> NormalizeEnvCandidate(string value)
    {
        var trimmed = value.Trim().Trim('"');
        if (trimmed.Length == 0)
            yield break;

        var direct = SafeFullPath(trimmed);
        if (direct is not null)
            yield return direct;

        var asDirectory = SafeCombine(trimmed, FfmpegExeName);
        if (asDirectory is not null)
            yield return asDirectory;
    }

    private static string? SafeCombine(string left, params string[] parts)
    {
        try
        {
            var all = new string[parts.Length + 1];
            all[0] = left;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.GetFullPath(Path.Combine(all));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsUsableFfmpeg(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
