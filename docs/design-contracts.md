# 设计契约清单（Design Contracts）

> 本清单是「已设计定型、不得被新功能回退」的行为契约的唯一权威来源：每条 = **断言**（一句可判定的话）+ **由来**（版本与根因背景）+ **锁定**（测试名或文件；没有的如实标注，不编造）+ **关联**（spec/plan/记录）。除非用户当次明确要求，任何改动不得改变这些行为；发现冲突时停下来问用户，不自行动刀；确需改契约先开 Issue 并更新本清单。
>
> 历史：本清单前身为 `docs/improvement-loop.md` 内嵌的「设计冻结清单」（11 条），2026-10-07 迁出为第一公民并补登后续契约。夜间循环规则中的「冻结清单」名称保留，指向本文档。视觉/动效数值规范（token）另见[设计规范](design.md)；红线与流程另见维护者本地文档 `AGENTS.md`、`PROJECT_GUIDE.md`。
>
> 注：2026-10-08 起仓库只保留软件本体与源码，本文「关联」中出现的 `docs/superpowers/`、`docs/research/`、`docs/observations.md` 等路径均为维护者本地资料（文件名保持原样，仅作历史追溯），不在仓库内。

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
- **关联**：批量置顶设计：`docs/superpowers/specs/2026-08-28-batch-pin-design.md`。

### 3. 卸载只删受管数据目录

- **断言**：卸载只删除应用登记并管理的 `Data` 目录，不扩大到用户选择的父目录；数据清理递归目标仅为已验证的 `Data`，上层目录仅在空目录时移除，同级文件必须保留。
- **由来**：1.4.2 仓库维护与卸载边界硬化（防「卸载器删掉用户整个下载目录」级事故）。
- **锁定**：`tests/FloatingTransferStation.Tests/LifecycleTests.Installer.cs`。
- **关联**：仓库维护计划：`docs/superpowers/plans/2026-09-03-repository-maintenance.md`。

### 4. 复盘标签语义

- **断言**：复盘标签不接收剪贴板或拖放内容；按天 Markdown、自动保存与外部变更合并语义不变。
- **由来**：1.7.0 每日复盘标签设计（复盘是记录面，不是中转面）。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/DailyReviewStoreTests.cs`、`tests/FloatingTransferStation.Core.Tests/DailyReviewMigrationTests.cs`、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.DailyReview.cs`。
- **关联**：每日复盘设计：`docs/superpowers/specs/2026-09-14-daily-review-tab-design.md`。

### 5. 设置即时生效与自启边界

- **断言**：设置改动即时生效并自动原子保存；开机自启的 Run 值仅在用户于设置界面显式切换时写入（`WindowsStartupManager`），启动路径与开发启动不写注册表。
- **由来**：1.8.0 设置首版（用户对「装完就自启」的信任边界）。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs`、`tests/FloatingTransferStation.Tests/LifecycleTests.Runtime.cs`。
- **关联**：右缘裁切与设置设计：`docs/superpowers/specs/2026-09-23-edge-bleed-and-settings-design.md`。

### 6. 面板外形恒定（可见态四角圆角）

- **断言**：面板处于可见态（展开/收起/拖放轨道）时，窗口矩形完整落在工作区内、四角圆角恒定，窗口宽度恒等于可见宽度；不随任务栏停靠侧、多显示器布局、显示器插拔或系统广播改变外形；`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` 只触发两件事——贴边隐藏离屏恢复兜底（见 #17）与按当前面板状态重新贴齐工作区右缘。内容层不得为任何"越屏区域"做右内缩。贴边隐藏离屏态不属本断言范围。
- **由来**：1.8.0 曾引入右缘越屏裁切（`WindowSettings.EdgeBleed`：窗口右移一个 DWM 圆角半径，右侧两圆角落屏外贴平）换取收起态右缘平直，但其条件性使外形随环境漂移；2026-10-08 用户决策删除该机制本身——可见态外形恒定为四角圆角，1.21.0 落地。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.EdgeGeometry.cs`、`tests/FloatingTransferStation.Tests/WindowControllerTests.cs`（右缘贴齐与可见宽钳制）。
- **关联**：[设计规范 §四/§五·C](design.md)、右缘裁切历史设计：`docs/superpowers/specs/2026-09-23-edge-bleed-and-settings-design.md`（历史档案，机制已删除）。

### 7. 编辑器持有焦点期间不自动收起

- **断言**：编辑器（含分类改名、卡片就地编辑与 1.28.0 起的卡片备注编辑）持有键盘焦点期间面板不自动收起；IME 候选窗补发的假指针离开不得引发收起。
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

- **断言**：`Ctrl + Z` 仅在面板持有键盘焦点且无文本编辑时生效，绝不注册为全局热键；恢复用锚点插回（不回退删除后的新增/移动/重新置顶），批内保持删除时显示顺序；会话级最多 20 批；多批次时后进先出——恢复的总是最近一批，栈满驱逐最旧一批（图片文件随之清理）；删除的图片文件保留到批次被驱逐、显式丢弃或正常退出时清理；撤销保存失败必须回到删除状态。
- **由来**：1.11.0 撤销切片（用户误删的最后防线，同时不能变成热键劫持）；1.33.0 显式化多批次后进先出——原实现误用 FIFO 队列弹出最旧批，自动清扫批与用户删除共栈，置顶卡删除后一次撤销恢复出的是用户无感知的旧清扫批（Issue Oiawlm/floating-transfer-station#104）。
- **锁定**：`tests/FloatingTransferStation.Tests/BoardMutationServiceUndoTests.cs`（含 `UndoLastDelete_PopsMostRecentBatchNotOldest`、`UndoLastDelete_PinnedCardRestoredOverAutoCleanupBatch`、`UndoStack_EvictionKeepsLifoOrder`、`UndoLastDelete_SaveFailureWithMultipleBatchesKeepsOlderBatches`）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.Undo.cs`（含 `Undo_RestoresMostRecentDeleteWhenMultipleBatches`）。
- **关联**：撤销与回收站设计草案：`docs/superpowers/specs/2026-09-25-undo-recycle-bin-design.md`。

## 二、补登（12–15，历史已定型但此前未入清单）

### 12. 迁移原子性（1.8.1 起）

- **断言**：一次面板状态迁移（展开/收起/拖放轨道揭示，及 1.20.0 起的贴边隐藏/恢复）只允许一次窗口矩形变更；STA 观察到的每个窗口矩形 ∈ {初始, 终态}；禁止用 `SetWindowRgn` 或动效/延迟/隐藏窗口装饰性掩盖。
- **由来**：1.8.0 展开闪现报障的根因修复——陈旧依赖属性混合值重推出可见中间矩形。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs`（`ExpandedPlacement_ChangesTheObservedWindowRectangleOnlyBetweenStableStates` 系列）。
- **关联**：几何原子化设计：`docs/superpowers/specs/2026-09-23-placement-atomicity-design.md`、[设计规范 §五·C](design.md)。

### 13. 真实输入可达性（1.17.1 起）

- **断言**：指针交互行为不得只以合成路由事件验证——「合成事件可达、真实鼠标不可达」是缺陷（WPF 输入管线与 ListBox 默认处理会吞掉按钮命中）；关键指针路径必须有 Win32 消息直驱（完整输入管线）的守护测试。
- **由来**：1.16.0–1.17.0「悬停后点击置顶/选择按钮无反应」实机报障（B-007 同族），此前测试用合成事件绕过真实管线而漏测。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardGestures.cs`（Win32 消息直驱用例）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardHalves.cs`（`CardHalves_RealInput*` 系列）。
- **关联**：`docs/observations.md` B-007、CHANGELOG 1.17.0/1.17.1。

### 14. 卡片对半分区（1.18.0 起；1.28.0 修订右半连点逐击 toggle）

- **断言**：卡片命中层左右对半（两列 `*`/`*`，区域=元素身份，DPI 无关）：左半双击编辑、按住拖拽、单击无选择语义；右半裸/`Ctrl` 单击 toggle、`Shift` 范围选择、右键复制（不改选择）；右半每击都 toggle——双击窗只适用于左半文字卡的就地编辑判定，双击=两次 toggle=净零，原地快速连点每击都生效（1.28.0 修订）；卡片 12px 边缘环带归左半。内容视觉层全宽独立（图片/文字横贯整卡），与命中层解耦。
- **由来**：1.17.0「内容列 + 60px 操作列」表达不了用户心智的「同一元素左右对半」（Bug B）；1.18.0 改为独立叠加命中层。1.28.0 用户报障「右半同一位置快速连点第二击被吞」——双击窗分支对右半无分发路径且被吞点击仍刷新痕迹，修订为窗限定左半（`_pressZone == Content` 外层守卫）；右半 Operations 痕迹保留，职责改为重置左半双击窗（防快速 左→右→左 同位点击拼成伪双击误开编辑）。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardHalves.cs`（`CardHalves_HitLayerSplitsCardContentIntoEqualHalves` 等）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardGestures.cs`（含 `CardGestures_OperationsZoneRapidRepeatClicksToggleEachTime`，Win32 消息直驱）。
- **关联**：CHANGELOG 1.18.0/1.28.0（无独立 spec，如实标注）；1.28.0 修订 Issue Oiawlm/floating-transfer-station#93。

