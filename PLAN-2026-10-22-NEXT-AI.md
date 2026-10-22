# Wan3.0 长视频交接计划 — 给下一位 AI（2026-10-22 02:10 CST）

**Owner:** Tiny  
**本轮执行:** 主理人（Lead）+ eng-media + eng-api + qa（后两者会话被中断，产物已落盘）  
**代码目录:** `C:\Users\<user>\Desktop\Wan3.0`  
**状态一句话:** **长视频页从「占位壳」变成「可用页面」**（导入→切段→缩略图→勾选→预览→续接面板→可选硬拼全部落地，本地媒体层与续接协议均已实测通过）；**UI 驱动的「勾选生成→入池」链路尚未端到端验证**；**真 Key 端到端仍 BLOCKED**；整包**未过 ship gate**。

---

## 1. 先看这个：本轮到底变了什么

| 类型 | 内容 | 证据 |
|------|------|------|
| 新增能力 | 长视频页完整实现（原来只有「切段服务马上接上」的 InfoBar） | `qa\lv-page.png`（导入 20s 夹具，自动切 4 段，缩略图蓝/红/绿/testsrc 正确，零勾选时「生成所选（0）」禁用并写明原因） |
| 新增能力 | 本地媒体引擎：探测 / 场景切点 / 抽帧 / 切段 / 拼接（ffmpeg 优先，WinRT 兜底） | `tools\media-check` 全 PASS，见 §4.1 |
| 新增能力 | 官方续接 `SubmitContinueAsync`：`media:[{type:"first_clip"}]` + 可选 `last_frame`，本地路径直接拒绝 | `tools\api-check` mock 实测，见 §4.2 |
| 新增能力 | 视频池 + 任务成功自动入池（UI 线程安全） | `Services\VideoPool.cs`、`VideoJobRunner` 成功分支 |
| 新增能力 | 任务页真实播放器 + 「另存为…」 | `Pages\TasksPage.xaml(.cs)` |
| 修复 bug | **「长视频点不动」**：`NumberBox.Value` 写整数字面量导致 `XamlParseException`，整页构造失败、导航无反应 | 见 §5.1 |
| 修复 bug | `App.MainWindowHint` 从未赋值 → 所有文件选择器都会抛「窗口未就绪」 | `App.xaml.cs` |
| 修复 bug | `VideoTaskStore.EnsureRunningDemo()` 无条件插入假 RUNNING 行（soft-pass 味道） | 已删除，仅保留 `qa\seed-running.flag` 显式 seed |
| 纪律 | 文件归属制：三个角色各管各的文件，禁止并发改同一文件 | `ENG-CONTRACTS-2026-10-22.md` |

---

## 2. 验收状态总表（诚实版）

标记含义：`PASS`=有本轮可复现证据；`PARTIAL`=代码在、证据不全；`BLOCKED`=缺真 Key，禁止标 PASS；`TODO`=没做。

### 2.1 B 主路径（文生）

| ID | 状态 | 说明 |
|----|------|------|
| AC-01 | **PASS** | 冷启动可用（`qa\lv-page.png` 本轮启动实拍） |
| AC-02 | **PASS** | 新建页提示词/模型/分辨率可提交（`qa\p2-home.png`） |
| AC-03 | **PARTIAL** | 取消按钮 + 进度条存在；历史证据是 demo seed，本轮未补「真任务可取消」 |
| AC-04 | **PARTIAL** | 任务页已加 `MediaPlayerElement` 播放 + 另存为；**未用真实结果验证播放** |
| AC-05 | **PARTIAL** | `PasswordVault` + profile 已实现；跨重启持久化未正式证明 |
| AC-06 | **BLOCKED** | 两模型真提交，缺 Key |
| AC-07 | **BLOCKED** | task_id→SUCCEEDED→video_url 真机，缺 Key（mock 版已通，见 §4.2） |
| AC-08 | **PARTIAL** | 缺 Key 禁用有图；错 Key 的 401 只在客户端/mock 层验过，**UI 未验** |
| AC-09 | **TODO** | 设计自检清单没写 |

