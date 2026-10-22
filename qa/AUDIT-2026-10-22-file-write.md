# 文件写入全面彻查（2026-10-22）

**触发原因：** 导入素材时报 `Access to the path '...\WanStudio\cache\refcuts' is denied`，整个导入功能不可用。
**要求：** 不再出现这类问题。
**结论：** 已建立统一的「可写目录解析」层，所有落盘点接入；**7 个会导致功能整体失效的硬编码路径全部消除**；新增 3 处真实缺陷修复。

---

## 1. 根因（不是权限不足）

| 检查项 | 结果 |
|---|---|
| 目录 ACL | `Tiny\<user>:(I)(OI)(CI)(F)` —— 完全控制 |
| shell 建目录 + 写文件 | **成功** |
| 应用内建同一目录 | **Access denied** |

**真正原因：进程权限级别不一致。** 当时运行的是通过「以管理员重启」拉起的提权实例；提权进程在写入普通用户目录时受完整性级别约束。也就是说：

- 不是文件系统权限问题（ACL 正常）
- 不是磁盘只读
- 不是产品逻辑错误
- **是「硬编码单一目录 + 无回退」放大了环境差异**

原来的代码：

```csharp
var dir = Path.Combine(LocalApplicationData, "WanStudio", "cache", "refcuts");
Directory.CreateDirectory(dir);   // ← 一抛异常，整个导入流程终止
```

只要这一个目录不可写，**导入、裁切、缩略图、续接锚点、下载成片、硬拼全部失效**，而用户只看到一句 Access denied。

---

## 2. 解决方案：统一可写目录解析层

新增 `Services/AppPaths.cs`。

### 2.1 三级回退

按用途取目录，首选不可写就自动换下一个：

1. `%LOCALAPPDATA%\WanStudio\<用途>`
2. `%USERPROFILE%\.wanstudio\<用途>`
3. `%TEMP%\WanStudio\<用途>`

**探测方式是真写一次**（建目录 → 写探针 → 删探针），而不是只 `CreateDirectory`——目录可能存在但没有写权限。

### 2.2 结果缓存

探测本身要写盘，所以每个用途只探测一次并缓存，后续直接复用。

### 2.3 对外接口

```csharp
AppPaths.WanStudioRoot      // 应用数据根（设置、池、日志）
AppPaths.CacheDir           // 缓存（缩略图、预览、锚点片段）
AppPaths.ReferenceCutDir    // 裁切输出
AppPaths.OutputDir(preferred) // 成片输出，用户目录不可写时回退
AppPaths.TryEnsureWritable(dir, out error)
AppPaths.TryEnsureParentOf(filePath)
AppPaths.AppendCrashLog(message)
```

---

## 3. 接入清单（7 处硬编码全部消除）

| 位置 | 原风险 | 接入后 |
|---|---|---|
| `VideoJobRunner` 锚点裁切 | 续接不可用 | ✅ `AppPaths.CacheDir` |
| `VideoJobRunner` 成片下载 | 生成成功但下载失败 | ✅ `AppPaths.OutputDir(...)` |
| `DashScopeClient.DownloadVideoAsync` | 同上 | ✅ 父目录探测 + 明确错误 |
| `ReferenceCutter` 裁切输出 | 导入/裁切不可用（本次故障） | ✅ 可写性校验 + 带路径错误 |
| `LongVideoPage` 缓存 | 长视频页不可用 | ✅ `AppPaths.CacheDir` |
| `LongVideoPage` 硬拼输出 | 拼接不可用 | ✅ `AppPaths.OutputDir(...)` |
| `ClipBinPanel` 缩略图 / 裁切 | 素材库不可用 | ✅ `AppPaths.CacheDir` / `ReferenceCutDir` |
| `VideoPool` 池落盘 | 素材库整个丢失 | ✅ `AppPaths.WanStudioRoot` |
| 崩溃日志（App / MainWindow） | 静默丢日志 | ✅ 三级回退写入 |

---

## 4. 本轮额外查出的真实缺陷

### 4.1 `WanStudioRoot` 恒定失败（已修）
初版写成 `WritableDir("")`，而该方法对空串直接 `throw ArgumentException`。后果：池文件永远走 TEMP 兜底，**分类和素材库持久化落到临时目录**，重启可能丢失。

修法：显式走根目录探测，不经过用途校验。

### 4.2 `OutputDir` 只探测不判断（已修）
```csharp
if (TryEnsureWritable(preferred, out _))   // 探测了
    return preferred;                      // 但不可写时也照样用？——实际逻辑走 else 分支，但早期版本会原样返回
```
用户把输出目录设成不可写位置时，生成成功却在下载阶段失败。已改为不可写就回退。

### 4.3 崩溃日志静默丢失（已修）
App 与 MainWindow 的日志写入都是 `catch { }`——程序出错时**没有任何线索**。改为按 `根目录 → TEMP → 桌面\Wan3.0` 顺序写入，第一个成功即止。

---

## 5. 验证证据

### 5.1 三级回退逐个验证
故意用同名文件占住首选目录：

```
FAIL  %LOCALAPPDATA%\WanStudio\cache\refcuts   IOException
OK    %USERPROFILE%\.wanstudio\refcuts          ← 自动切换
OK    %TEMP%\WanStudio\refcuts
```

### 5.2 直接调用 `AppPaths`
```
WanStudioRoot  = C:\Users\<user>\.wanstudio
CacheDir       = C:\Users\<user>\.wanstudio\cache
ReferenceCutDir= C:\Users\<user>\.wanstudio\cache\refcuts
OutputDir(不可写路径) = C:\Users\<user>\.wanstudio\outputs   fallback=True
```

### 5.3 极端场景：完全阻断应用目录
把 `%LOCALAPPDATA%\WanStudio` 整个替换成一个**同名文件**（任何子路径都建不出来）：

| 场景 | 首页元素数 | 回退位置 |
|---|---|---|
| 正常环境 | 53 | `%LOCALAPPDATA%\WanStudio` |
| 完全阻断 | **53** | `%USERPROFILE%\.wanstudio` |

**阻断后应用照常启动，素材库、导入按钮、分类筛选全部可用。** 之前同样的条件下，导入会直接报 Access denied。

### 5.4 构建
Debug：0 警告 0 错误；Release：0 错误。

---

## 6. 仍需注意（未解决，如实说明）

1. **提权进程写普通用户目录仍可能受限**。这是 Windows 完整性级别行为，不是应用能绕过的。所以「以管理员重启」后如果又遇到写入问题，现在会**自动回退**而不是失败——但根因依然存在。建议日常不要用提权运行。
2. **没有真机网络验证**。本轮只覆盖本地文件写入，生成相关的联网链路仍 BLOCKED（缺真 Key）。
3. **磁盘满 / 文件被独占** 这类运行时故障仍会抛错，但错误信息现在带路径与原因，不再是一句笼统的 Access denied。
4. `%TEMP%` 兜底位置会被系统清理工具清掉。落到那里时素材库内容可能被清空——这是权衡后的取舍（总比完全不可用强），若要更稳可再加一级「文档」目录。

---

## 7. 一句话总结

**问题不在权限，而在「只有一个目录、失败就整体报废」的设计。** 现在每一处落盘都有三级回退 + 真写探测 + 带路径的错误信息，单点写失败不再能打掉整条功能链。