### 15. 大数据量显示根因契约（1.19.0 起）

- **断言**：显示路径对条目数 N 的开销有界——批量结构变更以单 Reset 事件重排（不逐条广播）；显示层只绑定有界预览（600 字符）而全文保留用于搜索/编辑/复制/拖出；缩略图缓存命中为纯内存查询（UI 线程零磁盘 I/O），受管图片重写后主动失效；虚拟化列表只实现可见容器；入场动画仅限真正新入库内容。
- **由来**：1.19.0「东西一多就卡」根因修复——卡顿成本全部来自随 N 线性/平方增长的开销（启动 5264ms→1101ms、8 万字排版 470.8ms→4.3ms 等）。
- **锁定**：`tests/FloatingTransferStation.Tests/BoardServiceBatchNotificationTests.cs`（单 Reset）、`tests/FloatingTransferStation.Core.Tests/BoardItemPreviewTextTests.cs`（有界预览）、`tests/FloatingTransferStation.Tests/AsyncThumbnailImageTests.cs`（缓存与解码桶宽）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardRendering.cs`（`BoardList_RealizesOnlyVisibleContainersForLargeCategories`、`BatchStructuralChange_DoesNotResetScrollToTop`）。
- **关联**：CHANGELOG 1.19.0（无独立 spec，如实标注）。

## 三、本次新增（16–17，1.20.0）

### 16. 文字卡避让操作列（1.20.0 起；1.24.0 修订为三列，1.29.0 修订为四列，1.33.0 修订为右上两列网格）

- **断言**：文字卡正文**与备注显示行/备注交互条（1.28.0 起，见 #22）**右侧为右上角操作按钮网格预留排他空间，任何时刻（悬停/非悬停）零像素争用；内缩量唯一来源是派生资源 `CardTextReservedRightInset`（操作网格 `CardOperationColumnWidth` 两列总宽 + 4 DIP 间隙；1.20.0 为两列横排计 64，1.24.0 随卡片删除按钮修订为三列计 94，1.29.0 随添加备注按钮修订为四列计 124，1.33.0 随网格化回归两列计 64，见 #19），不得在别处写死第二个数值；内缩为静态值、不随悬停派生（动态派生会牵动正文回流，已否决），测量型锁定测试在网格最大态（备注钮激活的完整两列网格）度量；操作按钮网格为固定两列 × N 按钮行（列与按钮行绝对 30 DIP，按钮行间 4 DIP 独立间隔行，右上角锚定，行优先序见 #19），按钮隐藏时格子留空、其余按钮零漂移；命中层对半分区、图片卡全幅与角按钮契约不变。
- **由来**：1.18.0 对半分区分层解决了命中歧义但视觉层没有为按钮预留空间——悬停淡入的按钮压在正文第一、二行右上角（实测 60px 全额争用）；1.24.0 操作条扩为三列（新增删除按钮），派生内缩同步加宽一列；1.29.0 备注入口迁入右上操作条最左列（#22 修订，Issue #98），派生内缩同步加宽一列。四列横排持续挤压正文而卡片右下角纵向空间闲置——结构性浪费而非数值失调；1.33.0 用户裁决改右上角固定两列网格（行优先 选择|删除|置顶|备注，Issue #103），横占地减半、纵向闲置被利用，正文内缩回归 64。网格选型：显式 Grid + 绝对行列 + 每钮固定 `Grid.Row`/`Grid.Column`；否决 UniformGrid（ArrangeOverride 跳过 Collapsed 子项会让其余按钮补位重排，恰好违背固定格子）与定宽 WrapPanel（换行语义随宽度漂移），也否决悬浮覆盖层替代静态内缩（悬停按钮压正文正是 1.18.0 报障根因）。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardOperationAvoidance.cs`（零交集 + 派生关系，网格最大态测量 + `CardOperationStrip_HiddenNoteButtonKeepsGridCells` 固定格几何）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs`（`TextCard_ReservesFixedTwoColumnOperationGrid` 两列网格与行优先占格断言、`TextCard_MinPanelWidthFitsOperationGrid` 最小面板宽度守卫）。
- **关联**：同日设计规格：`docs/superpowers/specs/2026-10-07-text-ops-avoidance-and-edge-hide-design.md`、[设计规范 §五](design.md)；1.24.0/1.29.0 修订见 #19 关联；1.33.0 两列网格修订记录 Issue Oiawlm/floating-transfer-station#103。

### 17. 一次性贴边隐藏（1.20.0 起）

- **断言**：头部按钮为切换语义（再按取消）；armed 且复用收起抑制条件（`PanelStateMachine.WouldCollapse`：指针在内/拖拽/文本编辑/PanelHold 均抑制）成立时，整窗**同尺寸纯移动**到所有显示器右缘之外（与每一个显示器矩形零交集，另留 DWM 阴影余量），面板保持展开、内容冻结；隐藏时刻保存回位区（屏幕内可见矩形外扩小容差，物理像素，此后不做运行时 DPI 换算）；光标驻留 ≥200ms 才恢复，恢复即回原矩形并自动解除（一次性）；热键与显示器变化在离屏态立即恢复兜底；armed 仅展开态有意义，面板经其他路径离开展开态即自动解除；armed 期间头部按钮常显并切强调色。
- **由来**：1.20.0 新交互——比收起到轨道更彻底的「完全移出」，业界无「一次性、唤回即解除」先例（调研 job `20261007-165705-pwo`），必须待命显式可见 + 多重找回兜底。
- **锁定**：`tests/FloatingTransferStation.Tests/EdgeHideStateMachineTests.cs`（相位机）、`tests/FloatingTransferStation.Tests/WindowControllerTests.cs`（`EdgeHidden_*`/`EdgeRecallZone_*` 纯几何）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.EdgeHide.cs`（窗口层全套）。
- **关联**：同日设计规格：`docs/superpowers/specs/2026-10-07-text-ops-avoidance-and-edge-hide-design.md`。

## 四、本次新增（18，1.23.0）

### 18. 自动清理范围与逐卡 TTL 语义（1.23.0 起；1.25.0 修订为逐卡 24 小时 TTL）

