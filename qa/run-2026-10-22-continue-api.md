# QA run — Wan3.0 续接协议复验（独立 8788 端口）

**日期：** 2026-10-22 Asia/Shanghai
**执行：** eng-api
**STATUS：** 协议级 PASS / 真机 BLOCKED
**task：** task-2

---

## 1. 环境

| 项 | 值 |
|----|----|
| 工作目录 | `C:\Users\<user>\Desktop\Wan3.0` |
| python | 3.13.13 |
| curl | Windows 原生 curl |
| dotnet | 8.0.425 |
| mock | `tools/mock-dashscope/mock_server.py` |
| mock 实例 | **8788**（本任务自起，任务结束已 kill） |
| fixture | `fixtures/long-20s.mp4`（303365 B） |
| 被测程序 | `tools/api-check/bin/Release/net8.0/api-check.exe`（已构建，未重新 build） |
| 假 Key | `demo-key`（mock 侧视为合法；`bad-key` 视为非法） |

**隔离声明：** 8787 端口全程由 Lead 的 UI 取证 mock 独占（pid 22108，仅用 `netstat` 只读确认过监听，未发任何请求、未 reset）。本任务所有请求只打 8788。

**启动命令（原文）：**

```bash
python tools/mock-dashscope/mock_server.py --port 8788 --fixture fixtures/long-20s.mp4 --log qa/api-8788-requests.jsonl
```

---

## 2. api-check 原始输出

**命令（原文）：**

```bash
./tools/api-check/bin/Release/net8.0/api-check.exe http://127.0.0.1:8788 demo-key
```

第一次运行时控制台按 GBK 解码导致中文乱码，故重置 mock 后以 UTF-8 代码页重跑一次作为**唯一权威捕获**（下为原文全文，无删改）：

```
endpoint=http://127.0.0.1:8788 key=****
task_id=mock-1
poll[0] status=RUNNING url=
poll[1] status=RUNNING url=
poll[2] status=RUNNING url=
poll[3] status=SUCCEEDED url=http://127.0.0.1:8788/files/sample.mp4
--- local path guard ---
guard OK: first_clip 需要公网可访问 URL（http/https），本地文件（file:// 或盘符路径）不能直接提交，请先上传到可公网访问的地址。当前值：C:\tmp\a.mp4
```

落盘：`qa/api-check-8788-stdout.txt`（UTF-8，8 行）

**读法：** submit 返回 `task_id=mock-1`；前 3 次轮询 `RUNNING`，第 4 次 `SUCCEEDED` 并带回 `video_url`（mock `--success-after` 默认 4）；`api-check` 自带的盘符路径守卫用例被正确拒绝。

---

## 3. first_clip 请求体原文 JSON

**捕获命令（原文）：**

```bash
curl -s -m 5 http://127.0.0.1:8788/__requests -o qa/api-8788-requests.json
```

本次 submit 共捕获 **5 条**请求：`POST …/video-synthesis → 200`，随后 `GET /api/v1/tasks/mock-1 → 200` ×4（第 4 次拿到 SUCCEEDED 后 break）。

**该 POST 的头部（mock 记录值）：**

```
status            = 200
authorization     = Bearer ****      <- 打码，mock 侧统一写入（mock_server.py:39）
x_dashscope_async = enable
content_type      = application/json; charset=utf-8
task_id           = mock-1
ts                = 2026-10-22T02:15:14+0800
```

**请求体原文（`qa/api-8788-first-clip-body.json`）：**

```json
{
  "model": "wan2.7-i2v",
  "input": {
    "prompt": "延续上一段的主体与镜头运动，动作自然衔接。",
    "media": [
      {
        "type": "first_clip",
        "url": "http://127.0.0.1:8787/files/sample.mp4"
      }
    ]
  },
  "parameters": {
    "resolution": "480P",
    "ratio": "adaptive",
    "duration": 5
  }
}
```

**关于 body 里出现 8787 的说明（必读）：** 该 URL 是 `tools/api-check/Program.cs` 里写死的 fixture 地址，**只是请求体里的一个字符串**。mock 只把 body 原样记录（`mock_server.py:204`），**没有任何出站拉取逻辑**（全文 grep 出站客户端 `urllib|urlopen|http.client|socket.create_connection` 零命中；`requests.` 的命中全部是 `STATE.requests` 这个内存列表的增删，不是 HTTP 库），因此本次复验**没有产生任何到 8787 的流量**。同时第 5 节的 http 对照用例用的是 `http://127.0.0.1:8788/files/sample.mp4` 并被接受，证明守卫放行的是"http/https 形式"，与 8787 这个字面值无关。

