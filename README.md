# 悬浮中转站

一个贴在屏幕边缘的文字与图片中转站。复制、拖进来、分个类，再把内容拖到真正需要它的软件里。

> 当前正式版支持 Windows 10/11 64 位。macOS 14+ 的 Apple Silicon / Intel 适配已加入源码与同步构建，处于候选验证阶段，尚未正式发布。

## 下载与安装

前往 [Releases](https://github.com/Oiawlm/floating-transfer-station/releases) 下载 `FloatingTransferStation-Setup-1.5.1.exe`。

1. 运行安装程序。
2. 选择程序安装位置和内容存储父目录；不修改时使用当前用户的本地目录。
3. 安装完成后软件会启动，并在以后登录 Windows 时自动运行。

本地构建产物使用中文名，GitHub Release 为了稳定下载链接使用上面的英文文件名。Release 页面中的 `.zip` / `.tar.gz` 是 GitHub 自动生成的源码包，不是 Windows 安装程序。

## 它能做什么

以下为 Windows 正式版说明；Mac 候选版的操作与验证边界见下方“Mac 版”。

- **随手收集**：复制图片或文字后，内容自动进入当前默认分类；来源标记为禁止历史记录的内容会跳过自动采集。
- **图片容错**：同次复制提供多个图片表示时，读取或解码某个表示失败仍会尝试其他有效表示，优先保存可用图片中像素最多的一份；照片保留正确的旋转或镜像方向。
- **批量复制**：一次复制多张图片保留来源顺序，损坏文件不影响同批其他可用图片。
- **指定位置放入**：可以从资源管理器、浏览器、微信等软件把常见静态图片或非空文字直接拖到某个分类。
- **整理内容**：支持四个可改名分类、置顶、批量置顶、直接多选、批量移动、批量删除和分类内排序。
- **再拖出去**：图片和文字使用 Windows 通用拖放格式，可拖到支持这些格式的软件；纯图片多选可以按原顺序一起拖出。
- **不挡工作区**：窗口贴在屏幕右侧并保持置顶，空闲时收成一条分类标签，移入后再展开。
- **本地保存**：内容、顺序、置顶状态、分类名称和窗口位置保存在本机。

四个分类的默认名称从上到下是“图片、文本1、文本2、待分类”，都可以修改。覆盖安装新版本时会保留已保存的分类名称；只有尚未保存名称的分类使用默认值。

## 几个常用操作

- 单击右侧分类标签：切换本次运行的默认接收分类。
- 双击分类标签：原地改名，最多 6 个可见文字单元。
- `F2`：改名当前展开分类；进入编辑后按 `Enter` 保存、按 `Esc` 取消。
- `Ctrl + 单击` 或卡片选择框：多选内容。
- `Shift + 单击`：从最近一次用选择框或 `Ctrl + 单击` 选中的卡片开始，选择到当前卡片的连续区间；`Ctrl + Shift + 单击` 将区间追加到现有选择。连续调整终点会保持起点；没有有效起点时只选择当前卡片。
- `Ctrl + A`：选择当前分类全部内容；正在编辑分类名称时仍然只会全选文字。
- `Esc`：取消当前分类的全部选择；正在编辑分类名称时仍然取消本次改名。
- 点击卡片图钉：置顶或取消置顶。
- 多选后点击顶部图钉或按 `Ctrl + P`：批量置顶或取消置顶；只要所选内容中有未置顶项就会统一置顶，全都已置顶时则统一取消置顶；`Ctrl + P` 只在面板展开且不在编辑分类名称时生效。
- 拖动卡片：分类内排序、移动到其他分类，或拖到外部软件。
- 顶部垃圾桶：有选择时删除选中项，没有选择时清空当前分类；删除保存期间按钮暂时禁用，避免重复点击误清空。
- `Delete` 或 `Backspace`：只删除选中项；正在编辑文字时仍然正常删字。

## 数据和卸载

默认程序目录是 `%LocalAppData%\Programs\悬浮中转站\`，默认数据目录是 `%LocalAppData%\悬浮中转站\Data\`。安装或更新时可以改选两者的位置。

卸载会删除程序、自启项，以及应用登记并管理的 `悬浮中转站\Data`；不会删除你选择的父目录里的其他文件。重要内容仍建议另外备份。

从 1.4.3 或更早版本升级时，先保留原程序目录完成一次原地更新（1.4.4 或更新版本），再运行安装器更换程序目录；内容存储位置可以照常选择。新卸载器会核对当前安装目录的归属，清理失败会提示并保留数据位置登记。修复前已遗留的旧卸载器无法追溯保护，应从 Windows 设置或当前程序目录进入卸载。

## Mac 版

Mac 候选版使用 Avalonia 界面，复用 Windows 的分类、排序、置顶、批量变更和原子保存核心。支持文字/静态图片收集、四分类改名、连续选择、批量置顶/移动/删除、向外拖出文字或多张图片、右侧置顶与悬停展开。单击分类指定本次运行的默认收集分类；初始为“待分类”。两端共用 `version.txt`，版本号相同不表示 Mac 候选包已经正式发布。

Mac 使用 `⌘` 替代上述快捷键中的 `Ctrl`；双击分类或 `F2` 改名，编辑时按 `Enter` 保存、`Esc` 取消。`⌘ + V` 或“粘贴”按钮手动收集，`⌘ + C` 复制选中的一段文字或一组图片，`⌘ + Q` 或窗口右上角 × 保存后退出。

- **安装**：`FloatingTransferStation-<版本>-osx-arm64.zip` 用于 Apple Silicon，`osx-x64.zip` 用于 Intel。解压后将 `FloatingTransferStation.app` 拖入“应用程序”，无需另装 .NET。
- **数据**：保存在 `~/Library/Application Support/FloatingTransferStation/Data/`。删除应用本身保留数据；需要彻底删除时，先退出并备份，再由用户手动删除这个精确目录。Mac 不读取 Windows 安装登记。
- **采集边界**：每 500 ms 检查一次剪贴板，规范化/保存期间只处理一个采集，极快连续复制可能无法逐条记录；手动粘贴或重新复制可补收。尊重 NSPasteboard 的隐私/临时内容标记。暂不自动登记登录启动，可在 macOS 系统设置的登录项中添加应用。
- **验证状态**：本机可交叉编译两个 Mac 包，并运行跨平台测试和 Windows 上的 Avalonia 窗口验证。两种 Mac 架构的 CI 验证原生启动、窗口截图，以及合成文字、隐私标记、编码图片和文件剪贴板传输；具体结果以对应提交的 CI 和附件为准。第三方软件间拖放仍需 Mac 人工验收；Windows 截图不作为 Mac 实机证据。
- **签名状态**：本机构建是未经 Apple 公证的候选包，首次打开可能被 Gatekeeper 阻止；正式分发前仍需 Developer ID 签名和公证。Mac CI 仅做临时签名供测试。

单独生成两个 Mac 候选包：

```powershell
& ./scripts/build-macos.ps1
```

输出在 `artifacts/macos/<架构>/`，同时提供 SHA-256 和签名/原生验证状态的 JSON。中间 `.app` 打包后自动清理，只保留压缩包；开发时需要保留可加 `-KeepAppBundle`。

## 当前限制

以下限制适用于 Windows 正式版。

- 动态分类增删、设置界面、快捷启动和常驻模式仍在路线图中，不属于 1.5.1 承诺。
- 每个编码图片表示或源文件最多 64 MiB、6,400 万像素；超限不会静默缩小原图。连续大量复制达到待处理容量上限时，会提示稍后重新复制。
- 外部拖放基于 Windows 通用格式；不同软件实际提供的格式不同，因此不是所有来源都能接收。
- B-005 图片分类反馈稍晚、B-006 微信复制图片偶发生成两份目前属于低优先级[现场观察](docs/observations.md)，自动测试环境未能稳定复现。

遇到问题可以提交 [Bug 报告](https://github.com/Oiawlm/floating-transfer-station/issues/new?template=bug_report.yml)，有新想法可以提交 [功能建议](https://github.com/Oiawlm/floating-transfer-station/issues/new?template=feature_request.yml)。

## 接下来准备做什么

分类管理、设置界面、外部拖入激活方式和快捷启动仍需逐项设计。完整说明见 [路线图](ROADMAP.md)。

## 本地开发

```powershell
& .\scripts\bootstrap-dotnet.ps1
& .\.tools\dotnet\dotnet.exe restore FloatingTransferStation.slnx
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
```

同步生成 Windows 安装包和两种 Mac 候选包：

```powershell
& .\scripts\build-release.ps1
```

默认构建允许 `CHANGELOG.md` 中保留未发布记录；正式发布使用 `build-release.ps1 -ForRelease`，检查步骤见[发布指南](docs/releasing.md)。开发运行不会登记开机自启，自启项由安装器统一管理。版本变化见[更新记录](CHANGELOG.md)。

Mac 上开发使用 `dotnet test FloatingTransferStation.Mac.slnx -c Release` 和 `dotnet run --project src/FloatingTransferStation.Mac`。Windows 上运行此项目仅预览 Mac 界面，数据保存在独立的 `%LocalAppData%/FloatingTransferStation.MacPreview/Data/`，自动采集关闭。

更完整的改动规则见 [贡献指南](CONTRIBUTING.md)，主要组件与数据流见 [架构说明](docs/architecture.md)，可复现的仓库检查命令见 [项目指南](PROJECT_GUIDE.md)。

## 参与贡献

欢迎提交 Bug、使用反馈和 PR。新增主要交互、数据格式、安装/卸载范围或拖放契约前，请先开 Issue 对齐问题和边界，避免大家在不同假设上重复工作。

## 许可证

本项目使用 [MIT License](LICENSE)。
