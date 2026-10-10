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

### 16. 文字卡避让操作列（1.20.0 起；1.24.0 修订为三列）

- **断言**：文字卡正文右侧为右上角操作按钮条预留排他空间，任何时刻（悬停/非悬停）零像素争用；内缩量唯一来源是派生资源 `CardTextReservedRightInset`（操作列 `CardOperationColumnWidth` 总宽 + 4 DIP 间隙；1.20.0 为两列按钮计 64，1.24.0 随卡片删除按钮修订为三列计 94，见 #19），不得在别处写死第二个数值；命中层对半分区、图片卡全幅与角按钮契约不变。
- **由来**：1.18.0 对半分区分层解决了命中歧义但视觉层没有为按钮预留空间——悬停淡入的按钮压在正文第一、二行右上角（实测 60px 全额争用）；1.24.0 操作条扩为三列（新增删除按钮），派生内缩同步加宽一列。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardOperationAvoidance.cs`（零交集 + 派生关系）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs`（`TextCard_ReservesFixedPinSelectionAndDeleteColumns`）。
- **关联**：同日设计规格：`docs/superpowers/specs/2026-10-07-text-ops-avoidance-and-edge-hide-design.md`、[设计规范 §五](design.md)；1.24.0 修订 Issue 见 #19 关联。

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

### 19. 卡片操作按钮状态矩阵与单卡删除（1.24.0 起）

- **断言**：卡片右上操作条三枚按钮（选择/置顶/删除）的可见性与可点性由 (悬停 `IsMouseOver`, 选中 `IsSelected`, 置顶 `IsPinned`) 三信号的状态矩阵唯一派生，集中实现在卡片样式触发器层：**未选中+悬停**三钮全部显示可点；**未选中+未悬停**仅已置顶卡片的置顶钮常显可点（=取消置顶），其余隐藏；**选中（无论悬停）**选择钮常显可点，置顶钮未置顶隐藏、已置顶常显为纯状态徽章（换纯展示模板、以次文字色呈现，点击不改置顶、不改选中），删除钮一律隐藏；选中卡片的置顶/删除操作一律走顶部批量按钮。徽章的「点击无任何效果」由 `ToggleCardPinAsync` 入口守卫统一实现（按钮保持启用且可命中以吞掉点击——WPF 禁用元素不可命中，`IsEnabled=false` 会让点击穿透为右半选择手势误改选中），真实输入（手势层 OperationsPin 会话）与合成/UIA 路径（`BoardList_ButtonClick`）共用同一条守卫。删除钮单击只删该卡：手势层新增 `OperationsDelete` 会话（模式同 OperationsPin，按钮点击不进双击窗、滑离释放取消意图），经窗口层既有删除路径 `DeleteContentAsync → BoardMutationService.DeleteManyAsync` 执行——撤销栈（Ctrl+Z 锚点插回）、原子持久化、保存失败整卡恢复、删除淡出与重入保护全复用；不改当前选中集合（保存成功后原样还原删除前选择，`DeleteContentAsync` 以可选 `selectionToRestore` 参数化，垃圾桶/键盘删除路径行为不变）、不滚动列表；操作条内右键复制不受三列扩展影响。文字卡避让内缩随三列派生（`CardTextReservedRightInset` = 三列 `CardOperationColumnWidth` + 4 DIP 间隙，见 #16）。
- **由来**：1.24.0 用户需求「卡片级删除按钮 + 选中态卡片操作规则」（两轮对齐确认）。行内悬停操作逐条生效 + 批量走顶部工具栏与桌面邮件类（Gmail/Outlook）及 Fluent/Material「行内次级操作 × 选择模式」正交维度一致（调研 job `20261010-160927-oa3`）；「选择模式=批量模式」下隐藏行内按钮是文件管理器与移动端主流。矩阵替代 1.20.0 前的隐式规则时同步修订了两处现状：选中+未置顶卡片的置顶钮由「悬停可见可点」收紧为隐藏（R2 表格明确选中卡片一律走顶部批量）；选中+已置顶置顶钮由「可点=就地取消置顶」改为纯状态徽章。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardDeleteButton.cs`（矩阵四状态 + Win32 消息直驱的单卡删除保留选择滚动与撤销、徽章真实点击无效、滑离取消、操作条内右键复制）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CardOperationAvoidance.cs`（三列派生内缩）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.VisualLifecycle.cs`（`TextCard_ReservesFixedPinSelectionAndDeleteColumns`）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.SelectionAndDeletion.cs` 与 `RangeSelection.cs`（矩阵化的单卡置顶用例：置顶单击只作用于未选中卡片）、批量置顶语义锁定测试（SelectionAndDeletion 批量用例）继续全绿。
- **关联**：CHANGELOG 1.24.0；契约 #16 三列修订记录 Issue（Oiawlm/floating-transfer-station#88）。

## 六、本次新增（20–21，1.25.0）

### 20. 复盘编辑器 Esc 退出编辑态（1.25.0 起）

