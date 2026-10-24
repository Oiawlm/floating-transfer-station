# README 风格指南

> **一句话定位**：本仓库 README 与对外门面文档的**项目增量规则**——在通用 README 写作方法论之上叠加本仓库特有约束（版本化联动冻结、路径与术语书写）；通用方法论（版面骨架、列表与序号、视觉件、中文排版、质量验收清单）不在本仓库重复维护，见文末「公开元规范指引」。
> **来源**：2026-10-18「README 风格体系」调研定稿（10＋2 软件类标杆仓库核实）入库；2026-10-19 通用部分上收为跨项目资产，本文件瘦身为项目增量。
> **日期**：2026-10-19（v2）　**行数预算**：≤60 行

## 0. 适用范围与优先级

- 适用对象：根 README 与对外门面文档；`docs/usage.md` 等门面配套文档参照执行。
- 优先级：本文件的项目约束与已核实事实**优先于**通用方法论建议；冲突按本文件执行并显式说明。
- 门面使命：让陌生访客「3 秒懂 → 30 秒信 → 3 分钟装」；服务不到旅程的内容移入 `docs/` 或折叠，不删除（信息零丢失）。

## E. 版本化联动（本项目根因规则）

- **E1** 版本展示一律用动态徽章，正文不写「最新版本是 x.y.z」。
- **E2** 「下载与安装」节是**发行列车冻结段**：版本号、发行页 URL、安装包文件名、`SHA256SUMS` 表述等事实 token 只在发行列车（`docs/releasing.md`）从 main 改动；普通分支改 README 不得触碰——排版可调，token 逐字保留；没把握就整节原文保留。
- **E3** 正文散落的版本号注（「1.20.0 新增」类）可精简，但须先对照 `docs/README.md` 功能溯源表与 CHANGELOG 确认该事实已有公开记录。
- **E4** 内部编号（B-005/B-006 等观察编号、设计契约编号）不得出现在公开 README；对应事实（已知问题、低优先级、未能稳定复现）保留。
- **E5** 每次发版改 README 前过一遍质量验收（信息零丢失映射、引用资产与链接可达、GitHub 渲染验证、徽章可达、中文排版抽查、冻结 token 逐字一致），并留验证证据。

## D8 与项目事实句

- **D8** 本项目路径书写与安装器字面一致：自定义数据目录为所选文件夹下的「Data」（2026-10-24 起，契约 #46）、默认数据目录「`%LocalAppData%\悬浮中转站\Data`」、程序目录 `%LocalAppData%\Programs\悬浮中转站\`。
- 引号全文统一「」；术语全文统一（中文叙述用「发行版」，GitHub Release 作为专有名词除外）。

## 公开元规范指引（通用方法论）

- [awesome-readme](https://github.com/matiassingers/awesome-readme)、[makeareadme](https://www.makeareadme.com/)、[GitHub 写作文档（alerts / details / 锚点）](https://docs.github.com/get-started/writing-on-github)。
- [中文文案排版指北](https://github.com/sparanoid/chinese-copywriting-guidelines)、[中文技术文档写作规范](https://github.com/ruanyf/document-style-guide)。
- 通用视觉决策（配图媒介选择、体积预算、演示 GIF 录制管线）以上述公开规范与社区惯例为准；本仓库视觉资产构成与再生方式见 [images/README.md](images/README.md)。
