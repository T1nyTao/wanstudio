using System;
using System.Collections.Generic;
using System.IO;

namespace WanStudio.Services;

/// <summary>
/// 统一的「可写目录解析」。
///
/// 为什么要有这个东西：<c>Directory.CreateDirectory</c> 在真实机器上会失败——
/// 权限策略、漫游配置、受控目录、磁盘只读、路径被同名文件占用……只要失败，
/// 对应功能就整条不可用，用户只看到一句 Access denied，既不知道原因也没有出路。
///
/// 这里的原则：<b>每一个「按用途取目录」的调用都带多级回退</b>，
/// 首选位置不可写就自动换下一个，并且永远记住第一次探测成功的结果，
/// 避免反复探测（探测本身要写盘）。
///
/// 探测方式：建目录 → 写探针 → 删探针。只 CreateDirectory 不够，
/// 因为目录可能存在但没有写权限。
/// </summary>
public static class AppPaths
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>上一次探测失败的原因汇总，供 UI 提示（不含敏感信息）。</summary>
    public static string? LastProbeError { get; private set; }

    /// <summary>
    /// 按用途取一个可写目录。<paramref name="purpose"/> 是子目录名（如 "cache"、"refcuts"、"outputs"）。
    /// 回退顺序：LOCALAPPDATA → USERPROFILE\.wanstudio → TEMP。
    /// 全部失败时抛 <see cref="IOException"/>，消息里列出试过哪些路径。
    /// </summary>
    public static string WritableDir(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            throw new ArgumentException("需要指定用途子目录名。", nameof(purpose));

        lock (Cache)
        {
            if (Cache.TryGetValue(purpose, out var cached)) return cached;

            var errors = new List<string>();

            foreach (var root in CandidateRoots())
            {
                var dir = Path.Combine(root, purpose);
                if (TryEnsureWritable(dir, out var error))
                {
                    Cache[purpose] = dir;
                    return dir;
                }
                errors.Add(error!);
            }

            LastProbeError = string.Join("；", errors);
            AppendCrashLog($"[WritableDir:{purpose}] " + LastProbeError);
            throw new IOException($"找不到可写的 {purpose} 目录。原因：" + LastProbeError);
        }
    }

    /// <summary>
    /// 应用数据根目录（放设置、池、崩溃日志）。
    /// 注意：不能调 <see cref="WritableDir"/> 传空串——那会被参数校验直接拒掉，
    /// 导致根目录永远落到兜底位置。这里显式走根目录探测。
    /// </summary>
    public static string WanStudioRoot => RootDir();

    private static string RootDir()
    {
        lock (Cache)
        {
            if (Cache.TryGetValue("<root>", out var cached)) return cached;

            var errors = new List<string>();
            foreach (var root in CandidateRoots())
            {
                if (TryEnsureWritable(root, out var error))
                {
                    Cache["<root>"] = root;
                    return root;
                }
                errors.Add(error!);
            }

            LastProbeError = string.Join("；", errors);
            throw new IOException("找不到可写的应用数据目录。原因：" + LastProbeError);
        }
    }

    /// <summary>缓存目录（缩略图、预览片段、锚点片段）。</summary>
    public static string CacheDir => WritableDir("cache");

    /// <summary>裁切输出目录。</summary>
    public static string ReferenceCutDir => WritableDir(Path.Combine("cache", "refcuts"));

    /// <summary>
    /// 成片输出目录。优先用户配置的目录；它会跟着「输出目录」设置走，
    /// 所以不走缓存，但同样要能回退——用户可能把输出目录设到一个只读位置。
    /// </summary>
    public static string OutputDir(string? preferred)
    {
        // 用户配置的目录优先，但必须真的可写才用；否则回退到应用自己的可写位置。
        // 早先版本在这里只探测不判断，用户把输出目录设成只读位置时会原样返回，
        // 结果生成成功却在下载阶段炸掉。
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            lock (Cache)
            {
                var key = "out:" + preferred;
                if (Cache.TryGetValue(key, out var hit)) return hit;

                if (TryEnsureWritable(preferred!, out _))
                {
                    Cache[key] = preferred!;
                    return preferred!;
                }
            }
        }

        return WritableDir("outputs");
    }

    /// <summary>候选根目录，按优先级。</summary>
    private static IEnumerable<string> CandidateRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            yield return Path.Combine(local, "WanStudio");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            yield return Path.Combine(profile, ".wanstudio");

        yield return Path.Combine(Path.GetTempPath(), "WanStudio");
    }

    /// <summary>目录可写性探测：建目录 + 写探针 + 删探针。</summary>
    public static bool TryEnsureWritable(string dir, out string? error)
    {
        try
        {
            Directory.CreateDirectory(dir);

            // 目录存在不代表能写，必须真写一次。
            var probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "1");

            // 写成功就已经证明可写。删除探针失败（杀毒软件/索引器正好在扫这个新文件）
            // 不能反过来判成「不可写」——早先就是这样把三个候选目录全部误杀的。
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try { File.Delete(probe); break; }
                catch (Exception) when (attempt < 2) { System.Threading.Thread.Sleep(50); }
                catch (Exception) { /* 留下一个 1 字节探针文件，无害 */ }
            }

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // 带上真实原因，否则用户和日志里只剩一句「找不到可写目录」，无从排查。
            error = $"{dir} → {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 确保某个父目录存在且可写（用于只知道完整文件路径的场景）。
    /// 返回该父目录；父目录不可写时返回 null，由调用方决定怎么提示。
    /// </summary>
    public static string? TryEnsureParentOf(string filePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (string.IsNullOrEmpty(dir)) return null;
        return TryEnsureWritable(dir, out _) ? dir : null;
    }

    /// <summary>清空探测缓存（切换输出目录之类的场景用）。</summary>
    public static void InvalidateCache()
    {
        lock (Cache) Cache.Clear();
    }

    /// <summary>
    /// 追加一条崩溃/诊断日志。多个位置依次尝试，第一个写成功的就用：
    ///   LOCALAPPDATA\WanStudio → USERPROFILE\.wanstudio → 桌面\Wan3.0 → TEMP
    ///
    /// 为什么不能只写一个地方：日志本身写失败时如果直接吞掉，
    /// 程序出问题就完全没线索。桌面是「用户一定能自己找到」的兜底位置。
    /// </summary>
    public static void AppendCrashLog(string message)
    {
        var line = DateTime.Now.ToString("s") + " " + message + Environment.NewLine;

        var targets = new List<string>();

        try { targets.Add(Path.Combine(WanStudioRoot, "wanstudio-crash.log")); } catch { }
        targets.Add(Path.Combine(Path.GetTempPath(), "wanstudio-crash.log"));

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
            targets.Add(Path.Combine(desktop, "Wan3.0", "wanstudio-crash.log"));

        foreach (var path in targets)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line);
                return;   // 写成功就停，不重复写多份
            }
            catch
            {
                // 换下一个位置
            }
        }
    }
}
