# 上传通道协议复验：mock 扩展 + upload-check 探针

**日期**：2026-10-22
**执行者**：eng-upload（teammate）
**端口**：8788（8787 被 Lead 的 UI 取证独占，全程未访问、未 reset）
**结论口径（先写在最前面）**：本报告是**协议级证据**，不是真机上传证据。所有请求都打在本地 mock 上，没有触碰阿里云真实端点，也没有真 Key。**真机上传仍 BLOCKED（缺真 Key）**。

---

## 1. 改了什么

### 1.1 `tools/mock-dashscope/mock_server.py`（扩展，保持纯标准库）

新增端点：

| 端点 | 作用 |
| --- | --- |
| `GET /api/v1/uploads?action=getPolicy&model=<model>` | 返回临时上传凭证 `data{upload_dir, oss_access_key_id, signature, policy, x_oss_object_acl, x_oss_forbid_overwrite, upload_host}`，`upload_host` 指向本 mock 自己的 `/oss/upload` |
| `POST /oss/upload` | 解析 `multipart/form-data`，记录 key / filename / 字节数 / 字段清单，返回 **200 空体**（对应 `success_action_status=200`） |

新增校验（`video-synthesis`）：

- `media[].url` 以 `oss://` 开头且缺 `X-DashScope-OssResourceResolve: enable` → **400**，错误信息里点名缺哪个 header 以及是哪条 url。

新增记录（`/__requests`）：

- `x_dashscope_oss_resource_resolve`：每次请求的该 header 原值（含 null）
- `policy_model` / `policy_upload_dir`：getPolicy 请求
- `oss_upload`：`{key, filename, bytes, content_type, field_names, field_count, oss_access_key_id, x_oss_object_acl, x_oss_forbid_overwrite, success_action_status}`
- `oss_keys_uploaded`：提交时对每条 `oss://` url 标注该 key 是否真被本 mock 收过（`true/false`），用于区分「真跑通上传」和「手写一个 oss:// 假地址」
- `oss_resolve_required_for`：被 400 拦下的 url 列表
- `authorization` 仍然一律 `Bearer ****` 打码

实现要点（都是踩过坑才写对的）：

1. **multipart 解析自己写**（`cgi` 在 3.13 已移除），按 boundary 切分后解析每段的 `Content-Disposition`。
2. **`name`/`filename` 必须兼容带引号和不带引号两种形态**。`.NET Core 3.0+` 起 `MultipartFormDataContent` **不再给 name/filename 加引号**（发 `form-data; name=OSSAccessKeyId`），而 `curl -F` 仍然带引号。第一版正则只认引号形态，导致 8 个字段全部解析成 `null`、上传被 400 拒掉。修正后用 `(?:^|[;\s])name=(?:"([^"]*)"|([^;\s]+))`，前置 `[;\s]` 是为了不让 `name=` 误匹配到 `filename=` 内部。
3. **支持 chunked transfer**。`DashScopeClipUploader` 用 `StreamContent` 上传，长度未知 → .NET 自动改 `Transfer-Encoding: chunked`。mock 的 `_read_body()` 先看 `Transfer-Encoding`，命中则走 `_read_chunked()`（读 size 行 → 读数据 → 吃 CRLF → 遇 0 块排空 trailer）。只按 `Content-Length` 读会在真实客户端上读到 0 字节。

### 1.2 `tools/upload-check/`（新建 .NET 8 控制台探针）

参数：`<baseUrl> <apiKey> <localFile>`，默认 `http://127.0.0.1:8788 demo-key fixtures/long-20s.mp4`。

流程：`getPolicy` → multipart 上传 → 得 `oss://` → 带 `X-DashScope-OssResourceResolve: enable` 提交 `media=[{type:"first_clip",url:"oss://..."}]` → 轮询到终态 → 跑负路径（不带 header，期望 400）。

两个刻意的设计决定：

