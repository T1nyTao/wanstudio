# Eng 设计自检清单 — Wan3.0（2026-10-22）

**作者:** eng-docs
**覆盖 AC:** AC-09（B 主路径设计自检）、AC-M09（Cut-3 四模式自检）、AC-L11（续接/硬拼文案不混淆）
**判定口径:** 每条只允许两种结论 —— **代码级已确认** 或 **需截图补证**。PARTIAL/BLOCKED 一律不写成 PASS。
**代码基线:** `WanStudio\Pages\*`、`WanStudio\Services\*`（本清单所有行号以 2026-10-22 当前工作区为准）。
**未做:** 本清单**不含**任何 UI 实拍截图；凡涉及「看起来对不对」的项，一律标 `需截图补证`，由 Lead 过一眼主路径截图时签字。

---

## 0. 一句话结论

| 项 | 结论 |
|----|------|
| AC-09 | **代码级已确认**（结构/留白/禁用原因/错误态四处有据）；**层级与留白的最终观感需截图补证** |
| AC-M09 | **代码级已确认**（四模式高亮 + 缺素材禁用原因全部落到具体文案）；**切换后的视觉层级需截图补证** |
| AC-L11 | **代码级已确认，且未发现越线**（37 处相关文案逐条判定，见 §3）；**「产物 + 截图」仍缺，AC-L11 的 screenshot 半边未闭合** |

> 三条里没有任何一条可以凭本清单标 PASS。AC-09/AC-M09/AC-L11 的 Binding 要求都是「自检清单 **+ screenshot**」，本文件只交付前半。

---

## 1. AC-09 — B 主路径设计自检（层级 / 留白 / 主路径可用性 / 错误态清晰）

Binding 原文（`CUT-2026-10-22-Wan3.0-BINDING.md:69`）：Eng/SWE 设计自检：无未修 P0（层级/留白/主路径可用性/错误态清晰）；Lead 过一眼主路径截图。

### 1.1 主路径可用性 —— 代码级已确认

| 检查点 | 结论 | 依据 |
|--------|------|------|
| 新建页三要素（提示词/模型/分辨率）齐备且可提交 | 代码级已确认 | `HomePage.xaml:25-28`（PromptBox）、`HomePage.xaml:49-53`（ModelCombo）、`HomePage.xaml:54-59`（ResolutionCombo）、`HomePage.xaml:78-80`（GenerateButton） |
| 提交后真的建任务并跳转任务页 | 代码级已确认 | `HomePage.xaml.cs:257-275`：`VideoTaskStore.Add(item)` → `VideoJobRunner.RunAsync` → `Frame.Navigate(typeof(TasksPage), item.Id)` |
| 提交前强制重算就绪态，禁用时直接 return（防误提交） | 代码级已确认 | `HomePage.xaml.cs:246-247`：`RefreshReadyState(); if (!GenerateButton.IsEnabled) return;` |
| 任务页可播可另存 | 代码级已确认 | `TasksPage.xaml:66-67`（MediaPlayerElement）、`TasksPage.xaml.cs:130-148`（ShowPlayer）、`TasksPage.xaml.cs:179-204`（SaveAs） |
| 任务页按钮随选中项启停，不会点空 | 代码级已确认 | `TasksPage.xaml.cs:109-112`（`CanCancel`/`CanRetry`/`HasVideo` 驱动四个按钮） |
| 长视频页「生成所选」有硬性零提交守卫 | 代码级已确认 | `LongVideoPage.xaml.cs:492`：`if (selected.Count == 0 || !GenerateSelectedButton.IsEnabled) return;`（注释即 `hard zero-submit guard`） |

### 1.2 层级（主按钮 vs 状态）—— 代码级已确认 + 需截图补证

| 检查点 | 结论 | 依据 |
|--------|------|------|
| 主按钮用 Accent 样式，与普通按钮区分 | 代码级已确认 | `HomePage.xaml:78-80`（`AccentButtonStyle`）、`LongVideoPage.xaml:50`（导入视频）、`LongVideoPage.xaml:155-156`（生成所选） |
| 分段状态文字不抢主按钮层级（小字号 + 降透明度） | 代码级已确认 | `LongVideoPage.xaml:129`（StatusText `FontSize=12`）、`LongVideoPage.xaml:127`（DurationLabel `Opacity=0.5`）、`LongVideoPage.xaml:23-27`（HintText 统一 `Opacity 0.7` / `FontSize 12`） |
| 当前模式按钮高亮，非当前模式回落默认样式 | 代码级已确认 | `HomePage.xaml.cs:87-100`（`HighlightModeButtons`：on→AccentButtonStyle，off→`ClearValue(StyleProperty)`） |
| **实际视觉层级/字号对比是否成立** | **需截图补证** | 只能靠渲染结果判断；本清单不含实拍 |