### 2.2 Cut-3 四模式（AC-M*）

| ID | 状态 | 说明 |
|----|------|------|
| AC-M01 | **PASS** | 四模式切换 + 当前模式高亮（`qa\p3-modes-*.png`） |
| AC-M02/04/06 | **PARTIAL** | 缺素材禁用与原因文案在代码里；无独立 proof 报告 |
| AC-M03/05/07 | **BLOCKED** | 缺真 Key；且 `media[]` 字段是否匹配该 host 上的 Wan 文档**仍未验证** |
| AC-M08/M09 | **TODO** | 非法素材错误态、模式自检没做 |

### 2.3 长视频（AC-L*，本轮主战场）

| ID | 状态 | 说明 / 缺什么 |
|----|------|---------------|
| AC-L01 | **PARTIAL** | 固定秒数（1–15、默认 5）+ 切镜模式都已实现并可选；截图只覆盖固定秒数（`qa\lv-page.png`），**切镜模式的 UI 截图缺** |
| AC-L02 | **PARTIAL** | 预览播放器（transport 可 scrub）+ 起止微调 + 「应用调整」已实现；**没有截「预览中 + `/__requests` 为 0」的图** |
| AC-L03 | **PASS（图）** | 零勾选时按钮禁用 + 原因文案，见 `qa\lv-page.png`；「零提交」需配合 `/__requests` 原始输出才算铁证 |
| AC-L04 | **TODO（未验证）** | 代码有：勾选→建 Task（`ImageToVideo`，首帧当 `first_frame`）→ 分段状态跟随 task 变化→失败可重试；**UI 全流程没跑过** |
| AC-L05 | **TODO（未验证）** | 代码有：`VideoJobRunner` 成功后 `VideoPool.AddFromTask`；**没截到池里有 2 条的图** |
| AC-L06 | **PARTIAL** | 协议层已验：请求体 `media[0].type=first_clip`（§4.2）；**UI 里「设为首片段→提交」没跑过** |
| AC-L07 | **BLOCKED** | 需要真 Key 才能谈「单次续写时长落在 2–15s」；mock 的时长不算 |
| AC-L08 | **PARTIAL** | 默认关 + 开启后池内勾选 + 池空空态都有；拼接触发没跑过 |
| AC-L09 | **PARTIAL** | 文案已守线：写「本地硬拼」「接缝 N 处」「不是官方续接」，全文无「无缝」；**产物与截图缺** |
| AC-L10 | **PARTIAL** | 模型列表含 `wan2.7-i2v`，非续写模型/无首片段/无提示词都会禁用并写原因；截图缺 |
| AC-L11 | **TODO** | 自检清单没写 |

> **禁止 soft-pass**：以上 PARTIAL 一律不许当 PASS 报。真 Key 项没有真请求 JSON 就是 BLOCKED。

---

## 3. 代码地图（谁负责哪块，下一位按这个找）

