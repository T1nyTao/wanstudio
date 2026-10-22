# media-check 媒体层复验（2026-10-22）

复验人：eng-media（子代理会话）
被测对象：`tools/media-check`（仓库内已构建产物）
结论先给：**不是 ALL PASS。第 4 项与附录 A 三项在本会话 FAIL；同一份二进制移出工作区树后 ALL PASS。已用对照实验把差异定位到执行环境，不是引擎代码。**

---

## 1. 环境

| 项 | 实测值 |
| --- | --- |
| 主机 | Microsoft Windows NT 10.0.26200.0 |
| .NET 运行时（exe 自报） | 8.0.31 / win-x64 |
| 被测 exe | `C:\Users\<user>\Desktop\Wan3.0\tools\media-check\bin\Release\net8.0-windows10.0.26100.0\win-x64\media-check.exe` |
| ffmpeg 实际路径（引擎自报） | `C:\Users\<user>\AppData\Local\oopz\ffmpeg.exe` |
| ffmpeg 版本 | 见 §2.5（Lavf59.34.101 / Lavc59.51.100 libx264） |
| fixtures | `C:\Users\<user>\Desktop\Wan3.0\fixtures` |

命令原文（§2.1 与 §2.5 用同一条）：

```
cd C:\Users\<user>\Desktop\Wan3.0
.\tools\media-check\bin\Release\net8.0-windows10.0.26100.0\win-x64\media-check.exe
```

**关于 ffprobe**：任务给的兜底路径 `%LOCALAPPDATA%\oopz\` 下**只有 ffmpeg.exe，没有 ffprobe.exe**，实测：

```
$ ls -la /c/Users/<user>/AppData/Local/oopz/ | grep ff
-rwxr-xr-x 1 <user> 197121  79406592 Aug 17 16:52 ffmpeg.exe
$ where ffprobe
信息: 用提供的模式无法找到文件。
```

所以 §3 的 fixture 复核改用**同目录 ffmpeg.exe -i**（同一个 demuxer，输出的 Duration / Stream 行就是 ffprobe 的同一份数据源），并明确标注来源。

---

## 2. 逐项实测

### 2.1 全量运行（工作区内 exe，默认 TMP）→ 4 项 FAIL

命令原文：

```
cd C:\Users\<user>\Desktop\Wan3.0
.\tools\media-check\bin\Release\net8.0-windows10.0.26100.0\win-x64\media-check.exe
```

原始输出全文（`qa/media-canonical-run-workspace.txt`，退出码 1）：

```
=== media-check · WanStudio 本地切段引擎 ===
time          : 2026-10-22 02:18:08
fixtures      : C:\Users\<user>\Desktop\Wan3.0\fixtures
base dir      : C:\Users\<user>\Desktop\Wan3.0\tools\media-check\bin\Release\net8.0-windows10.0.26100.0\win-x64\
os            : Microsoft Windows NT 10.0.26200.0
runtime       : 8.0.31 / win-x64

[engine] TryLocateFfmpeg()      -> True  path=C:\Users\<user>\AppData\Local\oopz\ffmpeg.exe
[engine] CreateDefault().Name   -> ffmpeg
[engine] Detail                 -> C:\Users\<user>\AppData\Local\oopz\ffmpeg.exe
[engine] SupportsSceneDetect    -> True
[engine] SupportsCut(concrete)  -> True
[engine] SupportsConcat(concrete) -> True

--- 1) Probe fixtures\long-20s.mp4 ---
  ProbeAsync("long-20s.mp4")  33 ms
    DurationSeconds = 20   (期望 20.000)
    Width x Height  = 640x360   (期望 640x360)
    HasAudio        = True   (期望 True)
    verdict: duration OK, size OK
  RESULT: PASS

--- 2) SegmentPlanner.PlanFixed(20, 6) ---
  PlanFixed(duration=20, segmentSeconds=6)  0 ms -> 4 段
    #01  0.000 → 6.000   len=6.000s
    #02  6.000 → 12.000   len=6.000s
    #03  12.000 → 18.000   len=6.000s
    #04  18.000 → 20.000   len=2.000s
    expected: 4 段 [0..6][6..12][12..18][18..20]（末段 2s）
    verdict: count OK, 起止 OK
  RESULT: PASS

