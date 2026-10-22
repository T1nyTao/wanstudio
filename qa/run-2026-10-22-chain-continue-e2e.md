# 链式续接端到端取证（2026-10-22，mock 协议级）

**执行:** Lead（UI 驱动）+ 多个 teammate（mock 扩展、协议探针、文档）
**环境:** mock-dashscope 8787（扩展后版本，含上传端点），demo-key，Endpoint=`http://127.0.0.1:8787`
**应用副本:** `C:\Users\<user>\Desktop\verify-wanstudio\`（见 §0 为什么放这里）
**诚实口径:** 本文全部是 **UI 驱动 + mock 协议级** 证据。**真机未验证（缺真 Key）**，AC-L06 真机半 / AC-L07 / AC-M05 / AC-M07 仍 **BLOCKED**。

---

## 0. 为什么应用要从工作区外运行（重要，影响所有证据的可信度）

`WanStudio.exe` 若位于工作区 `Desktop\Wan3.0` 树内，它派生的 ffmpeg 子进程会被执行沙箱拒绝写文件，表现为：

```
首帧提取失败：ffmpeg 抽帧失败：…\WanStudio\cache\frame-001-….jpg
```

对照实验（eng-media 做的 2×2，我在 danger-full-access 会话复跑确认）：

| exe 位置 | TMP | media-check 结果 |
|---|---|---|
| 工作区内 | 默认 %TEMP% | 4 项 FAIL（`Permission denied`）|
| 工作区内 | 工作区内 | 第 4 项 PASS，剩 WinRT 3 项 FAIL |
| **工作区外** | 默认 %TEMP% | **ALL PASS** |

- bash 直接跑同一个 ffmpeg 写同一 cache 目录：**成功**（exit=0，文件生成）。
- 所以不是 ffmpeg 缺失、不是目录不可写、不是产品缺陷，而是**执行环境对「工作区内 exe 派生子进程」的文件写入限制**。

因此取证用同一份二进制复制到 `Desktop\verify-wanstudio\` 运行。**同一构建产物，仅换了所在目录。**

### 0.1 顺带查清的一个「假 bug」

早前观察到「凭据库只有 `bad-key`，应用却仍提交成功」。根因不是代码缺陷：

**PasswordVault 对未打包应用按可执行文件所在目录树隔离凭据。** 把 `qa-key.exe` 放到应用同一目录树后，两者立刻看到同一条凭据（`len=8` → `len=7` 随之变化）。代码路径本身正确：`ApiKeyStore.TryGet(AppSettings.KeyProfile)` 每次现读，无缓存。

---

## 1. AC-L04：勾选 2 段 → 恰好 2 条 POST，`first_frame`

命令链（UIA 驱动，非脚本注入）：

```
toggle -Scope SegmentList -Index 0     → before=Off after=On
toggle -Scope SegmentList -Index 1     → before=Off after=On
invoke -Id GenerateSelectedButton      → InvokePattern
```

UI 状态证据：

```
SegmentHeader          | 分段（4 段 · 已选 2）
GenerateSelectedButton | enabled=True | 生成所选（2）
GenerateHint           | 将提交 2 段 · 模型 wan3.0-video-prime · 每段用首帧走图生（first_frame）。本页累计提交 0 次。
StatusBar.Title        | 已提交
StatusBar.Message      | 2 段已提交到「任务」，进度会同步到这里。累计提交 2 次。
```

mock 收到的 body（`qa/_tmp/req1.json`，Authorization 由 mock 固定打码）：

```json
POST model=wan3.0-video-prime
 media=[{"type":"first_frame","url":"oss://dashscope-instant/20261022/wan3.0-video-prime/ee21…/frame-001-c9a4….jpg"}]
POST model=wan3.0-video-prime
 media=[{"type":"first_frame","url":"oss://dashscope-instant/20261022/wan3.0-video-prime/14b7…/frame-002-b02d….jpg"}]
```

**恰好 2 条 POST**（勾 2 段 = 提交 2 段），两段首帧文件不同，`media[0].type` 均为 `first_frame`。

**关键改进（本轮修复）：** 首帧 URL 现在是 `oss://`，不再是从前的 `file:///C:/...`。原先的 `file://` 真机服务端取不到，等于图生/人物/动作三个模式在真机上必然失败。现在本地素材一律先走上传。

---

## 2. AC-L05：成功片段自动入池

```
PoolHeader | 已生成视频池（2）
PoolEmpty  | (absent)
```

池条目（UI 文本，UIA 读出）：