```
WanStudio/
├─ App.xaml.cs                    启动参数 --open=home|tasks|long|settings / --import=<path>；MainWindowHint 赋值（本轮修）
├─ MainWindow.xaml.cs             导航；启动页解析（本轮加）
├─ Pages/
│  ├─ HomePage.xaml(.cs)          四模式新建页（未改结构，仅保持兼容）
│  ├─ TasksPage.xaml(.cs)         本轮加 MediaPlayerElement + 另存为；移除无条件 demo 行
│  └─ LongVideoPage.xaml(.cs)     ★本轮重点：导入/切段/分段列表/预览/池/续接/硬拼（约 700 行）
├─ Services/
│  ├─ LongVideoProject.cs         ★页面状态模型：LongVideoSegment / PoolRow / LongVideoWorkspace
│  ├─ Media/
│  │  ├─ MediaContracts.cs        冻结接口：IMediaEngine（Name/Detail/Supports*/Probe/DetectSceneCuts/ExtractFrame/Cut/Concat）+ 常量
│  │  ├─ FfmpegMediaEngine.cs     ffmpeg 实现（探测/场景/抽帧/切段/拼接）
│  │  ├─ WinRtMediaEngine.cs      Windows.Media 兜底（可探测/抽帧；不支持切段拼接）
│  │  ├─ MediaEngines.cs          ffmpeg 定位：ENV → 应用目录 tools → 向上 6 层 → PATH → %LOCALAPPDATA%\oopz
│  │  └─ SegmentPlanner.cs        纯函数：PlanFixed / PlanFromSceneCuts（1–15s 约束、>15s 均分、<1s 并入前段）
│  ├─ ModelCatalog.cs             三模型 + FirstClipContinue 能力位（wan2.7-i2v=true）
│  ├─ VideoPool.cs                池（内存，重启清空）；AddFromTask 幂等；ContinueBlockReason 中文原因
│  ├─ DashScopeClient.cs          本轮加 SubmitContinueAsync + RequirePublicUrl
│  ├─ VideoTaskStore.cs           本轮加 GenerationMode.ContinueFromClip / FirstClipUrl / LastFrameUrl / SegmentLabel / PoolItemId
│  └─ VideoJobRunner.cs           续接分支 + 成功后入池（UI 线程）
tools/
├─ media-check/                   媒体层 4 项实测（§4.1）
├─ api-check/                     续接协议实测（§4.2）
├─ mock-dashscope/                DashScope 替身（8787，RUNNING×3→SUCCEEDED，401 错 Key）
├─ qa-key/                        PasswordVault 写/读/删（resource=WanStudio.DashScope）
└─ capture-window.ps1             窗口截图（注意 §5.2 的路径 bug）
```

---

## 4. 本轮实测证据（可复现）

### 4.1 媒体层：`tools\media-check\bin\Release\net8.0-windows10.0.26100.0\win-x64\media-check.exe`

```
1) probe fixtures\long-20s.mp4        → 20s / 640x360
2) PlanFixed(20, 6)                   → 4 段，末段 2s，连续不重叠
3) DetectSceneCuts(scenes-4x5s.mp4)   → 命中 3/3（≈5/10/15），阈值实测 0.02 才不漏红→绿
4) Cut(10,5) + Cut(0,5) + Concat      → 5s / 5s / 10s（±0.2/±0.3），产物可 probe
附录 A) WinRt 兜底                     → Probe 146ms、抽帧 429ms、场景检测 21.8s（慢但可用）；Cut/Concat 正确抛 NotSupported
=== 结论：ALL PASS ===
```

### 4.2 续接协议：`tools\api-check\bin\Release\net8.0\api-check.exe http://127.0.0.1:8787 demo-key`

```
task_id=mock-1
poll[0..2] RUNNING → poll[3] SUCCEEDED url=http://127.0.0.1:8787/files/sample.mp4
mock 收到的 body：
{"model":"wan2.7-i2v","input":{"prompt":"延续上一段的主体与镜头运动，动作自然衔接。",
 "media":[{"type":"first_clip","url":"http://127.0.0.1:8787/files/sample.mp4"}]},
 "parameters":{"resolution":"480P","ratio":"adaptive","duration":5}}
本地路径守卫生效：first_clip 需要公网可访问 URL（http/https）…
```

> 这是**协议级**证据，不是真机生成证据。AC-L06/L07 的真机结论仍然 BLOCKED。

---

## 5. 已知坑（下一位必读，能省几小时）

### 5.1 「长视频点不动 / 白屏」的两个真凶

1. **`NumberBox.Value` 不能写整数字面量。** XAML 里 `Value="5"` 会在 `LoadComponent` 阶段抛
   `Failed to assign to property 'NumberBox.Value'` → 整页构造失败 → 点导航没反应（异常被
   `MainWindow.NavView_SelectionChanged` 的 try 吞掉，只写 `wanstudio-crash.log`）。
   **修法**：XAML 里不要写 `Value`，在 `Loaded` 里赋 `5.0`（double）。已这样处理。