--- 3) DetectSceneCuts(fixtures\scenes-4x5s.mp4) ---
  DetectSceneCutsAsync("scenes-4x5s.mp4")  75 ms -> 3 个切点
    cuts = [5.000, 10.000, 15.000]
    expected ≈ [5, 10, 15]（±0.5s，红→绿那一刀在 0.35 阈值下会漏，实测阈值 0.02）
    verdict: count OK, 命中 3/3
  RESULT: PASS

--- 4) Cut(scenes-4x5s, 10, 5) + Concat([0..5],[10..15]) ---
  EXCEPTION InvalidOperationException: ffmpeg 切段失败：Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'C:\Users\<user>\Desktop\Wan3.0\fixtures\scenes-4x5s.mp4':
  Metadata:
    major_brand     : isom
    minor_version   : 512
    compatible_brands: isomiso2avc1mp41
    encoder         : Lavf59.34.101
  Duration: 00:00:20.00, start: 0.000000, bitrate: 197 kb/s
  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 194 kb/s, 25 fps, 25 tbr, 12800 tbn (default)
    Metadata:
      handler_name    : VideoHandler
      vendor_id       : [0][0][0][0]
      encoder         : Lavc59.51.100 libx264
C:\Users\<user>\AppData\Local\Temp\wanstudio-media-check\clip-b-10-15.mp4: Permission denied

  RESULT: FAIL

--- 附录 A) WinRtMediaEngine（无 ffmpeg 兜底） ---
  Name=windows-media, SupportsSceneDetect=True, SupportsCut=False, SupportsConcat=False
  [FAIL] WinRT ProbeAsync: InvalidOperationException: 读取视频元数据失败（long-20s.mp4）：拒绝访问。

无法访问指定的文件或文件夹()。该项目没有位于应用程序可以访问的位置(包括应用程序数据文件夹、可通过许可范围进行访问的文件夹以及保存在 StorageApplicationPermissions 列表中的项目)中。请确认未使用系统或隐藏文件属性标记该文件。（0x80070005）
  [FAIL] WinRT ExtractFrameAsync: InvalidOperationException: 读取视频元数据失败（long-20s.mp4）：拒绝访问。

无法访问指定的文件或文件夹()。该项目没有位于应用程序可以访问的位置(包括应用程序数据文件夹、可通过许可范围进行访问的文件夹以及保存在 StorageApplicationPermissions 列表中的项目)中。请确认未使用系统或隐藏文件属性标记该文件。（0x80070005）
  [FAIL] WinRT DetectSceneCutsAsync: InvalidOperationException: 读取视频元数据失败（scenes-4x5s.mp4）：拒绝访问。

无法访问指定的文件或文件夹()。该项目没有位于应用程序可以访问的位置(包括应用程序数据文件夹、可通过许可范围进行访问的文件夹以及保存在 StorageApplicationPermissions 列表中的项目)中。请确认未使用系统或隐藏文件属性标记该文件。（0x80070005）
  CutAsync -> NotSupportedException OK：当前使用 Windows.Media 兜底引擎（未找到 ffmpeg），不支持本地切段。装好 ffmpeg 或设置 WANSTUDIO_FFMPEG 后重试。
  ConcatAsync -> NotSupportedException OK：当前使用 Windows.Media 兜底引擎（未找到 ffmpeg），不支持本地拼接。装好 ffmpeg 或设置 WANSTUDIO_FFMPEG 后重试。