- **断言**：自动清理仅在设置开关开启时运行；只删除 `AutoCleanupSchedule.SweepCategories`（= 分类目录 `BoardCategoryCatalog.Ordered` 显式排除复盘分类的推导结果）内的非置顶条目；删除判据是**逐卡年龄阈值（TTL）**——条目自身 `CreatedAt` ≤ `AutoCleanupSchedule.GetExpiryCutoff(now)`（= now − 24h，边界含等于；CreatedAt 在未来自然不到期），即每张非置顶卡入库满 24 小时（固定值，不配置）即删，与检查时刻无关、清扫幂等、无「上次运行」记账（1.23.0 的间隔清扫记账与建基线机制已随之删除，旧 preferences.json 残留的 `autoCleanupLastRunAtUtc` 字段按未知成员忽略、不影响加载）；**存量立即生效**（用户 2026-10-10 裁决）：升级后首次检查即删除所有已超 24h 的非置顶卡；置顶条目与复盘内容（Reference 分类条目与 `reviews/*.md` 复盘文件）绝不触碰（即使超龄）；板面装载未成功本会话绝不清扫；跨分类清扫是单次原子操作——一次保存、一批撤销（可整批 Ctrl+Z，图片文件按撤销栈既有生命周期保留到驱逐/退出），保存失败整批恢复原内容与原顺序、不进撤销栈；偏好原子落盘。手工「清空非置顶」（垃圾桶无选择左键等）与自动清扫共用 `BoardService.RemoveNonPinned` 唯一移除管线，前者不带 cutoff（删全部非置顶，语义不变）。
- **由来**：1.23.0 用户需求「每隔 24 小时自动清理所有非置顶内容，直接删除，绝不包括复盘」，当时实现为间隔清扫 + 记账基线（null=建基线防升级首启静默清空）。1.25.0 用户澄清真实语义是**逐卡 TTL**（「当一个东西存储满 24 小时，就把它删掉」）并裁决「纯 TTL，存量立即生效」——间隔清扫会让新入库卡存活近两个周期、存量卡超期不受清理，都不是用户心智；TTL 判定幂等使记账机制失去存在理由（外部调研 Ditto/Maccy/CopyQ/ClipClip：业界主流=规则对存量一视同仁 + 置顶豁免，无存量宽限先例；本项目「整批 Ctrl+Z」强于业界不可撤销基线）。触发保留「装载成功补跑 + 1 小时常驻巡检」，拒绝在 `ApplyPreferences` 内检测开关转变触发清扫（既有裁决不变）。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/AutoCleanupScheduleTests.cs`（TTL 截止时刻推导与 SweepCategories 清单）、`tests/FloatingTransferStation.Tests/BoardServiceTests.cs`（`RemoveNonPinned_WithExpiryCutoff_*`/`RemoveNonPinned_WithoutCutoff_*`/`RemoveNonPinned_MultiCategory_*`）、`tests/FloatingTransferStation.Tests/BoardMutationServiceTests.cs`（`ClearNonPinned_WithExpiryCutoff_*`、保存失败整批恢复不进撤销栈、`ClearNonPinned_MultiCategory_*`）、`tests/FloatingTransferStation.Tests/AutoCleanupIsolationTests.cs`（超龄卡清扫下复盘文件字节不变全链路）、`tests/FloatingTransferStation.Tests/MainWindowAutoCleanupTests.cs`（漏斗闸门/存量立即生效/边界含等于/未来时间戳保留/超龄图片卡撤销栈生命周期/关闭停表）、`tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs`（自动清理字段回落、旧 JSON 残留时间戳字段可加载）、`tests/FloatingTransferStation.Tests/SettingsWindowInteractionTests.cs`（开关默认开启与立即持久化）。
- **关联**：CHANGELOG 1.23.0/1.25.0；1.25.0 语义修订记录 Issue（Oiawlm/floating-transfer-station#90，无独立 spec）。

## 五、本次新增（19，1.24.0）

### 19. 卡片操作按钮状态矩阵与单卡删除（1.24.0 起；1.29.0 扩展备注钮行，1.33.0 几何改右上两列网格）

- **断言**：卡片右上操作按钮区 1.33.0 起为固定两列网格（列与按钮行绝对 30 DIP、按钮行间 4 DIP 独立间隔行、右上角锚定），行优先网格序 选择|删除|置顶|备注——选择=(0,0)、删除=(0,1)、置顶=(1,0)、备注=(1,1)（行号按按钮行计；WPF 实现中第二行按钮物理 `Grid.Row=2`，行 1 为间隔行）；此后新增按钮按行优先向下占格（追加「按钮行+间隔行」后左→右），每枚按钮格子恒定、按钮隐藏时格子留空、其余按钮零漂移。**备注钮**的可见性由 #22 的七条件单独定义（内容门控：截断文字卡 ∧ 无备注；悬停揭示；正文/备注编辑期隐藏；搜索态隐藏；图片卡恒无——它不参与本条三信号矩阵，因为门槛是内容状态而非纯 UI 信号），且隐藏为**格子固定预留、可见性塌缩**语义：按钮基态 `Visibility.Collapsed`（经 `FadeAnimation.ManageVisibility`，禁止纯 Opacity 隐藏），隐藏时其格子留空、其余按钮几何逐像素不动（绝对行列下 Collapsed 天然留空；1.29.0 的备注列 `Auto` 塌缩机制随固定格子方案删除——布局更简、所有按钮语义一致：固定格子+显隐）。其余三枚按钮（选择/置顶/删除）的可见性与可点性由 (悬停 `IsMouseOver`, 选中 `IsSelected`, 置顶 `IsPinned`) 三信号的状态矩阵唯一派生，集中实现在卡片样式触发器层：**未选中+悬停**三钮全部显示可点；**未选中+未悬停**仅已置顶卡片的置顶钮常显可点（=取消置顶），其余隐藏；**选中（无论悬停）**选择钮常显可点，置顶钮未置顶隐藏、已置顶常显为纯状态徽章（换纯展示模板、以次文字色呈现，点击不改置顶、不改选中），删除钮一律隐藏；选中卡片的置顶/删除操作一律走顶部批量按钮。徽章的「点击无任何效果」由 `ToggleCardPinAsync` 入口守卫统一实现（按钮保持启用且可命中以吞掉点击——WPF 禁用元素不可命中，`IsEnabled=false` 会让点击穿透为右半选择手势误改选中），真实输入（手势层 OperationsPin 会话）与合成/UIA 路径（`BoardList_ButtonClick`）共用同一条守卫。删除钮单击只删该卡：手势层新增 `OperationsDelete` 会话（模式同 OperationsPin，按钮点击不进双击窗、滑离释放取消意图），经窗口层既有删除路径 `DeleteContentAsync → BoardMutationService.DeleteManyAsync` 执行——撤销栈（Ctrl+Z 锚点插回）、原子持久化、保存失败整卡恢复、删除淡出与重入保护全复用；不改当前选中集合（保存成功后原样还原删除前选择，`DeleteContentAsync` 以可选 `selectionToRestore` 参数化，垃圾桶/键盘删除路径行为不变）、不滚动列表；操作网格内右键复制不受几何改版影响。文字卡避让内缩随网格列数派生（`CardTextReservedRightInset` = 网格两列总宽 + 4 DIP 间隙，1.33.0 起计 64，见 #16）。
- **由来**：1.24.0 用户需求「卡片级删除按钮 + 选中态卡片操作规则」（两轮对齐确认）。行内悬停操作逐条生效 + 批量走顶部工具栏与桌面邮件类（Gmail/Outlook）及 Fluent/Material「行内次级操作 × 选择模式」正交维度一致（调研 job `20261010-160927-oa3`）；「选择模式=批量模式」下隐藏行内按钮是文件管理器与移动端主流。矩阵替代 1.20.0 前的隐式规则时同步修订了两处现状：选中+未置顶卡片的置顶钮由「悬停可见可点」收紧为隐藏（R2 表格明确选中卡片一律走顶部批量）；选中+已置顶置顶钮由「可点=就地取消置顶」改为纯状态徽章。1.29.0 操作条扩为四列（添加备注钮最左列，#22 修订，Issue #98）：备注钮可见性不并入三信号矩阵——内容门控引用 #22；塌缩语义保证既有三钮几何零漂移（肌肉记忆不变），新钮 `CommandParameter` 复用 `NoteStripCommand`，手势层与 UIA 路径零改动。1.33.0 用户裁决按钮区改右上角固定两列网格（行优先 选择|删除|置顶|备注，Issue #103）：四列横排持续挤压正文而卡片右下角纵向闲置，网格化横占地减半、纵向闲置被利用；可见性矩阵与备注门槛原样保留，只动几何——`CommandParameter` 意图路由与手势层零改动，真实输入/UIA 测试随按钮新位置自然通过。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardDeleteButton.cs`（矩阵四状态 + Win32 消息直驱的单卡删除保留选择滚动与撤销、徽章真实点击无效、滑离取消、操作条内右键复制）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardOperationAvoidance.cs`（网格最大态两列派生内缩 + `CardOperationStrip_HiddenNoteButtonKeepsGridCells` 固定格几何）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs`（`TextCard_ReservesFixedTwoColumnOperationGrid` 网格行列与行优先占格断言、`TextCard_MinPanelWidthFitsOperationGrid` 最小宽度守卫）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardNote.cs`（`CardOperationMatrix_NoteButtonRow` 备注钮行：图片卡恒无/置顶共存/编辑期塌缩）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.SelectionAndDeletion.cs` 与 `RangeSelection.cs`（矩阵化的单卡置顶用例：置顶单击只作用于未选中卡片）、批量置顶语义锁定测试（SelectionAndDeletion 批量用例）继续全绿。
- **关联**：CHANGELOG 1.24.0/1.29.0/1.33.0；契约 #16 三列修订记录 Issue（Oiawlm/floating-transfer-station#88）；1.29.0 四列扩展与备注钮行记录 Issue Oiawlm/floating-transfer-station#98；1.33.0 两列网格几何修订记录 Issue Oiawlm/floating-transfer-station#103。

## 六、本次新增（20–21，1.25.0）

### 20. 复盘编辑器 Esc 退出编辑态（1.25.0 起）

