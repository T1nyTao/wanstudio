# 多人参考 / 参考视频剪切 / 统一素材库 — 实现与取证（2026-10-22）

**执行:** Lead + eng-pool + eng-cutter
**口径:** 全部为 **UI 驱动 + mock 协议级** 证据。**真机未验证（缺真 Key）**。

---

## 0. 一个必须先纠正的结论

上一轮我把 `reference_image` / `reference_video` 判定为「非法 type」，把它们降级成
`first_frame` / `first_clip`。**那个判断是错的**，本轮已纠正。

事实：这两个 type **本身合法**，只是属于 **`wan2.7-r2v`（参考生视频）**，不属于 `wan2.7-i2v`。
官方两个接口的合法 type 不同：

| 模型 | 合法 media type | 说明 |
|---|---|---|
| `wan2.7-i2v` | `first_frame` / `last_frame` / `driving_audio` / `first_clip` | 图生 + 续写，组合白名单受限 |
| `wan2.7-r2v` | `reference_image` / `reference_video` / `first_frame` | 参考生视频，**参考素材合计 ≤5** |

所以「多人人物替换」的正解不是改 type，而是**换模型**。降级 type 会把多人能力直接砍掉。

来源：[万相2.7-参考生视频API参考](https://www.alibabacloud.com/help/tc/model-studio/wan-video-to-video-api-reference)

---

## 1. 三条需求的官方依据

### 1.1 多人人物替换
`wan2.7-r2v` 官方原文：「支持指定人物或任意物品进行参考，精准保持形象和声音的一致性，**支持多角色参考合拍**」。
- `reference_image`：参考图像，提供主体角色（人物/动物/物体）和情境参考
- 素材限制：**参考图像 + 参考视频 ≤ 5**；首帧图像最多 1 张；**每份素材仅包含单一角色**
- 所以「多人」= **多张 `reference_image`，每张一个角色**
- 提示词用「图1、图2、视频1」指代，顺序与 `media` 数组一致（图像、视频分别计数）

### 1.2 参考视频自动剪切
`reference_video` 官方限制：**时长 1～30s**，mp4/mov，分辨率 240–4096，≤100MB。
长视频必须切成 ≤30s 的片段才能提交，所以需要自动剪切。

### 1.3 统一素材库
剪切片段和生成结果放同一个库，卡片显示缩略图/时长/来源，可多选、可删除。

### 1.4 音色参考（reference_voice）
官方原文要点：
- `reference_voice` **不是独立 type**，而是 `media` 数组每一项里的**可选字段**，挂在 `reference_image` 或 `reference_video` 上
- 格式：**wav、mp3**；时长 **1～10s**；文件大小 **≤15MB**
- **只参考音色，与说话内容无关**；建议参考音频语种与提示词语种一致
- 与带音轨的 `reference_video` 同时存在时，**优先 `reference_voice`**，覆盖视频原声
- 只有 wan2.7 支持

我们的实现：音色按顺序配对到参考素材（第 1 个音色 → 第 1 个参考图/视频），
提交时嵌进对应 media 项的 `reference_voice` 字段。

### 1.5 人物替换的时长：按原视频，不给选

产品要求：人物替换不要让人选生成时长，按原视频长度来。

实现：人物替换模式下隐藏时长下拉，改为显示「按原视频 N s」，
时长由目标视频（或素材库里勾选的视频）实际探测得到。

**但官方硬限制必须说清**：`wan2.7-r2v` 的 `duration` 在**带参考视频时只能 2～10s**
（不带视频才是 2～15s）。所以原片超过 10s 时，单次请求最多只能出 10s，
不可能一次生成出等长成片。UI 会明写这句话，不假装能一次出满：

> 单次上限 10s（带参考视频），本次按 10s 生成；要覆盖整段请切段逐段替换。

要真正覆盖整段原片，正确做法是把原视频按 ≤10s 切成多段、逐段替换再拼回，
这也正是素材库 + 自动切段存在的意义。

---

## 2. 代码改动

| 文件 | 改动 |
|---|---|
| `Services/ModelCatalog.cs` | 新增 `wan2.7-r2v`：`ReferenceToVideo=true`、`MaxReferenceMaterials=5`、`AcceptsReferenceVideo=true`、带/不带视频的 duration 上限（10 / 15） |
| `Services/VideoTaskStore.cs` | `VideoTaskItem` 加 `ReferenceImages` / `ReferenceVideos` / `ReferenceCount` |
| `Services/DashScopeClient.cs` | 新增 `SubmitReferenceAsync`（多图 + 多视频，校验 ≤5、至少 1 份）；r2v 的 `resolution` 归一为 720P/1080P、`ratio` 归一为 16:9/9:16/1:1/4:3/3:4、`duration` 按有无视频夹到 10/15 |
| `Services/VideoJobRunner.cs` | `ReferenceCount > 0` 且模型支持参考 → 走 `SubmitReferenceAsync` |
| `Services/VideoPool.cs` | 加 `ClipKind`（Generated / CutReference / Imported）、`DurationSeconds`、`AddCutReference`、`CutReferences`、`Generated`、`Remove(id)`；pool.json 升 v2 且兼容 v1 |
| `Services/Media/ReferenceCutter.cs` | **新增**：`Plan`（纯函数）+ `CutAsync`，把长视频切成每段 ≤30s 且 ≥1s |
| `Services/Media/ClipUploader.cs` | OSS object key 改用 ASCII 文件名（`clip-{guid}.mp4`），避免中文 key |
| `Controls/ClipBinPanel.xaml(.cs)` | **新增**：卡片网格素材库控件（缩略图/时长/来源/删除/多选/卡片大小） |
| `Pages/HomePage.xaml(.cs)` | 参考图改成**列表**（可多人）、参考视频自动剪切入口、内嵌素材库、r2v 路由 |

---

## 3. 取证

### 3.1 素材库跨重启恢复（卡片正常渲染）

```
HeaderBlock | 素材库（1 段）
卡片内容   | 删除按钮 ✕ / 标题 / "图生 · 段 01 · 00:00.0 – 00:05.0" / "20s" / "可作首片段"
```

时长 `20s` 是从 mock 返回的 `sample.mp4` 真实探测出来的（mock 固定回 20s 夹具），
说明缩略图/时长回填链路生效。

### 3.2 多人参考的就绪门禁

人物替换 + 未选任何参考素材：

```
ReadyHint | 未就绪：人物替换需要人物参考 + 目标素材。     ← 原文案，AC-M04 依赖
GenerateButton | enabled=False
```

勾选素材库片段后：

```
SelectionBlock | 当前选中：保持原片主体与构图，镜头…
ReadyHint      | 就绪：当前模式「人物替换」，点击生成将提交任务。
GenerateButton | enabled=True
```

### 3.3 r2v 参考生视频的实际请求（`qa/_tmp/req-r2v2.json`）

```json
model  = "wan2.7-r2v"
params = {"resolution":"720P","ratio":"16:9","duration":5}
media  = [{"type":"reference_video",
           "url":"oss://dashscope-instant/20261022/wan2.7-r2v/78d970ec…/clip-c1aabf9a….mp4"}]
```

三条关键点都成立：
1. `type == "reference_video"` —— 官方 r2v 的合法类型
2. `resolution=720P` / `ratio=16:9` —— 从非法的 `480P`/`adaptive` 归一而来
3. URL 是 `oss://` 且文件名是 ASCII（`clip-<guid>.mp4`），不再带中文

### 3.4 参考视频自动剪切的规划（eng-cutter 实测，临时工程）

| 输入 | 输出 | 说明 |
|---|---|---|
| 20s / chunk 25 | 1 段 20s | 已在窗口内，不重切（直接引用源文件） |
| 75s / chunk 25 | 25 + 25 + 25 | 均分 |
| 30.5s / chunk 30 | 15.25 + 15.25 | 末段 0.5s 并入后超 30s → 两段均分 |
| 31s / chunk 30 | 30 + 1 | 末段恰好卡 1s 下限 |
| 61s / chunk 30 | 30 + 30 + 1 | **2 段装不下 61s**（2×30=60），所以 3 段 |
| 300s / chunk 25 | 12 × 25s | 长片 |
| 0 / 负数 / NaN | 0 段 | 无合法片段 |

eng-cutter 同时指出我任务书里 `61s + chunk 30 → 2 段` 是数学错误（2×30=60<61），
它的 3 段输出是对的。

### 3.5 音色参考的实际请求（反射直调已编译产物 + mock）

用临时控制台工程引用 `DashScopeClient.cs` / `ClipUploader.cs` / `ModelCatalog.cs` / `AppSettings.cs`
（即编译产物同一份源码），对 mock 跑一次带音色的 `SubmitReferenceAsync`：

```json
model = "wan2.7-r2v"
media = [
  {
    "type": "reference_image",
    "url": "oss://dashscope-instant/20261022/wan2.7-r2v/e6eb5936…/clip-03d1dbd9….wav",
    "reference_voice": "oss://dashscope-instant/20261022/wan2.7-r2v/bf122e0f…/clip-8244dc34….wav"
  }
]
```

三条关键点成立：
1. `reference_voice` 是 **media 项内的字段**，不是独立 type —— 与官方结构一致
2. 音色文件同样走了上传链路，拿到 `oss://` 地址
3. 文件名是 ASCII（`clip-<guid>.ext`），不带中文

> 探针里为了省事把 wav 当 `reference_image` 的占位素材用；实际 UI 里参考图是图片、
> 音色是音频，字段结构完全相同。

### 3.6 音色 UI 面板

```
参考音色（wav/mp3，1–10s；按顺序对应参考图1/视频1，只取音色不看内容）
[添加音色] [清空音色]        仅 wan2.7-r2v 支持。
```

音色列表每行显示序号 + 文件名 + 移除按钮。添加时校验：扩展名 wav/mp3、≤15MB、
能探测到时长时校验 1–10s（探测失败不拦，交给服务端）。

### 3.7 人物替换的自动时长（UI 实测）

模式切换：文生模式下 `DurationCombo` 可见；切到人物替换后它消失，换成自动时长块。

勾选一条 20s 的原视频后：

```
DurationAutoText | 按原视频 20s
DurationAutoHint | 单次上限 10s（带参考视频），本次按 10s 生成；要覆盖整段请切段逐段替换。
```

实际提交请求（`wan2.7-r2v`）：

```json
params = {"resolution":"720P","ratio":"16:9","duration":10}
media  = [{"type":"reference_video","url":"oss://…/clip-0cf6ca74….mp4"}]
```

`duration: 10` —— 原片 20s 被正确夹到官方上限 10s，与 UI 文案一致，
不是「假装按原片长度」。


---

## 4. 本轮修掉的缺陷

| # | 缺陷 | 后果 | 修法 |
|---|---|---|---|
| 1 | 上一轮把 `reference_image`/`reference_video` 降级成 i2v 类型 | **多人能力被砍掉**，且 r2v 根本没被用上 | 新增 r2v 模型与 `SubmitReferenceAsync`，type 回归官方语义 |
| 2 | 参考图只能选一张 | 无法做多人 | 改成列表，每张一个角色，受 ≤5 约束 |
| 3 | 长参考视频无法使用 | `reference_video` 限 1–30s，超过就没法提交 | 新增 `ReferenceCutter` 自动切段 |
| 4 | 剪切片段与生成结果分属两处 | 用户要「在一个里面」 | `ClipKind` + 统一 `ClipBinPanel` |
| 5 | r2v 传 `480P`/`adaptive` | 官方只认 720P/1080P 与具体宽高比 → 真机非法 | 参数归一 |
| 6 | OSS key 带中文提示词 | key 编码后可能取不到对象 | 改用 `clip-{guid}.mp4` |
| 7 | 勾选素材库不触发就绪重算 | 勾了卡片「生成」仍禁用 | 订阅 `Bin.SelectionChanged` |
| 8 | 音色参考没有入口 | 官方 r2v 支持音色迁移，产品用不上 | 加音色列表 + 校验 + 按序配对进 `reference_voice` |

---

## 5. 仍然 BLOCKED / 未验证

- **真机全部 BLOCKED（缺真 Key）**：AC-M03 / M05 / M07、AC-L06 真机半、AC-L07。
- **r2v 的多角色实际效果未验证**：只验证了请求体结构正确，模型是否真的能区分
  「图1 / 图2」两个不同角色，必须真机跑才知道。
- **`reference_video` 的 1–30s 上限未真机验证**：目前只按官方文档实现与本地切段。
- **`reference_voice`（音色参考）已接入**：作为 media 项内字段，按顺序配对到参考素材。
  但**真机是否真的能把音色迁移过去未验证**（缺真 Key）；多音色与多角色的配对效果也未验证。
- **素材库的「点卡片预览」未实现**：目前点卡片是选中，不是播放。用户截图里有预览语义，
  当前版本先做选中/多选/删除，预览留给下一轮。
- **剪切是本地剪辑，不是 AI 生成**，UI 文案已按此写，不许说成「无缝」或「续接」。

---

## 6. 复现

```bash
# mock（显式指定 log，避免与其它实例串记录）
python tools/mock-dashscope/mock_server.py --port 8787 \
  --fixture fixtures/long-20s.mp4 --log qa/_tmp/mock-8787.jsonl

cd WanStudio && dotnet build -c Debug -p:Platform=x64 -v:q
cp -r bin/x64/Debug/net8.0-windows10.0.26100.0/win-x64/. /c/Users/<user>/Desktop/verify-wanstudio/

# 应用必须从工作区外运行：工作区内的 exe 派生 ffmpeg 会被执行沙箱拒绝写文件
"C:\Users\<user>\Desktop\verify-wanstudio\WanStudio.exe" --open=home

pwsh -File tools/uia/drive.ps1 -Action invoke -Id ModeCharBtn
pwsh -File tools/uia/drive.ps1 -Action combosel -Id ModelCombo -Index 3   # wan2.7-r2v
pwsh -File tools/uia/drive.ps1 -Action settext -Id PromptBox -TextFile qa/_tmp/prompt.txt
curl -s http://127.0.0.1:8787/__requests
```