- **上传用 `StreamContent`**，和 `DashScopeClipUploader` 完全一致，从而覆盖 chunked 这条真实路径。用 `ByteArrayContent` 探针会静默跳过它。
- **不引用 `WanStudio/` 任何源码**。第一版像 `tools/api-check` 那样 include `DashScopeClient.cs`，结果编译失败：该文件正被其他成员编辑（新加了 `using WanStudio.Services.Media;`，而 `Media` 命名空间不在编译单元里）。探针要测的是线协议，不该被并发编辑拖累，因此改为裸 `HttpClient` 独立编译。

---

## 2. 实测输出

### 2.1 启动 mock（8788，独立日志，不污染 Lead 的证据文件）

```
python tools/mock-dashscope/mock_server.py --port 8788 \
  --fixture fixtures/long-20s.mp4 --log /tmp/wan30-bak/upload-8788.jsonl
```

```
[mock] DashScope mock listening on http://127.0.0.1:8788
[mock] submit : POST /api/v1/services/aigc/video-generation/video-synthesis
[mock] poll   : GET  /api/v1/tasks/<task_id>  (1-3 RUNNING, 4+ SUCCEEDED)
[mock] policy : GET  /api/v1/uploads?action=getPolicy&model=<model>
[mock] upload : POST /oss/upload  (multipart/form-data, OSS form POST)
[mock] fixture: C:\Users\<user>\Desktop\Wan3.0\fixtures\long-20s.mp4 (303365 bytes) at /files/sample.mp4
[mock] log    : C:\Users\<user>\AppData\Local\Temp\wan30-bak\upload-8788.jsonl
```

> 注：默认 `--log` 是 `tools/mock-dashscope/last-requests.jsonl`，与 Lead 的 8787 实例共用。这里显式指定了独立路径，避免污染 Lead 的请求计数证据。

### 2.2 upload-check 全流程（命令与原始输出）

```
tools\upload-check\bin\Release\net8.0\upload-check.exe http://127.0.0.1:8788 demo-key fixtures/long-20s.mp4
```

```
baseUrl=http://127.0.0.1:8788 key=**** file=fixtures/long-20s.mp4
local file=C:\Users\<user>\Desktop\Wan3.0\fixtures\long-20s.mp4 bytes=303365

=== step 1: getPolicy ===
GET http://127.0.0.1:8788/api/v1/uploads?action=getPolicy&model=wan2.7-i2v -> 200
{"request_id": "policy-8e99cd79", "data": {"upload_dir": "dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e", "oss_access_key_id": "MOCK-OSS-AK-NOT-A-REAL-CREDENTIAL", "signature": "MOCK-SIGNATURE-NOT-A-REAL-CREDENTIAL", "policy": "MOCK-POLICY-BASE64-NOT-A-REAL-CREDENTIAL", "x_oss_object_acl": "private", "x_oss_forbid_overwrite": "true", "upload_host": "http://127.0.0.1:8788/oss/upload"}}
upload_dir  = dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e
upload_host = http://127.0.0.1:8788/oss/upload
oss key     = dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e/long-20s.mp4

=== step 2: multipart upload to upload_host ===
multipart fields: OSSAccessKeyId, Signature, policy, x-oss-object-acl, x-oss-forbid-overwrite, key, success_action_status, file
file part: name=file filename=long-20s.mp4 bytes=303365 (streamed -> chunked transfer)
POST http://127.0.0.1:8788/oss/upload -> 200 OK body=<empty>
temporary url = oss://dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e/long-20s.mp4

=== step 3: submit video-synthesis with oss:// (X-DashScope-OssResourceResolve: enable) ===
request body: {"model":"wan2.7-i2v","input":{"prompt":"\u5EF6\u7EED\u4E0A\u4E00\u6BB5\u7684\u4E3B\u4F53\u4E0E\u955C\u5934\u8FD0\u52A8\uFF0C\u52A8\u4F5C\u81EA\u7136\u8854\u63A5\u3002","media":[{"type":"first_clip","url":"oss://dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e/long-20s.mp4"}]},"parameters":{"resolution":"480P","ratio":"adaptive","duration":5}}
-> 200 OK
{"request_id": "mock-1", "output": {"task_id": "mock-1", "task_status": "PENDING"}}

=== step 4: poll until terminal ===
task_id=mock-1
poll[1] status=RUNNING url=
poll[2] status=RUNNING url=
poll[3] status=RUNNING url=
poll[4] status=SUCCEEDED url=http://127.0.0.1:8788/files/sample.mp4

=== negative path: oss:// WITHOUT X-DashScope-OssResourceResolve (expect 400) ===
-> 400 BadRequest
{"code": "InvalidParameter", "message": "Header 'X-DashScope-OssResourceResolve: enable' is required when media[].url uses the oss:// scheme, but it was missing or not 'enable'. Offending url(s): oss://dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e/long-20s.mp4"}
negative path OK: 400 as expected
```