- **断言**：复盘编辑器持有键盘焦点时按 Esc 释放键盘焦点并标记按键已处理；焦点去向是宿主 Window 本身（必须先清空焦点域记录再聚焦——WPF 会把「聚焦焦点域本体」重定向回域内 `FocusedElement`，即编辑器自身），绝不把焦点塞给隐藏的 BoardList；面板去留交给既有焦点链（`Root_PreviewLostKeyboardFocus` 统一清算编辑保持原因并重估表面）——指针在面板内时维持展开（与 #7 同源），否则恢复收起节奏；IME 组合期的 Esc 属输入法操作（取消候选）不触发退出（组合标记守卫，与卡片就地编辑/分类改名框同族，标记在编辑器失焦时清理）；不引入提交/取消会话（复盘是 700ms 防抖自动保存，无「取消」概念）；卡片就地编辑、分类改名、搜索框的既有 Esc 语义不变。
- **由来**：1.25.0 用户需求「复盘界面打字时按 Esc 退出可输入状态，但鼠标还在界面里所以保持展开」。此前复盘编辑器对 Esc 彻底无操作——窗口级 PreviewKeyDown 的搜索分支在复盘页不可达（搜索排除复盘），清选择分支被 `is not TextBoxBase` 守卫挡住；「保持展开」机制（焦点链 + 表面重估）现成，缺的只是 ESC 断焦点这一环。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ReviewEditorEscape.cs`（焦点释放到宿主窗口且编辑保持清算、真实光标在面板内时保持展开且不启动收起计时、组合标记存在时守卫不触发且焦点不移交）；`tests/FloatingTransferStation.Tests/PanelStateMachineTests.cs`（#7）与既有 ESC 路径用例继续全绿。
- **关联**：CHANGELOG 1.25.0（无独立 spec，如实标注）。

### 21. 标签显示顺序（1.25.0 起；2026-10-13 补活切换画刷锁定，修订 Issue Oiawlm/floating-transfer-station#140）

- **断言**：四个标签（三个板卡分类 + 复盘）的显示顺序是独立持久化状态（settings.json `CategoryOrder`，原子写；与分类名零耦合、与 board.json 条目序/`SweepCategories`/`WithCategoryName` 全量快照序解耦——后三者按目录默认序继续）；只调顺序、不能增删分类；读取端唯一收敛于 `WindowSettings.DisplayOrder`——null（未定制）或非法（成员未定义、数量不符、重复）一律回落目录默认序，校验一次做齐；消费面=面板标签轨（`MainWindowViewModel.Categories` 按显示顺序重排，只复用既有 `CategoryViewModel` 实例、绝不新建——实例身份被 ActivePanel/默认接收/复盘表面切换依赖）与收起把手几何（行号=分类在显示顺序中的位置）；排序入口只在设置窗口「标签顺序」节（面板标签轨本身不提供拖拽）；节内拖拽自研零依赖（把手按下捕获鼠标 → 拖起原位行半透明跟随 → 其余行 TranslateTransform 让位 → 2px 强调色插入指示线；动效只用既有 DesignTokens 档，「界面动效」关闭退化为瞬时换位；浅/深主题用主题字典画刷，**行画刷经动态资源引用解析（`SetResourceReference`，2026-10-13 修订 Issue #140）——主题活切换即跟随新字典，无需重开窗口或重建行**）并必须同时提供上移/下移按钮（WCAG 2.5.7 单指针等价操作）；拖拽会话中的 Esc 由顺序节优先消费（回弹原序不提交，处理顺序在设置窗口既有 Esc 关窗路径之前），捕获丢失/失焦同样回弹；拖拽与按钮提交都走宿主 `ApplyCategoryOrderAsync` → settings.json 原子保存 + 标签轨即时重排（契约 #5），保存失败恢复内存原顺序；`ResetToDefault` 保留顺序定制（与 CategoryNames 同口径）。
- **由来**：1.25.0 用户需求「设置界面可以调整『图片、复盘、文本、待分类』这几个东西的顺序和位置……希望可以变成那种可以拖动的」。顺序与目录解耦使显示序成为纯 UI 偏好，不触碰任何数据语义（清扫范围、拖放、搜索、默认接收全部按分类身份工作）。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/CategoryDisplayOrderTests.cs`（非法回落、JSON 往返、ResetToDefault 保留、ViewModel 重排复用实例并通知）、`tests/FloatingTransferStation.Tests/LocalStoreTests.cs`（CategoryOrder 经 LocalStore 原子往返）、`tests/FloatingTransferStation.Tests/WindowControllerTests.cs`（自定义序收起行号与非法回落）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CategoryOrder.cs`（rail 按显示顺序渲染、收起把手行号、宿主采纳即重排并持久化）、`tests/FloatingTransferStation.Tests/SettingsWindowCategoryOrderTests.cs`（节渲染与端点禁用、上移/下移提交即落盘、真实消息直驱拖拽提交、拖拽中 Esc 优先消费不关窗、捕获丢失回弹不提交；2026-10-13 起含 `OrderRows_FollowLiveThemeSwitchWithoutRebuild` 行画刷活切换跟随——含提交重建行后）。
- **关联**：CHANGELOG 1.25.0（无独立 spec，如实标注）；2026-10-13 活切换画刷锁定修订 Issue Oiawlm/floating-transfer-station#140。

## 七、本次新增（22，1.28.0）

### 22. 长卡片备注（1.28.0 起；1.29.0 添加入口迁入右上按钮组，1.33.0 固定于网格右下格，2026-10-12 编辑器移回备注行原位并点击定位光标，2026-10-13 编辑表面提交语义：点击本卡非编辑器区域=提交并退出）

- **断言**：备注是用户手写的短摘要元数据，**仅文字卡**（图片卡无备注），目的是一眼识别长卡内容——不参与搜索、复制、拖出（卡片内容语义零变化），不加设置开关、不做自动摘要。**添加入口有门槛、判据是渲染布局事实**：「添加备注」按钮只对 `Note` 为空 ∧ 正文被截断 ∧ 卡片悬停 ∧ 无编辑会话进行中 ∧ 非搜索态的文字卡出现——1.29.0 起按钮位于卡片右上操作条（`NoteButtonStyle`，`CommandParameter` 复用 `NoteStripCommand`），1.33.0 起固定于操作网格**右下格 (1,1)**（行优先网格序见 #19；图片卡恒无备注钮，其右下格恒空），1.28.0 的左上幽灵标签入口已整件删除（重叠根因=标签与正文同格叠加盖写，随入口移除消除；触发条件逐条原样迁移）；截断判据唯一权威实现是 `TrimAwareTextBlock`（Decorator 承载 TextBlock，`TextBlock.MeasureOverride` 密封不可子类化；`MeasureOverride` 双次测量：先不限高取自然高度、再以 `min(约束高, MaxHeight)` 限高收尾，比较带 0.5 DIP 容差）产出的只读 `IsTruncated`（非 `AffectsMeasure`）；`MaxHeight` 是限高唯一来源（正文仍 100=5 行×LineHeight 20）；禁止字符数启发式、反射内部截断状态与 `LayoutUpdated` 兜底。**添加按钮的隐藏必须是格子固定预留、可见性塌缩语义**：基态 `Visibility.Collapsed` + 网格绝对行列（经 `FadeAnimation.ManageVisibility`：激活立即 Visible、失活动画完成后 Collapsed、动效关闭立即切），禁止纯 Opacity 隐藏——按钮隐藏时其 (1,1) 格子留空、置顶/选择/删除三钮几何逐像素不动（锁定 `CardOperationStrip_HiddenNoteButtonKeepsGridCells`）。**编辑入口无门槛**：已写入的备注无论卡片当前是否仍截断，备注行常显（在流、纯展示 `IsHitTestVisible=false`、不随 hover 变化、无高度跳变）且随时可点入编辑或清空——备注是用户数据，不随正文变短而消失。备注上限 120 字符由编辑器 `MaxLength` 承载（单一来源，模型侧不重复钳制）；打开编辑必须预填现值；空白提交=删除已有备注（无备注时空白提交=无操作），`Esc` 取消；备注编辑不进撤销栈（与正文就地编辑一致）。备注可点元素（点击面/备注编辑器）在卡片模板命中层**之上**（契约 #13：真实输入可点，不只合成可达；命中层让位会顶下右上按钮破坏 #19 几何，故在按钮层实现），添加按钮则位于命中层操作网格内（与置顶/选择/删除同层，按钮分支天然承接、零新手势代码）；备注交互条左键绝不 toggle 选中（独立意图 `OperationsNote` 会话，模式同置顶/删除按钮：按下记会话、滑离释放取消、不进双击窗、不写 `_lastCardClick` 痕迹），右键语义零改动（按既有命中层分区：右半复制、左半无操作，备注判定只作用于左键）；搜索态下备注入口隐藏（与搜索态不进入就地编辑同族守卫）。备注编辑与正文编辑共用同一会话机器（IME 组合守卫、Enter/Esc、失焦提交、锚点终结、`PanelStateMachine` 编辑保持——契约 #7），双向互斥单会话；提交路由到 `BoardMutationService.UpdateItemNoteAsync`，与正文更新共享同一条「字段变更→原子快照→失败还原+状态条」保存实现（不平行复制两套持久化路径），保存失败提示「备注未保存，已还原原备注。」；`board.json` SchemaVersion 保持 1（旧文件缺字段读为 null、空串归一 null，`CloneForSnapshot` 同步复制 `Note`）。备注显示行/交互条纳入 #16 避让（复用 `CardTextReservedRightInset`，与右上操作按钮网格零像素争用）。**编辑器行0原位（2026-10-12 修订）**：备注编辑器 `CardNoteEditor` 位于卡片模板行0 备注行 Grid 的内容列——行0 自身两列（Auto 图标列 + `*` 内容列），列1 内同格叠加备注显示 TextBlock（先声明）与编辑器（后声明=渲染在上，CardBrush 不透明盖写），取代 1.28.0–1.33.0 的「行1 顶部盖写正文」落位（该落位使已有备注的卡在编辑时呈现「旧备注在上、编辑框在下」的第三态，用户报障「修改框不在备注原位」）；行0 Grid 置于模板声明序末尾，备注可点元素（点击面/编辑器）在命中层与备注交互条之上的 z 序不变量保持（真实输入可点）；编辑框左缘与备注文字左缘的像素对齐由结构保证（同列同格，Auto 图标列同时约束显示/编辑两态），禁止硬编码偏移。**图标可见性统一规则** `Note 非空 ∨ IsEditingNote`（行级单规则，同时覆盖「编辑已有备注时图标保持可见」与「无备注卡添加备注」两态，不加 per-case 触发器）；无备注卡编辑期行0 有界长高（行0=max(显示行, 编辑器) ≤40 DIP），提交/取消后回落塌缩，与 D1 同一规则、不做有/无备注条件分叉、不预留 MinHeight、不锁行高；显示态编辑期间保持可见（不透明盖写，非显隐互斥切换）；编辑器行距经 `TextBlock.LineHeight`/`TextBlock.LineStackingStrategy` 附加属性在编辑器样式本地设置（Inherits 进 TextBoxView，与显示行精确同行距；不得提升到容器级防继承泄漏）。**进入编辑光标定位到点击处（取代卡片正文/备注编辑器的 SelectAll；分类改名入口不动）**：真实输入入口（正文双击、备注单击）由手势层携带按压点位（窗口 DIP 空间，正文双击取第二击按下位），经 `TranslatePoint` 从窗口换算到编辑器坐标后以 `GetCharacterIndexFromPoint` 定位 `CaretIndex`；合成/UIA 路径（`BoardList_ButtonClick`）无点位传 null，索引 <0 或点位为 null 一律回退光标到文末（搜索框/分类改名同族先例）；正文与备注编辑器共用同一实现、不写两份。**编辑表面提交语义（2026-10-13 修订）**：备注/正文编辑中，「正在编辑的卡片」是编辑表面——编辑卡容器内、编辑器 TextBox 之外的一切按下（正文显示区、备注行、右上操作按钮、左半拖拽区与卡片边缘环带）＝终结会话并整卡吞掉（`e.Handled=true`：防 ListBoxItem 默认选择、防折叠重排期误启拖拽/分发）：经既有 `CommitCardTextEditing()` 同一冲刷路径提交已输入内容并退出编辑（与点击其他卡片完全一致，**禁止出现第二套提交逻辑**），一次点击只做「退出编辑」一件事——不启动按压/拖拽会话、不作手势分发、不写双击痕迹且**作废既有双击痕迹**（与拖拽同哲学：进入编辑那记双击的陈旧痕迹若不作废，退出后立即再击的极快序列里第二击会配对误判双击重开编辑器），随后的下一次点击恢复正常交互；编辑器上的按下行为不变（交给编辑器放光标/选择，TextBox 自行标记已处理）。编辑中的双击＝首击提交会话、第二击按普通单击处理（无痕迹配对、不判双击、不直接进入正文编辑）；编辑期点击本卡右半操作钮（置顶/删除/备注）或左半拖拽区＝仅退出编辑，按钮动作不执行、拖拽不启动；焦点路径与 IME 零新增（`LostKeyboardFocus`「窗口内迁移不终结」现状保持；IME 组合中点击本卡与点击其他卡同路径同行为——组合标记随会话终结一并清理）。
- **由来**：1.28.0 用户需求「内容比较长的卡片希望备注一下写的是什么，简短几句就够；没超过卡片可见高度的短卡不需要备注」（门槛判据由执行设计落定为截断这一渲染事实——业界剪贴板管理器（CopyQ/EcoPaste 等）的备注功能均对全部条目开放、无「按长度给入口」先例，门槛是用户明确要求的产品决策；备注置顶作次级标题行 + 悬停幽灵入口的形态取 Apple HIG/Fluent 列表行与 Trello/Notion 悬停入口先例，调研 job `20261011-111718-c9m`）。1.29.0 用户报障「添加备注的按钮和文字会和原卡片底部的字体重叠」，裁决把入口移到卡片右侧按钮组并加一个位置（点击效果与旧入口完全一致；wbx 双 lane T7 评审确认迁移为根因修复、手势路由可零改动复用、ElementName 绑定同 namescope 原样可用，job `20261011-164515-h7o`/`20261011-171424-nvb`）。1.33.0 随按钮区网格化（Issue #103）入口从「操作条最左列」固定到「网格右下格 (1,1)」，七条件门槛与点击路由零改动。2026-10-12 用户报障「备注修改框不在备注原位」（1.28.0 把编辑器落在行1 顶部盖写正文：编辑已有备注时旧备注仍在行0、编辑框出现在其下方正文区，两段并存还被误读为新增第二条备注；Trello/Notion/Linear/GitHub 与 Atlassian/PatternFly/Cloudscape 的就地编辑惯例均为同页原位替换且 read/edit 两态样式一致，现状是惯例之外的第三态），用户三轮对齐裁决：编辑器移回备注行原位（三路调研同构收敛的 E 案=行0 两列、同格叠加、结构化对齐；硬编码 21px 偏移的 B 案与显隐互斥的 D 案被否，wbx job `20261012-195645-89b`），并要求进入编辑时光标定位到用户点击处（D4，取代 SelectAll——调研中「进入即全选」建议与用户裁决冲突，以 D4 为准）；会话机器与持久化路径零改动，本修订只动落位与光标。同窗 T7 评审 job `20261012-204651-6pi` 闭环：光标定位用例的期望字位改由 FormattedText 按实际字体量测（不依赖「汉字宽=字号」假定）、衬底断言加严为与 CardBrush 同例、补零 Padding/BorderThickness 断言。记录 Issue Oiawlm/floating-transfer-station#121。2026-10-13 用户需求「备注编辑中，点击卡片其他区域（非编辑框）应退出编辑」：1.22.0 起编辑表面分支（覆盖层时代语义的等价迁移）对编辑卡容器内的非编辑器按下只吞掉不冲刷，点击本卡非备注区无任何效果——冲刷只存在于「编辑卡之外」分支。业界主流（Windows/macOS 重命名、Excel/Sheets、MUI/ag-Grid/Ant Design 等）一律「点击别处=提交、Esc=取消」，绝大多数实现不区分「同容器非编辑区」与「完全外部」（ag-Grid 为唯一反例；Files 社区 PR #5896 为桌面端同需求先例，调研 job `20261013-134029-vcl`）；WPF 技术面上 PreviewMouseDown 吞掉后键盘焦点不移转、LostKeyboardFocus 不触发（点击非可聚焦区域焦点根本不变），焦点路径不可承载，手势层 tunneling 最早处理器是唯一正确落点；wbx T7 评审（job `20261013-134334-8o7`）确认「提交后吞掉」优于放行（模态编辑面背景点击=关闭编辑面，规避折叠重排期误启拖拽与双击重入两类风险），执行窗终版复评无 BLOCKER；痕迹作废（D4 极快序列角落）由执行窗 Explore 反矛盾扫描发现并闭环。记录 Issue Oiawlm/floating-transfer-station#134。
- **锁定**：`tests/FloatingTransferStation.Tests/TrimAwareTextBlockTests.cs`（截断判据与翻转）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardNote.cs`（`CardNote_StripEntry_OnlyOnTruncatedTextCardsOnHover` 门槛矩阵、`CardNote_StripEntry_SelectedTruncatedCardStillShowsOnHover`、`CardNote_RealInputClickOnStripButtonEntersEditingWithoutChangingSelection` 真实输入、`CardOperationMatrix_NoteButtonRow`、常显/高度稳定/键盘语义/空白删除/失败还原/持焦不收起/回收终结/右键零改动/避让零争用/持久化往返/缺字段免迁移/合成路径；2026-10-12 起 `CardNote_EditorOverlaysNoteRowInPlaceWithIconVisible` 行0 原位同格+图标可见+结构化对齐、`CardNote_EmptyNoteEditingExpandsRowBoundedThenFallsBack` 无备注卡有界长高回落、`CardNote_RealClickPlacesCaretNearClickPoint` 真实点击光标命中、`CardNote_SyntheticEntryPlacesCaretAtTextEnd` 无点位文末回退）、`CardEditing_DoubleClickPlacesCaretNearClickPoint`（`MainWindowInteractionTests.CardEditing.cs`，正文双击光标命中）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardOperationAvoidance.cs`（`CardOperationStrip_HiddenNoteButtonKeepsGridCells` 格子固定预留语义）；2026-10-13 编辑表面提交语义锁定：`CardEditing_ClickOnEditingCardCommitsAndDoubleTapDoesNotReenter`（改写自旧 `CardEditing_DoubleClickWhileEditingDoesNotReenter`——首击提交并作废痕迹、第二击按普通单击、随后的正常双击可恢复重入，`MainWindowInteractionTests.CardEditing.cs`）、`CardNote_ClickOutsideEditorOnEditingCardCommitsSession`（非编辑器区提交、幂等单次持久化、选中集不变）、`CardNote_ClickOnPinButtonWhileNoteEditingCommitsWithoutTogglingPin`（D6 置顶钮仅退出）、`CardNote_DragZonePressWhileNoteEditingCommitsWithoutStartingDrag`（D6 拖拽区不启动拖拽，Win32 消息直驱，`MainWindowInteractionTests.CardNote.cs`）。
- **关联**：CHANGELOG 1.28.0/1.29.0/1.33.0（无独立 spec，如实标注）；契约记录 Issue Oiawlm/floating-transfer-station#94；#7/#16 的 1.28.0 扩展同 Issue；1.29.0 入口迁移修订记录 Issue Oiawlm/floating-transfer-station#98；1.33.0 网格右下格修订记录 Issue Oiawlm/floating-transfer-station#103；2026-10-12 编辑器行0 原位与点击定位光标修订记录 Issue Oiawlm/floating-transfer-station#121；2026-10-13 编辑表面提交语义修订记录 Issue Oiawlm/floating-transfer-station#134（正文编辑同分支同语义，非 #22 备注独有）。

## 八、本次新增（23，1.31.0）

### 23. BoardList viewport 几何与滚动条可见性解耦（1.31.0 起）

- **断言**：BoardList 的卡片几何不随垂直滚动条出现/消失而变化——同一窗口下可滚动与不可滚动两种内容量，ScrollViewer viewport 宽、单张卡片宽（全宽拉伸）、卡片相对视口位置（含置顶卡右缘=右上操作条锚定缘）逐项相等；滚动条保留 `Auto` 语义（需要时才出现）但永不占布局宽，条带（8 DIP，隐式瘦条样式给出）落在卡片右缘外边距带（12 DIP）内，与每张卡片内容及右上按钮条零像素相交，Track 命令接线保持可交互（上页点击必须移动视口）；挂接唯一路径是 keyed overlay ScrollViewer 模板样式经 BoardList.Resources 作用域隐式样式作用于模板内部生成的 ScrollViewer——不做全局隐式 ScrollViewer 样式（不波及复盘编辑器等其他 ScrollViewer 表面）、不重写 ListBox 模板、不改隐式 ScrollBar 样式与卡片模板内部几何；模板内三个 `PART_*` 命名部件（ScrollContentPresenter、垂直/水平 ScrollBar）与 `CanContentScroll`/两向 `ScrollBarVisibility`/`Background` 透传绑定齐备（虚拟化 `ScrollUnit=Pixel`/`Recycling` 与键盘滚动的生命线），条带 `MinWidth`/`MinHeight=0` 压掉主题 ScrollBar 样式的系统尺寸下限；禁止负 margin 反向溢出与任何宽度补偿特判。
- **由来**：用户报障「卡片多时右侧出现滚动条、卡片变窄；删除部分卡片后滚动条消失、卡片变宽——剩余置顶卡片位置轻微位移」。根因链是 WPF 默认 ScrollViewer 模板把垂直滚动条放进独立 Auto 布局列（出现/消失即列宽 0↔N），且主题 ScrollBar 样式带系统尺寸下限（`MinWidth`=系统滚动条宽，150% 缩放实测 17.33 DIP，压过瘦条样式的 `Width=8`——比「瘦条 8 DIP」的表象根因更深一层，失败先行测试实测暴露）；`BoardListItemStyle` 的 `HorizontalContentAlignment=Stretch` 把 viewport 宽传导给每张卡片。单格叠加（overlay，滚动条悬浮内容之上）是 WinUI 3/Win11 Fluent/浏览器 overlay 滚动条的业界主流，与既有透明轨道瘦条天然契合；稳定留槽（`Hidden` 常驻占位）被否决（卡片恒窄 17.33 DIP 且空闲留一条空槽，与「无滚动条时卡片应是全宽」的用户心智相反），宽度补偿/负 margin 属补丁禁用。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ScrollViewport.cs`（`ScrollViewport_WidthIsIndependentOfVerticalScrollBarVisibility`、`ScrollViewport_CardWidthIsIndependentOfVerticalScrollBarVisibility`、`ScrollViewport_CardPositionIsIndependentOfVerticalScrollBarVisibility`、`ScrollViewport_OverlayScrollBarRemainsInteractive` 四条）。
- **关联**：CHANGELOG 1.31.0（无独立 spec，如实标注）；同日根因调研底稿 `docs/research/2026-10-12-scrollbar-viewport-stability.md`（维护者本地资料，不入库）。

