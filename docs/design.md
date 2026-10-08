# 设计规范（Design Tokens）

> 唯一数值来源是共享核心 `src/FloatingTransferStation.Core/Design/DesignTokens.cs`。WPF 资源字典（`src/FloatingTransferStation/Resources/MainWindowStyles.xaml`）引用这些值；Windows 端 STA 测试（`MainWindowInteractionTests.DesignTokens.cs`）锁定 WPF 镜像与 Core 相等。修改任何值必须同步本文件与 Core。
>
> 历史注记：2026-10-08 起 Mac 端已停止并从仓库移除，涉及另一端镜像的历史表述随之失效；带日期的历史记录不改写。

设计基线为 Windows 11 Fluent（微软官方动效规范），参考调研见 [docs/research/2026-09-22-ui-motion-resources.md](research/2026-09-22-ui-motion-resources.md)。

## 一、色板（浅色主题）

强调色为紫 `#6D5DFB` 及其派生色族（2026-09-22 决策：保留既有识别度）。

| Token | 值 | 用途 |
|---|---|---|
| WindowShellHex | `#F7F8FA` | 窗壳背景 |
| TabRailHex | `#EFF1F4` | 分类轨道背景 |
| CardHex | `#FFFFFF` | 卡片、编辑框背景 |
| BorderHex | `#E2E5EA` | 通用描边、窗壳描边 |
| PrimaryTextHex | `#1D1D1F` | 主文字 |
| SecondaryTextHex | `#5F6368` | 次文字、图标默认前景 |
| AccentHex | `#6D5DFB` | 强调色（焦点/置顶/选中/插入指示器） |
| DangerHex | `#D92D20` | 清空按钮 |
| HeaderHex | `#FBFBFC` | 头部表面、Compact 弹层 |
| HoverSurfaceHex | `#F4F5F7` | 悬停表面 |
| HoverBorderHex | `#C9CDD5` | 悬停描边 |
| SelectedCardHex | `#F3F1FF` | 选中卡片背景 |
| SelectedCardBorderHex | `#8B7CF6` | 选中卡片描边 |
| ActiveCategoryHex | `#E7E4FF` | 活动分类层、按下态 |
| DropTargetHex | `#EAE7FF` | 拖放目标层 |
| ScrollThumbHex | `#B8BDC7` | 滚动条滑块 |
| ScrollThumbHoverHex | `#969CA7` | 滑块悬停 |
| StatusSurfaceHex | `#FCFCFD` | 状态浮层背景 |
| OnAccentHex | `#FFFFFF` | 强调色上的前景（对勾、徽标文字） |

深色主题：跟随系统亮暗切换（`WM_SETTINGCHANGE` 的 `ImmersiveColorSet` + `AppsUseLightTheme` 注册表），按同键位提供第二套字典（`Resources/DesignTheme.Light.xaml` / `DesignTheme.Dark.xaml`，由 `DesignThemeManager` 整本替换）。1.8.0 起用户可在设置中强制浅色/深色（`AppPreferences.ThemeMode`）：强制模式不再响应系统亮暗广播；`FTS_PREVIEW_THEME` 预览覆盖仍为最高优先。

| Token | 浅色 | 深色 |
|---|---|---|
| WindowShellHex | `#F7F8FA` | `#202021` |
| TabRailHex | `#EFF1F4` | `#2B2B2C` |
| CardHex | `#FFFFFF` | `#2D2D2E` |
| BorderHex | `#E2E5EA` | `#414143` |
| PrimaryTextHex | `#1D1D1F` | `#FFFFFF` |
| SecondaryTextHex | `#5F6368` | `#C8C8CA` |
| AccentHex | `#6D5DFB` | `#9C8CFF` |
| DangerHex | `#D92D20` | `#FF8A7A` |
| HeaderHex | `#FBFBFC` | `#272728` |
| HoverSurfaceHex | `#F4F5F7` | `#343436` |
| HoverBorderHex | `#C9CDD5` | `#505053` |
| SelectedCardHex | `#F3F1FF` | `#343054` |
| SelectedCardBorderHex | `#8B7CF6` | `#7A6DF2` |
| ActiveCategoryHex | `#E7E4FF` | `#3A3560` |
| DropTargetHex | `#EAE7FF` | `#322E52` |
| ScrollThumbHex | `#B8BDC7` | `#505054` |
| ScrollThumbHoverHex | `#969CA7` | `#6E6E74` |
| StatusSurfaceHex | `#FCFCFD` | `#282829` |
| OnAccentHex | `#FFFFFF` | `#FFFFFF` |