### 1.3 留白 —— 需截图补证

| 检查点 | 结论 | 依据 |
|--------|------|------|
| 页面级留白有明确取值、容器有最大宽度防拉满 | 代码级已确认 | `HomePage.xaml:11`（`Padding="24,16,24,16" Spacing="16" MaxWidth="860"`）、`LongVideoPage.xaml:31`（`Padding="24,20,24,32" Spacing="16" MaxWidth="1080"`）、`TasksPage.xaml:10`（`Padding="24,16,24,16" ColumnSpacing="16" RowSpacing="12"`） |
| 卡片统一圆角/描边/内边距 | 代码级已确认 | `LongVideoPage.xaml:12-18`（`CardBorder`：`CornerRadius 10` / `Padding 20` / 1px 描边） |
| 列表项有统一纵向呼吸 | 代码级已确认 | `LongVideoPage.xaml:106`（`Padding="0,6"`）、`TasksPage.xaml:28`（`Padding="0,8"`） |
| **是否存在视觉拥挤 / 大块空白 / 错位** | **需截图补证** | 留白取值合理 ≠ 观感合理，必须看渲染 |

### 1.4 错误态清晰度 —— 代码级已确认

| 检查点 | 结论 | 依据 |
|--------|------|------|
| 缺 Key → 禁用 + 写明去哪补 | 代码级已确认 | `HomePage.xaml.cs:189-194`：`"未就绪：请先到「设置」保存 API Key。"` |
| 缺提示词 → 禁用 + 写明原因 | 代码级已确认 | `HomePage.xaml.cs:195-200`：`"未就绪：请填写提示词。"` |
| 各模式缺素材 → 分别写明缺什么 | 代码级已确认 | `HomePage.xaml.cs:204-227`（三条独立 reason：参考图 / 人物参考+目标素材 / 动作视频+主体图） |
| 提交异常 → InfoBar Error + 原始 message | 代码级已确认 | `HomePage.xaml.cs:277-283` |
| 任务失败 → 红色错误文字进列表项 | 代码级已确认 | `TasksPage.xaml:46`（`Foreground={ThemeResource SystemFillColorCriticalBrush}`）、`TasksPage.xaml.cs:113-115` |
| 分段级失败 → 红色 + 可重试按钮 | 代码级已确认 | `LongVideoPage.xaml:130-133`（Error + `ErrorVisibility`）、`LongVideoPage.xaml:138-139`（重试按钮 `RetryVisibility`） |
| 导入失败 / 切段失败 / 续接失败均有明确 InfoBar | 代码级已确认 | `LongVideoPage.xaml.cs:167`、`:312`、`:754`、`:854` |
| 空态有引导文案，不是纯空白 | 代码级已确认 | `LongVideoPage.xaml:145-147`（SegmentEmpty）、`:193-194`（PoolEmpty）、`TasksPage.xaml:68-69`（PreviewEmpty）、`TasksPage.xaml.cs:102-104`（无任务引导） |

### 1.5 AC-09 发现的两个问题（不计 P0，但需登记）

**P2-A｜生产代码里留了 QA seed 后门。**
`TasksPage.xaml.cs:41-67` 在 `TasksPage_Loaded` 里检查 `Desktop\Wan3.0\qa\seed-running.flag`，存在且任务列表为空时**自动插入一条假 RUNNING 任务**（`RemoteTaskId = "seed-task-ac03"`、`Progress = 0.42`）。
- 依据：`TasksPage.xaml.cs:43-65`。
- 风险：`PLAN §1` 已记录「`EnsureRunningDemo()` 无条件插假行」被删除，但删除只做了一半 —— 带 flag 的版本仍编在发布路径里。只要 flag 文件在，界面就会显示一条不存在的「轮询中」任务，属于 soft-pass 味道。
- 建议：移到 `#if DEBUG` 或独立测试入口；至少不要让 Release 读桌面 flag。