`exit code = 0`

### 2.3 multipart 字段清单（mock 侧实际收到的）

```json
{
  "key": "dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e/long-20s.mp4",
  "filename": "long-20s.mp4",
  "bytes": 303365,
  "content_type": "application/octet-stream",
  "field_names": ["OSSAccessKeyId", "Signature", "policy", "x-oss-object-acl",
                  "x-oss-forbid-overwrite", "key", "success_action_status", "file"],
  "field_count": 8,
  "oss_access_key_id": "MOCK-OSS-AK-NOT-A-REAL-CREDENTIAL",
  "x_oss_object_acl": "private",
  "x_oss_forbid_overwrite": "true",
  "success_action_status": "200"
}
```

字节数 303365 与 `fixtures/long-20s.mp4` 完全一致，说明 chunked 上传体是完整送达的，不是截断的。

### 2.4 提交请求体与 oss:// URL

`oss://` URL：

```
oss://dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e/long-20s.mp4
```

提交请求体（`media[0].type` 确为 `first_clip`）：

```json
{"model":"wan2.7-i2v","input":{"prompt":"延续上一段的主体与镜头运动，动作自然衔接。","media":[{"type":"first_clip","url":"oss://dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e/long-20s.mp4"}]},"parameters":{"resolution":"480P","ratio":"adaptive","duration":5}}
```

mock 侧记录的 header 与 key 归属：

```
--- POST /api/v1/services/aigc/video-generation/video-synthesis -> 200
   oss_keys_uploaded: {"oss://...long-20s.mp4": true}
   auth: Bearer **** | resolve hdr: enable
```

`true` 表示这个 key 确实先被 `/oss/upload` 收过，不是凭空写出来的地址。

### 2.5 负路径：缺 `X-DashScope-OssResourceResolve` → 400（符合预期）

```
-> 400 BadRequest
{"code": "InvalidParameter", "message": "Header 'X-DashScope-OssResourceResolve: enable' is required when media[].url uses the oss:// scheme, but it was missing or not 'enable'. Offending url(s): oss://dashscope-instant/20261022/wan2.7-i2v/bf2c5abc2c94432981940d66a2b7df6e/long-20s.mp4"}
```

**结论：符合预期。** 400 且 message 明确点名缺的是 `X-DashScope-OssResourceResolve: enable`，并回显了触发的那条 url。

---

## 3. 最强的一条证据：WanStudio 真实上传实现驱动本 mock

探针自己发的请求毕竟是探针。为了证明 mock 满足 **App 自己的** `DashScopeClipUploader` 契约，把 `WanStudio/Services/Media/ClipUploader.cs`（只读引用，不改动）编进一个**临时目录**的验证项目，用真实实现打 mock：

```
=== REAL WanStudio DashScopeClipUploader -> mock 8788 ===
baseUrl=http://127.0.0.1:8788 key=**** file=C:\Users\<user>\Desktop\Wan3.0\fixtures\long-20s.mp4
file exists=True bytes=303365
UploadAsync OK -> oss://dashscope-instant/20261022/wan2.7-i2v/2eb1c35f41f24f13a0db37fba5377779/long-20s.mp4
LastOssUrl       = oss://dashscope-instant/20261022/wan2.7-i2v/2eb1c35f41f24f13a0db37fba5377779/long-20s.mp4
starts with oss:// = True
```

mock 侧记录：