## 九、本次新增（24，设置窗口双栏改造）

### 24. 设置窗口双栏信息架构、冻结几何与跨会话几何持久化（记录 Issue #106；1.38.0 外观组增补「置顶分隔」节，修订 Issue #135；2026-10-13 插件行/标签顺序行画刷活切换锁定与 AI 节模型收敛，修订 Issue #140/#141）

- **断言**：设置窗口是「标题栏 + 左导航 + 右内容页 + 页脚」双栏布局——左导航 5 组按冻结表（外观=主题/动效/置顶分隔（置顶分隔为 1.38.0 增补，修订 Issue Oiawlm/floating-transfer-station#135）；面板=标签顺序/内容复制/垃圾桶按钮；通用=开机自启/全局快捷键/自动检查更新（#26）；数据与维护=自动清理/数据目录/插件；AI=AI 设置）RadioButton 实现（同组互斥、`RadioButtonAutomationPeer` 原生 SelectionItem 语义、每项 `AutomationProperties.Name`=组名），↑/↓ 循环切换、Home/End 跳首末（预览隧道期消费、一次按键一步），Tab 可达（头部关闭钮不占 Tab），页脚承载关于（应用名+版本+GitHub 链接）与退出按钮（Danger 样式），不作为导航项；右列 `SettingsScrollHost` 是全部页共享的唯一滚动视口（单一 x:Name 实例，垂直 Auto/水平 Disabled），5 个内容页以可视树直书叠放（无 DataTemplate，43 个既有 x:Name 全部保持在窗口同一 namescope、FindName/UIA 永远可达），页切换只翻 Visibility，入场页可做 167ms 档 Opacity 淡入（可见性即时生效，UIA/命中不依赖动画收尾；「界面动效」关闭或构造期直接就位）。**冻结几何**：`SizeToContent="Manual"`、`ResizeMode="CanResize"` + `WindowChrome.ResizeBorderThickness=6`（无边框系统级 8 向缩放），默认 680×600 DIP、最小 560×460、最大宽 900、最大高 = 所在工作区高 − 48（矮屏抬到最小高，禁止 MaxHeight < MinHeight）；独立浮动窗口四边描边、标题栏双上角圆（不再是主窗贴右缘的停靠形态）。**几何持久化与恢复**：`AppPreferences` 的可空字段 `SettingsWindowLeft/Top/Width/Height`（WPF DIP；null=默认定位；旧 preferences.json 免迁移），只在拖动（Thumb `DragCompleted`）/系统缩放（`WM_EXITSIZEMOVE`）结束且 `WindowState==Normal` 时经 `ISettingsHost.ApplyPreferences` 原子管线落盘（契约 #5 同路，禁止平行保存路径；拖拽进行中零写盘；最大化/最小化状态不落盘）；恢复在 `SourceInitialized`：整组非法（缺失/NaN/±∞/非正尺寸）或完全落在所有显示器之外 → 整组回落 owner 相对默认定位；否则按持久化矩形中心解析目标显示器工作区（Win32 `MonitorFromPoint`+`GetMonitorInfo`+`GetDpiForMonitor`，DIP 内部口径、边界按显示器有效 DPI 换算，无句柄/查询失败退 `SystemParameters.WorkArea`），并按其重算 MaxHeight、把矩形归一（尺寸钳 [min,max]、部分越界钳到最近工作区边界、负坐标在目标屏工作区内原样合法——不钳非负）；运行中变化（显示器断开/缩放/DPI）由 SizeChanged 安全网与恢复共用同一钳制（`ClampIntoCurrentWorkArea`，系统交互缩放期间挂起、`WM_EXITSIZEMOVE` 收口一次）；`WM_GETMINMAXINFO` 按所在显示器工作区修正无边框最大化几何。标题栏拖拽的 DragDelta 钳制维持主屏工作区语义（GOAL §三/§四.2 冻结）。节内容用既有卡片语言分组（Card/Border/圆角 8），颜色全走既有主题字典键；插件行与标签顺序行同卡片行语言，**代码构建行的画刷经动态资源引用解析（`SetResourceReference`，2026-10-13 修订 Issue #140）——主题活切换即跟随新字典（含清单非法行 DangerBrush 分支与 Mica 失败的不透明壳兜底），无需重开窗口或重刷区块**；「AI=AI 设置」页仅配置 API Key，模型恒为 `DeepSeekClient.DefaultModel`（无选择 UI，见 #28，2026-10-13 修订 Issue #141）。
- **由来**：用户报障「设置窗口默认太高、宽高都调不了、窗口高≈屏高致拖动幅度极小」——三者同源（380 固定宽 + `SizeToContent=Height` 全节纵堆 + `NoResize`）。双栏信息架构与冻结几何为规划窗口定稿（GOAL-V1-SETTINGS-PROMPT §四），wbx 双 lane T7 设计评审（job `20261012-103045-vsq`）采纳 15 项修订（单一 ScrollViewer namescope、恢复先选屏再钳制、DIP/物理换算边界、矮屏 MaxHeight 抬底、Win32 可注入缝合点、EXITSIZEMOVE 落盘时机、交互期挂起钳制、最大化修正、RadioButton 原生 SelectionItem、入场页-only 淡入、同 namescope 直书、测试盲区清单等）。
- **锁定**：`tests/FloatingTransferStation.Tests/SettingsWindowNavPaneTests.cs`（冻结顺序与命名、SelectionItem 语义 UIA 路径、↑↓ 循环/Home/End/Tab、双主题导航完好、双主题全节渲染巡检、动效关闭即时切换；2026-10-13 起含 `WindowShell_OpaqueFallbackFollowsLiveThemeSwitch` 不透明壳兜底活切换跟随）、`tests/FloatingTransferStation.Tests/SettingsWindowPluginTests.cs`（插件行渲染/开关即时生效/清单非法行可见不可启用/双主题渲染；2026-10-13 起含 `PluginRows_FollowLiveThemeSwitchWithoutRebuild` 插件行画刷活切换跟随，含 DangerBrush 分支）、`tests/FloatingTransferStation.Tests/SettingsWindowGeometryTests.cs`（冻结约束声明与强制、持久化往返原样恢复、负坐标合法（假左副屏）、部分越界钳入、NaN 回落默认定位、全屏外回落、结束才落盘（拖中零写盘/DragCompleted/EXITSIZEMOVE 路径）+最大化不落盘、矮屏 MaxHeight 抬底、显示器断开退主屏钳制）、`tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs`（几何字段 JSON 往返与旧文件回落）、`tests/FloatingTransferStation.Tests/SettingsWindowInteractionTests.cs`（`SettingsWindow_FixedViewportScrollsOverflowAndKeepsFooterVisible` 取代旧 MaxHeight 公式断言：固定视口滚动 + 页脚常驻可见）。
- **关联**：契约记录 Issue Oiawlm/floating-transfer-station#106；CHANGELOG 未发布区；#5（ApplyPreferences 管线）、#13（标签顺序真实输入用例先导航到「面板」页再消息直驱）、#21（标签顺序节语义零变化，仅容器迁页）不受影响；1.38.0 置顶分隔节修订 Issue #135；2026-10-13 画刷活切换锁定修订 Issue Oiawlm/floating-transfer-station#140（AI 节模型收敛部分见 #28，Issue #141）。