**P3-B｜`HomePage` 的四模式 switch 没有 `default` 分支。**
`HomePage.xaml.cs:54-83` 的 `switch (_mode)` 只覆盖四个模式；`GenerationMode.ContinueFromClip`（`VideoTaskStore.cs:27` 定义）落进来时 `ModeHint` / `MediaPanel` 都不更新。
- 依据：`HomePage.xaml.cs:54-83` vs `VideoTaskStore.cs:19-27`。
- 现状影响：**当前不可触发** —— `_mode` 只由 `ModeButton_Click`（`:40-46`）赋四模式之一，`ContinueFromClip` 只在长视频页创建任务时用（`LongVideoPage.xaml.cs:763`），不进新建页。属防御性缺口，非现网 P0。

---

## 2. AC-M09 — Cut-3 四模式自检（模式切换层级 / 缺素材禁用原因可读）

Binding 原文（`CUT-2026-10-22-Wan3.0-CUT3-MODES.md:54`）：Eng 自检无未修 P0（模式切换层级/留白/禁用原因可读）；Lead 过一眼。

### 2.1 四模式切换层级 —— 代码级已确认 + 需截图补证

| 检查点 | 结论 | 依据 |
|--------|------|------|
| 四个模式按钮同排、等权、间距一致 | 代码级已确认 | `HomePage.xaml:17-22`（`Orientation="Horizontal" Spacing="8"`，四个 Button） |
| 当前模式用 Accent 高亮，互斥 | 代码级已确认 | `HomePage.xaml.cs:87-100`（四个 `Style(...)` 调用互斥） |
| 当前模式有**文字**回述，不只靠颜色 | 代码级已确认 | `HomePage.xaml:23`（`ModeHint`）+ `HomePage.xaml.cs:57/61/68/76`（四条「当前：…」文案） |
| 切模式后立刻重算就绪态 | 代码级已确认 | `HomePage.xaml.cs:47-48`（`ApplyModeUi(); RefreshReadyState();`） |
| **高亮是否真的一眼可见 / 四按钮是否换行挤压** | **需截图补证** | 观感项 |

### 2.2 缺素材禁用原因是否写清 —— 代码级已确认（逐模式核对）

| 模式 | 必填素材 | 禁用原因原文 | 依据 |
|------|----------|--------------|------|
| 文生视频 | 无 | （仅需 Key + 提示词，已在上表覆盖） | `HomePage.xaml.cs:56-59` |
| 图生视频 | 参考图 | `未就绪：图生视频需要选择参考图。` | `HomePage.xaml.cs:204-210` |
| 人物替换 | 人物参考 + 目标素材 | `未就绪：人物替换需要人物参考 + 目标素材。` | `HomePage.xaml.cs:212-218` |
| 动作参考 | 动作视频 + 主体图 | `未就绪：动作参考需要动作视频 + 主体图。` | `HomePage.xaml.cs:220-226` |

补充核对（均代码级已确认）：
- 每个模式切换时 `ModeHint` 同时回述该模式的素材要求与 API 语义（`HomePage.xaml.cs:61` 图生写 `media.type=first_frame`），用户不需要猜。
- 素材选择器的文件类型过滤与模式匹配：动作参考主素材只给视频（`HomePage.xaml.cs:122` → `:155-160`），人物替换目标素材给图+视频（`:131` → `:161-169`），图生只给图（`:170-176`）。
- 切换模式**不清空已选素材**（`ApplyModeUi` 只改可见性与标签，不动 `_primaryPath`/`_secondaryPath`），清空只由「清除素材」触发（`:139-145`）—— 属有意设计，不是 bug。
- 就绪后提示语回述当前模式名：`HomePage.xaml.cs:230`（`就绪：当前模式「{ModeLabel(_mode)}」，点击生成将提交任务。`）。

### 2.3 AC-M09 结论

**模式切换层级 + 缺素材禁用原因：代码级已确认，未发现未修 P0。**
**视觉层级/留白：需截图补证**（`qa\p3-modes-*.png` 已有四张实拍，但那是 01:30 的旧版，本轮 UI 改动后需重拍；且 `p3-modes-*.png` 的 qa/ 副本仍在，可直接复核）。

---

## 3. AC-L11 — 续接与硬拼入口文案不混淆（重点）

Binding 原文（`CUT-2026-10-22-Wan3.0-BINDING.md:80`）：Eng 自检：续接与硬拼入口文案不混淆。
硬线（同文件 `:35`）：**禁止**把 ffmpeg 硬拼默认成「续接」，**禁止**「无缝」话术；硬拼默认关、接缝必须标出。