```
--- GET /api/v1/uploads?action=getPolicy&model=wan2.7-i2v -> 200
   policy_model = wan2.7-i2v
--- POST /oss/upload -> 200
   oss_upload = {"key": "dashscope-instant/20261022/wan2.7-i2v/2eb1c35f41f24f13a0db37fba5377779/long-20s.mp4", "filename": "long-20s.mp4", "bytes": 303365, "content_type": "application/octet-stream", "field_names": ["OSSAccessKeyId", "Signature", "policy", "x-oss-object-acl", "x-oss-forbid-overwrite", "key", "success_action_status", "file"], "field_count": 8, "oss_access_key_id": "MOCK-OSS-AK-NOT-A-REAL-CREDENTIAL", "x_oss_object_acl": "private", "x_oss_forbid_overwrite": "true", "success_action_status": "200"}
```

即：**WanStudio 现有上传代码不需要任何改动，就能对着本 mock 拿到合法 `oss://` URL**。该验证项目建在 `%TEMP%\wan30-bak\uploader-compat\`，不属于交付物，也没往仓库里加文件。

---

## 4. 向后兼容回归（老路径逐条实测）

mock 改动里把授权检查抽成了 `_auth_failure()`（原先是内联两段）。语义等价要靠实测证明，不能只看代码。

| # | 路径 | 命令要点 | 期望 | 实测 |
| --- | --- | --- | --- | --- |
| R1 | 无 Authorization | `POST submit` 不带 Authorization | 401 `No API-key provided.` | ✅ `HTTP 401 {"code":"InvalidApiKey","message":"No API-key provided."}` |
| R2 | bad-key | `Authorization: Bearer bad-key` | 401 `Invalid API-key provided.` | ✅ `HTTP 401 {"code":"InvalidApiKey","message":"Invalid API-key provided."}` |
| R3 | 缺 X-DashScope-Async | 好 key，不带 Async | 400 | ✅ `HTTP 400 {"code":"InvalidParameter","message":"X-DashScope-Async header is required and must be 'enable'."}` |
| R4 | 正常提交 | 好 key + Async | 200 PENDING | ✅ `HTTP 200 {"request_id":"mock-1","output":{"task_id":"mock-1","task_status":"PENDING"}}` |
| R5 | 轮询 | 连打 4 次 `/api/v1/tasks/mock-1` | RUNNING×3 → SUCCEEDED | ✅ 前 3 次 `RUNNING`，第 4 次 `SUCCEEDED` + `video_url` |
| R6 | 未知 task | `/api/v1/tasks/does-not-exist` | 404 | ✅ `HTTP 404 {"code":"NotFound","message":"Task does-not-exist does not exist."}` |
| R7 | 完整下载 | `GET /files/sample.mp4` | 200 + 303365 字节 | ✅ `HTTP 200 bytes=303365 type=video/mp4`，落盘 303365 |
| R8 | Range 下载 | `Range: bytes=0-99` | 206 + 100 字节 | ✅ `HTTP 206 bytes=100` |
| R9 | 未知路由 | `GET /nope` | 404 | ✅ `HTTP 404 {"code":"NotFound","message":"No route /nope"}` |
| R10 | `/__reset` | 打 reset 后查旧 task | 状态清空、计数归零 | ✅ 旧 task 变 404，`counter` 重新从 `mock-1` 起 |
| R11 | 老探针 api-check | `api-check.exe http://127.0.0.1:8788 demo-key` | 全流程通过 + 本地路径守卫仍拒绝 | ✅ `task_id=mock-1` → `poll[0..2] RUNNING` → `poll[3] SUCCEEDED`；`guard OK: first_clip 需要…不能直接提交` |

R11 用的是**改动前就编译好的** `tools/api-check/bin/Release/net8.0/api-check.exe`（内含当时的 `DashScopeClient` 代码），它打新 mock 完全正常 —— 这是最贴近"现有调用方没被我的改动影响"的证据。

**老路径无回归。**

（R11 输出里中文显示为乱码，是 Windows 控制台代码页问题，非接口问题；守卫本身按预期拒绝了 `C:\tmp\a.mp4`。）

---

## 5. 上传侧负路径实测

