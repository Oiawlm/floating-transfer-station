# 文档索引

当前规则以[架构说明](architecture.md)、[项目指南](../PROJECT_GUIDE.md)和[贡献指南](../CONTRIBUTING.md)为准。版本发布使用[发布指南](releasing.md)，尚未稳定复现的问题见[现场观察](observations.md)。

设计和计划是当时的决策记录。下面的提交与发布版本说明落地位置；历史计划中的未勾选步骤保持原样，不表示今天仍需重新执行，也不替代人工验收证据。

| 功能 | 规格 | 实施计划 | 已落地代码 / 发布 |
|---|---|---|---|
| 批量置顶 | [设计](superpowers/specs/2026-08-28-batch-pin-design.md) | [计划](superpowers/plans/2026-08-28-batch-pin.md) | `8b9df2f` / 1.1.0 |
| Ctrl+A 全选 | [设计](superpowers/specs/2026-08-29-select-all-shortcut-design.md) | [计划](superpowers/plans/2026-08-29-select-all-shortcut.md) | `b7d18d0` / 1.1.0 |
| Esc 清除选择 | [设计](superpowers/specs/2026-08-30-clear-selection-shortcut-design.md) | [计划](superpowers/plans/2026-08-30-clear-selection-shortcut.md) | `4cc197e` / 1.2.0 |
| Delete 删除选择 | [设计](superpowers/specs/2026-08-31-delete-key-shortcut-design.md) | [计划](superpowers/plans/2026-08-31-delete-key-shortcut.md) | `935700e` / 1.3.0 |
| F2 分类改名 | [设计](superpowers/specs/2026-09-01-f2-category-rename-design.md) | [计划](superpowers/plans/2026-09-01-f2-category-rename.md) | `925bbc6` / 1.4.0 |
| 隐藏批量置顶守卫 | [设计](superpowers/specs/2026-09-02-batch-pin-command-guard-design.md) | [计划](superpowers/plans/2026-09-02-batch-pin-command-guard.md) | `6e73eb2` / 1.4.1 |
| 仓库维护与边界修复 | 已批准的审查结论写入计划 | [计划与完成记录](superpowers/plans/2026-09-03-repository-maintenance.md) | 当前开发分支 / 未发布 |

批量置顶早期设计中的“恢复最初选择”，后来受到 Esc 设计中“用户已取消选择则不恢复”的修订；隐藏状态下 Ctrl+P 的约束由 1.4.1 守卫设计补充。理解当前行为时需要连同后续修订一起阅读。

| 历史发布计划 | 对应 main 提交 |
|---|---|
| [1.1.0](superpowers/plans/2026-08-29-release-1.1.0.md) | `a7668c4` |
| [1.2.0](superpowers/plans/2026-08-30-release-1.2.0.md) | `bdbdf9f` |
| [1.3.0](superpowers/plans/2026-08-31-release-1.3.0.md) | `497c414` |
| [1.4.0](superpowers/plans/2026-09-01-release-1.4.0.md) | `28fa0c6` |
| [1.4.1](superpowers/plans/2026-09-02-release-1.4.1.md) | `0db9f2f` |

后续发布以当前发布指南为入口，旧计划留作追溯。截图、安装包和本机验证结果继续放在不提交的 `artifacts/`、`TestResults/` 中。