**检索方式:** 对 `WanStudio\Pages\LongVideoPage.xaml` 与 `.xaml.cs` 全量 grep
`拼接|续接|接缝|无缝|硬拼|首片段|first_clip|衔接|连贯`，共命中 **37 处**（xaml 13 处 / cs 24 处），逐条列出并判定。

### 3.1 LongVideoPage.xaml（13 处，逐条）

| # | 行号 | 原文 | 归属 | 判定 |
|---|------|------|------|------|
| 1 | `:36` | `导入 → 切段（固定秒 / 切镜）→ 预览确认 → 勾选生成 → 进池 → 官方续接。切段与预览只在本地完成，确认前不提交任何生成请求。` | 页面总述 | **守线**。这里的「官方续接」指的是流程末端真实的 first_clip 路径，且同句明确「切段与预览只在本地完成」 |
| 2 | `:189` | `<!-- C. 视频池 + 官方续接 -->` | 注释 | **守线**。注释归属正确（池 + 官方续接卡片） |
| 3 | `:194` | `池为空：生成成功的片段会自动进入这里，之后才能作为官方续接的首片段。` | PoolEmpty 空态 | **守线**。说的是官方续接的前置条件，非硬拼 |
| 4 | `:222` | `设为首片段` | 按钮 | **守线**。中性动作词 |
| 5 | `:231` | `官方续接（media type = first_clip）` | 续接卡片标题 | **守线**。把官方路径与协议字段直接绑在一起，无法误解 |
| 6 | `:232` | `首片段：未选择` | ContinueTarget 默认值 | **守线** |
| 7 | `:244` | `续接提示词` | TextBox Header | **守线**。位于官方续接卡片内部 |
| 8 | `:252` | `官方续接生成` | 续接按钮 | **守线**。按钮名自带「官方」限定 |
| 9 | `:261` | `<!-- D. 本地拼接 -->` | 注释 | **守线**。与「官方续接」并列但分区独立 |
| 10 | `:264` | `本地拼接（可选）` | 卡片标题 | **守线**。标题即写明「本地」「可选」 |
| 11 | `:266` | `用 ffmpeg 把池中片段硬拼成一个文件：这是本地剪辑，不是官方续接，接缝处不保证连贯，也不叫「无缝」。` | 卡片说明 | **守线（最强的一条）**。一句话里同时否掉「官方续接」和「无缝」两个说法，并主动标出接缝不保证连贯 |
| 12 | `:267` | `启用本地拼接（默认关）` | ToggleSwitch Header | **守线**。默认关写进控件标题本身 |
| 13 | `:274` | `拼接所选片段` | 拼接按钮 | **守线**。用「拼接」不用「续接」 |

### 3.2 LongVideoPage.xaml.cs（24 处，逐条）

