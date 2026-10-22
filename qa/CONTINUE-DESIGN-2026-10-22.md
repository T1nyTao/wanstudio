# 链式续接设计对齐（官方 API 事实 ↔ 我们的实现）

**日期：** 2026-10-22
**作者：** eng-design（task-7）
**性质：** 纯文档。本文不含任何验收结论，AC 状态见 §D 与 `qa/CANONICAL-AC-PACK.md`。

---

## 0. 一句话讲清这次改动的由来

模型单次生成上限 **15s**。要做 30s 的片子必须切成多段，**第二段必须锚住第一段生成的尾巴**，否则接缝会跳。

官方 API 里这件事只有一条路：`first_clip`（首视频片段续写）。而 `first_clip` 只认公网 `http(s)://` 或 `oss://`——**本地切出来的片段提交不了**。所以必须补一条「上传本地片段换临时 URL」的通道。

> 这条链缺任何一环，在 API 层面都不成立。这是本次改动的全部理由，不是为了「功能更多」。

---

## A. 官方能力（引用已查证事实，不自行发挥）

来源：
- [万相2.7-图生视频 API 参考](https://help.aliyun.com/zh/model-studio/image-to-video-general-api-reference)
- [上传本地文件获取临时URL](https://help.aliyun.com/zh/model-studio/get-temporary-file-url)

### A.1 `media[].type` 合法值只有四个

| type | 含义 |
|---|---|
| `first_frame` | 首帧图 |
| `last_frame` | **本段生成的结尾帧** |
| `driving_audio` | 驱动音频 |
| `first_clip` | 首视频片段（续写锚） |

### A.2 合法素材组合（白名单，非法组合直接报错）

| # | 组合 |
|---|---|
| 1 | `first_frame` |
| 2 | `first_frame` + `driving_audio` |
| 3 | `first_frame` + `last_frame` |
| 4 | `first_frame` + `last_frame` + `driving_audio` |
| 5 | `first_clip` |
| 6 | `first_clip` + `last_frame` |

**白名单是硬约束。** 由此得到两个必须记住的推论：

- `first_clip` **不能**与人物参考图 / 参考视频同时传（组合 5/6 里没有它们的位置）。
- 任何「`first_clip` + 参考图」的想法在协议层就不可行，不是我们没实现。

### A.3 `first_clip` 的限制

| 项 | 约束 |
|---|---|
| 容器格式 | mp4 / mov |
| **时长** | **2 ～ 10s** |
| 分辨率 | 240 – 4096 |
| 体积 | ≤ 100MB |

### A.4 `duration` 是「输出总时长」，不是「续写增量」

官方例子：**输入视频 3s + `duration=15` → 续写 12s，最终输出 15s，按 15s 计费。**

这条最容易写错，必须钉死：

- ❌ `duration` ≠ 本次新增的秒数
- ✅ `duration` = 最终文件的总长度
- 推论：`duration` 必须 **大于** 首片段时长，否则等于零续写还照扣费。

### A.5 `last_frame` 的语义（易混点）

`last_frame` 是**本段生成结果的结尾帧**——即「我要让这一段收在这个画面上」。

它**不是**「参考前一段的尾巴」。参考前一段的尾巴这件事，只有 `first_clip` 能做。

| 想做的事 | 用哪个字段 |
|---|---|
| 拿上一段生成的尾巴当输入接着写 | `first_clip` |
| 指定本段生成结束时要到达的画面 | `last_frame` |

两者可以同时用（组合 6），但语义是叠加的，不是替代的。

### A.6 上传本地文件三步

```
1) GET  {base}/api/v1/uploads?action=getPolicy&model={model}
   → { "data": { upload_dir, oss_access_key_id, signature, policy,
                 x_oss_object_acl, x_oss_forbid_overwrite, upload_host } }
2) POST {upload_host}   multipart/form-data
   字段：OSSAccessKeyId / Signature / policy / x-oss-object-acl /
        x-oss-forbid-overwrite / key={upload_dir}/{filename} /
        success_action_status=200 / file=<bytes>
   → 200，得到 oss://{upload_dir}/{filename}
3) 调模型时必须带 header：X-DashScope-OssResourceResolve: enable
   → 缺这个 header，oss:// 解析失败
```

### A.7 上传约束（会影响产品设计，不只是技术细节）

| 约束 | 影响 |
|---|---|
| 文件与 `model` 绑定（getPolicy 时指定的那个） | 后续模型调用必须用**同一个 model**，否则 URL 解析不出来 |
| 临时 URL **48h** 有效 | 池里的远端地址会过期，不能当长期资产 |
| 上传与模型调用必须**同主账号** | 多租户场景要重做鉴权 |
| 单文件 ≤ **1GB** | 对 2–10s 片段绰绰有余 |
| 凭证接口 **100QPS**，不适合生产 | 高并发批量生成要自建中转/长期 OSS |

---

## B. 为什么必须做上传（因果链）

```
① 链式续接需要「上一段生成结果的尾巴」当 first_clip
        ↓
② first_clip 只认 http(s):// 或 oss://
   本地切出来的文件（file:// / 盘符路径）提交不了
        ↓
③ 上一段若 > 10s，还必须先裁到 10s 以内（first_clip 上限）
        ↓
④ 所以完整链路只能是：
   裁尾段 → 上传换 oss:// → 带 OssResourceResolve 头提交
```

**结论：缺上传这一环，链式续接在 API 层面直接不成立。** 这不是优化项。

顺带说明一个被这次改动修正的旧错误：早期实现把本地素材以 `file://` 形式塞进 `media`，真机根本取不到文件。现在本地素材一律先上传（`DashScopeClient.cs:85-92`）。

---

## C. 我们改了什么（代码级，含文件:行号）

> 全部行号对应当前工作区代码（2026-10-22）。

### C.1 `Services/Media/ClipUploader.cs`（新文件）

| 内容 | 位置 |
|---|---|
| `IClipUploader` 接口（`UploadAsync(localPath, model, ct) → oss://`） | `ClipUploader.cs:30-34` |
| `DashScopeClipUploader` 类，实现上传全流程 | `ClipUploader.cs:36-145` |
| `UploadAsync`：校验 → getPolicy → multipart POST → 返回 oss:// | `ClipUploader.cs:52-67` |
| `GetPolicyAsync`：`GET {base}/api/v1/uploads?action=getPolicy&model=` | `ClipUploader.cs:69-101`（URL 在 `:71`） |
| `PostToOssAsync`：multipart 字段齐全（含 `key`、`success_action_status=200`） | `ClipUploader.cs:103-133`（字段 `:111-122`，`key` `:106`，返回值 `:132`） |
| `ClipAnchor.MinAnchorSeconds = 2.0` | `ClipUploader.cs:155` |
| `ClipAnchor.MaxAnchorSeconds = 10.0` | `ClipUploader.cs:158` |
| `ClipAnchor.DefaultAnchorSeconds = 6.0` | `ClipUploader.cs:161` |
| `TailSecondsFor(duration, wanted)`：夹到 2–10s 且不超过源时长 | `ClipUploader.cs:167-172` |
| `CanBeUsedWhole(duration)`：源本身在窗口内就不必裁 | `ClipUploader.cs:175-176` |

**设计要点：** 上传器只负责「本地文件 → oss://」，不做裁切。裁切是 runner 的事，两者分开才好测。

### C.2 `Services/DashScopeClient.cs`

| 内容 | 位置 |
|---|---|
| `RequireServerReachableUrl`：**放行 oss://**，仍拒绝 `file://` / 盘符 / UNC | `:207-225`（`oss://` 判定 `:218`，本地判定 `:212-214`，抛错 `:220-224`） |
| `SubmitAsync`：payload 含 `oss://` 时自动加 `X-DashScope-OssResourceResolve: enable` | `:228-263`（判定 `:236`，加头 `:238`） |
| `SubmitVideoJobAsync`：本地素材一律先上传（不再发 `file://`） | `:70-137`（`ReachableAsync` `:87-92`） |
| `UploadLocalAsync`：http(s)/oss:// 原样返回，本地路径走上传 | `:143-154` |
| 图生：`first_frame` | `:98` |
| **人物替换：`reference_image` → `first_frame`** | `:107-111` |
| **动作参考：`reference_video` → `first_clip`** | `:113-117` |
| `SubmitContinueAsync`：`media[0].type = first_clip`，可选追加 `last_frame` | `:160-200`（守卫 `:170`，`first_clip` `:174`，`last_frame` `:177-181`） |

**关键改动说明：** 早期实现发的是 `reference_image` / `reference_video`，这两个 type 不在 A.1 的四个合法值里，真机会被判非法组合。现在改成合法 type——但**语义随之变弱**，见 §D.4。

### C.3 `Services/VideoJobRunner.cs`

| 内容 | 位置 |
|---|---|
| `PrepareAnchorAsync`：探测 → 判断是否需裁 → 裁尾段 → 上传 | `:17-55` |
| 来源不存在时明确抛错 | `:23-24` |
| 用媒体引擎探测真实时长（不信任传入值） | `:26-28` |
| 源在 2–10s 内：整段直接上传，不裁 | `:31-35` |
| 超 10s：裁出尾部窗口到 `%LOCALAPPDATA%\WanStudio\cache` | `:38-51` |
| 引擎不支持切段（缺 ffmpeg）时明确报错，不静默降级 | `:38-40` |
| 上传换 `oss://` | `:53-54` |
| `item.Anchor != null` → 走续接分支 | `:100-121` |
| 旧路径 `Mode == ContinueFromClip` 且无 Anchor → 仍需 `FirstClipUrl` | `:122-137` |

**顺序很重要：** `Anchor` 分支在 `Mode` 分支**之前**判断（`:100` 早于 `:122`）。所以任务对象即使 `Mode` 保持原样，只要 `Anchor != null` 就走续接。

### C.4 `Services/VideoTaskStore.cs`

| 内容 | 位置 |
|---|---|
| `VideoTaskItem.Anchor`（`ClipAnchorSpec?`，为空表示独立生成） | `:60-64` |
| `ClipAnchorSpec` 记录锚点来源四元组 | `:134-147` |
| `SourceLocalPath` / `SourcePoolItemId` / `TailSeconds` / `SourceDurationSeconds` | `:135-138` |
| `CanUseWhole`（2–10s 判定） | `:141` |
| `EffectiveSeconds`（实际提交时长） | `:144-146` |

### C.5 `Pages/LongVideoPage.xaml(.cs)` —— 续接内嵌进生成动作

| 内容 | 位置 |
|---|---|
| 顶部流程说明「…进池 → 官方续接」 | `LongVideoPage.xaml:36` |
| 续接开关嵌在**生成动作**里（不是独立页） | `LongVideoPage.xaml:102-127` |
| 开关文案「衔接上一段（续接）」 | `LongVideoPage.xaml:110` |
| 说明文案：拿「已生成结果」的尾巴当 first_clip；单次上限 15s | `LongVideoPage.xaml:114` |
| 锚点面板（来源下拉 + 尾部秒数 NumberBox 2–10 + 提示） | `LongVideoPage.xaml:116-127` |
| 池 + 官方续接区（`media type = first_clip`） | `LongVideoPage.xaml:218-281`（标题 `:260`） |
| 硬拼区明确声明「本地剪辑，不是官方续接…也不叫「无缝」」 | `LongVideoPage.xaml:295` |
| 状态恢复（开关 / 面板 / 秒数 / 提示） | `LongVideoPage.xaml.cs:84-87` |
| `AnchorToggle_Changed` | `:352-359` |
| `AnchorSource_Changed` | `:361-369` |
| `AnchorTail_Changed`（夹到 2–10） | `:371-377` |
| `SyncAnchorSources`（只填 `CanContinue && File.Exists`，默认最近一条） | `:380-389` |
| `BuildAnchor`（构建 `ClipAnchorSpec`，`SourceDurationSeconds` 传 0 由 runner 探测） | `:395-412`（传 0 在 `:411`） |
| `AnchorBlockReason`（续接不可用原因，中文写清） | `:415-427` |
| 生成就绪校验接入锚点原因 | `:469` |
| 提交时按 anchor 决定 `Mode`、`MediaPaths` 清空、`Anchor` 赋值 | `:589-623`（`Mode` `:606`，`MediaPaths` `:612`，`Anchor` `:614`，无锚才抽首帧 `:617-623`） |
| `duration` 按「输出总时长」处理，且必须大于首片段 | `:842-855`（注释 `:842-844`，校验 `:854-855`） |
| 独立续接面板：仍要求 `RemoteUrl` 为 http(s) | `:870-881`（校验 `:876-881`） |

### C.6 `Services/VideoPool.cs` —— 谁能当首片段

| 内容 | 位置 |
|---|---|
| `IsImported`（本地导入标记） | `:48` |
| `CanContinue => !IsImported && !string.IsNullOrWhiteSpace(RemoteUrl)` | `:50` |
| `ContinueBlockReason`：导入片段 / 无公网地址各有中文原因 | `:53-61` |

### C.7 `Services/ModelCatalog.cs` —— 只有续接模型可选

| 内容 | 位置 |
|---|---|
| `wan3.0-video-prime` / `wan3.0-video`：`FirstClipContinue: false` | `:19-20` |
| `wan2.7-i2v`：`FirstClipContinue: true` | `:21` |
| `DefaultContinueModelId = "wan2.7-i2v"` | `:28` |
| 续接模型下拉只填 `FirstClipContinue == true` 的项 | `LongVideoPage.xaml.cs:75-76` |

### C.8 `Pages/HomePage.xaml(.cs)` —— 首页四模式内嵌续接

| 内容 | 位置 |
|---|---|
| 续接开关 XAML（`AnchorToggle` / `AnchorPanel` / `AnchorSourceCombo` / `AnchorTailBox` / `AnchorHint`） | `HomePage.xaml:82-93` |
| `AnchorToggle_Changed`（切面板 + 填下拉 + 刷新就绪） | `HomePage.xaml.cs:270-278` |
| `AnchorSource_Changed` | `:280-285` |
| `AnchorTail_Changed`（NaN 兜底 + 夹到 2–10 + 回写收敛，不递归） | `:287-299` |
| `SyncAnchorSources`（默认选最近一条，池是 Insert(0)） | `:302-311` |
| `UsableAnchorSources`（`CanContinue && File.Exists`） | `:317-318` |
| `TailSeconds`（统一夹到 first_clip 窗口） | `:321-328` |
| `UpdateAnchorHint`（可用写「谁的尾巴几秒」，不可用写阻断原因） | `:331-353` |
| `BuildAnchor`（`SourceDurationSeconds` 传 0 由 runner 探测） | `:359-374`（传 0 在 `:373`） |
| `RefreshReadyState` 的续接校验：池里无可续接条目 → 禁用并写原因 | `:235-259`（空池文案 `:243`，来源失效 `:249-256`，就绪补注 `:258`） |
| `GenerateButton_Click`：有锚点时**不带首帧素材**，`Mode` 保持原样，`Anchor` 赋值 | `:384-419`（`anchor` `:396`，仅无锚才加素材 `:399-404`，`Mode = _mode` `:408`，`Anchor = anchor` `:415`） |

**四模式行为未被破坏：** 续接校验插在四模式素材校验**之后**（`:235` 起，四模式在 `:233` 之前结束），原有禁用原因文案一字未改（AC-M02/04/06 依赖它）。未勾续接时 `BuildAnchor()` 返回 null（`:361`），`MediaPaths` 组装与 `Mode` 赋值都走原路径。

---

## D. 诚实边界（一条都不省）

### D.1 全部是代码级 + mock 协议级验证，真机未验证

- 本文 §C 的每一条都是**读代码 + 行号**确认的，不是跑出来的。
- 协议侧只有 mock 证据：`qa/run-2026-10-22-continue-api.md`（`first_clip` 请求体、401/400 负路径、本地路径守卫）。
- **真机一次都没跑过**——缺真 Key。没有真机输出，就不能说「上传可用」「续接能生成」。

### D.2 仍然 BLOCKED 的 AC

| AC | 状态 | 原因 |
|---|---|---|
| AC-L06 | **真机半**（协议级有证据，真机无证据） | 缺真 Key；上传链路真机未验 |
| AC-L07 | **BLOCKED** | 时长带宽（~2–15s）需真机产物才可判 |
| AC-M05 | **BLOCKED** | 人物替换真机生成未跑 |
| AC-M07 | **BLOCKED** | 动作参考真机生成未跑 |

**协议通过 ≠ 真机可用。** 上面任何一条都不许写成 PASS。

### D.3 `first_clip` 与人物参考素材不能同时传 → 30s 换人的正确路径

白名单（§A.2）里 `first_clip` 只能和 `last_frame` 组合，**没有**参考图/参考视频的位置。

所以「30s 且要换人」的唯一可行路径是：

```
第 1 段：用人物替换生成（first_frame = 人物参考图）
         ↓
第 2…N 段：用 first_clip 续写第 1 段的尾巴
         → 人物外观由首片段自动继承，不再传参考图
```

**关键认知：** 后续段不需要（也不允许）再传人物参考图——外观是从 `first_clip` 的画面里继承的。传了反而非法。

### D.4 改成合法 type 之后，语义变弱了

为了通过白名单，`reference_image` / `reference_video` 被改成了 `first_frame` / `first_clip`。代价是**语义不等价**：

| 原来想要的 | 现在实际得到的 | 差距 |
|---|---|---|
| 人物替换（reference_image，视频换人） | 首帧参考（`first_frame`，只影响开头画面） | 后续帧的人物一致性没有保证 |
| 动作参考（reference_video，动作迁移） | 片段续写（`first_clip`，按输入片段接着写） | 是「接着写」，不是「把动作搬过来」 |

**这不等于是真正的「视频换人」。** 真正的换人/动作迁移需要 VACE 类模型，当前**未接入**。AC-M05/M07 的真机结论必须按这个弱化后的语义去判，不能按「换人」判。

### D.5 其他必须写明的实现边界

1. **首片段来源不含本地导入**：`VideoPool.cs:50` 的 `CanContinue` 排除 `IsImported`。这是用户明确的产品决定（导入片段没有公网 URL），**不是缺陷**。AC-L06 表述已据此收窄。
2. **独立「官方续接」面板仍走公网 URL**：`LongVideoPage.xaml.cs:876-881` 要求 `RemoteUrl` 为 http(s)，不走上传通道。因此该面板对导入片段不可用；池条目的远端地址是 DashScope 的 **24h 临时地址**，过期后也不能再用。链式续接（`Anchor`）不受此限，因为它走 `PrepareAnchorAsync` 现裁现传。
3. **上传 URL 48h 有效且与 model 绑定**：跨模型复用同一个 oss:// 会解析失败；48h 后失效。
4. **凭证接口 100QPS 不适合生产**：批量生成场景需要自建中转或长期 OSS。
5. **缺 ffmpeg 时无法处理 >10s 的来源**：`VideoJobRunner.cs:38-40` 直接抛中文错误，不静默降级、不偷偷硬拼。
6. **两个入口的续接能力不对等**：长视频页的「衔接上一段」（`Anchor`）与首页的「衔接上一段」都走 `PrepareAnchorAsync` 现裁现传，不受 URL 过期影响；而长视频页那块独立的「官方续接生成」面板（`:870-881`）仍只认池里的公网 `RemoteUrl`。同一页上两个入口的可用范围不同，属于已知的不一致，未在本次改动中统一。

---

## E. 结论

- 链式续接在**协议层**已经成立：裁尾段 → 上传换 `oss://` → 带 `X-DashScope-OssResourceResolve` 提交 `first_clip`。
- 在**真机层**一次都没验证过，因为缺真 Key。
- 因此本次改动可以写「已实现、协议自洽」，**不能**写「已验证可用」。

证据索引：
- 续接协议：`qa/run-2026-10-22-continue-api.md`
- 上传协议：`qa/run-2026-10-22-upload.md`（getPolicy → multipart → oss:// 全链路 + 缺 `X-DashScope-OssResourceResolve` 的 400 负路径）
- 媒体引擎：`qa/run-2026-10-22-media-engine.md`
