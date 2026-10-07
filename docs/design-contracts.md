# 设计契约清单（Design Contracts）

> 本清单是「已设计定型、不得被新功能回退」的行为契约的唯一权威来源：每条 = **断言**（一句可判定的话）+ **由来**（版本与根因背景）+ **锁定**（测试名或文件；没有的如实标注，不编造）+ **关联**（spec/plan/记录）。除非用户当次明确要求，任何改动不得改变这些行为；发现冲突时停下来问用户，不自行动刀；确需改契约先开 Issue 并更新本清单。
>
> 历史：本清单前身为 `docs/improvement-loop.md` 内嵌的「设计冻结清单」（11 条），2026-10-07 迁出为第一公民并补登后续契约。夜间循环规则中的「冻结清单」名称保留，指向本文档。视觉/动效数值规范（token）另见[设计规范](design.md)；红线与流程另见仓库 `AGENTS.md`、`PROJECT_GUIDE.md`。

## 一、自冻结清单迁入（1–11）

### 1. 磁盘写入原子性与失败恢复

- **断言**：`LocalStore` 磁盘写入保持原子性；保存失败时对象、状态、精确顺序、选择与滚动位置全部恢复到保存前。
- **由来**：1.0.x 起的持久化基线——半份 `board.json` 比丢数据更糟。
- **锁定**：`tests/FloatingTransferStation.Tests/LocalStoreTests.cs`、`tests/FloatingTransferStation.Tests/BoardMutationServiceTests.cs`（含 `tests/FloatingTransferStation.Tests/FailingFirstSaveBoardStore.cs` 驱动的失败恢复用例）。
- **关联**：[架构说明](architecture.md)、`PROJECT_GUIDE.md`「必须保持的契约」。

### 2. 置顶分区与批量顺序

- **断言**：每个分类内始终先置顶区、后普通区；批量操作保持源显示顺序；内部批量拖放保持源数据、选择作用域与跨分类分区语义。
- **由来**：1.1.0 批量置顶引入的分区基线；1.18.0 批量事件收敛为单 Reset 时同步重申。
- **锁定**：`tests/FloatingTransferStation.Tests/BoardServiceTests.cs`、`tests/FloatingTransferStation.Tests/BatchDragPlannerTests.cs`、`tests/FloatingTransferStation.Tests/BoardServiceBatchNotificationTests.cs`（单 Reset 契约）。
- **关联**：[批量置顶设计](superpowers/specs/2026-08-28-batch-pin-design.md)。

### 3. 卸载只删受管数据目录

- **断言**：卸载只删除应用登记并管理的 `Data` 目录，不扩大到用户选择的父目录；数据清理递归目标仅为已验证的 `Data`，上层目录仅在空目录时移除，同级文件必须保留。
- **由来**：1.4.2 仓库维护与卸载边界硬化（防「卸载器删掉用户整个下载目录」级事故）。
- **锁定**：`tests/FloatingTransferStation.Tests/LifecycleTests.Installer.cs`。
- **关联**：[仓库维护计划](superpowers/plans/2026-09-03-repository-maintenance.md)。

### 4. 复盘标签语义

- **断言**：复盘标签不接收剪贴板或拖放内容；按天 Markdown、自动保存与外部变更合并语义不变。
- **由来**：1.7.0 每日复盘标签设计（复盘是记录面，不是中转面）。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/DailyReviewStoreTests.cs`、`tests/FloatingTransferStation.Core.Tests/DailyReviewMigrationTests.cs`、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.DailyReview.cs`。
- **关联**：[每日复盘设计](superpowers/specs/2026-09-14-daily-review-tab-design.md)。

### 5. 设置即时生效与自启边界

- **断言**：设置改动即时生效并自动原子保存；开机自启的 Run 值仅在用户于设置界面显式切换时写入（`WindowsStartupManager`），启动路径与开发启动不写注册表。
- **由来**：1.8.0 设置首版（用户对「装完就自启」的信任边界）。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs`、`tests/FloatingTransferStation.Tests/LifecycleTests.Runtime.cs`。
- **关联**：[右缘裁切与设置设计](superpowers/specs/2026-09-23-edge-bleed-and-settings-design.md)。

### 6. 贴边几何与裁切

- **断言**：贴边一侧窗口边缘与屏幕平齐；右缘裁切（`WindowSettings.EdgeBleed` = DWM 圆角 token）、展开/收起节奏与既有动效开关语义不变；右贴任务栏或右邻显示器时回退贴齐，`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` 后重估。
- **由来**：1.8.0 右缘裁切（DWM 圆角落屏外）；1.11.1/1.14.1 重估时机补齐。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.EdgeBleed.cs`、`tests/FloatingTransferStation.Tests/WindowControllerTests.cs`（`EdgeBleed_MatchesTheDwmCornerRadiusToken` 等）。
- **关联**：[设计规范 §四/§五·C](design.md)、[右缘裁切设计](superpowers/specs/2026-09-23-edge-bleed-and-settings-design.md)。