| # | 行号 | 原文 | 归属 | 判定 |
|---|------|------|------|------|
| 1 | `:24` | `/// generate selected → pool → official first_clip continuation. Local stitch is optional and off by default.` | 类注释 | **守线**。注释明确 local stitch 是 optional + off by default |
| 2 | `:703` | `ShowInfo("不能作为首片段", row.Item.ContinueBlockReason, ...)` | 选首片段失败 | **守线**。拒绝时给出具体原因（见 §3.3） |
| 3 | `:710` | `延续上一段的主体与镜头运动，动作自然衔接，画面稳定。` | 官方续接默认提示词 | **守线，但登记为观察项**。这是喂给续写模型的 prompt，属「期望效果」而非「承诺」；出现在官方续接路径内，不涉及硬拼。见 §3.4 观察项 O-1 |
| 4 | `:713` | `ShowInfo("已选首片段", $"{row.Title} 将作为 first_clip 提交续写。", ...)` | 选首片段成功 | **守线**。点明 first_clip 协议字段 |
| 5 | `:730` | `模型列表里没有支持 first_clip 续写的 i2v 模型，续接入口已禁用。` | 续接禁用原因 | **守线**。同时满足 AC-L10「无该模型时禁用并写原因」 |
| 6 | `:732` | `先在池里选一个片段「设为首片段」。` | 续接禁用原因 | **守线** |
| 7 | `:734` | `请填写续接提示词。` | 续接禁用原因 | **守线** |
| 8 | `:738` | `将用 {model} 提交：media[0].type=first_clip，时长 …s（首段 2–10s，单次总长 2–15s）。` | ContinueHint 就绪 | **守线**。把协议字段与时长窗直接写给用户 |
| 9 | `:741` | `首片段：未选择` | ContinueTarget | **守线** |
| 10 | `:742` | `首片段：{Title} · {SourceLabel}\nURL：{RemoteUrl ?? "（无公网 URL）"}` | ContinueTarget 就绪 | **守线**。显式暴露将提交的 URL，无 URL 时直说 |
| 11 | `:754` | `ShowInfo("无法续接", "first_clip 需要公网可访问的 http(s) URL；本地文件无法直接提交。", ...)` | 续接失败 | **守线**。**关键一条**：明确本地文件不能直接当 first_clip，没有偷换 |
| 12 | `:777` | `ShowInfo("已提交续接",` | 续接提交 | **守线** |
| 13 | `:778` | `官方续接任务已提交（{model}，first_clip）。结果会进「任务」并在成功后入池。` | 续接提交 | **守线**。此处确为真实官方路径，用「官方续接」名实相符 |
| 14 | `:807` | `默认关闭；开启后才会出现池内勾选与拼接。` | 硬拼禁用原因 | **守线**。默认关 + 未开时连勾选都不出现 |
| 15 | `:809` | `至少选 2 段才能拼接。` | 硬拼禁用原因 | **守线** |
| 16 | `:810` | `当前引擎不支持本地拼接（缺 ffmpeg）。` | 硬拼禁用原因 | **守线** |
| 17 | `:813` | `将按池中顺序硬拼 {n} 段，产生 {n-1} 处接缝（本地 ffmpeg，非官方续接）。` | StitchHint 就绪 | **守线（核心）**。执行前就预告接缝数量，并括注「非官方续接」 |
| 18 | `:824` | `拼接中…` | 进度 | **守线** |
| 19 | `:846` | `无接缝` | StitchResult 分支 | **守线（但为死分支）**。见 §3.4 观察项 O-2 |
| 20 | `:847` | `接缝 {n} 处（{h:mm:ss / ...}）` | StitchResult | **守线**。接缝逐处列出时间点 |
| 21 | `:848` | `已生成：{outPath}\n{seamText}。这是本地硬拼，不是官方续接，接缝处不保证连贯。` | StitchResult 结果 | **守线（核心）**。产物落地时再次否认「官方续接」并标接缝 |
| 22 | `:849` | `ShowInfo("拼接完成", $"{n} 段 → 1 个文件，{seamText}。本地硬拼，非官方续接。", ...)` | 拼接完成 InfoBar | **守线** |
| 23 | `:853` | `拼接失败：{ex.Message}` | 失败 | **守线**。失败标红（`InfoBarSeverity.Error` 见 `:854`） |
| 24 | `:854` | `ShowInfo("拼接失败", ex.Message, InfoBarSeverity.Error)` | 失败 | **守线**。满足 AC-L09「失败标红」 |

### 3.3 池卡片绑定的文案（跨文件，但属于 AC-L11 覆盖范围）

池卡片里 `ContinueHint`（`LongVideoPage.xaml:219`）绑的是 `PoolRow.ContinueHint`（`WanStudio\Services\LongVideoProject.cs:167`），实际取值来自 `VideoPoolItem.ContinueBlockReason`（`WanStudio\Services\VideoPool.cs:53-61`）：

| 位置 | 原文 | 判定 |
|------|------|------|
| `LongVideoProject.cs:167` | `可作首片段` / 否则回落 `ContinueBlockReason` | **守线**。只说「可作首片段」，不提硬拼 |
| `VideoPool.cs:58` | `本地导入的片段没有公网 URL，不能作为首片段（first_clip）提交。` | **守线**。主动否认本地文件能当 first_clip |
| `VideoPool.cs:59` | `这个片段没有拿回公网 video_url（例如只存在本地或接口未返回地址），不能作为首片段（first_clip）提交。` | **守线** |

### 3.4 观察项（不构成越线，但登记）

**O-1｜`LongVideoPage.xaml.cs:710` 默认续接提示词含「动作自然衔接」。**
判定：**不算越线**。理由：该字符串是提交给官方续写模型的 prompt 正文，描述的是期望生成效果，且只出现在官方续接路径（`UseAsFirstClip_Click` 内），不会被拼到硬拼卡片上；硬拼路径（`:807-849`）没有任何一处复用这条文案。若 PM 要求「连期望语也不许出现衔接字样」，需改这句，但不属 AC-L11 硬线范围。