## 十、本次新增（26，检查更新与一键更新）

### 26. 检查更新与一键更新

- **断言**：面板头部按钮行最左常驻「更新」按钮（填充图标 16×16，横杠+向上箭头），双语义由「本地版本 < 远端 tag（剥 v 前缀后 Version 严格大于）」唯一派生——无更新=次级文字色「检查更新」，点击手动检查并就地反馈一次（已是最新版本/失败原因）；有更新=强调色点亮 + ToolTip 与 AutomationProperties.Name 携带新版本号，点击执行完整一键更新，原子顺序固定：下载精确名资产（FloatingTransferStation-Setup-{version}.exe）到 %TEMP% 专用子目录（先清理同版本旧文件与半成品）→ 下载 SHA256SUMS.txt 校验通过才继续 → 排空既有保存管线（提交在编辑内容、冲刷复盘、等待在途操作归零）→ 以 /SILENT /SUPPRESSMSGBOXES /NORESTART 启动安装器（脱钩进程）→ 经既有优雅退出路径关闭 → 安装器 [Run] 条目自动重启新版本（该条目已去 skipifsilent——契约性修订，静默安装后自动重启）。校验失败或下载失败：清理临时文件、状态条提示、按钮保持点亮可重试，绝不启动安装器；绝不带着未排空的写盘启动安装器。自动检查节奏=启动 30 秒后首检 + 每 24 小时一次，任何失败（网络/403/404/429/5xx/超时/坏 JSON/解析）完全静默；检测到新版本且面板可见时状态条提示一次（同一版本不重复提示）；无弹窗、无 toast。精确名资产或校验清单缺失（AssetMissing）仅在手动检查时状态条提示并回退打开发布页（仅 https 地址，浏览器回退每会话至多一次），自动检查对 AssetMissing 同样静默。检查与下载全程单飞防重入（在途时点击与定时 tick 都被忽略）；检查响应/校验清单/安装包读取均有硬字节上限。设置「通用」节「自动检查更新」开关（默认开启，旧 preferences.json 缺字段回落开启），走 ApplyPreferences 唯一管线即时生效：关=停检查定时器，开=恢复节奏；手动检查不受开关影响。仓库 API/HTML 地址常量单一来源 ProductIdentity（更新服务与设置窗页脚链接共用，XAML 无字面量）；仅在自动节奏与用户点击时对 GitHub 联网，零遥测。
- **由来**：用户需求「软件能感知 GitHub 上的新版本并推送到界面——顶部按钮行最左新增更新按钮，用户可自主选择是否点击更新」，三项裁决：①应用内下载+静默安装+自动重启；②按钮常驻双语义；③启动 30 秒延迟首检+每 24 小时一次、设置可关（默认开）。全部逻辑在 Core 可测服务（UpdateService，HttpMessageHandler/进程启动器注入离线可测），UI 只做接线；业界先例（Chrome/PowerToys 着色点亮、Sparkle 24h 节奏、通知疲劳防护）支撑「点亮+状态条一次、无 toast」的克制取向；Inno AppMutex 在安装器启动瞬间检查、静默下互斥提示框默认取 Cancel（安装器静默退出=更新静默失败），故「先排空后启动随即退出」是硬性时序；竞态万一失守仅静默失败，按钮状态由版本比较派生仍可重试。wbx 双 lane 批判评审（job 20261012-220201-djr）修正 1 BLOCKER（资产 302→CDN 必须跟随重定向）与多项失败折叠/资源上限/取消清理问题。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/UpdateServiceTests.cs`（版本比较矩阵、失败折叠为无更新、精确名匹配与 AssetMissing 信号、SHA256 校验通过/失败与清理、同版本旧文件清理、取消清理上抛、静默安装参数、请求头与 URL 契约）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.Updates.cs`（按钮最左、双态点亮与文案、自动静默/手动反馈、单飞防重入、下载失败保持可用可重试、安装→退出顺序、头部悬停淡显与 #17 armed 常显继承、SendInput 真实输入可达、设置开关即时生效与持久化）、`tests/FloatingTransferStation.Tests/SettingsWindowInteractionTests.cs`（头部按钮顺序断言：更新按钮最左，齿轮右移一位）、`tests/FloatingTransferStation.Tests/LifecycleTests.ReleaseMetadata.cs`（RepoUrl_IsSingleSourcedFromProductIdentity：设置窗 XAML 无仓库地址字面量）、`tests/FloatingTransferStation.Tests/LifecycleTests.Installer.cs`（[Run] 体精确等于去 skipifsilent 后的启动条目）、`tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs`（自动检查字段默认开启、JSON 往返、缺字段回落）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.HeaderIconMetrics.cs`（更新按钮图标纳入 16×16 填充图标居中扫描）。
- **关联**：CHANGELOG 未发布区；#5（ApplyPreferences 唯一管线）、#8（version.txt 唯一版本来源）、#13（SendInput 真实输入，PostMessage 在头部区不可达为测试环境既有限制）、#17（armed 常显机制复用）、#24（「通用」节枚举补列）。无独立 spec（交接提示词 GOAL-UPDATE-CHECK-20261012-PROMPT 锁定决策，如实标注）。