深色主题的卡片阴影为 `#000000`、透明度 0.35（浅色为 `#1D1D1F`、0.12）。

## 二、动效时长（三档 + 场景映射）

三档取自微软 `timing-and-easing` 官方表：**83ms**（微交互、按压、减弱动效纯淡入）/ **167ms**（悬停、快速反馈、标准入场）/ **250ms**（标准控件动画）。

| 场景 | Token | 值 |
|---|---|---|
| 面板展开内容入场 | PanelExpandContentMs | 167 |
| 分类切换内容过渡 | PanelSwitchContentMs | 140 |
| 减弱动效纯淡入 | ReducedMotionFadeMs | 83 |
| 分类轨道揭示 | CategoryRevealMs | 120 |
| 面板收起出场 | PanelCollapseExitMs | 200 |
| 悬停反馈 | HoverFeedbackMs | 167 |
| 按压反馈 | PressFeedbackMs | 83 |
| 显隐淡入淡出 | FadeVisibilityMs | 120 |
| 删除反馈 | DeleteFeedbackMs | 120 |
| 新条目交错步长 | ItemEntranceStaggerMs | 40（区间 30–50 取中） |

## 三、缓动曲线

| 场景 | WPF KeySpline |
|---|---|
| 入场（减速） | `0,0,0,1` |
| 出场（加速） | `1,0,1,1` |
| 减弱动效 | Linear |

## 四、圆角

| Token | 值 | 用途 |
|---|---|---|
| DwmCornerRadius（Windows） | 8 | Windows 窗壳：DWM 四角圆角与内容层自裁剪；可见态窗口完整落在工作区内，四角圆角恒定（2026-10-08 决策删除 1.8.0 的右缘越屏裁切，1.21.0 起） |
| HeaderSurfaceCornerRadius | 11（左上） | 头部表面 |
| CardCornerRadius | 8 | 卡片、分类 Tab |
| ControlCornerRadius | 7 | 头部按钮、状态弹层、活动层、徽标 |
| PinButtonCornerRadius | 6 | 置顶按钮 |
| CheckboxCornerRadius | 5 | 选择框 |
| ScrollThumbCornerRadius | 2 | 滚动条滑块 |

## 五、间距与位移

| Token | 值 | 用途 |
|---|---|---|
| ContentEntranceOffsetPx | 6 | 内容入场位移 |
| CollapseExitOffsetPx | 24 | 面板收起出场向轨道方向的位移 |
| CardInsetPx | 12 | 卡片水平内缩 |
| CardGutterPx | 4 | 卡片纵向间距 |
| CardPaddingPx | 12 | 卡片内边距 |

结构性尺寸（轨道宽 58、面板宽 280–640、最小窗高 360 等）不属于设计 token，权威来源是 Core 的 `WindowSettings`。

## 五·B、层级（Elevation）

- 卡片模板为三层结构（1.11.3 起）：根容器（`CardRootStyle`，承载外距与悬停上浮，无 Effect）+ 阴影层（`CardShadowLayerStyle`，与卡片同形状的不透明底 + 共享冻结 `CardShadowEffect`，不承载内容、不参与命中）+ 内容面（`CardContainerStyle`，不透明卡片底、描边与焦点态，**不得携带 Effect**）。带 `Effect` 的元素会把整个子树渲染进中间位图并强制关闭 ClearType（卡片正文发糊主因），阴影因此必须隔离在文字子树之外；小字号文本（卡片正文/复盘编辑器 14、状态文本 12、徽标计数 9）按像素对齐渲染（`TextFormattingMode=Display`），16px 标题保持 Ideal。
- 卡片统一携带共享冻结的 `CardShadowEffect`（浅色 `#1D1D1F`/0.12、深色 `#000000`/0.35，模糊 8、深度 2、方向 270°），不逐卡片实例化以保证虚拟化列表性能。
- 卡片悬停时以渲染位移上浮 1px（`LiftAnimation.LiftedOffsetPx = -1`，167ms 减速曲线，回落用加速曲线），不参与布局、不影响命中测试。
- 滚动条滑块悬停时经 6px 宽悬停层交叉淡入表现展宽（基宽 4px）。

## 五·C、窗口材质（Mica，Windows 11）

