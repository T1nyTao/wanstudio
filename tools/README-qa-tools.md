# QA 工具（tools/）

给 `WanStudio` 做验收用的本地替身工具。**不参与产品构建**，只在 QA 时手动启动。

| 文件 | 用途 |
|---|---|
| `tools/mock-dashscope/mock_server.py` | 本地 DashScope 视频合成 API 替身（纯标准库，默认 `127.0.0.1:8787`） |
| `tools/capture-window.ps1` | 抓窗口/桌面的 PNG 截图，供 AC 取证 |
| `tools/mock-dashscope/last-requests.jsonl` | 运行期生成，submit 请求体流水（Authorization 已打码） |
| `tools/mock-dashscope/_selftest.sh` | 冒烟自检脚本，输出直接贴进 QA run log |

---

## 1. mock-dashscope

### 启动

```bash
python tools/mock-dashscope/mock_server.py --port 8787 --fixture fixtures/long-20s.mp4
```

可选参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `--port` | `8787` | 监听端口 |
| `--host` | `127.0.0.1` | 只绑本机，不对外暴露 |
| `--fixture` | `fixtures/long-20s.mp4` | `/files/sample.mp4` 回的字节 |
| `--success-after` | `4` | 第几次轮询翻成 `SUCCEEDED`（默认前 3 次 `RUNNING`） |
| `--log` | `tools/mock-dashscope/last-requests.jsonl` | submit 流水文件 |

Ctrl+C 退出；状态存在内存里，重启即清零。

### 端点

| 方法 | 路径 | 行为 |
|---|---|---|
| POST | `/api/v1/services/aigc/video-generation/video-synthesis` | 提交任务，返回 `task_id` |
| GET | `/api/v1/tasks/<task_id>` | 前 3 次 `RUNNING`，第 4 次起 `SUCCEEDED` + `output.video_url` |
| GET/HEAD | `/files/sample.mp4` | 回 fixture 字节，支持 `Range`（206） |
| GET | `/__requests` | 已收到的全部请求（`count` + `requests[]`） |
| GET | `/__reset` | 清任务计数、请求列表、jsonl |

### 固定错误分支

| 条件 | 响应 |
|---|---|
| 无 `Authorization` | `401 {"code":"InvalidApiKey","message":"No API-key provided."}` |
| `Bearer bad-key`（或空 key） | `401 {"code":"InvalidApiKey","message":"Invalid API-key provided."}` |
| 缺 `X-DashScope-Async: enable` | `400 {"code":"InvalidParameter", ...}` |
| 未知 task_id | `404 {"code":"NotFound", ...}` |

鉴权检查在 Async 头之前，与真实 API 一致（无 key 时优先报 401）。

### 把 App 指向 mock

`Services/AppSettings.cs` 的 `Endpoint` 落到
`%LOCALAPPDATA%\WanStudio\settings.json`，所以有两种方式，**都不需要改代码**：

1. 在 App 设置页把 Endpoint 填 `http://127.0.0.1:8787`；
2. 直接写文件（App 关闭时改，启动后读缓存）：

```json
{ "Endpoint": "http://127.0.0.1:8787" }
```

API Key 随便填一个非 `bad-key` 的字符串即可（如 `sk-qa-test`）；
填 `bad-key` 就是 401 分支，可以用来测错误提示。

`DashScopeClient` 会拼 `<Endpoint>/api/v1/...`，mock 的路径与线上一致，
`video_url` 也由 mock 自己吐 `http://127.0.0.1:8787/files/sample.mp4`，
所以下载/入库流程可以整条走通。

### curl 示例