**O-2｜`LongVideoPage.xaml.cs:846` 的 `"无接缝"` 是不可达分支。**
依据：`Stitch_Click` 在 `:819` 已守卫 `selected.Count < 2` 直接 return，而 `seams` 在 `:835` 执行 `RemoveAt(seams.Count - 1)` 后，2 段时 `seams.Count == 1`，故 `seams.Count == 0` 永不成立。
判定：**守线（且无害）**，因为不可达。但这条文案若哪天被改成可达，就会变成「宣称无接缝」的越线表述。建议直接删掉该三元分支，只保留「接缝 N 处」，或改成「接缝 0 处（仅 1 段）」。

**O-3｜硬拼开关默认关闭有双重保障。**
依据：UI 侧 `LongVideoPage.xaml:267-268`（`Header="启用本地拼接（默认关）"`，`ToggleSwitch` 默认 `IsOn` 为 false）+ 状态侧 `LongVideoPage.xaml.cs:807`（未开时给出「默认关闭」reason 并禁用按钮）+ 池勾选侧 `:643`/`:789-793`（关闭时 `ShowSelect=false` 且清空已选）。满足 AC-L08「默认关 / 关着零硬拼」。

### 3.5 AC-L11 结论

**未发现文案越线。** 37 处命中里：
- 官方续接路径（`first_clip`）一律自称「官方续接」，且绑定 `media[0].type=first_clip` 协议字段 —— 名实相符。
- 本地硬拼路径一律自称「本地拼接 / 硬拼」，并在 **3 个不同位置**（`xaml:266` 执行前说明、`cs:813` 执行前 hint、`cs:848`/`849` 执行后结果与 InfoBar）主动否认「官方续接」。
- 「无缝」二字全文仅出现在 `xaml:266` 的**否定句**中（`也不叫「无缝」`），无任何一处正面宣称。
- 接缝在**执行前**（`cs:813` 预告 N-1 处）和**执行后**（`cs:847` 逐处列时间点）都被标出，满足「接缝必须标出」。

**但 AC-L11 整体不能标 PASS**：Binding 的 proof 要求是「自检清单 **+ screenshot**」，本文件只交了清单；硬拼产物与产物截图仍缺（`PLAN §2` 已把 AC-L09 记为 PARTIAL，同因）。

---

## 4. 附：本次顺带发现的、超出三条自检但需 Lead 决策的项

**D-1｜AC-L06 的「导入 clip 可作 first_clip」在实现上被否掉。**
- Binding（`CUT-2026-10-22-Wan3.0-BINDING.md:75`）写的是「可选**已生成/导入** clip 为 `first_clip`」。
- 实现（`WanStudio\Services\VideoPool.cs:50`）：`CanContinue => !IsImported && !string.IsNullOrWhiteSpace(RemoteUrl)`，即**导入片段被显式排除**，并在 `:58` 给出拒绝原因「本地导入的片段没有公网 URL」。
- 这不算文案越线，但**AC-L06 只满足了「已生成」那一半**。需要 PM 裁决：是收窄 Binding 为「仅已生成」，还是要 App 支持导入片段（后者还需要一条公网 URL 通路）。
- 已同步写进 `qa\CANONICAL-AC-PACK.md` 文末的 discrepancy 段，避免 QA 拿「池里能选」就当 AC-L06 通过。

**D-2｜AC-L04 的分段生成走的是 `ImageToVideo` 而非独立模式。**
`LongVideoPage.xaml.cs:515` 把每段建成 `Mode = GenerationMode.ImageToVideo`、首帧当 `first_frame` 提交。这与 `LongVideoPage.xaml:98` 的「走图生 first_frame」文案一致，属设计如此；仅提示 QA 在核 AC-L04 时不要以为走了长视频专用接口。

---

## 5. 交付边界（诚实声明）

- 本清单**全部依据代码阅读**，未运行应用、未截图、未访问 8787/8788、未 build、未改 `WanStudio\` 或 `tools\` 下任何文件。
- 凡标 `需截图补证` 的项，需 Lead 在过主路径截图时一并签字，方可闭合 AC-09 / AC-M09。
- AC-L11 的文案守线部分已可闭合；`screenshot` 半边与硬拼产物仍缺，**不得据此标 PASS**。