### 7. 编辑器持有焦点期间不自动收起

- **断言**：编辑器（含分类改名与卡片就地编辑）持有键盘焦点期间面板不自动收起；IME 候选窗补发的假指针离开不得引发收起。
- **由来**：1.7.x 编辑期面板「自己收起来」的报障根因。
- **锁定**：`tests/FloatingTransferStation.Tests/PanelStateMachineTests.cs`（`TextEditing_KeepsPanelOpenAcrossPointerLeaveUntilEditingEnds`）。
- **关联**：`PROJECT_GUIDE.md`「必须保持的契约」。

### 8. 单实例、登记目录与版本来源

- **断言**：单实例锁；安装/数据目录只认安装器登记；`version.txt` 是唯一版本来源（程序集、产品标识、安装器与打包脚本共同读取）。
- **由来**：1.4.x 安装器数据目录治理与多实例互踩报障。
- **锁定**：`tests/FloatingTransferStation.Tests/LifecycleTests.Installer.cs`、`tests/FloatingTransferStation.Tests/LifecycleTests.ReleaseMetadata.cs`。
- **关联**：[架构说明](architecture.md)、[发布指南](releasing.md)。

### 9. 插件系统边界

- **断言**：声明式清单、默认全部禁用、插件不得把内容清成空白、启用状态原子持久化、用户目录同名插件覆盖内建版本、清单非法的插件可见但不可启用。
- **由来**：1.9.0 插件系统（第三方文本整理的受控入口）。
- **锁定**：`tests/FloatingTransferStation.Tests/PluginCatalogTests.cs`。
- **关联**：CHANGELOG 1.9.0（无独立 spec，如实标注）。

### 10. 连续重复采集抑制

- **断言**：只与「最近一条成功采集」比对，窗口 5 秒；文本比对整理插件处理后的全文，图片比对规范化后的像素指纹（跨表示互认）；仅作用于剪贴板自动采集的单条内容，批量多条导入与外部拖入永不因重复被抑制；保存失败不记录指纹。
- **由来**：1.9.1「微信复制图片偶发双份」根因修复（抑制面必须最小，否则误伤正常重复复制）。
- **锁定**：`tests/FloatingTransferStation.Tests/ClipboardCaptureServiceTests.Dedup.cs`。
- **关联**：`docs/research/loop-2026-09.md`。

### 11. 撤销最近删除语义

- **断言**：`Ctrl + Z` 仅在面板持有键盘焦点且无文本编辑时生效，绝不注册为全局热键；恢复用锚点插回（不回退删除后的新增/移动/重新置顶），批内保持删除时显示顺序；会话级最多 20 批；删除的图片文件保留到批次被驱逐、显式丢弃或正常退出时清理；撤销保存失败必须回到删除状态。
- **由来**：1.11.0 撤销切片（用户误删的最后防线，同时不能变成热键劫持）。
- **锁定**：`tests/FloatingTransferStation.Tests/BoardMutationServiceUndoTests.cs`、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.Undo.cs`。
- **关联**：[撤销与回收站设计草案](superpowers/specs/2026-09-25-undo-recycle-bin-design.md)。

## 二、补登（12–15，历史已定型但此前未入清单）

### 12. 迁移原子性（1.8.1 起）

- **断言**：一次面板状态迁移（展开/收起/拖放轨道揭示，及 1.20.0 起的贴边隐藏/恢复）只允许一次窗口矩形变更；STA 观察到的每个窗口矩形 ∈ {初始, 终态}；禁止用 `SetWindowRgn` 或动效/延迟/隐藏窗口装饰性掩盖。
- **由来**：1.8.0 展开闪现报障的根因修复——陈旧依赖属性混合值重推出可见中间矩形。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs`（`ExpandedPlacement_ChangesTheObservedWindowRectangleOnlyBetweenStableStates` 系列）。
- **关联**：[几何原子化设计](superpowers/specs/2026-09-23-placement-atomicity-design.md)、[设计规范 §五·C](design.md)。

### 13. 真实输入可达性（1.17.1 起）

- **断言**：指针交互行为不得只以合成路由事件验证——「合成事件可达、真实鼠标不可达」是缺陷（WPF 输入管线与 ListBox 默认处理会吞掉按钮命中）；关键指针路径必须有 Win32 消息直驱（完整输入管线）的守护测试。
- **由来**：1.16.0–1.17.0「悬停后点击置顶/选择按钮无反应」实机报障（B-007 同族），此前测试用合成事件绕过真实管线而漏测。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardGestures.cs`（Win32 消息直驱用例）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardHalves.cs`（`CardHalves_RealInput*` 系列）。
- **关联**：`docs/observations.md` B-007、CHANGELOG 1.17.0/1.17.1。

### 14. 卡片对半分区（1.18.0 起）

