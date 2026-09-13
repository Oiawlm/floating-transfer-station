# 文本1每日复盘标签实施计划

> **For agentic workers:** 按任务顺序实现，每个行为先写失败测试，再写最小实现；保持 Windows 与 Mac 同步。

**Goal:** 将“文本1”变为可按天编辑、自动保存并支持外部 Markdown 同步的复盘入口。

**Architecture:** Core 提供独立的按日期 Markdown 存储、监听和三方合并；WPF/Avalonia 仅负责编辑器、日期导航、冲突交互和生命周期。现有板卡服务契约保留，生产 UI 阻止复盘分类接收板卡。

**Tech Stack:** .NET 10、WPF、Avalonia 11、MSTest、`FileSystemWatcher`、现有 `AtomicTextWriter`。

## 执行顺序

1. 创建 GitHub Issue #26 和 `codex/daily-review-tab` 分支，提交本设计记录。
2. 先写并通过 Core 的存储、监听、合并和迁移测试，再实现 `AppPaths`、`WindowSettings`、`IDailyReviewStore`、`DailyReviewMigration` 和 `DailyReviewMerge`。
3. 接入 Windows 启动迁移与复盘 UI，补 WPF 自动保存、日期切换、冲突和快捷键隔离测试。
4. 接入 Mac 启动迁移与复盘 UI，补 Avalonia headless 测试和 smoke 验证。
5. 更新 README、架构、路线图和 CHANGELOG，运行格式验证、两套 Release 测试和严格构建，生成不入库的真实 UI 截图。
6. 质量门通过后提交并推送分支，创建 PR；本次不修改版本号、不发布安装包。

## 关键验收

- 首次迁移只清空 `Reference` 卡片并保持其他分类不变。
- 今天的 Markdown 内容可写入、重启恢复、按日期回看，空白内容删除文件。
- 应用与 Obsidian 修改同一文件时能刷新；未保存冲突不会静默丢失。
- 复盘标签不改变剪贴板目标、不接受拖放、不触发板卡操作。
- Windows 和 Mac 的保存、失败恢复和关闭等待一致。
