# UI 动效与界面资源调研（2026-09-22）

> 交接材料之一。由规划窗口产出（多路并行网络调研，链接均经检索验证，个别标注“未验证”）。
> 配套文件：[项目文档梳理报告](2026-09-22-docs-review.md)、[新窗口执行提示词](../handoff/2026-09-22-ui-motion-execution-prompt.md)。
> 调研目标：为悬浮中转站（Windows 端 WPF、Mac 端 Avalonia 11.3）寻找 Win11 Fluent 风格的界面与动效改造资源。

## 〇、结论速览

| 决策点 | 结论 | 依据 |
|---|---|---|
| UI 库 | **默认不引入第三方 UI 库**，手工对齐 Fluent 规范；`lepoco/wpfui` 仅作为参考实现与备选（引入需用户确认） | 本项目 UI 高度定制（贴边悬浮窗、自绘控件），零第三方 UI 依赖是现状优点；wpfui 的 Mica/主题管理价值可在 Plan 阶段单独评估 |
| 动效参数 | 采用微软官方三档时长 83/167/250ms + 进出场双曲线（进 `cubic-bezier(0,0,0,1)`、出 `(1,0,1,1)`），WPF 用 `KeySpline` 精确复刻 | WinUI 官方 timing-and-easing 表 |
| 图标 | 从微软官方 `fluentui-system-icons`（MIT，SVG）取几何重绘现有 Path，不引入字体文件 | 规避 Segoe Fluent Icons 字体分发许可 |
| 材质（Mica/亚克力） | 列为**独立评估项**：与现有 `AllowsTransparency=True` 分层窗口存在技术冲突，需先做小样验证 | DWM `DWMWA_SYSTEMBACKDROP_TYPE` 要求非分层窗口路径 |
| 深色模式 | 跟随系统；先把所有散落颜色 token 化，再实现亮/暗两套资源 | 现状全部硬编码浅色 |
| Mac 端 | 与 Windows 共用同一组设计 token 数值；动效用 Avalonia 原生 Transitions；FluentAvalonia 仅评估不默认引入 | Mac 端当前零动效、配色与 Windows 不一致 |

## 一、本项目 UI/动效现状盘点（改造前基线）

Windows 端（WPF）：

- 样式集中在 `src/FloatingTransferStation/Resources/MainWindowStyles.xaml`：扁平浅色（壳 `#F7F8FA`、强调紫 `#6D5DFB`）、圆角 7–12、**无深色模式、无 Mica/亚克力、无阴影层级（`Effect=x:Null`）**。
- 窗口形态：`AllowsTransparency=True` 的无边框分层窗口（`MainWindow.xaml`），圆角靠自绘 `WindowShellClip`，1px 自绘描边，无系统阴影/深度。
- 动效现状（`MainWindow.xaml.cs:26-34`、`CategoryFeedbackAnimation.cs`、`MainWindow.VisualTransitions.cs`）：
  - 分类激活层/标记：120ms 透明度淡入，CubicEase Out；
  - 面板展开：167ms 淡入 + 6px X 位移；分类切换 140ms；减弱动效 83ms；
  - 分类标签揭示：120ms 淡入 + 6px 位移；
  - **收起为硬切换**（透明度直接置 0/1 换布局，无过渡动画）；
  - hover 颜色/边框变化全部瞬时跳变（Style Trigger 无过渡）；StatusOverlay、选择框、图钉按钮的显隐也是瞬时；
  - 已正确尊重系统“减弱动效”（`SystemParameters.ClientAreaAnimationKey`），改造后必须保留。
- 图标：手绘 `Path` 几何（图钉/垃圾桶/恢复），风格不统一、细节粗糙。
- 复盘编辑器硬编码 `Background=White`（`MainWindow.xaml` 复盘区），阻碍任何主题化。

Mac 端（Avalonia，纯代码构建 UI）：

- 配色与 Windows 完全不同（墨色 `#243447`、青绿强调 `#327A72`、底 `#F3F5F2`），**两端无共享设计 token**。
- 全项目 **零动效代码**（无 Transitions、无淡入淡出），展开/收起瞬时切换。

