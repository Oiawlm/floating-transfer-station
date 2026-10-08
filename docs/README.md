# 文档索引

使用者与贡献者入口：根目录 [README](../README.md)（下载、安装与功能说明）、[贡献指南](../CONTRIBUTING.md)、[路线图](../ROADMAP.md)。本目录内：

- [架构说明](architecture.md)：主要组件与数据流。
- [发布指南](releasing.md)：版本发布流程与质量门。
- [设计规范](design.md)：界面颜色、圆角、间距与动效 token。
- [设计契约清单](design-contracts.md)：已定型、不得回退的行为契约（断言/由来/锁定测试/关联）。
- [images/README.md](images/README.md)：README 视觉资产的构成与再生方式。

## 功能溯源

功能如何落地以公开记录为准：变更明细见 [CHANGELOG](../CHANGELOG.md) 对应版本节，代码状态见仓库内同版本号 tag，安装包见 [GitHub Releases](https://github.com/Oiawlm/floating-transfer-station/releases)。下表为主要功能与版本对照（链接指向 CHANGELOG 锚点）。

| 功能 | 落地版本 | 记录 |
|---|---|---|
| 批量置顶 / Ctrl+A 全选 / Esc 清除选择 | 1.1.0 / 1.2.0 | [CHANGELOG](../CHANGELOG.md#110) |
| Delete 删除选择 | 1.3.0 | [CHANGELOG](../CHANGELOG.md#130) |
| F2 分类改名 / 隐藏批量置顶守卫 | 1.4.0 / 1.4.1 | [CHANGELOG](../CHANGELOG.md#140) |
| 仓库维护与卸载边界 | 1.4.2 | [CHANGELOG](../CHANGELOG.md#142) |
| 剪贴板损坏图片表示回退 | 1.4.3 | [CHANGELOG](../CHANGELOG.md#143) |
| 连续范围选择 | 1.5.0 | [CHANGELOG](../CHANGELOG.md#150) |
| 跨平台统一发行（历史，Mac 端已移除） | 1.6.0 | [CHANGELOG](../CHANGELOG.md#160) |
| 每日复盘标签 / 界面与动效升级 | 1.7.0 | [CHANGELOG](../CHANGELOG.md#170) |
| 设置首版 | 1.8.0 | [CHANGELOG](../CHANGELOG.md#180) |
| 窗口几何原子化 | 1.8.1 | [CHANGELOG](../CHANGELOG.md#181) |
| 插件系统 | 1.9.0 | [CHANGELOG](../CHANGELOG.md#190) |
| 采集去重 | 1.9.1 | [CHANGELOG](../CHANGELOG.md#191) |
| 全局快捷键唤起 | 1.10.0 | [CHANGELOG](../CHANGELOG.md#1100) |
| 撤销最近删除 | 1.11.0 | [CHANGELOG](../CHANGELOG.md#1110) |
| 插入指示条闪烁 + 卡片文字清晰度 | 1.11.3 | [CHANGELOG](../CHANGELOG.md#1113) |
| 卡片内容就地编辑 | 1.14.0 | [CHANGELOG](../CHANGELOG.md#1140) |
| 内容搜索切片 1 | 1.13.0 | [CHANGELOG](../CHANGELOG.md#1130) |
| 卡片复制到剪贴板 / 垃圾桶双交互 | 1.15.0 | [CHANGELOG](../CHANGELOG.md#1150) |
| 复盘主题修复 / 数据目录迁移 | 1.16.0 | [CHANGELOG](../CHANGELOG.md#1160) |
| 真实输入可达性 | 1.17.0 / 1.17.1 | [CHANGELOG](../CHANGELOG.md#1170) |
| 卡片对半分区 | 1.18.0 | [CHANGELOG](../CHANGELOG.md#1180) |
| 大数据量显示根因修复 | 1.19.0 | [CHANGELOG](../CHANGELOG.md#1190) |
| 卡片文字避让、一次性贴边隐藏 | 1.20.0 | [CHANGELOG](../CHANGELOG.md#1200) |
| 可见态四角圆角恒定 | 1.21.0 | [CHANGELOG](../CHANGELOG.md#1210) |

尚未实施的方向（内容搜索后续切片、回收站、键盘直达、卡片多行编辑等）见[路线图候选池](../ROADMAP.md#持续改进候选池)。

设计与实施计划类过程资料由维护者本地保存，自 2026-10-08 起不在仓库内；仓库只保留软件本体与源码。截图、安装包和本机验证结果放在不提交的 `artifacts/`、`TestResults/` 中。