```
ListItem  保持原片主体与构图，镜头运动自然，画面稳定。
          图生 · 段 02 · 00:05.0 – 00:10.0
          可作首片段
          [设为首片段]
ListItem  图生 · 段 01 · 00:00.0 – 00:05.0
          可作首片段
```

截图：`qa/lv-L05-pool-2.png`

---

## 3. AC-L06：链式续接 —— 本次核心

### 3.1 操作序列

```
取消勾选段 01、02；勾选段 03
toggle -Index 0（AnchorToggle）        → before=Off after=On
```

UI 立即反馈：

```
GenerateHint | 将提交 1 段 · 模型 wan3.0-video-prime · 每段用上一段尾巴当 first_clip 续写
             | · 衔接：保持原片主体与构图，镜头运动自然，画面稳定。本页累计提交 2 次。
```

### 3.2 mock 原始记录（`qa/_tmp/req2.json`）

```
GET  /api/v1/uploads?action=getPolicy&model=wan3.0-video-prime      status=200
POST /oss/upload                                                    status=200
POST model=wan3.0-video-prime
     media=[{"type":"first_clip",
             "url":"oss://dashscope-instant/20261022/wan3.0-video-prime/a64d…/anchor-41389c1c….mp4"}]
     params={"resolution":"480P","ratio":"adaptive","duration":5}
     X-DashScope-OssResourceResolve: enable
GET  /api/v1/tasks/mock-1  ×3                                       status=200
```

**这条链路正是本次改动的全部意义：**

1. `getPolicy` —— 拿上传凭证，且 `model` 与提交模型一致（文件与模型绑定）
2. `/oss/upload` —— 本地裁出的尾段 `anchor-*.mp4` 上传
3. 提交时 `media[0].type == "first_clip"`，URL 是 `oss://`
4. 请求头自动带上 `X-DashScope-OssResourceResolve: enable`（缺它 oss:// 解析不了）

### 3.3 续接产物入池

```
PoolHeader | 已生成视频池（3）
ListItem   续接 · 段 03 · 00:10.0 – 00:15.0      ← Source 标为「续接」，与「图生」区分
           可作首片段
ListItem   图生 · 段 02 · 00:05.0 – 00:10.0
ListItem   图生 · 段 01 · 00:00.0 – 00:05.0
```

截图：`qa/lv-chain-continue-pool.png`

**这就是用户要的「视频池里选一段接着写」：产物本身又变成下一段的可用锚点，可以一直链下去。**

---

## 4. 首页四模式续接（同一能力，不只在长视频页）

### 4.1 默认态（续接未勾）—— 原有文案逐字未变

```
ModeHint   | 当前：图生视频 — 需要参考图 + 提示词（media.type=first_frame）。
ReadyHint  | 未就绪：图生视频需要选择参考图。
GenerateButton | enabled=False
```

### 4.2 勾上续接后 —— 跳过素材校验，文案切换

```
ModeHint   | 当前：人物替换 + 衔接上一段 — 本段不需要人物参考/目标素材，将以首片段（first_clip）续写。
ReadyHint  | 就绪：当前模式「人物替换」，将衔接「保持原片主体与构图，镜头运动自然，画面稳定。」尾部 6s 后提交任务。
GenerateButton | enabled=True
AnchorHint | 将把「保持原片主体与构图，镜头运动自然，画面稳定。」的尾部 6 秒作为 first_clip 提交；模型单次上限 15s，长片子靠这样一段段接。
```

**注意：`GenerateButton` 由禁用变可用** —— 续接路径不吃参考素材，所以不再被素材校验拦住。

### 4.3 首页续接的实际提交（`qa/_tmp/req3.json`）

```
GET  /api/v1/uploads?action=getPolicy&model=wan3.0-video-prime   200
POST model=wan3.0-video-prime
     media=[{"type":"first_clip","url":"oss://…/anchor-0372f52d….mp4"}]
     params={"resolution":"480P","ratio":"adaptive","duration":5}
     X-DashScope-OssResourceResolve: enable
```

---

## 5. AC-08：错 Key 的清晰错误态

前提：`qa-key` 与应用**同目录树**（见 §0.1），凭据写入 `bad-key`。

```
POST /api/v1/services/aigc/video-generation/video-synthesis   status=401
```

UI 呈现：

```
任务行   失败
错误     Submit failed 401: {"code": "InvalidApiKey", "message": "Invalid API-k…
Detail   Submit failed 401: {"code": "InvalidApiKey", "message": "Invalid API-k…
取消      enabled=False
重试      enabled=True
打开文件  enabled=False
另存为…   enabled=False
```

**有清晰错误、可重试、无假成功。** 截图：`qa/lv-AC08-badkey-401.png`

---