**一行摘要：** `model=wan2.7-i2v, media[0]={type:"first_clip", url:"http://127.0.0.1:8787/files/sample.mp4"}, parameters={480P, adaptive, 5s}, X-DashScope-Async=enable, Authorization=Bearer ****`。

---

## 4. 三条负路径响应

三条均在 `GET /__reset` 清空状态后逐一实测，请求体统一为含 `first_clip` 的最小合法 body。

| # | 构造 | 期望 | 实测 HTTP | 响应体原文 |
|---|------|------|-----------|------------|
| 1 | 不带 `Authorization` | 401 | **401** | `{"code": "InvalidApiKey", "message": "No API-key provided."}` |
| 2 | `Authorization: Bearer bad-key` | 401 | **401** | `{"code": "InvalidApiKey", "message": "Invalid API-key provided."}` |
| 3 | 带 `Bearer demo-key`，**缺** `X-DashScope-Async` | 400 | **400** | `{"code": "InvalidParameter", "message": "X-DashScope-Async header is required and must be 'enable'."}` |

**结论：三条全部符合预期。**

对应 mock 实现：`mock_server.py:159-168`（无 auth → 401）、`:170-177`（空 key / `bad-key` → 401）、`:180-189`（缺 async 头 → 400）。注意 auth 闸门在 async 头之前（`:161` 注释："the real API answers 401 before it looks at headers"），所以第 3 条必须先带合法 key 才能打到 400 分支——这也是本表第 3 条用 `demo-key` 而非无 key 的原因。

落盘：`qa/api-neg1-noauth.json`、`qa/api-neg2-badkey.json`、`qa/api-neg3-noasync.json`

---

## 5. 代码核对

### 5.1 `SubmitContinueAsync` 生成的 `media[0].type` 确为 `first_clip`

`WanStudio/Services/DashScopeClient.cs:157` 起：

- **`:167`** `RequirePublicUrl(firstClipUrl, "first_clip");` —— 发请求前先过守卫
- **`:169-172`**
  ```csharp
  var media = new List<object>
  {
      new { type = "first_clip", url = firstClipUrl }
  };
  ```
  → `type` 是**字面量 `first_clip`**，不是变量、不是从枚举映射来的，不存在写错成 `first_frame` 之类的可能。
- **`:174-178`** `last_frame` 为可选第二项，同样先过 `RequirePublicUrl`；本次调用 `lastFrameUrl: null`，所以 `media` 只有 1 项 —— 与第 3 节实测 body 一致。
- **`:180-194`** 组装 `model` / `input.prompt` / `input.media` / `parameters{resolution,ratio,duration}`，与实测 body 逐字段对齐。
- **`:196`** `return await SubmitAsync(body, ct);`
- **`:217-222`** `SubmitAsync` 打到 `{_baseUrl}/api/v1/services/aigc/video-generation/video-synthesis`，显式加 `X-DashScope-Async: enable`，UTF-8 JSON body。

调用方：`WanStudio/Services/VideoJobRunner.cs:54-68`，`GenerationMode.ContinueFromClip` 分支取出 `item.FirstClipUrl`，为空时 `:57-58` 先抛"续接任务缺少首片段 URL（first_clip）"，否则 `:60-68` 调 `SubmitContinueAsync`。

**核对结果：`media[0].type == "first_clip"`，代码级已确认 + 实测请求体已确认（双重）。**

### 5.2 `RequirePublicUrl` 是否真的拒绝 `file://` 与盘符路径

`WanStudio/Services/DashScopeClient.cs:199-214`：

- **`:201-202`** 空白 → 抛"当前为空"
- **`:204-206`** `looksLocal` 三个判据：`file:` 前缀（忽略大小写）、`\\` UNC 前缀、`[字母]:` 盘符形式
- **`:208-213`** `looksLocal || 不是 http(s) 前缀` → 抛异常，文案含"本地文件（file:// 或盘符路径）不能直接提交"，并回显当前值