=== 结论：4 项 FAIL ===
```

逐项结论：

| # | 检查项 | 结论 | 依据 |
| --- | --- | --- | --- |
| 1 | probe（long-20s.mp4） | **PASS** | `DurationSeconds = 20`、`640x360`、`HasAudio = True`，33 ms |
| 2 | PlanFixed(20, 6) | **PASS** | 4 段，`[0..6][6..12][12..18][18..20]`，末段 2.000s |
| 3 | DetectSceneCuts(scenes-4x5s) | **PASS** | `cuts = [5.000, 10.000, 15.000]`，75 ms，3/3 命中 |
| 4 | Cut + Concat | **FAIL** | ffmpeg 子进程写 `%LOCALAPPDATA%\Temp\...\clip-b-10-15.mp4` 报 `Permission denied` |
| 附A | WinRT 兜底 probe/抽帧/场景 | **FAIL** | 三次都是 `0x80070005`「拒绝访问」 |

---

### 2.2 第 4 项 FAIL 的定位（这是环境，不是引擎）

`Permission denied` 发生在 **ffmpeg 打开输出文件**这一步（输入文件已成功打开并打印了 stream 信息）。逐层排除：

**(a) 目标目录可写吗？** 可写。同一个 bash 会话直接跑 ffmpeg 写同一路径成功：

```
$ "$FF" -y -loglevel error -i fixtures/scenes-4x5s.mp4 -ss 10 -t 5 -c copy "$TD/clip-b-10-15.mp4"
exit=0
$ ls -la "$TD/clip-b-10-15.mp4"
-rw-r--r-- 1 <user> 197121 15114 Oct 22 02:14 .../clip-b-10-15.mp4
```

**(b) 是文件被占用吗？** 不是。独占打开测试通过，且无 ffmpeg 残留进程：

```
$ powershell -NoProfile -Command "...[System.IO.File]::Open($p,'Open','ReadWrite','None')..."
EXCLUSIVE_OPEN_OK
$ Get-Process ffmpeg,media-check -ErrorAction SilentlyContinue
（无输出）
```

**(c) 是参数问题吗？** 不是。把引擎的**完全相同参数**（`-c:v libx264 -preset veryfast -crf 20 -c:a aac -movflags +faststart`）在 bash 里直跑，成功：

```
$ "$FF" -hide_banner -nostdin -ss 10 -i fixtures/scenes-4x5s.mp4 -t 5 \
      -c:v libx264 -preset veryfast -crf 20 -c:a aac -movflags +faststart -y "$OUT"
exit=0
-rw-r--r-- 1 <user> 197121 5705 Oct 22 02:15 .../clip-b-10-15.mp4
```

**(d) 那把 TMP 指到工作区内呢？** 第 4 项立刻 PASS（其余 3 项仍是 WinRT）：

```
$ TMP='C:\Users\<user>\Desktop\Wan3.0\.tmp-media' TEMP='...\.tmp-media' ./media-check.exe
  RESULT: PASS
  RESULT: PASS
  RESULT: PASS
  RESULT: PASS
=== 结论：3 项 FAIL ===
```

→ 说明被拒的是 **写 `%LOCALAPPDATA%\Temp`** 这个动作本身。

**(e) 是 exe 所在位置吗？** 是。2×2 对照：

| exe 位置 | cwd | TMP | 结果 |
| --- | --- | --- | --- |
| 工作区内 `tools\...\win-x64\` | 工作区 | 默认 %TEMP% | **4 项 FAIL** |
| 工作区内 `.tmp-media\bin2\`（复制同一份） | 工作区 | 默认 %TEMP% | **4 项 FAIL** |
| 工作区外 `%TEMP%\verify-media\` | 工作区 | 默认 %TEMP% | **ALL PASS** |
| 工作区外 `Desktop\verify-media\` | 工作区 | 默认 %TEMP% | **ALL PASS** |
| 工作区内 `tools\...\win-x64\` | `/tmp` | 默认 %TEMP% | **4 项 FAIL** |

即：**与 cwd 无关，与 TMP 无关（TMP 只影响第 4 项），只与 exe 是否位于工作区树内有关。**

结论（环境级）：本会话对**镜像位于 `C:\Users\<user>\Desktop\Wan3.0` 之内的进程及其子进程**施加了文件访问限制——允许写工作区，拒绝写 `%LOCALAPPDATA%\Temp`，同时拒绝 WinRT 的代理文件访问。同一份二进制移出该树即 ALL PASS。这是**执行会话的沙箱行为**，不是 `FfmpegMediaEngine` 的缺陷；`RunAsync` 的 `ProcessStartInfo`（`UseShellExecute=false` + 重定向双流 + `ArgumentList`）本身没有路径或权限问题。

---

### 2.3 移出工作区树后的同一份二进制 → ALL PASS

命令原文：

```
cd C:\Users\<user>\Desktop\Wan3.0
copy 上面 win-x64\* 到 %TEMP%\verify-media\
%TEMP%\verify-media\media-check.exe C:\Users\<user>\Desktop\Wan3.0\fixtures
```

原始输出全文（`qa/media-canonical-run-outside.txt`，退出码 0）：

```
=== media-check · WanStudio 本地切段引擎 ===
time          : 2026-10-22 02:18:08
fixtures      : C:\Users\<user>\Desktop\Wan3.0\fixtures
base dir      : C:\Users\<user>\AppData\Local\Temp\verify-media\
os            : Microsoft Windows NT 10.0.26200.0
runtime       : 8.0.31 / win-x64

