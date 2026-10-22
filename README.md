# WanStudio · 万象工作台

Windows 桌面端 AI 视频生成工作台，接入 **千问AI平台** 与 **阿里云百炼**。

用 WinUI 3（未打包 / unpackaged）写成，重点解决一件事：**把「一次只能生成十几秒」变成能连续产出长片的工作流**。

---

## 它解决什么问题

各家视频模型单次输出都有时长上限（wan3.0 是 30 秒，wan2.7 系列更短）。想做一个完整长片，必须把片子切段、逐段生成、再让每一段接住上一段的尾部——否则接缝会跳。

WanStudio 把这套流程做成了图形界面：

- **长片切段**：导入长视频，按镜头边界或固定秒数切分，逐段生成
- **链式续接**：拿上一段生成结果的**尾部**当 `first_clip`（或 wan3.0 的 `reference_video` 延长）接着写，接缝不跳
- **素材库**：左侧放「原视频 → 裁切片段」，右侧放「已生成成品」，两侧各有独立分类
- **人物替换**：把参考图里的人换进目标视频

---

## 功能

### 四种生成方式

| 模式 | 说明 |
|---|---|
| 文生视频 | 只给提示词 |
| 图生视频 | 首帧图 + 提示词（`first_frame`） |
| 人物替换 | 参考图 + 素材库里的目标视频 |
| 动作参考 | 主体图 + 素材库里的动作视频（`reference_video`） |

### 素材库

- **原视频区**：导入后**不会自动裁**，要不要切、切几秒由你决定（默认 13 秒）
- **素材片段区**：裁切产物，可建分类、可改名、可删除分类
- **已生成区**：生成结果，可点选当续接源
- 卡片上有 `▶` 可**内嵌预览**，不用跳出页面
- 重复导入同一文件会按源文件判重，不会堆重复条目

### 长视频页

- 导入长片 → 切段规划 → 逐段提交 → 进度同步
- 段间可选续接（锚上一段尾巴）
- 支持本地硬拼接（**注意：本地硬拼不等于模型级无缝**，只是按顺序接起来）

### 平台切换

两家平台的域名和可用模型不完全重合，界面上一键切换：

| 平台 | 端点 |
|---|---|
| 千问AI平台 | `https://cn-beijing.maas.qianwenaiapi.com` |
| 阿里云百炼 | `https://<你的工作空间ID>.cn-beijing.maas.aliyuncs.com` |

> 百炼的工作空间域名**每个账号都不一样**，源码里给的是模板；在「设置 → API 平台」选中百炼后把 `<你的工作空间ID>` 换成自己的那段。

---

## 环境要求

- Windows 10 1809 或更高（x64）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows App SDK（由 NuGet 自动还原）
- **ffmpeg**：切段、抽帧、探测时长要用

ffmpeg 的查找顺序：`PATH` → 常见安装位置。找不到时会退化成 Windows 内置媒体引擎（能力受限，建议装 ffmpeg）。

---

## 构建与运行

```bash
git clone <repo-url>
cd WanStudio
dotnet build -p:Platform=x64
```

产物在 **`bin/x64/WanStudio.exe`**。

> 本项目**只保留 Release 配置**：`Configuration` 在 csproj 里强制为 Release，输出路径也拍平到 `bin/<platform>/`，不会产生 `bin/x64/Debug` 这类目录。
> 这是有意的——Debug/Release 混用会导致「改了代码跑的还是旧版本」，排查问题时极容易被误导。

### 怎么运行（只有一个目录）

双击项目根目录的 **`启动.cmd`**：它会检查是否已构建，没构建就先构建，然后启动。

命令行等价写法：

```bash
dotnet build WanStudio\WanStudio.csproj -p:Platform=x64
WanStudio\bin\x64\WanStudio.exe
```

**就一个目录，不需要再复制一份到别处。** 早先版本建议把 `bin/x64/` 拷到桌面另跑，那是受限环境下的临时规避；现在 `AppPaths` 加了「exe 同级目录」兜底，任何环境下都能找到可写位置，副本因此取消。

数据目录按以下顺序选择第一个可写的位置（不会丢数据，只是换地方）：

```
%LOCALAPPDATA%\WanStudio    ← 正常情况用它
%USERPROFILE%\.wanstudio    ← 上面写不进时
%TEMP%\WanStudio            ← 再不行时
<exe 同级>\WanStudioData     ← 最后兜底（便携运行 / 受限环境）
```

---

## 配置

打开「设置」：

1. **API 平台**：选千问AI平台或阿里云百炼（会自动填接口地址，可手改）
2. **API Key**：粘贴你的 Key，点保存
3. **默认模型 / 输出目录**：按需调整

### Key 存在哪

保存进 **Windows 凭据管理器**（`PasswordVault`），资源名 `WanStudio.DashScope`，**不落明文、不进配置文件**。

「设置」里有「清除密钥」按钮。命令行清除：

```bash
# 需要与主程序在同一个程序目录树里运行（未打包应用的凭据作用域跟路径有关）
clear-key.exe
```

> 未打包应用的 `PasswordVault` 可见范围与 exe 所在路径相关：换个目录运行就看不到之前存的 Key。这是 Windows 的行为，不是 bug。

---

## 模型能力表

能力值来自各家官方文档，不是猜的：