## 二、WPF / Win11 UI 库对比（2026-09 实测数据）

| 库 | 链接 | 状态 | 许可 | 对本项目价值 | 优先级 |
|---|---|---|---|---|---|
| **WPF UI (lepoco)** | https://github.com/lepoco/wpfui ・ https://wpfui.lepo.co/documentation/ | 9.7k★，v4.3.0（2026-05），活跃 | MIT | 最完整 Win11 Fluent 实现：`FluentWindow`/`WindowBackdrop.ApplyBackdrop(hwnd, Mica)` 可对任意句柄（含无边框悬浮窗）施材质；`ApplicationThemeManager` 明暗切换；`SymbolIcon` 图标 | P0（参考实现/备选依赖） |
| **.NET 9/10 内置 WPF Fluent 主题** | https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/dotnet-90 ・ [ThemeMode API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.thememode) | 官方，标注 "still in progress" | .NET | 零依赖：`Application.ThemeMode="Light/Dark/System"` 或合并 `Fluent.xaml`；无 Mica、控件少；已知坑：合并字典方式会让 VS XAML 设计器白屏，建议用 `ThemeMode` 属性 | P1（深色模式候选路径） |
| **Fluent UI System Icons** | https://github.com/microsoft/fluentui-system-icons | 10.9k★，持续更新 | MIT | 官方图标库（SVG），转 XAML Path 使用，规避字体分发许可 | P0 |
| MahApps.Metro | https://github.com/MahApps/MahApps.Metro | 9.8k★，v3.0（2023 中） | MIT | Metro ≠ Fluent，风格不对口 | P2 |
| HandyControl / fork | https://github.com/HandyOrg/HandyControl | 7.2k★，原库 2024-02 后放缓 | MIT | 无 Win11 原生支持 | P2 |
| Panuon.WPF.UI | https://github.com/Panuon/Panuon.WPF.UI | 1.3k★，1.3.0.2（2025-03） | Apache-2.0 | 无 Mica/暗色 | P2 |