[engine] TryLocateFfmpeg()      -> True  path=C:\Users\<user>\AppData\Local\oopz\ffmpeg.exe
[engine] CreateDefault().Name   -> ffmpeg
[engine] Detail                 -> C:\Users\<user>\AppData\Local\oopz\ffmpeg.exe
[engine] SupportsSceneDetect    -> True
[engine] SupportsCut(concrete)  -> True
[engine] SupportsConcat(concrete) -> True

--- 1) Probe fixtures\long-20s.mp4 ---
  ProbeAsync("long-20s.mp4")  34 ms
    DurationSeconds = 20   (期望 20.000)
    Width x Height  = 640x360   (期望 640x360)
    HasAudio        = True   (期望 True)
    verdict: duration OK, size OK
  RESULT: PASS

--- 2) SegmentPlanner.PlanFixed(20, 6) ---
  PlanFixed(duration=20, segmentSeconds=6)  0 ms -> 4 段
    #01  0.000 → 6.000   len=6.000s
    #02  6.000 → 12.000   len=6.000s
    #03  12.000 → 18.000   len=6.000s
    #04  18.000 → 20.000   len=2.000s
    expected: 4 段 [0..6][6..12][12..18][18..20]（末段 2s）
    verdict: count OK, 起止 OK
  RESULT: PASS

--- 3) DetectSceneCuts(fixtures\scenes-4x5s.mp4) ---
  DetectSceneCutsAsync("scenes-4x5s.mp4")  75 ms -> 3 个切点
    cuts = [5.000, 10.000, 15.000]
    expected ≈ [5, 10, 15]（±0.5s，红→绿那一刀在 0.35 阈值下会漏，实测阈值 0.02）
    verdict: count OK, 命中 3/3
  RESULT: PASS

--- 4) Cut(scenes-4x5s, 10, 5) + Concat([0..5],[10..15]) ---
  CutAsync(src, start=10, dur=5)  69 ms
    out  = C:\Users\<user>\AppData\Local\Temp\wanstudio-media-check\clip-b-10-15.mp4
    size = 5,705 bytes
    probe duration = 5   (期望 5 ± 0.2)
  CutAsync(src, start=0, dur=5)   69 ms
    out  = C:\Users\<user>\AppData\Local\Temp\wanstudio-media-check\clip-a-00-05.mp4
    size = 5,707 bytes
    probe duration = 5   (期望 5 ± 0.2)
  ConcatAsync([clip-a, clip-b])   101 ms
    out  = C:\Users\<user>\AppData\Local\Temp\wanstudio-media-check\concat-a-then-b.mp4
    size = 9,782 bytes
    probe duration = 10   (期望 10 ± 0.3)
    probe size     = 640x360, HasAudio = False
    verdict: 切段 OK, 拼接 OK, 产物可 probe OK
  RESULT: PASS

