# 设计规范（Design Tokens）

> 唯一数值来源是共享核心 `src/FloatingTransferStation.Core/Design/DesignTokens.cs`。WPF 资源字典（`src/FloatingTransferStation/Resources/MainWindowStyles.xaml`）与 Mac 端常量（`src/FloatingTransferStation.Mac/MainWindow.cs`）镜像这些值；Windows 端 STA 测试（`MainWindowInteractionTests.DesignTokens.cs`）锁定 WPF 镜像与 Core 相等，Mac 端直接引用 Core 常量，编译期一致。修改任何值必须同步本文件、Core 与两端镜像。

设计基线为 Windows 11 Fluent（微软官方动效规范），参考调研见 [docs/research/2026-09-22-ui-motion-resources.md](research/2026-09-22-ui-motion-resources.md)。

## 一、色板（浅色主题）

强调色两端统一为紫 `#6D5DFB` 及其派生色族（2026-09-22 决策：保留 Windows 既有识别度，Mac 放弃青绿 `#327A72`）。

| Token | 值 | 用途 |
|---|---|---|
| WindowShellHex | `#F7F8FA` | 窗壳背景 |
| TabRailHex | `#EFF1F4` | 分类轨道背景、Mac 非活动分类底 |
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

深色主题：跟随系统亮暗切换，按同键位提供第二套字典（引入于界面与动效升级 S5）。

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

| 场景 | WPF KeySpline | Avalonia Easing |
|---|---|---|
| 入场（减速） | `0,0,0,1` | CubicEaseOut（现有实现等价） |
| 出场（加速） | `1,0,1,1` | CubicEaseIn |
| 减弱动效 | Linear | Linear |

## 四、圆角

| Token | 值 | 用途 |
|---|---|---|
| ShellCornerRadius | 12（左上/左下，右贴边直角） | 窗壳与描边 |
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
| CardInsetPx | 12 | 卡片水平内缩 |
| CardGutterPx | 4 | 卡片纵向间距 |
| CardPaddingPx | 12 | 卡片内边距 |

结构性尺寸（轨道宽 58、面板宽 280–640、最小窗高 360 等）不属于设计 token，权威来源是 Core 的 `WindowSettings`。

## 六、动效原则（评审基准）

1. 进入减速、出场加速，退出比进入快；面板展开 167ms / 收起 200ms 不对称。
2. 动效可中断、可重定向：新动画或停止必须先复位终值；不得让用户等动画播完。
3. 动效不阻断命中测试；不作为传达信息的唯一方式。
4. 系统开启「减弱动效」时全部退化为 83ms 纯淡入或立即定格终值（Windows 走 `SystemParameters.ClientAreaAnimationKey` 既有路径）。
5. 收起动画不得破坏「收起视觉交接」防闪烁机制与面板状态机语义。
6. 图标取自 fluentui-system-icons（MIT）几何重绘，不随应用分发字体文件。