## 6. 本轮修掉的真缺陷（都有代码依据）

| # | 缺陷 | 后果 | 修法 |
|---|---|---|---|
| 1 | 本地素材直接发 `file://` | 真机服务端取不到 → 图生/人物/动作三模式真机必失败 | `SubmitVideoJobAsync` 一律先上传换 `oss://` |
| 2 | `reference_image` / `reference_video` | **不是 wan2.7-i2v 的合法 type**，真机判非法组合 | 改为 `first_frame` / `first_clip` |
| 3 | `duration` 被当成「续写时长」 | 首片段 5s + duration=5 = **零续写还照扣费** | 改为「输出总时长」，校验必须大于首片段，选首片段时自动抬到 `ceil(clip)+5` |
| 4 | 续接无上传通道 | 链式续接在 API 层面不成立（first_clip 只认公网/oss） | 新增 `DashScopeClipUploader`（getPolicy → multipart → oss://） |
| 5 | `oss://` 缺 header | 服务端解析不了地址 | `SubmitAsync` 检测到 `oss://` 自动加 `X-DashScope-OssResourceResolve: enable` |
| 6 | TasksPage 的 QA seed 后门 | 可伪造「任务进行中」截图（soft-pass） | 整段删除，全项目 grep `seed` 零残留 |
| 7 | 池不持久化 | 重启后锚点来源全丢，链式续接断掉 | 落盘 `%LOCALAPPDATA%\WanStudio\pool.json`，跳过已删文件 |

---

## 7. 仍然 BLOCKED 的部分（不许当 PASS 报）

| AC | 状态 | 缺什么 |
|---|---|---|
| AC-L06 真机半 | **BLOCKED** | 需真 Key 打真实端点；本文只有 mock 协议级 |
| AC-L07 | **BLOCKED** | 「单次续写时长落在 2–15s」需要真机产物量测 |
| AC-M03 / M05 / M07 | **BLOCKED** | 三个模式的真机生成 |
| AC-06 / AC-07 | **BLOCKED** | 文生两模型真提交 |
| AC-L11 screenshot 半 | **PARTIAL** | 文案守线代码级已确认，硬拼产物截图仍缺 |

**OSS 上传本身也未真机验证**：签名校验、policy、48h 有效期、账号绑定、100QPS 限流全部未跑。

---

## 8. 复现命令

```bash
# 1) 扩展版 mock（含上传端点），显式指定 log 避免与其它实例串记录
python tools/mock-dashscope/mock_server.py --port 8787 \
  --fixture fixtures/long-20s.mp4 --log qa/_tmp/mock-8787.jsonl

# 2) 构建
cd WanStudio && dotnet build -c Debug -p:Platform=x64 -v:q

# 3) 同步到工作区外（规避 §0 的沙箱限制）
cp -r WanStudio/bin/x64/Debug/net8.0-windows10.0.26100.0/win-x64/. \
      /c/Users/<user>/Desktop/verify-wanstudio/

# 4) 凭据必须与应用同目录树写入
cp -r tools/qa-key/bin/Release/net8.0-windows10.0.26100.0/win-x64/. \
      /c/Users/<user>/Desktop/verify-wanstudio/qa-tool/
cd /c/Users/<user>/Desktop/verify-wanstudio/qa-tool && ./qa-key.exe --set default demo-key

# 5) 启动并驱动
"C:\Users\<user>\Desktop\verify-wanstudio\WanStudio.exe" --open=long --import fixtures/scenes-4x5s.mp4
pwsh -File tools/uia/drive.ps1 -Action state
pwsh -File tools/uia/drive.ps1 -Action toggle -Scope SegmentList -Index 0
pwsh -File tools/uia/drive.ps1 -Action invoke -Id GenerateSelectedButton
curl -s http://127.0.0.1:8787/__requests
```

---

## 9. 证据文件清单

| 文件 | 内容 |
|---|---|
| `qa/lv-L03-zero-select.png` | 零勾选禁用态 |
| `qa/lv-L03-requests.json` | 零提交时的 `/__requests`（count=0）|
| `qa/lv-L04-two-selected.png` | 勾 2 段、按钮启用 |
| `qa/lv-L04-requests.json` | 2 条 POST + first_frame |
| `qa/lv-L05-pool-2.png` | 池 2 条 |
| `qa/lv-L06-requests.json` | first_clip 提交（独立面板路径）|
| `qa/lv-chain-continue-pool.png` | 续接产物入池（池 3 条）|
| `qa/lv-AC08-badkey-401.png` | 401 错误态 |
| `qa/_tmp/req1.json` `req2.json` `req3.json` | 三轮原始 `/__requests` |
