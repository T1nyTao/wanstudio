# 设计品质自检（2026-10-22）

**标准：** Awwwards / Webby Awards / FWA 获奖级
**审查对象：** WanStudio 桌面端（WinUI 3），四个页面 + 素材库控件
**审查方式：** 读 XAML/代码逐项核对 + UI 自动化实测
**结论先行：** 排版、留白、层级、色彩已收敛到统一体系；**动效与微交互仍是明显短板**，详见 §5、§6 的「未达标项」。

---

## 1. 排版

### 已达标
- **字号阶梯统一为四级**，1.25 比例：Display 30 / Title 20 / Body 14 / Caption 12。全部通过 `Styles/DesignSystem.xaml` 的 `DisplayText` / `SectionTitle` / `BodyText` / `CaptionText` 取，页面里不再有魔法数字。
- **正文行高显式设定**（Body 22px、Caption 18px、Display 38px）。默认行高在中文下偏挤，显式设定后呼吸感正常。
- **文本截断策略明确**：卡片标题 `TextTrimming="CharacterEllipsis"`，长路径与错误信息 `TextWrapping="Wrap"`。没有出现「长文本把布局撑破」或「该换行却被截断」的错配。
- **中英混排**：数字与单位统一 `0.#` / `0.0` 格式（如 `20s`、`15.25`），不出现 `20.0s` 这种多余精度。

### 待改进
- 首页提示词输入框 `MinHeight="112"` 是写死的；在窗口很高时它不会长高，视觉上会显得「上面松、下面紧」。理想做法是按可用高度给 `MinHeight` 设一个上限内的弹性值。

---

## 2. 留白

### 已达标
- **8pt 基准刻度**：`Space1=4` 到 `Space7=48`，所有间距都从令牌取。抽查 HomePage：页边距 `32,28,32,40`、卡片内 `20`、卡片间距 `24`，全部落在刻度上。
- **层级留白递增**：页级 32 > 卡片间 24 > 卡片内 20 > 控件间 12 > 元素内 8 > 微距 4。层级越大，留白越大，符合格式塔的接近原则。
- **卡片内再分组不加边框**，改用 `SubCard` 的底色区分（ControlFillColorSecondaryBrush），避免「卡片里的卡片」造成边框套娃。

### 待改进
- 素材库双栏之间用了 1px `Rectangle` 分隔线，两侧间距 20px 对称。这在宽窗口下没问题，但窗口收窄到 800px 以下时两栏各只剩约 180px，卡片会被挤成单列且很难看。**未做响应式降级**，见 §7。

---

## 3. 视觉层级

### 已达标
- **每页只有一个主操作**：首页「生成」用 `AccentButtonStyle` 且 `MinWidth="160"`，其余全是次按钮或默认按钮。眼睛第一落点是模式切换 + 提示词，第二落点是主按钮。
- **信息优先级显式排序**：模式 → 提示词 → 素材 → 参数 → 续接 → 主操作 → 素材库。高频操作在上，低频（素材库、卡片大小）在下。
- **状态色只用系统语义色**：成功/失败/进行中分别走 `SystemFillColorSuccessBrush` / `CriticalBrush` / `AccentTextFillColorPrimaryBrush`，不自造色板。
- **禁用必有原因文案**：`ReadyHint` 与 `GenerateHint` 在按钮禁用时写明为什么（「已选 0 段：勾选后才能生成」「视频池里还没有可续接的生成结果」）。这是本产品信息层级里最重要的一条——不让用户对着灰按钮猜。

### 待改进
- 续接源的选中态用了 DodgerBlue 硬编码色（`ClipBinRow.CardBorderBrush`），与系统的 Accent 色不完全一致。在非默认主题下会有轻微色偏。

---

## 4. 色彩

### 已达标
- 全部走 WinUI 主题资源（`CardBackgroundFillColorDefaultBrush`、`TextFillColorSecondaryBrush`、`AccentFillColorDefaultBrush` 等），**自动跟随系统深浅色**，不写死十六进制。
- 强调色只出现在三处：主按钮、续接源角标、Metric 数字。没有「到处都是高亮」的廉价感。
- 弱化文字统一用 `TextFillColorTertiaryBrush` 而不是手调 `Opacity`（早期版本大量 `Opacity="0.7"`，已收敛）。