```bash
B=http://127.0.0.1:8787
S=$B/api/v1/services/aigc/video-generation/video-synthesis
BODY='{"model":"wan3.0-video-prime","input":{"prompt":"小猫在屋顶"},"parameters":{"resolution":"480P","ratio":"adaptive","duration":5}}'

# 401：没 key
curl -s -i -X POST $S -H 'X-DashScope-Async: enable' \
  -H 'Content-Type: application/json' --data "$BODY"

# 401：坏 key
curl -s -i -X POST $S -H 'Authorization: Bearer bad-key' \
  -H 'X-DashScope-Async: enable' -H 'Content-Type: application/json' --data "$BODY"

# 400：漏了 Async 头
curl -s -i -X POST $S -H 'Authorization: Bearer sk-qa-test' \
  -H 'Content-Type: application/json' --data "$BODY"

# 200：正常提交，拿 task_id
curl -s -X POST $S -H 'Authorization: Bearer sk-qa-test' \
  -H 'X-DashScope-Async: enable' -H 'Content-Type: application/json' --data "$BODY"
# -> {"request_id":"mock-1","output":{"task_id":"mock-1","task_status":"PENDING"}}

# 轮询 4 次：RUNNING x3 -> SUCCEEDED
for i in 1 2 3 4; do curl -s $B/api/v1/tasks/mock-1; echo; done

# 下视频（也可以加 -H 'Range: bytes=0-1023' 验 206）
curl -s -o qa/_tmp/sample.mp4 -w 'status=%{http_code} bytes=%{size_download}\n' \
  $B/files/sample.mp4

# 看收到了什么 / 清零
curl -s $B/__requests
curl -s $B/__reset
```

最后一次成功的 submit 请求体会落进 `tools/mock-dashscope/last-requests.jsonl`，
`authorization` 字段固定写 `Bearer ****`，可以直接贴进报告。

---

## 2. capture-window.ps1

### 用法

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/capture-window.ps1 -ProcessName WanStudio -OutFile qa\shot.png

# 整块虚拟屏（多显示器），用于弹窗跑到主窗口外面的情况
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tools/capture-window.ps1 -ProcessName WanStudio -OutFile qa\desktop.png -FullScreen
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `-ProcessName` | `WanStudio` | 进程名，不带 `.exe` |
| `-OutFile` | `qa\shot.png` | 相对当前目录或绝对路径，目录不存在会自动建 |
| `-FullScreen` | 关 | 抓整个虚拟屏，走 `CopyFromScreen` |
| `-DelayMs` | `400` | 前置窗口后等合成的时间 |

### 行为

1. 取进程主窗口句柄；窗口最小化先 `SW_RESTORE`。
2. 前置窗口：`AttachThreadInput` + `BringWindowToTop` + `SetForegroundWindow`
   （单纯 `SetForegroundWindow` 从后台进程调用会被系统忽略）。
3. 几何取 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`，排除不可见 resize 边，
   裁切对得上肉眼所见；失败退 `GetWindowRect`。
4. 抓图先试 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`，可抓被遮挡/后台窗口。
   若 `PrintWindow` 失败**或者返回整块纯色**（WPF/DirectComposition 已知问题），
   自动退 `CopyFromScreen`。
5. 打印 `process / hwnd / foreground / geometry / mode / image=WxH bytes=N / file=`，
   证据里能看出用的是哪条路径。

退出码：`0` 成功，`1` 找不到窗口或窗口矩形为空。

### 注意

- 脚本会**把目标窗口提到前台**，会抢焦点；取证时别和人工操作并行。
- `CopyFromScreen` 抓的是屏幕像素，窗口被别的窗口压住就会拍到遮挡物；
  优先看输出里的 `mode=PrintWindow(...)`。
- 进程名写错时输出 `ERROR no top-level window found for process '...'`。

---

## 3. 冒烟自检

```bash
python tools/mock-dashscope/mock_server.py --port 8787 --fixture fixtures/long-20s.mp4 &
bash tools/mock-dashscope/_selftest.sh
```

`_selftest.sh` 覆盖：401（无 key / 坏 key）、400（缺 Async 头）、submit 取 task_id、
连续 4 次轮询、未知 id 404、video_url 全量下载与 `Range` 206、jsonl 打码、`/__requests` 汇总。

### 坑

- 本机 `curl` 是 Windows 原生 curl，`-o /tmp/x` 和 Git Bash 的 `/tmp` 不是同一个目录；
  脚本里统一用相对路径 `qa/_tmp/`。
- `--port 8787` 被占用时先 `netstat -ano | findstr :8787` 找 PID。
- mock 用 HTTP/1.1 keep-alive，所有响应都带 `Content-Length`；自建脚本别漏读 body。