## 十、本次新增（25，两态行距同源与光标行带精度）

### 25. 两态行距同源（单一资源）与光标锚点行带精度

- **断言**：卡片文字的行距数值唯一来源是资源 `CardTextLineHeight`（20，`sys:Double`）——正文预览与备注显示行两处显示态、正文编辑器与备注编辑器两处编辑态（`TextBlock.LineHeight` 附加属性 + `BlockLineHeight`，实测在 TextBox 文本宿主上生效：资源值改变编辑态行距随之改变）共同引用，任何位置不得出现第二份行距字面量；显示态与编辑态行带逐像素对齐（同原点、同行距、同一文本同换行数——两态总高一致），这是 #22 修订的「进入编辑光标定位到点击处」就近行映射的确定性前提（点击像素位置在两态间无歧义换算）。锁定测试的精度口径：Win32 消息直驱双击显示态某行行中心，光标纵向落点必须落在编辑态同一视觉行的行带内（跨行断言防坐标空间误判；左缘双击 `CaretIndex <= 1`）；选区空时在光标处插入字符，插入点仍在双击行；自动换行卡（无显式换行符）双击第二视觉行，光标落同一视觉行且两态总高一致；合成双击序列不全选。横向语义与编辑器内原生点击同规（`GetCharacterIndexFromPoint(snapToText: true)` 就近字符）。
- **由来**：用户需求「双击在哪里，闪烁光标就落在哪里，直接从该处开始输入」（光标语义本体随 #22 修订于 2026-10-12 落地，本条固化其度量地基）。行距若两态各异，点击行与编辑行将错位；此前行距以字面量分散三处（显示两处）+ 编辑态自然度量，单一资源化消除双重维护面并使对齐成为结构保证而非巧合。spike 实证：资源临时改 40，编辑态行距随之变化、锁定测试即红——附加属性生效有测试存证。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardCaretAnchor.cs`（`CardCaretAnchor_DoubleClickPlacesCaretAtClickedLineRealInput` Win32 消息直驱端到端含跨行与左缘下界、`CardCaretAnchor_TypingStartsAtClickedPosition`、`CardCaretAnchor_EditorLineMetricsMatchDisplay` 含两态总高一致、`CardCaretAnchor_DoubleClickKeepsSelectionEmptySynthetic`、`CardCaretAnchor_WrappedLineDoubleClickMapsToSameVisualLine`）。
- **关联**：CHANGELOG 未发布区（无独立 spec，如实标注）；#22（2026-10-12 修订的点击定位光标语义与备注显示行行距引用，行距原字面量 20 收拢为本条资源）、#13（真实输入口径）、#7（编辑持焦零改动）、#14（对半分区零改动）。