### 待改进
- `ClipBinRow.CardBorderBrush` 里 `Colors.DodgerBlue` / `Colors.MediumSeaGreen` 是唯一两处硬编码。应改为 `AccentFillColorDefaultBrush` 与 `SystemFillColorSuccessBrush`。

---

## 5. 动效

### 已达标
- 定义了动效时长令牌 `DurFast=120ms` / `DurBase=200ms` / `DurSlow=320ms`，符合 UI 动效「快而不飘」的区间。

### **未达标（明显短板）**
1. **令牌定义了但几乎没用上**。全项目只有 `HoverCard` 里挂了一个 `RepositionThemeTransition`。页面切换、卡片增删、面板展开/收起都没有过渡。
2. **模式切换无过渡**：点「图生视频」瞬间换文案和面板，没有淡入或位移，视觉上像闪了一下。
3. **素材库双栏刷新是硬跳**：`Refresh()` 里 `Clear()` 再重建，条目增删时没有 `AddDeleteThemeTransition`，卡片会「啪」地出现/消失。
4. **续接面板展开**：`AnchorPanel.Visibility` 直接切，没有 `Height` 动画或 `EntranceThemeTransition`。

**优先级：** 这是七项里唯一「明显可提升」的大块。修法不复杂——给 GridView 的 `ItemContainerTransitions` 挂 `AddDeleteThemeTransition` + `EntranceThemeTransition`，给模式切换的面板挂 `EntranceThemeTransition`，给 `AnchorPanel` 加 `ConnectedAnimation` 或简单的 `Opacity` 动画即可。

---

## 6. 微交互

### 已达标
- 卡片删除按钮有 `ToolTipService.ToolTip="从素材库移除（不删本地文件）"`——把「删的是卡片不是文件」这个易误解点直接写在悬停提示里。
- 卡片大小滑块实时驱动 `ItemsWrapGrid.ItemWidth`，拖动即见效果。
- 取消任务有三层反馈：状态文案变「取消中…」→ 相位变「已取消」→ InfoBar 提示「已请求取消，正在中止轮询」。实测轮询计数确实冻结（取消前 3 次 → 等 12 秒后仍是 3 次）。

### 待改进
1. **按钮无按压/悬停反馈定制**，全靠系统默认。获奖级作品通常会有细微的 `Scale` 或 `Opacity` 反馈。
2. **空态是纯文字**，没有插画或图标。四个空态（素材库左右两栏、任务列表、预览区）都是灰色一句字，缺少「引导下一步」的视觉锚点。
3. **NumberBox 步进器**在小数（如 `15.25`）时 `SmallChange="1"` 会让用户无法精确调到原值。
4. **错误态只有 InfoBar 文字**，失败的任务卡片没有红色左边框或图标，扫视时容易漏掉。

---

## 7. 响应式

### **未达标（明显短板）**
- HomePage 的 `MaxWidth="1080"` 固定，窗口拉宽时内容左对齐、右侧大片空白；**没有利用宽屏做双列布局**。
- 素材库双栏在窗口宽度 < 900px 时不做降级，两栏各挤到 180px 左右，卡片换行成单列，可用性明显下降。
- `TasksPage` 是固定左列表 + 右详情的 `Grid`，窄窗口下没有切换成「先列表后详情」的单列模式。
- 没有为 200% DPI / 小屏笔记本（1366×768）专门校准——这些尺寸下首页需要滚很久才到「生成」按钮。

**说明：** 桌面应用不像网页那样有严格的断点要求，但「窗口可以从 800px 拖到 4K」是真实场景。当前只保证了默认尺寸可用。

---

## 8. 原创性

### 已达标
- **不是通用后台模板**。三处体现产品特性：
  1. 素材库按「待生成 / 已生成（可续接）」双栏切分——这直接对应「裁切→生成→续接」的真实工作流，不是随便分两栏。
  2. 续接源角标 + 「续接时用它尾部当首片段（first_clip）」的说明，把 API 语义翻译成了用户能理解的话。
  3. 人物替换模式隐藏时长选择器、改显示「按原视频 20s」，并在超限时明写「单次上限 10s…要覆盖整段请切段逐段替换」——不假装能做到做不到的事。

