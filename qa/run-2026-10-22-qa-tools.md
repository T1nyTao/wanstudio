# QA 工具自检 — 2026-10-22

范围：只做测试工具（`tools/`），**不碰 app 代码，不给 AC 结论**。
AC-L01/L02/L03/L05、AC-03/04/08 的正式取证等长视频页编译通过后再跑。

## 0. 环境

```
$ date "+%Y-%m-%d %H:%M:%S"
2026-10-22 01:48:13

$ python --version
Python 3.13.14

OS   : Windows 11 Pro (win32)
shell: Git Bash + Windows PowerShell 5.1
curl : 系统自带 Windows curl（注意 /tmp 与 Git Bash 的 /tmp 不是同一路径）
```

新增文件：

```
tools/mock-dashscope/mock_server.py      DashScope 视频合成 mock（纯标准库）
tools/mock-dashscope/_selftest.sh        冒烟自检脚本
tools/capture-window.ps1                 窗口/桌面截图
tools/README-qa-tools.md                 启动方式 + curl 示例
qa/run-2026-10-22-qa-tools.md            本文件
qa/_selftest-raw.txt                     自检原始输出（未编辑副本）
qa/_capture-selftest.png                 截图脚本实测产物
```

---

## 1. mock 服务启动

```
$ python tools/mock-dashscope/mock_server.py --port 8787 --fixture fixtures/long-20s.mp4
[mock] DashScope mock listening on http://127.0.0.1:8787
[mock] submit : POST /api/v1/services/aigc/video-generation/video-synthesis
[mock] poll   : GET  /api/v1/tasks/<task_id>  (1-3 RUNNING, 4+ SUCCEEDED)
[mock] fixture: C:\Users\<user>\Desktop\Wan3.0\fixtures\long-20s.mp4 (303365 bytes) at /files/sample.mp4
[mock] log    : C:\Users\<user>\Desktop\Wan3.0\tools\mock-dashscope\last-requests.jsonl
```

`fixtures/long-20s.mp4` 存在（303365 bytes），启动无 WARNING。

---

## 2. 端到端自检原始输出

```
$ bash tools/mock-dashscope/_selftest.sh
### [0] reset
{"ok": true, "reset": true}

### [1] POST submit without Authorization
HTTP/1.1 401 Unauthorized
Content-Type: application/json; charset=utf-8

{"code": "InvalidApiKey", "message": "No API-key provided."}

### [2] POST submit with Bearer bad-key
HTTP/1.1 401 Unauthorized

{"code": "InvalidApiKey", "message": "Invalid API-key provided."}

### [3] POST submit with key but missing X-DashScope-Async
HTTP/1.1 400 Bad Request

{"code": "InvalidParameter", "message": "X-DashScope-Async header is required and must be 'enable'."}

### [4] POST submit happy path
raw: {"request_id": "mock-1", "output": {"task_id": "mock-1", "task_status": "PENDING"}}
task_id=mock-1

### [5] poll GET /api/v1/tasks/mock-1 x4
poll#1 -> {"request_id": "mock-1", "output": {"task_id": "mock-1", "task_status": "RUNNING"}}
poll#2 -> {"request_id": "mock-1", "output": {"task_id": "mock-1", "task_status": "RUNNING"}}
poll#3 -> {"request_id": "mock-1", "output": {"task_id": "mock-1", "task_status": "RUNNING"}}
poll#4 -> {"request_id": "mock-1", "output": {"task_id": "mock-1", "task_status": "SUCCEEDED", "video_url": "http://127.0.0.1:8787/files/sample.mp4"}, "usage": {"video_count": 1, "duration": 5}}

### [6] poll unknown task id
HTTP/1.1 404 Not Found

{"code": "NotFound", "message": "Task does-not-exist does not exist."}

### [7] fetch video_url (full)
status=200 bytes=303365 type=video/mp4
local bytes=303365
byte-identical to fixtures/long-20s.mp4

### [8] fetch video_url (Range: bytes=0-1023)
status=206 bytes=1024
Accept-Ranges: bytes
Content-Length: 1024
Content-Range: bytes 0-1023/303365

### [9] last-requests.jsonl (Authorization masked)
{"ts": "2026-10-22T01:46:53+0800", "method": "POST", "path": "/api/v1/services/aigc/video-generation/video-synthesis", "status": 200, "authorization": "Bearer ****", "x_dashscope_async": "enable", "content_type": "application/json", "task_id": "mock-1", "body": "{\"model\":\"wan3.0-video-prime\",\"input\":{\"prompt\":\"qa selftest cat\"},\"parameters\":{\"resolution\":\"480P\",\"ratio\":\"adaptive\",\"duration\":5}}"}

### [10] GET /__requests
count=9
  POST /api/v1/services/aigc/video-generation/video-synthesis                 -> 401  auth=None
  POST /api/v1/services/aigc/video-generation/video-synthesis                 -> 401  auth=Bearer ****
  POST /api/v1/services/aigc/video-generation/video-synthesis                 -> 400  auth=Bearer ****
  POST /api/v1/services/aigc/video-generation/video-synthesis                 -> 200  auth=Bearer ****
  GET /api/v1/tasks/mock-1                                                   -> 200  auth=None
  GET /api/v1/tasks/mock-1                                                   -> 200  auth=None
  GET /api/v1/tasks/mock-1                                                   -> 200  auth=None
  GET /api/v1/tasks/mock-1                                                   -> 200  auth=None
  GET /api/v1/tasks/does-not-exist                                           -> 404  auth=None
```