| 模型 | 文生 | 图生 | 参考生视频 | 视频编辑 | 时长上限 |
|---|---|---|---|---|---|
| `wan3.0-video-prime` | ✓ | ✓ | ✓ | ✓ | 30s |
| `wan3.0-video` | ✓ | ✓ | ✓ | ✓ | 30s |
| `wan2.7-videoedit` | | | | ✓ | 10s（`0` = 跟随原片） |
| `wan2.7-i2v` | | ✓ | | | 15s |
| `wan2.7-r2v` | | | ✓ | | 10s（带视频） |

### 几个容易踩的官方约束

- **wan3.0 是 All-in-One**：不用换模型名，服务端按 `input.media[].type` 和提示词意图自动路由
- `reference_*` 与 `first_frame`/`last_frame` **互斥**，不能混用
- 提示词里要用 **「图1」「视频1」「音频1」** 指代素材，三者**分别计数**——不写指代，模型不知道要对谁做什么
- **`duration` 是输出总时长**，不是续写增量
- 参考音频字段名两代不同：wan3.0 是独立的 `reference_audio` 条目，wan2.7-r2v 是挂在素材内部的 `reference_voice`

---

## 项目结构

```
WanStudio/
├─ App.xaml(.cs)               应用入口；启动参数 --open= / --import=；崩溃日志
├─ MainWindow.xaml(.cs)        NavigationView 导航与窗口尺寸
├─ Controls/
│   └─ ClipBinPanel.xaml(.cs)  双栏素材库（导入 / 裁切 / 分类 / 预览）
├─ Pages/
│   ├─ HomePage.xaml(.cs)      新建视频：模式 + 参考素材 + 参数 + 续接 + 素材库
│   ├─ LongVideoPage.xaml(.cs) 长视频：导入 / 切段 / 逐段生成 / 续接
│   ├─ TasksPage.xaml(.cs)     任务列表 + 播放器 + 取消 / 重试 / 另存为
│   └─ SettingsPage.xaml(.cs)  平台 / Key / 模型 / 输出目录 / 权限
├─ Services/
│   ├─ AppPaths.cs             统一可写目录解析（三级回退）
│   ├─ AppSettings.cs          设置持久化 + API 平台预设
│   ├─ ApiKeyStore.cs          Windows 凭据管理器读写
│   ├─ DashScopeClient.cs      提交 / 轮询 / 下载 / 续接 / 参考 / 视频编辑
│   ├─ VideoJobRunner.cs       任务执行与分流
│   ├─ VideoTaskStore.cs       任务模型 + 落盘（tasks.json）
│   ├─ VideoPool.cs            素材库数据层（分类 / 持久化）
│   ├─ ModelCatalog.cs         模型能力表
│   ├─ PickerHost.cs           文件选择器统一入口
│   ├─ Win32FilePicker.cs      纯 Win32 文件对话框
│   └─ Media/                  本地媒体层（ffmpeg / WinRT 兜底 / 切段 / 上传）
└─ Styles/DesignSystem.xaml    设计令牌（间距 / 字号 / 圆角 / 动效）
```

---

## 数据落在哪

运行时数据都在 `%LOCALAPPDATA%\WanStudio\`：

| 文件 | 内容 |
|---|---|
| `settings.json` | 平台地址、默认模型、输出目录 |
| `pool.json` | 素材库条目与分类 |
| `tasks.json` | 任务历史（**关掉应用也不丢**；未跑完的会标成「已中断」可重试） |
| `cache/` | 裁切片段、抽出的首帧 |

写到不可写目录时会自动回退：`%LOCALAPPDATA%\WanStudio` → `%USERPROFILE%\.wanstudio` → `%TEMP%\WanStudio`。

---

## QA 工具

`tools/` 下是开发期用的协议探针。它们**不依赖主程序源码**，各自独立跑：

| 工具 | 用途 |
|---|---|
| `mock-dashscope/` | DashScope 本地替身：getPolicy / OSS 上传 / 提交 / 轮询，可离线验证整条链路 |
| `api-check/` | 续接协议（first_clip）实测 |
| `upload-check/` | 上传链路实测 |
| `media-check/` | 媒体层（探测 / 切段 / 抽帧 / 拼接）实测 |
| `clear-key/` | 清除凭据库里的 API Key |
| `uia/drive.ps1` | UI 自动化驱动（读控件 / 点击 / 勾选） |
| `capture-window.ps1` | 窗口截图 |

跑 mock：

```bash
python tools/mock-dashscope/mock_server.py --port 8787
```

---

## 已知限制

- **`wan2.7-videoedit` 需要账号开通**。未开通会返回 `403 AccessDenied.Unpurchased`——这是账号权限问题，不是请求格式问题。
- 视频接口的 `watermark` 参数**默认值在不同模型上不一致**，代码里已显式传 `false`；若平台仍强制加标识，那是内容合规标识，无法通过参数去除。
- 长视频页的「本地拼接」只是按顺序硬拼，**不是模型级无缝**。
- 未打包应用的文件选择器早期版本会失败：WinRT 的 `FileOpenPicker` 在本场景下会直接抛 `COMException 0x80004005`，本项目已改用纯 Win32 对话框（`GetOpenFileNameW`）绕开。
- 上传大文件时不能用 chunked 传输——OSS 的 PostObject 不接受，会返回 `MalformedPOSTRequest`。代码里手工拼 multipart 并固定 `Content-Length`。

---

## License

MIT，见 [LICENSE](LICENSE)。