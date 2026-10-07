# 实施计划：卡片文字避让、一次性贴边隐藏与设计契约沉淀（2026-10-07）

设计定稿见[同日设计规格](../specs/2026-10-07-text-ops-avoidance-and-edge-hide-design.md)。三轨道共用分支 `feature/edge-hide-and-text-avoidance` 与版本 1.20.0；每轨道至少一次提交，全部完成后统一质量门、PR、CI、合并、发行与本机安装。

## S0：规格与计划落盘

- [x] spec + plan 落 `docs/superpowers/`，登记 `docs/README.md` 索引。
- [x] 执行提示词已在 `docs/handoff/2026-10-07-edge-hide-and-text-overlap-execution-prompt.md`，随首个提交入库。

## 轨道一：文字避让（失败先行）

- [ ] 新增 STA 渲染测试（`MainWindowInteractionTests.CardOperationAvoidance.cs`）：文字卡 `PreviewText` 布局槽与两操作按钮边界零交集（按钮淡入不改布局几何，静态断言覆盖两种可见态）+ 派生资源关系锁定；先跑确认当前代码失败并保存输出。
- [ ] WBX T8 双份草稿（不同 lane 同题）→ 逐行评审择优 → 由主代理改写为与既有风格融合的最终实现：`MainWindowStyles.xaml` 派生资源 `CardTextReservedRightInset`（两列 `CardOperationColumnWidth` + 间隙）+ `PreviewText` Kind=Text 触发器右内缩。
- [ ] 既有 `CardGestures`/`CardHalves`/`CardRendering` 测试原样通过。
- [ ] 隔离实例截图取证：悬停长文字卡前后对比（不入库）。

## 轨道二：一次性贴边隐藏（失败先行）

- [ ] Core 失败先行单测：`EdgeHideStateMachine`（切换语义、一次性、抑制复用、其他收起路径解除、Docked→回位→disarm）。
- [ ] `WindowController` 几何失败先行单测：`EdgeHidden`（零交集不变量、多显示器样例、DWM 阴影余量）、`EdgeRecallZone`（外扩容差、物理像素包含语义）。
- [ ] WBX T8 双份状态机骨架草稿 → 逐行评审择优 → 主代理改写融合。
- [ ] Core 实现：`EdgeHideStateMachine`（`Idle ⇄ Armed → Docked → Idle`，`ToggleArm`/`TryDock(collapseWouldCommit)`/`Reset`）。
- [ ] 几何实现：`WindowController.EdgeHidden/EdgeRecallZone` + `PhysicalRectangle`；`ScreenEdgeGeometry` 增加显示器枚举并让既有裁切判定复用；`NativeMethods` 新增 `GetCursorPos`/`GetWindowRect`。
- [ ] Windows 端实现（`MainWindow.EdgeHide.cs` + 既有管线融合点）：头部第 6 按钮（图标/ToolTip/armed Accent/armed 期间头部常显）；`CollapseTimer_Tick` 贴边隐藏分支（同尺寸纯移动、保存回位区物理矩形与原放置）；回位轮询（`DispatcherTimer` ~100ms 仅 Docked 期间，驻留 ≥200ms）；恢复=纯移动回原矩形+disarm+`ShowStatus`；热键与显示器变化兜底；外部拖放收起路径解除 armed。
- [ ] STA 交互测试（`MainWindowInteractionTests.EdgeHide.cs`）全部转绿：离屏零交集且面板仍展开、同尺寸；回位驻留恢复原矩形且 disarm；驻留离开重置；恢复后再次离开仅普通收起；再按取消后仅普通收起；热键/显示器变化兜底；其他收起路径解除 armed；按钮可访问性状态。
- [ ] 隔离实例录屏取证（armed→离开→移出→移回驻留→原位原形→再次离开仅普通收起；热键找回、显示器变化兜底；不入库）。

## 轨道三：契约沉淀

- [ ] 新建 `docs/design-contracts.md`：迁出 improvement-loop.md 冻结清单 11 条（升级四字段）+ 补登（1.8.x 迁移原子性、1.17.1 真实输入、1.18.0 对半分区、1.19.0 性能根因）+ 新增本次两条；「锁定」宁缺毋滥。
- [ ] `docs/improvement-loop.md` 冻结清单段指针化；`AGENTS.md` 首条规则加必读指针；`docs/README.md` 索引登记。
- [ ] WBX T7 评审契约清单草稿后定稿。

## 轨道四：文档与版本

- [ ] `docs/design.md` 补记文字避让 token 与贴边隐藏行为契约两节。
- [ ] README 功能说明与截图同步（新增头部按钮属用户可见变化）。
- [ ] `CHANGELOG.md` 未发布区 → 1.20.0；`version.txt` → 1.20.0；ROADMAP 如有对应条目同步。

## 收尾（必做）

- [ ] WBX T7 评审批判全部代码改动（diff 要点 + 契约清单），按结论修正。
- [ ] 质量门四连：Core Debug 预构建 → `dotnet format --verify-no-changes` → `check-repo-hygiene` → Release 全量测试 → 严格 Release 构建 0 警告。
- [ ] PR → CI（Windows + Apple Silicon）全绿 → 合入 main。
- [ ] 统一发行 v1.20.0：`build-release.ps1 -ForRelease` → 同一 GitHub Release 上传 Windows 安装包、`osx-arm64` zip（如实标注未公证测试状态）、`SHA256SUMS.txt`；README 下载入口同步；记录链接与 SHA256。
- [ ] 本机安装：优雅关闭运行实例 → 静默原地更新 → 重启 → 验证版本 1.20.0 与自启项，向用户展示验证结果与轨道取证。