- **断言**：卡片命中层左右对半（两列 `*`/`*`，区域=元素身份，DPI 无关）：左半双击编辑、按住拖拽、单击无选择语义；右半裸/`Ctrl` 单击 toggle、`Shift` 范围选择、右键复制（不改选择）；右半双击净一次 toggle；卡片 12px 边缘环带归左半。内容视觉层全宽独立（图片/文字横贯整卡），与命中层解耦。
- **由来**：1.17.0「内容列 + 60px 操作列」表达不了用户心智的「同一元素左右对半」（Bug B）；1.18.0 改为独立叠加命中层。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardHalves.cs`（`CardHalves_HitLayerSplitsCardContentIntoEqualHalves` 等）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardGestures.cs`。
- **关联**：CHANGELOG 1.18.0（无独立 spec，如实标注）。

### 15. 大数据量显示根因契约（1.19.0 起）

- **断言**：显示路径对条目数 N 的开销有界——批量结构变更以单 Reset 事件重排（不逐条广播）；显示层只绑定有界预览（600 字符）而全文保留用于搜索/编辑/复制/拖出；缩略图缓存命中为纯内存查询（UI 线程零磁盘 I/O），受管图片重写后主动失效；虚拟化列表只实现可见容器；入场动画仅限真正新入库内容。
- **由来**：1.19.0「东西一多就卡」根因修复——卡顿成本全部来自随 N 线性/平方增长的开销（启动 5264ms→1101ms、8 万字排版 470.8ms→4.3ms 等）。
- **锁定**：`tests/FloatingTransferStation.Tests/BoardServiceBatchNotificationTests.cs`（单 Reset）、`tests/FloatingTransferStation.Core.Tests/BoardItemPreviewTextTests.cs`（有界预览）、`tests/FloatingTransferStation.Tests/AsyncThumbnailImageTests.cs`（缓存与解码桶宽）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardRendering.cs`（`BoardList_RealizesOnlyVisibleContainersForLargeCategories`、`BatchStructuralChange_DoesNotResetScrollToTop`）。
- **关联**：CHANGELOG 1.19.0（无独立 spec，如实标注）。

## 三、本次新增（16–17，1.20.0）

### 16. 文字卡避让操作列（1.20.0 起）

- **断言**：文字卡正文右侧为右上角操作按钮条预留排他空间，任何时刻（悬停/非悬停）零像素争用；内缩量唯一来源是派生资源 `CardTextReservedRightInset`（两列 `CardOperationColumnWidth` + 4 DIP 间隙），不得在别处写死第二个数值；命中层对半分区、图片卡全幅与角按钮契约不变。
- **由来**：1.18.0 对半分区分层解决了命中歧义但视觉层没有为按钮预留空间——悬停淡入的按钮压在正文第一、二行右上角（实测 60px 全额争用）。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardOperationAvoidance.cs`（零交集 + 派生关系）。
- **关联**：[同日设计规格](superpowers/specs/2026-10-07-text-ops-avoidance-and-edge-hide-design.md)、[设计规范 §五](design.md)。

### 17. 一次性贴边隐藏（1.20.0 起）

- **断言**：头部按钮为切换语义（再按取消）；armed 且复用收起抑制条件（`PanelStateMachine.WouldCollapse`：指针在内/拖拽/文本编辑/PanelHold 均抑制）成立时，整窗**同尺寸纯移动**到所有显示器右缘之外（与每一个显示器矩形零交集，另留 DWM 阴影余量），面板保持展开、内容冻结；隐藏时刻保存回位区（屏幕内可见矩形外扩小容差，物理像素，此后不做运行时 DPI 换算）；光标驻留 ≥200ms 才恢复，恢复即回原矩形并自动解除（一次性）；热键与显示器变化在离屏态立即恢复兜底；armed 仅展开态有意义，面板经其他路径离开展开态即自动解除；armed 期间头部按钮常显并切强调色。
- **由来**：1.20.0 新交互——比收起到轨道更彻底的「完全移出」，业界无「一次性、唤回即解除」先例（调研 job `20261007-165705-pwo`），必须待命显式可见 + 多重找回兜底。
- **锁定**：`tests/FloatingTransferStation.Tests/EdgeHideStateMachineTests.cs`（相位机）、`tests/FloatingTransferStation.Tests/WindowControllerTests.cs`（`EdgeHidden_*`/`EdgeRecallZone_*` 纯几何）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.EdgeHide.cs`（窗口层全套）。
- **关联**：[同日设计规格](superpowers/specs/2026-10-07-text-ops-avoidance-and-edge-hide-design.md)。

## 维护规则

- 新契约入清单时机：行为定型的同一版本，随 CHANGELOG 与锁定测试一起落。
- 「锁定」宁缺毋滥：没有锁定测试的如实标「无锁定测试（补测候选）」，不编造测试名。
- 改契约先开 Issue；改行为必须同步本清单与锁定测试，否则视为回退。