2. **同页两个 `x:Bind` DataTemplate 会打崩 XAML 编译器**：报 `WMC9999`（错误消息本地化还失败）
   或 `WMC0610 XBF syntax error 0x09c8`，并且**增量构建会污染判断**——必须 `rm -rf obj` 再编才看得到真错。
   **修法**：长视频页的「视频池」列表改用经典 `{Binding}`（`PoolRow` 实现 INPC），
   分段列表保留 `x:Bind`。**不要手贱把池模板改回 x:Bind**，否则又崩。

### 5.2 其他坑

| 坑 | 现象 | 处理 |
|----|------|------|
| `tools\capture-window.ps1` 绝对路径 bug | 传 `-OutFile "C:\...\a.png"` 会把目录拼成 `…\Wan3.0\C:\…` 然后报错 | **传相对路径** `qa\a.png`，先 `Set-Location` 到仓库根 |
| exe 被锁 | build 报 MSB3027/MSB3021 | 先 `Stop-Process -Name WanStudio -Force`（`taskkill //F` 在 Git Bash 下有时不生效） |
| `MainWindowHint` | 之前从未赋值，所有 `FileOpenPicker` 抛「窗口未就绪」 | 已在 `App.OnLaunched` 赋值 |
| 设置文件当前指向 mock | `%LOCALAPPDATA%\WanStudio\settings.json` 现在是 `Endpoint=http://127.0.0.1:8787`，凭据库是 `demo-key` | **换真 Key 前必须改回** `https://cn-beijing.maas.qianwenaiapi.com` 并清掉 demo-key |
| 池不持久化 | 重启后池清空 | 已知限制，未做持久化 |
| 导入的本地片段不能续接 | `ContinueBlockReason` 提示无公网 URL | 设计使然；首片段目前只能来自「已生成片段（有 video_url）」 |
| ffmpeg 不随仓库分发 | 本机靠 `%LOCALAPPDATA%\oopz\ffmpeg.exe` 兜底 | 换机器要设 `WANSTUDIO_FFMPEG` 或放 `tools\ffmpeg.exe` |
| 分段生成语义 | 每段用**首帧**走图生（`first_frame`）重新生成，不是「重切片」 | 若产品要「保持原片动作、只换风格」，需要新 cut |

---

## 6. 下一步执行计划（给下一位 AI 的施工单）

### P0 · 先把「勾选生成 → 入池」端到端跑通（不需要真 Key）

环境准备（三步，缺一不可）：

```powershell
# 1) 起 mock（后台）
python tools\mock-dashscope\mock_server.py --port 8787 --fixture fixtures\long-20s.mp4
# 2) 指向 mock
#    %LOCALAPPDATA%\WanStudio\settings.json 写 Endpoint=http://127.0.0.1:8787
# 3) 注入测试 Key
tools\qa-key\bin\Release\net8.0-windows10.0.26100.0\win-x64\qa-key.exe --set default demo-key
```

```powershell
# 启动并自动导入夹具
& "WanStudio\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\WanStudio.exe" `
  --open=long --import "C:\Users\<user>\Desktop\Wan3.0\fixtures\scenes-4x5s.mp4"
```

施工顺序与验收：

1. **AC-L03 铁证**：截图零勾选禁用态 + `curl http://127.0.0.1:8787/__requests` 证明提交数 0。
2. **AC-L04**：勾 2 段 →「生成所选（2）」→ 期望 `/__requests` 出现 2 条 POST（`input.media[0].type=="first_frame"`），任务页出现 2 条，状态 排队→生成中→已完成（mock 第 4 次轮询才 SUCCEEDED）。
3. **AC-L05**：两条成功后池计数=2；截图池条目（标题/来源/时长/「可作首片段」）。
4. **AC-L06**：池里「设为首片段」→ 续接面板 URL 就绪 → 提交 → `/__requests` 抓 `first_clip`（Authorization 打码）→ 截图。
5. **AC-L08/L09**：开硬拼 → 勾 2 段 → 拼接 → 记录输出路径、接缝时间、`ls -la` 产物；确认 UI 无「无缝」。
6. **AC-08**：`qa-key --set default bad-key` → 提交 → 任务页应显示 `Submit failed 401: {"code":"InvalidApiKey"...}`；截图为证，之后改回 `demo-key`。