判定要点（仅工具自检，非 AC）：

- 401 两条消息文本与任务约定逐字一致：`No API-key provided.` / `Invalid API-key provided.`。
- 鉴权先于 `X-DashScope-Async` 检查：无 key 时报 401 而不是 400。
- `task_id` 从 `output.task_id` 取到，与 `DashScopeClient.SubmitTextToVideoAsync` 的解析路径一致。
- 轮询 1→3 `RUNNING`、第 4 次 `SUCCEEDED` + `video_url`，不会提前终态。
- `video_url` 全量 200 拿到 303365 bytes 且与 fixture **逐字节一致**，可被 `DownloadVideoAsync` 落盘。
- `Range` 返回 206 + `Content-Range: bytes 0-1023/303365`（播放器拖动/断点续传场景不会 200 全量）。
- jsonl 里 `authorization` 固定 `Bearer ****`，可原样贴报告；body 是原始 JSON 请求体。
- `/__requests` 记录到 9 条：4 次 POST（401/401/400/200）+ 4 次轮询 + 1 次未知 id 404。

---

## 3. 截图脚本实测

`WanStudio` 当前没在运行（进程列表无该窗口），为避免干扰正在编译的长视频页，
本次用现有窗口 `Grok Bot` 验证脚本机制；正式 AC 取证时把 `-ProcessName` 换成 `WanStudio`。

### 3.1 PrintWindow 主路径

```
$ powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-window.ps1 `
    -ProcessName "Grok Bot" -OutFile qa\_capture-selftest.png
[capture] process=Grok Bot pid=126740 hwnd=0x640BB4 title='Grok Bot'
[capture] foreground=True
[capture] geometry=DwmGetWindowAttribute(extended frame bounds)
[capture] target=window x=800 y=295 w=1556 h=957
[capture] mode=PrintWindow(PW_RENDERFULLCONTENT=2)
[capture] image=1556x957 bytes=210451
[capture] file=C:\Users\<user>\Desktop\Wan3.0\qa\_capture-selftest.png
```

产物 `qa/_capture-selftest.png`（210451 bytes）人工核对：窗口完整、无黑边、文字可读，
说明 `PrintWindow(2)` 对 Chromium/WPF 混合客户端能出真实像素，不是纯色占位。

### 3.2 CopyFromScreen 兜底路径（-FullScreen）

```
$ powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-window.ps1 `
    -ProcessName "Grok Bot" -OutFile qa\_capture-selftest-fullscreen.png -FullScreen
[capture] process=Grok Bot pid=126740 hwnd=0x640BB4 title='Grok Bot'
[capture] foreground=True
[capture] geometry=DwmGetWindowAttribute(extended frame bounds)
[capture] target=virtual-screen x=0 y=0 w=2560 h=1440
[capture] mode=CopyFromScreen
[capture] image=2560x1440 bytes=2777930
[capture] file=C:\Users\<user>\Desktop\Wan3.0\qa\_capture-selftest-fullscreen.png
```

2560x1440 = 整块虚拟屏，兜底分支可用。该文件仅作分支验证，已删除，不留证据噪音。

### 3.3 错误分支（进程不存在）

```
$ powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-window.ps1 `
    -ProcessName "NoSuchAppXyz" -OutFile qa\_x.png
[capture] ERROR no top-level window found for process 'NoSuchAppXyz'.
[capture] hint: is the app running unpackaged as WanStudio.exe?
exitcode=1
```

退出码 1，不产生文件——夹具脚本里可直接用 `$LASTEXITCODE` 判定。

---

## 4. 把 App 指向 mock（留给下一轮 AC 取证用）

`Services/AppSettings.cs:76` 的 `Endpoint` 落在
`%LOCALAPPDATA%\WanStudio\settings.json`，写 `{"Endpoint":"http://127.0.0.1:8787"}`
或在设置页改 Endpoint 即可，**不需要改代码**。API Key 填 `sk-qa-test` 走成功路径，
填 `bad-key` 走 401 路径。详见 `tools/README-qa-tools.md` 第 1 节。

---

## 5. 已知限制 / 待办

1. mock 状态只在内存：重启后 `task_id` 从 `mock-1` 重新计数，旧 `task_id` 变 404。
   这正是「未知 id」分支的来源，符合预期。
2. 任务计数按 `task_id` 独立累计，`/__reset` 才能回到 `RUNNING` 首轮；
   多任务并行时每个任务各自 3 次 RUNNING，不会互相污染。
3. `--success-after` 可调，但默认 4 已满足「前 3 次 RUNNING」的约定，AC 取证保持默认。
4. `capture-window.ps1` 会抢焦点；取证时不要和人工操作并行，否则 `CopyFromScreen`
   会拍到遮挡物（此时 `mode=` 行会暴露用的是兜底路径）。
5. 本文件**不含任何 AC 通过/不通过的结论**——工具就绪，等长视频页编译完成后开跑。
