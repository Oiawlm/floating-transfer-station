# 悬浮中转站

一个贴在 Windows 屏幕边缘的文字与图片中转站。复制、拖进来、分个类，再把内容拖到真正需要它的软件里。

> 当前支持 Windows 10/11 64 位。项目刚刚公开，真实演示视频会在录制完成后补到这里。

## 下载与安装

前往 [Releases](https://github.com/Oiawlm/floating-transfer-station/releases) 下载 `FloatingTransferStation-Setup-1.2.0.exe`。

1. 运行安装程序。
2. 选择程序安装位置和内容存储父目录；不修改时使用当前用户的本地目录。
3. 安装完成后软件会启动，并在以后登录 Windows 时自动运行。

1.2.0 已通过自动质量门和安装包构建验证，安装包现已开放下载。本地构建产物使用中文名，GitHub Release 为了稳定下载链接使用上面的英文文件名。Release 页面中的 `.zip` / `.tar.gz` 是 GitHub 自动生成的源码包，不是 Windows 安装程序。

## 它能做什么

- **随手收集**：复制图片或文字后，内容自动进入当前默认分类。
- **指定位置放入**：可以从资源管理器、浏览器、微信等软件把常见静态图片或非空文字直接拖到某个分类。
- **整理内容**：支持四个可改名分类、置顶、批量置顶、直接多选、批量移动、批量删除和分类内排序。
- **再拖出去**：图片和文字使用 Windows 通用拖放格式，可拖到支持这些格式的软件；纯图片多选可以按原顺序一起拖出。
- **不挡工作区**：窗口贴在屏幕右侧并保持置顶，空闲时收成一条分类标签，移入后再展开。
- **本地保存**：内容、顺序、置顶状态、分类名称和窗口位置保存在本机。

## 几个常用操作

- 单击右侧分类标签：切换本次运行的默认接收分类。
- 双击分类标签：原地改名，最多 6 个可见文字单元。
- `Ctrl + 单击` 或卡片选择框：多选内容。
- `Ctrl + A`：选择当前分类全部内容；正在编辑分类名称时仍然只会全选文字。
- `Esc`：取消当前分类的全部选择；正在编辑分类名称时仍然取消本次改名。
- 点击卡片图钉：置顶或取消置顶。
- 多选后点击顶部图钉或按 `Ctrl + P`：批量置顶或取消置顶；只要所选内容中有未置顶项就会统一置顶，全都已置顶时则统一取消置顶。
- 拖动卡片：分类内排序、移动到其他分类，或拖到外部软件。
- 顶部垃圾桶：有选择时删除选中项，没有选择时清空当前分类。
- `Backspace`：只删除选中项；正在编辑文字时仍然正常删字。

## 数据和卸载

默认程序目录是 `%LocalAppData%\Programs\悬浮中转站\`，默认数据目录是 `%LocalAppData%\悬浮中转站\Data\`。安装或更新时可以改选两者的位置。

卸载会删除程序、自启项，以及应用登记并管理的 `悬浮中转站\Data`；不会删除你选择的父目录里的其他文件。重要内容仍建议另外备份。

## 当前限制

- 动态分类增删、设置界面、快捷启动和常驻模式仍在路线图中，不属于 1.2.0 承诺。
- 外部拖放基于 Windows 通用格式；不同软件实际提供的格式不同，因此不是所有来源都能接收。
- B-005 图片分类反馈稍晚、B-006 微信复制图片偶发生成两份目前属于低优先级现场观察，自动测试环境未能稳定复现。

遇到问题可以提交 [Bug 报告](https://github.com/Oiawlm/floating-transfer-station/issues/new?template=bug_report.yml)，有新想法可以提交 [功能建议](https://github.com/Oiawlm/floating-transfer-station/issues/new?template=feature_request.yml)。

## 接下来准备做什么

分类管理、设置界面、外部拖入激活方式和快捷启动仍需逐项设计。完整说明见 [路线图](ROADMAP.md)。

## 本地开发

```powershell
& .\scripts\bootstrap-dotnet.ps1
& .\.tools\dotnet\dotnet.exe restore FloatingTransferStation.slnx
& .\.tools\dotnet\dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore
```

生成安装包：

```powershell
& .\scripts\build-release.ps1
```

更完整的改动规则见 [贡献指南](CONTRIBUTING.md)，主要组件与数据流见 [架构说明](docs/architecture.md)，可复现的仓库检查命令见 [项目指南](PROJECT_GUIDE.md)。

## 参与贡献

欢迎提交 Bug、使用反馈和 PR。新增主要交互、数据格式、安装/卸载范围或拖放契约前，请先开 Issue 对齐问题和边界，避免大家在不同假设上重复工作。

## 许可证

本项目使用 [MIT License](LICENSE)。