**实测（4 个用例，直接反射调用 api-check 已编译产物里的真实 `DashScopeClient`，未重新 build）：**

脚本 `qa/api-guard-test.ps1`，命令 `pwsh -NoProfile -File qa/api-guard-test.ps1`，原始输出：

```
file://        REJECTED [InvalidOperationException] first_clip 需要公网可访问 URL（http/https），本地文件（file:// 或盘符路径）不能直接提交，请先上传到可公网访问的地址。当前值：file:///C:/tmp/a.mp4
drive-letter   REJECTED [InvalidOperationException] first_clip 需要公网可访问 URL（http/https），本地文件（file:// 或盘符路径）不能直接提交，请先上传到可公网访问的地址。当前值：C:\tmp\a.mp4
UNC            REJECTED [InvalidOperationException] first_clip 需要公网可访问 URL（http/https），本地文件（file:// 或盘符路径）不能直接提交，请先上传到可公网访问的地址。当前值：\\server\share\a.mp4
http-control   ACCEPTED -> mock-1
```

落盘：`qa/api-guard-test-out.txt`

- `file://` → 拒绝 ✅
- 盘符 `C:\tmp\a.mp4` → 拒绝 ✅（与第 2 节 api-check 自带用例结论一致）
- UNC `\\server\share\a.mp4` → 拒绝 ✅（额外覆盖，代码 `:205` 的分支）
- `http://` 对照 → **被接受** ✅（证明守卫不是"一刀切拒绝所有输入"，而是真的按 http/https 放行）

**核对结果：代码级已确认 + 实测已确认。**

---

## 6. 结论与 BLOCKED 声明

**结论：这是协议级证据，不是真机生成证据。**

已证明的是**客户端请求体形状正确**：

1. 续接走的是官方 `video-synthesis` 异步提交端点，`media[0].type` 字面量为 `first_clip`，`Authorization` 为 `Bearer`，`X-DashScope-Async: enable`。
2. 公网 URL 守卫真实生效：`file://`、盘符、UNC 三类本地路径在发请求**之前**就被 `RequirePublicUrl` 拦下，http/https 放行。
3. 认证/异步头三条负路径（401 / 401 / 400）与真实 DashScope 的先后顺序一致。

**仍然 BLOCKED（缺真 Key，本轮无法推进）：**

- **AC-L06 的真机 half：BLOCKED。** 本报告只覆盖"提交了正确的 `first_clip` 请求"，**没有**覆盖真机 `SUCCEEDED` 且输出里含原片段这一半。
- **AC-L07 的时长/带宽：BLOCKED。** mock 返回的 `duration=5`、`sample.mp4` 是我方 fixture，**不能**用来断言"单次续写时长落在 2–15s 文档窗内"。
- **`media[]` 字段在该 host 上是否被服务端接受：未验证。** 本条需要真 Key 打到真实端点才能下结论，本轮 mock 的 200 只证明"我方发得对"，不证明"对面收得下"。

**未越界声明：** 全程未修改 `WanStudio/` 下任何文件、未在主目录 build、未 `Stop-Process WanStudio`、未访问/重置 8787。报告内不含任何真 Key，`demo-key` 为假 Key。8788 mock 已在收尾时 kill。

---

## 附：本轮证据文件

| 文件 | 内容 |
|------|------|
| `qa/api-check-8788-stdout.txt` | api-check 原始输出全文（UTF-8） |
| `qa/api-8788-requests.json` | 8788 `/__requests` 原始捕获（5 条） |
| `qa/api-8788-first-clip-body.json` | first_clip 请求体（格式化，Authorization 未落盘） |
| `qa/api-8788-requests.jsonl` | mock `--log` 追加日志 |
| `qa/api-neg1-noauth.json` / `api-neg2-badkey.json` / `api-neg3-noasync.json` | 三条负路径响应体 |
| `qa/api-guard-test.ps1` / `qa/api-guard-test-out.txt` | 路径守卫 4 用例脚本与原始输出 |

```yaml
status: PROTOCOL_PASS
protocol_first_clip: PASS
guard_local_paths: PASS
neg_paths: PASS
ac_l06_real_device: BLOCKED
ac_l07_duration: BLOCKED
media_field_accepted_by_host: UNVERIFIED
port_used: 8788
port_8787_touched: false
run_id: run-2026-10-22-continue-api
```