- **断言**：复盘编辑器持有键盘焦点时按 Esc 释放键盘焦点并标记按键已处理；焦点去向是宿主 Window 本身（必须先清空焦点域记录再聚焦——WPF 会把「聚焦焦点域本体」重定向回域内 `FocusedElement`，即编辑器自身），绝不把焦点塞给隐藏的 BoardList；面板去留交给既有焦点链（`Root_PreviewLostKeyboardFocus` 统一清算编辑保持原因并重估表面）——指针在面板内时维持展开（与 #7 同源），否则恢复收起节奏；IME 组合期的 Esc 属输入法操作（取消候选）不触发退出（组合标记守卫，与卡片就地编辑/分类改名框同族，标记在编辑器失焦时清理）；不引入提交/取消会话（复盘是 700ms 防抖自动保存，无「取消」概念）；卡片就地编辑、分类改名、搜索框的既有 Esc 语义不变。
- **由来**：1.25.0 用户需求「复盘界面打字时按 Esc 退出可输入状态，但鼠标还在界面里所以保持展开」。此前复盘编辑器对 Esc 彻底无操作——窗口级 PreviewKeyDown 的搜索分支在复盘页不可达（搜索排除复盘），清选择分支被 `is not TextBoxBase` 守卫挡住；「保持展开」机制（焦点链 + 表面重估）现成，缺的只是 ESC 断焦点这一环。
- **锁定**：`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.ReviewEditorEscape.cs`（焦点释放到宿主窗口且编辑保持清算、真实光标在面板内时保持展开且不启动收起计时、组合标记存在时守卫不触发且焦点不移交）；`tests/FloatingTransferStation.Tests/PanelStateMachineTests.cs`（#7）与既有 ESC 路径用例继续全绿。
- **关联**：CHANGELOG 1.25.0（无独立 spec，如实标注）。

### 21. 标签显示顺序（1.25.0 起）

- **断言**：四个标签（三个板卡分类 + 复盘）的显示顺序是独立持久化状态（settings.json `CategoryOrder`，原子写；与分类名零耦合、与 board.json 条目序/`SweepCategories`/`WithCategoryName` 全量快照序解耦——后三者按目录默认序继续）；只调顺序、不能增删分类；读取端唯一收敛于 `WindowSettings.DisplayOrder`——null（未定制）或非法（成员未定义、数量不符、重复）一律回落目录默认序，校验一次做齐；消费面=面板标签轨（`MainWindowViewModel.Categories` 按显示顺序重排，只复用既有 `CategoryViewModel` 实例、绝不新建——实例身份被 ActivePanel/默认接收/复盘表面切换依赖）与收起把手几何（行号=分类在显示顺序中的位置）；排序入口只在设置窗口「标签顺序」节（面板标签轨本身不提供拖拽）；节内拖拽自研零依赖（把手按下捕获鼠标 → 拖起原位行半透明跟随 → 其余行 TranslateTransform 让位 → 2px 强调色插入指示线；动效只用既有 DesignTokens 档，「界面动效」关闭退化为瞬时换位；浅/深主题用主题字典画刷）并必须同时提供上移/下移按钮（WCAG 2.5.7 单指针等价操作）；拖拽会话中的 Esc 由顺序节优先消费（回弹原序不提交，处理顺序在设置窗口既有 Esc 关窗路径之前），捕获丢失/失焦同样回弹；拖拽与按钮提交都走宿主 `ApplyCategoryOrderAsync` → settings.json 原子保存 + 标签轨即时重排（契约 #5），保存失败恢复内存原顺序；`ResetToDefault` 保留顺序定制（与 CategoryNames 同口径）。
- **由来**：1.25.0 用户需求「设置界面可以调整『图片、复盘、文本、待分类』这几个东西的顺序和位置……希望可以变成那种可以拖动的」。顺序与目录解耦使显示序成为纯 UI 偏好，不触碰任何数据语义（清扫范围、拖放、搜索、默认接收全部按分类身份工作）。
- **锁定**：`tests/FloatingTransferStation.Core.Tests/CategoryDisplayOrderTests.cs`（非法回落、JSON 往返、ResetToDefault 保留、ViewModel 重排复用实例并通知）、`tests/FloatingTransferStation.Tests/LocalStoreTests.cs`（CategoryOrder 经 LocalStore 原子往返）、`tests/FloatingTransferStation.Tests/WindowControllerTests.cs`（自定义序收起行号与非法回落）、`tests/FloatingTransferStation.Tests/MainWindowInteractionTests.CategoryOrder.cs`（rail 按显示顺序渲染、收起把手行号、宿主采纳即重排并持久化）、`tests/FloatingTransferStation.Tests/SettingsWindowCategoryOrderTests.cs`（节渲染与端点禁用、上移/下移提交即落盘、真实消息直驱拖拽提交、拖拽中 Esc 优先消费不关窗、捕获丢失回弹不提交）。
- **关联**：CHANGELOG 1.25.0（无独立 spec，如实标注）。

## 维护规则

- 新契约入清单时机：行为定型的同一版本，随 CHANGELOG 与锁定测试一起落。
- 「锁定」宁缺毋滥：没有锁定测试的如实标「无锁定测试（补测候选）」，不编造测试名。
- 改契约先开 Issue；改行为必须同步本清单与锁定测试，否则视为回退。