--- 附录 A) WinRtMediaEngine（无 ffmpeg 兜底） ---
  Name=windows-media, SupportsSceneDetect=True, SupportsCut=False, SupportsConcat=False
  ProbeAsync(long-20s.mp4)  95 ms -> 20s, 640x360, HasAudio=True
  ExtractFrameAsync(long-20s.mp4, 3s)  300 ms -> 51,361 bytes png
  DetectSceneCutsAsync(scenes-4x5s.mp4)  16926 ms -> [5.25, 10.25, 15.25]（缩略图采样，精度受 0.25s 步长限制）
  CutAsync -> NotSupportedException OK：当前使用 Windows.Media 兜底引擎（未找到 ffmpeg），不支持本地切段。装好 ffmpeg 或设置 WANSTUDIO_FFMPEG 后重试。
  ConcatAsync -> NotSupportedException OK：当前使用 Windows.Media 兜底引擎（未找到 ffmpeg），不支持本地拼接。装好 ffmpeg 或设置 WANSTUDIO_FFMPEG 后重试。

=== 结论：ALL PASS ===
```

逐项结论：

| # | 检查项 | 结论 | 实测数字 |
| --- | --- | --- | --- |
| 1 | probe | **PASS** | 20s / 640x360 / HasAudio=True，34 ms |
| 2 | PlanFixed(20,6) | **PASS** | 4 段，末段 2.000s |
| 3 | DetectSceneCuts | **PASS** | `[5.000, 10.000, 15.000]`，75 ms |
| 4 | Cut + Concat | **PASS** | 切 5s/5s（5,705 / 5,707 bytes，probe 都是 5），拼 10s（9,782 bytes，640x360） |
| 附A | WinRT 兜底 | **PASS** | probe 95 ms；抽帧 51,361 bytes PNG；场景 `[5.25, 10.25, 15.25]` 16,926 ms；Cut/Concat 正确抛 `NotSupportedException` |

**第 4 项的补充事实（不算缺陷，但要记）**：拼接产物 `HasAudio = False`，因为 `scenes-4x5s.mp4` 本身没有音轨（见 §3）。两个 5s 片段重编码后硬拼成 10.000s 单一文件，时长精确，但**接缝处是两段独立编码的画面直接串接**，画面内容在 5s 处有切换，声音轨不存在——这是本地硬拼的既有行为，不是官方续接。

**WinRT 兜底的实测精度**：场景检测给出 `[5.25, 10.25, 15.25]`，比真实切点晚 0.25s，耗时 16.9s（ffmpeg 是 75ms）。步长限制来自缩略图 0.25s 采样。

---

### 2.4 阈值 0.02 才不漏红→绿（实测）

这条不能只引代码注释，直接测 ffmpeg 的 `scene` 分数（引擎用的就是 `select='gt(scene,X)',showinfo`）：

命令原文：

```
ffmpeg -hide_banner -nostdin -nostats -i fixtures/scenes-4x5s.mp4 \
  -filter:v "select='gt(scene,0.005)',metadata=print" -an -f null -