- 窗口壳弃用 `AllowsTransparency` 分层透明，改用 `WindowChrome`（`GlassFrameThickness=-1` 整窗玻璃帧）承载 DWM 材质，经 `DWMWA_SYSTEMBACKDROP_TYPE` 应用 Mica；材质不可用的旧系统回退到不透明壳色。
- 圆角由 DWM（`DWMWA_WINDOW_CORNER_PREFERENCE=ROUND`）裁剪，内容层以 `DesignTokens.DwmCornerRadius = 8` 四角同步自裁剪，保证渲染位图与屏幕一致。可见态窗口完整落在工作区内，四角圆角在任务栏停靠侧、多显示器、显示器插拔与系统广播下恒定（2026-10-08 决策删除 1.8.0 的右缘越屏裁切与内容层右内缩，1.21.0 起）；`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` 后按当前面板状态重新贴齐。
- 壳表面为半透明 Mica 色调（浅 `#CCF7F8FA`、深 `#CC202021`，`WindowShellTintHex` / `WindowShellTintDarkHex`），深浅切换时同步 `DWMWA_USE_IMMERSIVE_DARK_MODE`。
- **迁移原子性（1.8.1 起）**：一次面板状态迁移只允许一次窗口矩形变更。已创建 HWND 的窗口经单次 Win32 `SetWindowPos` 应用终态矩形（物理像素；两条锚定边各自取整后相减，右缘与底缘不因取整漂移），随后在 `WM_WINDOWPOSCHANGING` 守卫下对齐 WPF 尺寸/位置账本——守卫把对齐期间任何中间矩形改写回终态，陈旧 DP 混合值不会重推出可见中间态。构造期（HWND 未创建）保留纯属性路径。STA 契约：观察到的每个窗口矩形 ∈ {初始, 终态}（`*ChangesTheObservedWindowRectangleOnlyBetweenStableStates` 系列锁定）。禁止用 `SetWindowRgn` 或动效/延迟/隐藏窗口装饰性掩盖。
- 本机预览与截图取证：设 `FTS_PREVIEW_DATA_DIR`（隔离数据目录、独立单实例锁）与可选 `FTS_PREVIEW_THEME=dark|light` 启动，不影响已安装应用。

## 五·D、卡片文字避让与一次性贴边隐藏（1.20.0）

- **文字避让操作列（1.20.0 起）**：文字卡正文右侧为右上角操作按钮条预留排他空间，任何时刻（悬停/非悬停）零像素争用。内缩量唯一来源是 WPF 派生资源 `CardTextReservedRightInset`（两列 `CardOperationColumnWidth` 30×2 + 4 DIP 间隙 = 64），不得在别处写死第二个数值；命中层对半分区与图片卡全幅不变。280px 最窄面板下每行文字约损失 21% 宽度，是消除像素争用的确定代价。
- **一次性贴边隐藏（1.20.0 起）**：头部按钮（暂停符右侧、「箭头收入右缘」图标）切换语义；待命期间按钮转强调色、头部操作区常显（鼠标离开恰是最需要看到待命指示的时刻）。隐藏 = 整窗**同尺寸纯移动**到所有显示器右缘之外（含 DWM 阴影余量 32 物理像素，目标矩形与每一个显示器零交集），面板保持展开、内容冻结，沿用迁移原子性（隐藏与恢复各一次矩形变更）；触发融入既有 250ms 收起节奏，抑制条件复用 `PanelStateMachine.WouldCollapse`。回位区 = 隐藏时刻屏幕内可见矩形外扩 8 物理像素，以物理像素保存（此后不做运行时 DPI 换算），光标驻留 ≥200ms 恢复；恢复即回原位原形并自动解除（一次性），热键与显示器变化兜底。图标遵守 14.5×14.5 + 1.5 描边规范；窗口位移不做动画（全仓放置迁移一致）。行为契约全文见[设计契约清单](design-contracts.md) 第 16、17 条。

## 六、动效原则（评审基准）

1. 进入减速、出场加速，退出比进入快；面板展开 167ms / 收起 200ms 不对称。
2. 动效可中断、可重定向：新动画或停止必须先复位终值；不得让用户等动画播完。
3. 动效不阻断命中测试；不作为传达信息的唯一方式。
4. 系统开启「减弱动效」或用户在设置中关闭动效（`AppPreferences.AnimationsEnabled=false`，本地覆盖同一系统键）时，全部退化为 83ms 纯淡入或立即定格终值；恢复开启即移除覆盖、重新跟随系统。
5. 收起动画不得破坏「收起视觉交接」防闪烁机制与面板状态机语义。
6. 图标取自 fluentui-system-icons（MIT）几何重绘，不随应用分发字体文件。