## 十一、本次新增（27，1.38.0）

### 27. 置顶分区分隔样式（1.38.0 起）

- **断言**：每个分类内，置顶区与普通区之间的视觉分隔只出现在边界容器（`StartsNormalRegion=True` 的普通区首项）的容器模板行 0，**由 `StartsNormalRegion` 唯一驱动**（模板 DataTrigger，Recycling 复用下随 DataContext 重评；无置顶项时无边界标记、分隔不显示；搜索/批量/拖放态零特判，与既有间距机制完全同轴）——禁止第二套边界判定、代码后置逐容器设状态、哨兵数据项、CollectionView 分组。样式为内置四态 **无/实线/虚线/强调条，默认实线**，不可自定义（用户 2026-10-13 裁决；显示名映射只存在于设置窗填充处）；分隔元素宿主在 `BoardListItemStyle` 的 ListBoxItem 容器模板（两行 Grid 行 0），卡片 DataTemplate 叠加结构不动。四态外观以 keyed Style（`PinnedDividerNoneStyle/SolidStyle/DashedStyle/AccentBarStyle`）承载，经窗口级槽位资源 `PinnedDividerActiveStyle` + DynamicResource 生效，**换装只走 `ApplyPreferences` 唯一管线**（即时生效并原子持久化，契约 #5/#1；禁止平行保存路径）；实线=1 DIP `BorderBrush`、虚线=1 DIP `BorderBrush`+DashArray（4 3）、强调条=2 DIP `AccentBrush`、左右 12 DIP 与卡片外距对齐。**None 态零渲染**：分隔元素零高+空描边，即使边界触发器命中也不可见、不占布局，边界卡外距与既有纯间距现状 `12,10,12,4` 逐 DIP 一致（「无」= 升级前观感）；可见态分隔带上下呼吸空间各 12 DIP（4+8 上 / 2+10 下，Fluent Divider 节奏）。分隔不得改变卡片几何、viewport 几何、虚拟化与滚动语义（#2 置顶分区顺序、#15 有界开销、#23 viewport 解耦不受影响；分隔行只加在边界容器内部，不触碰其他卡片外距，不用宽度补偿/负 margin 特判）。
- **由来**：用户需求「置顶与非置顶之间必须有视觉标识（线/颜色均可），且样式要进设置界面供用户选择——只有内置样式、不可自定义」。现状唯一视觉分隔是 `CardRootStyle` 触发器给边界卡多 6 DIP 上边距（12,10,12,4），无线无标签，边界不可辨。方案长在既有分区边界机制上（第一性原理）：`StartsNormalRegion` 已由 `BoardService.Reindex` 逐条计算（Core 服务层零改动），容器模板是分隔元素的自然宿主（卡片模板内部叠加结构不宜动）；四态换装走 `DesignThemeManager.Apply` 同款窗口级资源替换先例。用户裁决四样式与默认实线（「无」保留关闭自由）；控件取 ComboBox 与仓库全部枚举设置一致；旧用户升级后默认从纯间距变实线（用户接受，「必须有标识」）。虚线用 Shape+`StrokeDashArray`（WPF Border 不支持虚线），DPI 防发虚以 `UseLayoutRounding`/`SnapsToDevicePixels` 承载。契约 #24 冻结表随本功能修订（外观=主题/动效/置顶分隔）。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs`（默认 Solid、四值 camelCase 字符串枚举 JSON 往返、旧 JSON 缺字段回落 Solid）、`tests/FloatingTransferStation.Tests/SettingsWindowInteractionTests.cs`（`PinnedDividerComboBox_OffersFourBuiltInStylesDefaultingToSolid` 四项顺序/Tag/默认选中/初始化守卫零保存、`PinnedDividerComboBox_AppliesImmediatelyPersistsAndSurvivesReopen` 即时生效+落盘+重开回显）、`tests/FloatingTransferStation.Tests/SettingsWindowNavPaneTests.cs`（冻结表外观=主题/动效/置顶分隔，双主题全节渲染巡检覆盖新锚点）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.PinnedDivider.cs`（`PinnedDivider_AppearsOnlyOnTheBoundaryContainer`、`PinnedDivider_NoPinnedItemsShowsNothing`、`PinnedDivider_StyleSwapAtRuntimeUpdatesRealizedContainers`、`PinnedDivider_NoneKeepsBoundaryGeometryIdenticalToSpacingOnlyBaseline`、`PinnedDivider_DoesNotChangeCardWidthOrViewportGeometry`）。
- **关联**：契约 #24 修订与新功能记录 Issue Oiawlm/floating-transfer-station#135；#2（分区顺序零改动）、#5（ApplyPreferences 管线）、#15（虚拟化有界实现）、#23（viewport 几何）；CHANGELOG 未发布区。无独立 spec（唯一事实源为维护者本地调研底稿 RESEARCH-PINNED-DIVIDER-20261013，如实标注）。

## 十二、本次新增（28，AI 唯一模型与设置窗代码构建行动态画刷）

### 28. AI 唯一模型与设置窗代码构建行动态画刷

- **断言**：AI 设置仅配置 API Key，模型恒为 `DeepSeekClient.DefaultModel`（deepseek-flash，生产代码唯一模型字面量点，请求 DTO `DeepSeekChatRequest.Model` 是线上协议字段镜像而非选择入口）——无模型选择 UI（下拉/提示行）、无偏好字段、无宿主/客户端 model 参数，线上请求 `model` 字段恒等于 DefaultModel；设置窗代码构建行（标签顺序节行、插件区块行）的画刷经 `SetResourceReference` 动态解析——主题活切换即跟随新字典（含清单非法行 DangerBrush 分支与 Mica 失败的 `WindowShellOpaqueBrush` 不透明壳兜底），无需重开窗口、无需重刷区块、`ApplyTheme` 零重刷逻辑。旧 `preferences.json` 的 `deepSeekModel` 残留键按未知成员静默忽略（与 `autoCleanupLastRunAtUtc` 退役同口径）。
- **由来**：用户 2026-10-13 两项裁决——「设置窗开着切深色，标签顺序与插件两区块不跟随」（根因：行构建期 `TryFindResource` 快照 Brush 实例，字典换装后永不更新；修复＝引用语义对齐全仓动态资源，先例 `MainWindow.Updates.cs`）与「AI 设置出现三个模型，只保留 DeepSeek Flash」（第三项实为设置页把存量偏好旧模型 ID 追加进下拉；产品只需一个模型，选择机制整体删除，记录 Issue Oiawlm/floating-transfer-station#141）。不透明壳兜底收编依据探针实证：复刻兜底赋值后活切主题，壳背景停在旧主题实例（浅 #FFF7F8FA，深应为 #FF202021）。
- **锁定**：`tests/FloatingTransferStation.Tests/SettingsWindowAiTests.cs`（`AiSection_HasNoModelSelector`、`TestConnection_SendsClientDefaultModelWithoutSelectionState`）、`tests/FloatingTransferStation.Core.Tests/DeepSeekClientTests.cs`（`TestConnectionAsync_Success_UsesClientDefaultModelWithLightweightRequest` payload model=DefaultModel、`ChatAsync_SendsBearerAuthToOfficialEndpointAndSerializesRequest`）、`tests/FloatingTransferStation.Core.Tests/AppPreferencesTests.cs`（`Default_HasNoDeepSeekKeyAndOmitsModelField` 默认序列化不含 deepSeekModel、`LegacyPreferencesFile_WithRetiredDeepSeekModelField_LoadsViaProductionStore` 旧键容忍）、`tests/FloatingTransferStation.Tests/SettingsWindowCategoryOrderTests.cs`（`OrderRows_FollowLiveThemeSwitchWithoutRebuild`）、`tests/FloatingTransferStation.Tests/SettingsWindowPluginTests.cs`（`PluginRows_FollowLiveThemeSwitchWithoutRebuild`）、`tests/FloatingTransferStation.Tests/SettingsWindowNavPaneTests.cs`（`WindowShell_OpaqueFallbackFollowsLiveThemeSwitch`）。
- **关联**：契约记录 Issue Oiawlm/floating-transfer-station#140（深色适配）与 Issue Oiawlm/floating-transfer-station#141（模型收敛）；#21/#24 同批修订；#5（即时生效唯一管线）、#9（插件边界）、#1（原子写不受影响）；无独立 spec（唯一事实源为维护者本地调研底稿 RESEARCH-DARKMODE-AI-MODEL-20261013，如实标注）。


## 维护规则

- 新契约入清单时机：行为定型的同一版本，随 CHANGELOG 与锁定测试一起落。
- 「锁定」宁缺毋滥：没有锁定测试的如实标「无锁定测试（补测候选）」，不编造测试名。
- 改契约先开 Issue；改行为必须同步本清单与锁定测试，否则视为回退。