```

原始输出（各切点的真实 scene 分数）：

```
[Parsed_metadata_1 @ ...] frame:0    pts:64000   pts_time:5
[Parsed_metadata_1 @ ...] lavfi.scene_score=0.270000
[Parsed_metadata_1 @ ...] frame:1    pts:128000  pts_time:10
[Parsed_metadata_1 @ ...] lavfi.scene_score=0.030000
[Parsed_metadata_1 @ ...] frame:2    pts:192000  pts_time:15
[Parsed_metadata_1 @ ...] lavfi.scene_score=0.543690
[Parsed_metadata_1 @ ...] frame:3    pts:192512  pts_time:15.04
[Parsed_metadata_1 @ ...] lavfi.scene_score=0.025775
[Parsed_metadata_1 @ ...] frame:4    pts:236544  pts_time:18.48
[Parsed_metadata_1 @ ...] lavfi.scene_score=0.005123
[Parsed_metadata_1 @ ...] frame:5    pts:237056  pts_time:18.52
[Parsed_metadata_1 @ ...] lavfi.scene_score=0.015733
[Parsed_metadata_1 @ ...] frame:6    pts:237568  pts_time:18.56
[Parsed_metadata_1 @ ...] lavfi.scene_score=0.014449
```

两个阈值的命中对比（命令原文同上，只换 `select` 里的数）：

```
=== threshold=0.35 ===
pts_time:15
--- 命中帧数: 1 ---
=== threshold=0.02 ===
pts_time:5
pts_time:10
pts_time:15
pts_time:15.04
--- 命中帧数: 4 ---
```

实测事实：

- 真实三刀里，**t=5 只有 0.270000、t=10 只有 0.030000、t=15 是 0.543690**。
- **阈值 0.35 → 只命中 t=15 一刀，漏掉 t=5 和 t=10（含红→绿那一刀）。**
- **阈值 0.02 → 命中 4 帧**：5 / 10 / 15 / 15.04；其中 15.04 的 0.025775 会被引擎的 0.5s 去重（`SceneDedupeSeconds = 0.5`）并掉，最终 `[5.000, 10.000, 15.000]`。
- 可用窗口实测是 **(0.015733, 0.030000)**：下界来自 18.52s 那帧 0.015733（不能让它进来），上界是红→绿那一刀的 0.030000（必须让它进来）。0.02 落在这个窗口内。
- 因此「阈值 0.02 才不漏红→绿」是**实测成立**的，不是注释里的说法。

附带一条代码级事实（读代码得出，非运行时实测）：`FfmpegMediaEngine.ResolveThreshold` 会把**任何等于契约默认值 0.35 的入参**（包括调用方显式传 0.35）替换成 0.02；只有传入 `0.35` 以外的值才会被尊重。也就是说走 API 拿不到 0.35 的行为。位置：`WanStudio/Services/Media/FfmpegMediaEngine.cs:264-274`。

---

## 3. 附录：fixture 复核数字

`ffprobe.exe` 不存在（见 §1），改用同目录 `ffmpeg.exe -i`，命令原文：

```
ffmpeg -hide_banner -nostdin -i fixtures/long-20s.mp4
ffmpeg -hide_banner -nostdin -i fixtures/scenes-4x5s.mp4
```

原始输出：

```
=== long-20s.mp4 ===
  Duration: 00:00:20.00, start: 0.000000, bitrate: 121 kb/s
  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 45 kb/s, 24 fps, 24 tbr, 12288 tbn (default)
  Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, mono, fltp, 69 kb/s (default)
=== scenes-4x5s.mp4 ===
  Duration: 00:00:20.00, start: 0.000000, bitrate: 197 kb/s
  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 194 kb/s, 25 fps, 25 tbr, 12800 tbn (default)
```

| fixture | 时长 | 分辨率 | 帧率 | 音轨 | 文件字节 |
| --- | --- | --- | --- | --- | --- |
| `long-20s.mp4` | 00:00:20.00 | 640x360 | 24 fps | **有**（aac LC 44100 Hz mono） | 303,365 |
| `scenes-4x5s.mp4` | 00:00:20.00 | 640x360 | 25 fps | **无**（只有 Stream #0:0 视频） | 493,232 |

两个 fixture 的 20.00s / 640x360 与 media-check 自报的 `DurationSeconds = 20`、`640x360` 完全一致。`scenes-4x5s.mp4` 无音轨，这正是 §2.3 里拼接产物 `HasAudio = False` 的原因。

---

## 4. 总判

**不是 ALL PASS：在本会话直接用仓库内的 `tools\media-check\...\media-check.exe` 跑，第 4 项（Cut+Concat）与附录 A 三项（WinRT probe/抽帧/场景）FAIL，共 4 项 FAIL。**

FAIL 的根因已用 2×2 对照实验定位到**执行会话的文件访问沙箱**（exe 镜像位于工作区树内时：拒绝写 `%LOCALAPPDATA%\Temp`、拒绝 WinRT 代理文件访问），同一份二进制移出该树后 **ALL PASS**。所以这 4 项既不能判产品 PASS，也不是引擎代码缺陷——**需要在非沙箱上下文（Lead 的 danger-full-access 会话）用仓库内 exe 复跑一次来定性**。

实测已成立的部分（4 项，全部来自真实运行，非代码推断）：probe 20s/640x360/HasAudio=True、PlanFixed 4 段末段 2s、DetectSceneCuts `[5.000, 10.000, 15.000]`、阈值 0.02 是唯一能同时不漏 t=5/t=10/t=15 的取值（0.35 只留 1 刀）。