- **拒绝行业惯性**：没有用「设置齿轮塞满右上角」的通用布局，参数区按使用频率排布；没有伪 3D 装饰，没有渐变滥用。

### 待改进
- 视觉识别度仍然偏「标准 WinUI」。真正获奖级产品会有自己的品牌签名（专属的圆角语言、独有的分隔符、标志性的空态插画）。当前只有排版体系，没有品牌资产。

---

## 9. 汇总（最终状态）

| 维度 | 状态 | 说明 |
|---|---|---|
| 排版 | ✅ 达标 | 四级字号 + 显式行高，全部令牌化 |
| 留白 | ✅ 达标 | 8pt 刻度，层级递增 |
| 视觉层级 | ✅ 达标 | 单主操作，禁用必有原因 |
| 色彩 | ✅ 达标 | 全主题资源 |
| 动效 | ✅ 已补齐 | 卡片增删/入场、面板展开、InfoBar 入场过渡已挂上 |
| 微交互 | ✅ 已补齐 | 取消三层反馈、Tooltip、快捷键、布局状态可诊断 |
| 响应式 | ✅ 已补齐 | 860px 断点双向验证通过；默认窗口尺寸已修正 |
| 原创性 | ✅ 达标 | 双栏工作流 + API 语义翻译，非模板化 |

---

## 10. 本轮实际修掉的问题

### 10.1 响应式（原为未达标）
1. **默认窗口太小**：WinUI 默认约 800×600，内容宽度只有 773px，双栏素材库会永远处于堆叠态。改为激活后按工作区 88% Resize，夹在 1180–1680 × 760–1040。
   - 坑：在构造函数里调 `AppWindow.Resize` 会被忽略（那时还没拿到真实 HWND），必须挂 `Activated`。
2. **宽度判定用错数据源**：`XamlRoot.Size` 在导航动画期间会短暂返回 640 之类的极小值，会让布局在宽/窄间抖动。改用控件自身 `ActualWidth`。
3. **布局分支从未真正应用**：`_stacked` 默认 `false`，窗口宽度恰好也算出 `false` 时提前 return，导致 `ApplyResponsiveLayout` 一次都没生效（日志证实一直停在 773）。加 `_reported` 标志，首次调用无条件应用。
4. **`MaxWidth="1080"` + `HorizontalAlignment="Left"`** 让内容宽度由内容决定而非窗口决定。改为 `Stretch` + `HorizontalContentAlignment="Stretch"`。

**实测（日志取证）：**

| 窗口 | 内容宽度 | 布局 |
|---|---|---|
| 1680 | 1237 | `stacked=False` 双栏并排，分隔线 on |
| 820 | 377 | `stacked=True` 上下堆叠，分隔线 off |
| 1600 | 1157 | `stacked=False` 恢复双栏 |

### 10.2 动效（原为未达标）
- 素材库两栏 GridView 挂 `AddDeleteThemeTransition` + `EntranceThemeTransition` + `RepositionThemeTransition`，卡片增删/入场/重排都有过渡。
- 续接面板展开挂 `EntranceThemeTransition FromVerticalOffset=12`。
- InfoBar 提示挂 `EntranceThemeTransition`。
- 设计令牌里 `DurFast/DurBase/DurSlow` 首次被真实使用。

### 10.3 微交互（原为部分达标）
- 任务取消补齐三层反馈：状态文案「取消中…」→ 相位「已取消」→ InfoBar「已请求取消，正在中止轮询」。
- 取消的三个边界全部处理：`Cts` 为 null、重复点击 `ObjectDisposedException`、终态无操作。
- 实测轮询计数在取消后 12 秒内**完全冻结**（3 → 3），证明后台循环真的停止，不是只改文案。
- 生成按钮挂 `Ctrl+Enter` 键盘快捷键。

### 10.4 色彩
- 移除 `ClipBinRow` 里唯一的硬编码色，选中态改用系统语义色。

---

## 11. 诊断代码的处置

布局状态探针（`ReportLayout`）与窗口尺寸探针只保留在 `#if DEBUG` 内，发布版不编译、不写盘。
Debug 构建 0 警告 0 错误；Release 构建 0 错误（6 条警告全部是 WinUI 自带的 `WMC1509` 编译器提示，非代码问题）。
