using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WanStudio.Services.Media;

namespace MediaCheck;

/// <summary>
/// Media 层验证程序。跑契约 2.5 的 4 条验收，外加 WinRT 兜底引擎的附录检查。
/// 输出刻意做成纯文本，方便原样贴进 qa/run-*.md。
/// </summary>
internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向时可能失败，无妨 */ }

        var fixtures = ResolveFixturesDirectory(args);
        if (fixtures is null)
        {
            Console.WriteLine("[FATAL] 找不到 fixtures 目录（含 long-20s.mp4 / scenes-4x5s.mp4）。用 media-check <fixtures 目录> 指定。");
            return 2;
        }

        Console.WriteLine("=== media-check · WanStudio 本地切段引擎 ===");
        Console.WriteLine($"time          : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"fixtures      : {fixtures}");
        Console.WriteLine($"base dir      : {AppContext.BaseDirectory}");
        Console.WriteLine($"os            : {Environment.OSVersion.VersionString}");
        Console.WriteLine($"runtime       : {Environment.Version} / {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
        Console.WriteLine();

        var longClip = Path.Combine(fixtures, "long-20s.mp4");
        var scenesClip = Path.Combine(fixtures, "scenes-4x5s.mp4");

        var engine = ReportEngine();

        Console.WriteLine();
        Console.WriteLine("--- 1) Probe fixtures\\long-20s.mp4 ---");
        var check1 = await CheckProbeAsync(engine, longClip);
        Mark(check1);

        Console.WriteLine();
        Console.WriteLine("--- 2) SegmentPlanner.PlanFixed(20, 6) ---");
        var check2 = CheckPlanFixed();
        Mark(check2);

        Console.WriteLine();
        Console.WriteLine("--- 3) DetectSceneCuts(fixtures\\scenes-4x5s.mp4) ---");
        var check3 = await CheckSceneCutsAsync(engine, scenesClip);
        Mark(check3);

        Console.WriteLine();
        Console.WriteLine("--- 4) Cut(scenes-4x5s, 10, 5) + Concat([0..5],[10..15]) ---");
        var check4 = await CheckCutAndConcatAsync(engine, scenesClip);
        Mark(check4);

        Console.WriteLine();
        Console.WriteLine("--- 附录 A) WinRtMediaEngine（无 ffmpeg 兜底） ---");
        await CheckWinRtFallbackAsync(longClip, scenesClip);

        Console.WriteLine();
        Console.WriteLine($"=== 结论：{(_failures == 0 ? "ALL PASS" : $"{_failures} 项 FAIL")} ===");
        return _failures == 0 ? 0 : 1;
    }

    private static IMediaEngine ReportEngine()
    {
        var located = MediaEngines.TryLocateFfmpeg(out var ffmpegPath);
        Console.WriteLine($"[engine] TryLocateFfmpeg()      -> {located}" + (located ? $"  path={ffmpegPath}" : string.Empty));

        var engine = MediaEngines.CreateDefault();
        // IMediaEngine 目前（冻结版）没有 SupportsCut/Concat 位，用具体类型判断，避免把编译错误引进验证工程。
        var isFfmpeg = engine is FfmpegMediaEngine;
        Console.WriteLine($"[engine] CreateDefault().Name   -> {engine.Name}");
        Console.WriteLine($"[engine] Detail                 -> {engine.Detail}");
        Console.WriteLine($"[engine] SupportsSceneDetect    -> {engine.SupportsSceneDetect}");
        Console.WriteLine($"[engine] SupportsCut(concrete)  -> {isFfmpeg}");
        Console.WriteLine($"[engine] SupportsConcat(concrete) -> {isFfmpeg}");
        return engine;
    }

    // ---------------------------------------------------------------- 1

    private static async Task<bool> CheckProbeAsync(IMediaEngine engine, string path)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var info = await engine.ProbeAsync(path);
            sw.Stop();

            Console.WriteLine($"  ProbeAsync(\"{Path.GetFileName(path)}\")  {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"    DurationSeconds = {info.DurationSeconds.ToString("0.###", Inv)}   (期望 20.000)");
            Console.WriteLine($"    Width x Height  = {info.Width}x{info.Height}   (期望 640x360)");
            Console.WriteLine($"    HasAudio        = {info.HasAudio}   (期望 True)");

            var durationOk = Math.Abs(info.DurationSeconds - 20.0) <= 0.1;
            var sizeOk = info.Width == 640 && info.Height == 360;
            var ok = durationOk && sizeOk;

            Console.WriteLine($"    verdict: duration {(durationOk ? "OK" : "MISMATCH")}, size {(sizeOk ? "OK" : "MISMATCH")}");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  EXCEPTION {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- 2

    private static bool CheckPlanFixed()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var plan = SegmentPlanner.PlanFixed(20, 6);
            sw.Stop();

            Console.WriteLine($"  PlanFixed(duration=20, segmentSeconds=6)  {sw.ElapsedMilliseconds} ms -> {plan.Count} 段");
            foreach (var (spec, index) in plan.Select((s, i) => (s, i)))
            {
                Console.WriteLine(
                    $"    #{index + 1:00}  {spec.StartSeconds.ToString("0.000", Inv)} → {spec.EndSeconds.ToString("0.000", Inv)}" +
                    $"   len={spec.DurationSeconds.ToString("0.000", Inv)}s");
            }

            var expected = new[] { (0.0, 6.0), (6.0, 6.0), (12.0, 6.0), (18.0, 2.0) };
            var countOk = plan.Count == expected.Length;
            var valuesOk = countOk && plan
                .Zip(expected, (actual, want) =>
                    Math.Abs(actual.StartSeconds - want.Item1) < 1e-6 &&
                    Math.Abs(actual.DurationSeconds - want.Item2) < 1e-6)
                .All(equal => equal);

            Console.WriteLine($"    expected: 4 段 [0..6][6..12][12..18][18..20]（末段 2s）");
            Console.WriteLine($"    verdict: count {(countOk ? "OK" : "MISMATCH")}, 起止 {(valuesOk ? "OK" : "MISMATCH")}");
            return valuesOk;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  EXCEPTION {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- 3

    private static async Task<bool> CheckSceneCutsAsync(IMediaEngine engine, string path)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var cuts = await engine.DetectSceneCutsAsync(path);
            sw.Stop();

            Console.WriteLine($"  DetectSceneCutsAsync(\"{Path.GetFileName(path)}\")  {sw.ElapsedMilliseconds} ms -> {cuts.Count} 个切点");
            Console.WriteLine($"    cuts = [{string.Join(", ", cuts.Select(c => c.ToString("0.000", Inv)))}]");

            var expected = new[] { 5.0, 10.0, 15.0 };
            var countOk = cuts.Count == expected.Length;
            var matched = expected.Count(want => cuts.Any(c => Math.Abs(c - want) <= 0.5));
            var ok = countOk && matched == expected.Length;

            Console.WriteLine($"    expected ≈ [5, 10, 15]（±0.5s，红→绿那一刀在 0.35 阈值下会漏，实测阈值 0.02）");
            Console.WriteLine($"    verdict: count {(countOk ? "OK" : "MISMATCH")}, 命中 {matched}/3");
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  EXCEPTION {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- 4

    private static async Task<bool> CheckCutAndConcatAsync(IMediaEngine engine, string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "wanstudio-media-check");
        Directory.CreateDirectory(dir);

        var clipB = Path.Combine(dir, "clip-b-10-15.mp4");
        var clipA = Path.Combine(dir, "clip-a-00-05.mp4");
        var joined = Path.Combine(dir, "concat-a-then-b.mp4");
        foreach (var file in new[] { clipA, clipB, joined })
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { /* 上一次残留，忽略 */ }
        }

        if (engine is not FfmpegMediaEngine)
        {
            Console.WriteLine("  SKIP：当前引擎不是 ffmpeg，本地切段/拼接不可用（这正是契约要求的降级行为）。");
            return false;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            await engine.CutAsync(path, 10, 5, clipB);
            sw.Stop();
            var infoB = await engine.ProbeAsync(clipB);
            Console.WriteLine($"  CutAsync(src, start=10, dur=5)  {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"    out  = {clipB}");
            Console.WriteLine($"    size = {new FileInfo(clipB).Length:N0} bytes");
            Console.WriteLine($"    probe duration = {infoB.DurationSeconds.ToString("0.###", Inv)}   (期望 5 ± 0.2)");

            sw.Restart();
            await engine.CutAsync(path, 0, 5, clipA);
            sw.Stop();
            var infoA = await engine.ProbeAsync(clipA);
            Console.WriteLine($"  CutAsync(src, start=0, dur=5)   {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"    out  = {clipA}");
            Console.WriteLine($"    size = {new FileInfo(clipA).Length:N0} bytes");
            Console.WriteLine($"    probe duration = {infoA.DurationSeconds.ToString("0.###", Inv)}   (期望 5 ± 0.2)");

            sw.Restart();
            await engine.ConcatAsync(new[] { clipA, clipB }, joined);
            sw.Stop();
            var infoJoin = await engine.ProbeAsync(joined);
            Console.WriteLine($"  ConcatAsync([clip-a, clip-b])   {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"    out  = {joined}");
            Console.WriteLine($"    size = {new FileInfo(joined).Length:N0} bytes");
            Console.WriteLine($"    probe duration = {infoJoin.DurationSeconds.ToString("0.###", Inv)}   (期望 10 ± 0.3)");
            Console.WriteLine($"    probe size     = {infoJoin.Width}x{infoJoin.Height}, HasAudio = {infoJoin.HasAudio}");

            var cutOk = Math.Abs(infoB.DurationSeconds - 5) <= 0.2 && Math.Abs(infoA.DurationSeconds - 5) <= 0.2;
            var concatOk = Math.Abs(infoJoin.DurationSeconds - 10) <= 0.3;
            var playsOk = infoJoin.Width == 640 && infoJoin.Height == 360;

            Console.WriteLine($"    verdict: 切段 {(cutOk ? "OK" : "MISMATCH")}, 拼接 {(concatOk ? "OK" : "MISMATCH")}, 产物可 probe {(playsOk ? "OK" : "MISMATCH")}");
            return cutOk && concatOk && playsOk;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  EXCEPTION {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ---------------------------------------------------------------- 附录 A

    private static async Task CheckWinRtFallbackAsync(string longClip, string scenesClip)
    {
        var engine = new WinRtMediaEngine();
        Console.WriteLine($"  Name={engine.Name}, SupportsSceneDetect={engine.SupportsSceneDetect}, SupportsCut={engine.SupportsCut}, SupportsConcat={engine.SupportsConcat}");

        try
        {
            var sw = Stopwatch.StartNew();
            var info = await engine.ProbeAsync(longClip);
            sw.Stop();
            Console.WriteLine($"  ProbeAsync(long-20s.mp4)  {sw.ElapsedMilliseconds} ms -> {info.DurationSeconds.ToString("0.###", Inv)}s, {info.Width}x{info.Height}, HasAudio={info.HasAudio}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  [FAIL] WinRT ProbeAsync: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var frame = Path.Combine(Path.GetTempPath(), "wanstudio-media-check", "winrt-frame.png");
            Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
            var sw = Stopwatch.StartNew();
            var path = await engine.ExtractFrameAsync(longClip, 3, frame);
            sw.Stop();
            var length = new FileInfo(path).Length;
            Console.WriteLine($"  ExtractFrameAsync(long-20s.mp4, 3s)  {sw.ElapsedMilliseconds} ms -> {length:N0} bytes png");
            if (length <= 0)
            {
                _failures++;
                Console.WriteLine("  [FAIL] 抽帧产物为空");
            }
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  [FAIL] WinRT ExtractFrameAsync: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var cuts = await engine.DetectSceneCutsAsync(scenesClip);
            sw.Stop();
            Console.WriteLine($"  DetectSceneCutsAsync(scenes-4x5s.mp4)  {sw.ElapsedMilliseconds} ms -> [{string.Join(", ", cuts.Select(c => c.ToString("0.00", Inv)))}]（缩略图采样，精度受 0.25s 步长限制）");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  [FAIL] WinRT DetectSceneCutsAsync: {ex.GetType().Name}: {ex.Message}");
        }

        await ExpectNotSupportedAsync("CutAsync", () => engine.CutAsync(longClip, 0, 5, Path.Combine(Path.GetTempPath(), "nope.mp4")));
        await ExpectNotSupportedAsync("ConcatAsync", () => engine.ConcatAsync(new[] { longClip, longClip }, Path.Combine(Path.GetTempPath(), "nope.mp4")));
    }

    private static async Task ExpectNotSupportedAsync(string name, Func<Task<string>> action)
    {
        try
        {
            await action();
            _failures++;
            Console.WriteLine($"  [FAIL] {name} 应该抛 NotSupportedException，实际返回了结果（假装支持）");
        }
        catch (NotSupportedException ex)
        {
            Console.WriteLine($"  {name} -> NotSupportedException OK：{ex.Message}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  [FAIL] {name} 抛的是 {ex.GetType().Name}，应为 NotSupportedException：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- helpers

    private static void Mark(bool ok)
    {
        if (!ok)
            _failures++;
        Console.WriteLine($"  RESULT: {(ok ? "PASS" : "FAIL")}");
    }

    private static string? ResolveFixturesDirectory(string[] args)
    {
        foreach (var candidate in EnumerateCandidates(args))
        {
            if (candidate is not null &&
                File.Exists(Path.Combine(candidate, "long-20s.mp4")) &&
                File.Exists(Path.Combine(candidate, "scenes-4x5s.mp4")))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string?> EnumerateCandidates(string[] args)
    {
        if (args.Length > 0)
            yield return args[0];

        // 从程序目录和当前目录各自往上找，最多 8 层，找到仓库根就停。
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            for (var i = 0; i < 8 && directory is not null; i++)
            {
                yield return Path.Combine(directory.FullName, "fixtures");
                directory = directory.Parent;
            }
        }
    }
}