| # | 场景 | 期望 | 实测 |
| --- | --- | --- | --- |
| U1 | getPolicy 无 Authorization | 401 | ✅ `{"code":"InvalidApiKey","message":"No API-key provided."}` |
| U2 | getPolicy + bad-key | 401 | ✅ `{"code":"InvalidApiKey","message":"Invalid API-key provided."}` |
| U3 | getPolicy 的 action 不是 getPolicy | 400 | ✅ `{"code":"InvalidParameter","message":"query parameter 'action' is required and must be 'getPolicy'."}` |
| U4 | `/oss/upload` 非 multipart | 400 | ✅ `{"code":"InvalidParameter","message":"Content-Type must be multipart/form-data with a boundary parameter."}` |
| U5 | `/oss/upload` 缺 file 字段 | 400 | ✅ `{"code":"InvalidParameter","message":"missing form field: file"}` |
| U6 | curl `-F` 上传（带引号 name） | 200，8 字段全识别 | ✅ `HTTP 200`，`field_names` 8 个全对，`x-oss-object-acl=private` 正确 |
| U7 | reset 后提交旧 oss key | `oss_keys_uploaded=false` | ✅ `{"oss://demo/curl-upload.mp4": false}` |
| U8 | 混合 media（oss:// + http） | 只标记 oss 那条 | ✅ `{"oss://ok/clip.mp4": true}`（http 那条不出现） |
| U9 | 纯 http media 不带 resolve header | 200（不该过度校验） | ✅ `HTTP 200 PENDING` |

U9 是特意加的：确认新校验**只对 `oss://` 生效**，不会把纯 http/https 的 `first_clip` 误伤成 400。

---

## 6. 与 WanStudio 现有实现的契约对照（只读核对，未改动）

读 `WanStudio/Services/Media/ClipUploader.cs` 与 `DashScopeClient.cs` 确认了三处必须对齐的点：

1. `DashScopeClipUploader.GetPolicyAsync` 用 `Need(...)` 要求 `upload_dir / oss_access_key_id / signature / policy / upload_host` **全部非空**，缺一个就抛异常。mock 返回的 7 个字段都非空，且都显式标了 `MOCK-...-NOT-A-REAL-CREDENTIAL`，不会被误认成真凭证。
2. `PostToOssAsync` 的 key 是 `{upload_dir.TrimEnd('/')}/{fileName}`，最终返回 `oss://{key}`；mock 记录的 key 与之逐字符一致。
3. `DashScopeClient.SubmitAsync` 在 `payload.Contains("oss://")` 时**自动补** `X-DashScope-OssResourceResolve: enable`。这意味着 **App 自身永远走不到那个 400 分支** —— 负路径只能用裸探针（upload-check step 5）或 curl 验证，这一点在解读 UI 取证结果时要注意。

另外记一条给后续接线的人：`DashScopeClient.RequireServerReachableUrl` 现在已接受 `oss://`（第 210-223 行），不再是早期只认 http/https 的版本。

---

## 7. 结论

- **协议级通过**：`getPolicy → multipart 上传 → oss:// → 带 resolve header 提交 → 轮询 SUCCEEDED` 全链路在本地 mock 上跑通，8 个 multipart 字段、303365 字节、`first_clip` 类型、chunked 传输均实测确认。
- **负路径符合预期**：`oss://` 缺 `X-DashScope-OssResourceResolve: enable` → 400，message 点名缺的 header 与触发 url。
- **老路径零回归**：R1–R11 逐条实测通过，包括改动前编译的 `api-check` 探针。
- **真机仍 BLOCKED**：这是协议级证据，不是真机上传证据。没有真 Key，没有打过阿里云真实端点，OSS 签名、policy 校验、48h 有效期、账号绑定等真实侧约束全部未验证。真机验证需要真 Key 到位后才能做。

## 8. 环境收尾

- 8788 的 mock 实例由本任务启动（pid 39380），验证结束后已 `taskkill` 掉。收尾核对：8788 已消失；**8787 仍在 LISTENING（pid 22108），即 Lead 的 mock 实例全程存活、未被触碰**。
- 全程未访问、未 reset 8787；未 `Stop-Process WanStudio`；未在 `WanStudio/` 里 build；未修改 `WanStudio/` 下任何文件。
- 未写入任何真 Key（`demo-key` 为假 key；mock 返回的凭证字段均带 `NOT-A-REAL-CREDENTIAL` 标记）。