> 已知 UI 自动化不好点：如果 UIA 抓不到，允许走键盘（Tab + Space）；**但不许加「测试专用预设勾选」开关**，那就是造假证据。

### P1 · 设计门禁与四模式补证

7. AC-M02/04/06：三种模式各截「缺素材禁用 + 原因」两张图，写 `qa\run-2026-10-22-modes.md`。
8. AC-09 / AC-M09 / AC-L11：Eng 自检清单（层级/留白/主路径可用/错误态清晰/续接与硬拼文案不混淆），Lead 过一眼截图。Critiquito 已出局，别再找它。
9. AC-04：拿 mock 成功的本地文件在任务页点播放 + 另存为，截图证明可播（mock 文件是真 mp4，可播）。

### P2 · 真 Key 端到端（BLOCKED，等 Tiny）

10. 复位 endpoint，用真 Key 跑：文生两模型各一次（AC-06/07）、图生/人物/动作各一次（AC-M03/05/07）、续接一次（AC-L06/L07）。
11. **若某模式 media 字段在该 host 上不被接受 → 停工改 cut，禁止假成功**（这是 SoT 明写的红线）。

### P3 · 收尾

12. 池持久化（可选）：`%LOCALAPPDATA%\WanStudio\pool.json`，重启恢复。
13. `qa\CANONICAL-AC-PACK.md` 的 AC 编号与 Binding 对齐（pack 里 AC-04/05/06 与 Binding 有错位）。
14. 清理根目录 `_write_*.py`、过期截图（勿删 SoT / qa）。

---

## 7. 复现命令速查

```bash
# 构建（主目录）
cd /c/Users/<user>/Desktop/Wan3.0/WanStudio && dotnet build -c Debug -p:Platform=x64 -v:q

# 他人验证代码：复制到临时目录构建，别抢主目录 obj/bin
mkdir -p /tmp/verify-x && cd /c/Users/<user>/Desktop/Wan3.0 \
  && (tar cf - --exclude=bin --exclude=obj WanStudio | tar xf - -C /tmp/verify-x) \
  && cd /tmp/verify-x/WanStudio && dotnet build -c Debug -p:Platform=x64 -v:q

# 媒体层实测
tools/media-check/bin/Release/net8.0-windows10.0.26100.0/win-x64/media-check.exe

# 续接协议实测
tools/api-check/bin/Release/net8.0/api-check.exe http://127.0.0.1:8787 demo-key

# 截图（必须相对路径）
powershell -File tools/capture-window.ps1 -ProcessName WanStudio -OutFile "qa\xxx.png"
```

---

## 8. 交接红线（和上一位的保持一致）

1. 工作目录只能 `C:\Users\<user>\Desktop\Wan3.0`；范围变更写新 cut 找 Owner 批。
2. **不许 soft-pass**：绿 build ≠ AC PASS；没真 Key 的联网项只能标 BLOCKED。
3. **不许**把 ffmpeg 硬拼叫「续接 / 无缝」；硬拼默认关。
4. Key 永不进仓库、报告、日志明文。
5. 每完成一项：截图/API JSON（打码）落 `qa\`，更新 `qa\CANONICAL-AC-PACK.md` 与一份 `qa\run-YYYY-MM-DD-*.md`。
6. 文件归属制：改别人文件前先在群里说，避免上次「并发改文件把 Home/Settings 打成 OK 占位」重演。

---

## 9. 本轮未拿到的东西（如实交代）

- eng-media / eng-api / qa 三个子代理都在中途被会话打断，**没交最终报告**；本文件的证据是我（Lead）在落盘代码上重跑得到的。
- 未产出：`qa\run-2026-10-22-media-engine.md`、`qa\run-2026-10-22-continue-api.md`、`qa\run-2026-10-22-longvideo.md`（报告模板可直接照 §4/§6 填）。
- 未验证：UI 驱动的生成/入池/续接/拼接/错 Key 全链路；任务页播放；切镜模式截图。
- 未做：AC-09/M09/L11 自检清单、AC-M02/04/06 独立 proof、池持久化、AC 编号对齐。