社区共识（r/dotnet、r/csharp 实测帖）：Win11 Fluent 首选 wpfui；轻量场景用内置 Fluent 主题；wpfui 公认短板是文档/教程薄弱。相关讨论：[wpfui vs .NET 9 内置 Fluent](https://www.reddit.com/r/dotnet/comments/1huygnw/lepoco_wpf_ui_library_vs_net_9_built_in_fluent_ui)、[WPF UI x WPF 团队合作讨论](https://github.com/lepoco/wpfui/discussions/880)。

## 三、微软官方设计与动效规范（核心数值）

**时长三档**（[timing-and-easing](https://learn.microsoft.com/en-us/windows/apps/design/motion/timing-and-easing)）：`ControlFasterAnimationDuration` 83ms（微交互）/ `ControlFastAnimationDuration` 167ms（悬停、按压等快速反馈）/ `ControlNormalAnimationDuration` 250ms（标准控件动画）；大元素/长距离另设 333ms。

**缓动曲线**（同页官方表，可直接映射为 WPF `KeySpline`）：

| 场景 | cubic-bezier | WPF KeySpline | 时长 |
|---|---|---|---|
| 直接入场 | (0,0,0,1) | `0,0,0,1` | 167/250/333ms |
| 点对点移动 | (0.55,0.55,0,1) | `0.55,0.55,0,1` | 167/250/333ms |
| 直接退场 | (0,0,0,1)+必须叠淡出 | `0,0,0,1` | 167ms |
| 轻柔退场 | (1,0,1,1) | `1,0,1,1` | 167ms |
| 最小动效（纯淡入淡出） | Linear | Linear | 83ms |
| 弹性入场（3 关键帧） | (0.85,0,0,1)→(0.85,0,0.75,1)→(0.85,0,0,1) | 关键帧 | 167/167/333ms |

其他官方资源：

- [Motion in Windows 总览](https://learn.microsoft.com/en-us/windows/apps/design/motion/)：四原则 Connected/Consistent/Responsive/Delightful；[连接动画](https://learn.microsoft.com/en-us/windows/apps/design/motion/connected-animation)（返回导航 Direct=150ms 直线减速；prepare→start 间隔 ≤250ms）；[视差](https://learn.microsoft.com/en-us/windows/apps/design/motion/parallax)。
- [Fluent 2 Design – Motion](https://fluent2.microsoft.design/motion)：原则 Functional/Natural/Consistent/Appealing；编排用**短偏移量交错（stagger）**；四种过渡模式（enter-exit/elevation/top-level 交叉淡化/container transform）；无障碍动效要求。
- [Segoe Fluent Icons 字体](https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font)：Win11 系统自带含字形表，**不可随应用分发**。
- [DWMWINDOWATTRIBUTE](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)：`DWMWA_SYSTEMBACKDROP_TYPE`(38)=Mica/Acrylic、`DWMWA_USE_IMMERSIVE_DARK_MODE`(20)、`DWMWA_WINDOW_CORNER_PREFERENCE`(33)。
- [WPF Easing Functions](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/easing-functions)、WPF Mica 参考实现 [wpfui WindowBackdrop 源码](https://github.com/lepoco/wpfui/blob/main/src/Wpf.Ui/Controls/Window/WindowBackdrop.cs)、独立教程 [tvc-16.science/coding/wpf-mica](https://tvc-16.science/coding/wpf-mica)（历史沿革：`DWMWA_MICA_EFFECT` 已废弃，22H2+ 用 SYSTEMBACKDROP_TYPE）。

## 四、苹果官方与通用动效原则

- [Apple HIG – Motion](https://developer.apple.com/design/human-interface-guidelines/motion)：不给毫秒表，给行为准则——反馈简短精准、高频交互不加多余动效、动效可中断、尊重 Reduce Motion、方向符合空间逻辑。
- [WWDC18 Session 803 "Designing Fluid Interfaces"](https://developer.apple.com/videos/play/wwdc2018/803/)：即时响应、可中断动画、**用弹簧模型（阻尼+响应频率）替代固定时长**、手势结束用动量投影预测终点。对贴边吸附交互直接适用。
- [Apple Design Resources](https://developer.apple.com/design/resources/) / SF Symbols（27 版，7000+ 可动效符号）。
- 通用工具站：[easings.net](https://easings.net/)（30 种缓动速查）、[cubic-bezier.com](https://cubic-bezier.com/)（曲线可视化）、[Material motion 时长分档（M2 存档）](https://web.archive.org/web/20190815000000/https://material.io/design/motion/speed.html)（小控件 100ms、抽屉 250/200ms 不对称等）、[Codrops](https://tympanus.net/codrops/)（动效案例库）。

**跨规范第一性原则**（动效改造的评审基准）：

1. 进入用减速曲线、退出用加速曲线，退出比进入更快。
2. 时长与元素大小、移动距离成正比；取“不显突兀的最短时长”；工具型微交互集中在 83–250ms。
3. 动效必须可中断、可重定向，不能让用户等动画播完（现有收起交接逻辑已符合，保持）。
4. 物理感优先：拖动 1:1 跟手，落位才动画；可选轻微过冲表达“磁性”。
5. 动效服务于连续性与空间关系（收起↔展开的形状/位置连续），不是装饰。
6. 编排用交错（stagger 30–50ms）而非齐动。
7. 无障碍是硬约束：尊重系统减弱动效；动效不能是传达信息的唯一方式。

## 五、开源标杆应用（学习对象）

| 应用 | 仓库 | Star | 借鉴点 |
|---|---|---|---|
| Files | https://github.com/files-community/Files | 45.6k | WinUI3 Fluent 天花板 |
| Notepads | https://github.com/0x7c13/Notepads（注意是 0x7c13） | 10.3k | **WPF 做出 Win11 原生感的最佳范本**：自绘标题栏、亚克力、暗色 |
| Flow Launcher | https://github.com/Flow-Launcher/Flow.Launcher | 15.6k | WPF 工具类应用的动效与主题系统 |
| Lenovo Legion Toolkit | https://github.com/LenovoLegionToolkit-Team/LenovoLegionToolkit | 2.1k | wpfui 最大规模实战（原仓库已归档，看 fork） |
| Text Grab | https://github.com/TheJoeFin/Text-Grab | 5.0k | 小型 WPF 工具的 Win11 视觉细节 |
| PowerToys | https://github.com/microsoft/PowerToys | 138.9k | 微软官方 WPF/WinUI 混合实践 |

## 六、UI 设计灵感站

| 站点 | 定位 | 备注 |
|---|---|---|
| https://fluent2.microsoft.design | 桌面软件最对口的官方规范+动效指引 | 免费 |
| WinUI Gallery（Microsoft Store 官方示例应用） | Win11 控件实景+参数对照 | 免费 |
| https://dribbble.com | 概念稿，搜 "windows 11 / fluent" | 浏览免费 |
| https://behance.net | 完整设计案例研究 | 免费 |
| https://collectui.com | 按组件分类的 Daily UI 精选 | 免费 |
| http://recent.design（原 godly.website） | 精品界面画廊，含 App 截图分类 | 免费浏览（已验证跳转） |
| https://mobbin.com | 真实产品截图按流程检索 | 免费额度收紧，偏移动端 |
| https://land-book.com / https://awwwards.com | 落地页/高端网页 | 部分未验证 |

桌面软件专属画廊站稀缺；实操路径 = Fluent 2 规范 + 标杆应用截图 + Dribbble 概念稿三方组合。

## 七、Avalonia / Mac 端生态

- **FluentAvalonia**（https://github.com/amwx/FluentAvalonia，1.6k★，MIT，2026-08 仍活跃；NuGet 包名 `FluentAvaloniaUI`）：WinUI 风格控件集；**大版本强绑定 Avalonia**（2.5.x↔11.3、3.x↔12.x），本项目 Avalonia 11.3.21 对应 2.5.x 线；README 版本矩阵滞后，以 NuGet 依赖范围为准。仅评估，不默认引入。
- Avalonia 官方动画文档（已验证）：[Animations 总览](https://docs.avaloniaui.net/docs/graphics-animation/animations)（Keyframe/Transitions/Composition 三类）、control-transitions、page-transitions（CrossFade/PageSlide）、[Composition API](https://api-docs.avaloniaui.net/api/Avalonia.Rendering.Composition.Animations)（渲染线程表达式动画）。
- Lottie：官方 `AvaloniaUI/Avalonia.Lottie` 2023 年起停更；社区方案低活跃——**Mac 端动效以原生 Transitions + Composition 为主，不引入 Lottie**。
- 图标：`Icons.Avalonia` / `FluentAvalonia.FluentIcons`（Fluent System Icons 移植）可选用。
- 双端一致性：Windows 手工 Fluent token + Mac 同数值 token（颜色/时长/缓动/圆角共用一份设计规范），是比“两端各引一套库”更轻的统一路径。

## 八、避坑清单

1. ModernWpf（2022 停更；2026-07 出现社区 RC 复活线，生产慎用）、FluentWPF（2022 停更）——旧博客仍在推荐，勿采。
2. CommunityToolkit v8 只面向 WinUI/Uno，**与 WPF 无关**；CommunityToolkit.Lottie 无 WPF 版。
3. .NET 9 内置 Fluent 主题用合并字典方式会让 VS XAML 设计器白屏，用 `ThemeMode` 属性。
4. Mica/`DWMWA_SYSTEMBACKDROP_TYPE` 与 WPF `AllowsTransparency=True` 分层窗口存在已知冲突，须先验证改造窗口壳（弃用 AllowsTransparency，改 WindowChrome+透明背景 或 DWM 圆角）再上材质。
5. Segoe Fluent Icons 字体不可随安装包分发（Win11 系统自带可直接引用字形）。
6. `godly.website` 已 301 跳转 recent.design；Lenovo LegionToolkit 看社区 fork；Notepads 仓库是 `0x7c13`。
7. Mobbin 免费额度收紧；r/WPF 版内热帖本次未直接抓取（社区证据来自 r/dotnet、r/csharp）。
