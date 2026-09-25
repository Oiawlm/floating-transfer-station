# README 演示资产与再生步骤

本目录存放 README 引用的演示资产（真实应用画面，非生成图）。素材取自 **1.12.0 安装版**（Windows 11，浅色主题）。UI 变化后按本文步骤重录对应资产即可，不要用截图工具随手补图。

## 资产清单

| 文件 | 尺寸 | 体积 | 内容 | README 位置 |
|---|---|---|---|---|
| `hero-loop.gif` | 730×630 | 185 KB | 复制文字 → 移入展开 → 卡片入库 → 拖回浏览器，完整闭环 | 一句话简介后 |
| `panel-anatomy.png` | 900×1290 | 58 KB | 展开态面板结构标注（六条） | 「它能做什么」引言段后 |
| `drag-into-category.gif` | 700×626 | 228 KB | 资源管理器拖图到「图片」分类标签落入 | 成组区「指定位置放入」 |
| `multi-select-batch-pin.gif` | 600×1054 | 503 KB | Ctrl 多选三卡 → 批量置顶 → 拖到「文本2」 | 成组区「整理内容」 |
| `edge-collapse-hold.gif` | 600×1054 | 288 KB | 移开收起 → 移入展开 → 保持展开开/关，原速 11.0s | 成组区「不挡工作区」 |
| `review-tab.gif` | 600×1054 | 156 KB | 复盘标签输入 → 切前一天 → 切回，内容仍在 | 复盘段落之后 |
| `settings-window.png` | 570×1166 | 90 KB | 设置窗口一屏（中性数据目录） | 成组区「设置」 |
| `plugins-section.png` | 570×265 | 27 KB | 插件区块（「整理空白」默认关闭） | 成组区「插件」 |

总量约 1.5 MB。引用宽度一律 ≤ 源宽；GIF 帧率 10–12 fps（hero 12）。

## 录制环境

- Windows 11，主屏 **2560×1600 物理、系统缩放 150%**；所有录制/裁剪坐标用**物理像素**。
- 统一**浅色主题**；演示数据用仓库外中性目录（如 `D:\fts-demo\`），绝不动真实用户数据目录。
- 录制前：优雅关闭真实实例（向其进程发 `WM_CLOSE`，等退出确认）；关闭全局通知横幅（注册表 `NOC_GLOBAL_SETTING_TOASTS_ENABLED=0`，录完删除）；隐藏桌面图标（`Advanced\HideIcons=1`，录完归 0 并刷新）；告知用户键鼠避让、剪贴板会被覆盖。

## 隔离启动与种子

预览模式用环境变量隔离（见 `src/FloatingTransferStation/App.xaml.cs` 的 `FTS_PREVIEW_DATA_DIR` / `FTS_PREVIEW_THEME`）：

```powershell
$env:FTS_PREVIEW_DATA_DIR = 'D:\fts-demo\data'
$env:FTS_PREVIEW_THEME   = 'light'
& "$env:LocalAppData\Programs\悬浮中转站\悬浮中转站.exe"
```

种子目录 `D:\fts-demo\seed\`：`board.json`（三个分类各 3–6 张中性卡片，部分置顶；短示例文字 + `seed-images\a1..a4.png` 低饱和渐变占位图）、`reviews\`（预置 ≥3 天 Markdown）、`settings.json`（分类名用 README 默认名「图片/复盘/文本2/待分类」，固定窗口位置）。资源管理器侧源图 `D:\fts-demo\assets\demo-01..05.png`（480×360 渐变 PNG，System.Drawing `LinearGradientBrush` 生成，无文字无个人信息）。演示拖放目标用本地页 `D:\fts-demo\drop-target.html`（浏览器接受文字拖放；Win11 记事本拒收 OLE 文字拖放）。

**每条资产开拍前一键重置**：`D:\fts-demo\reset-working.ps1`（robocopy `/MIR` 从 seed 镜像到 data）后重启预览实例。重录同一段文案间隔 >5 秒（产品有 5 秒去重规则）。

种子 settings 下展开态面板物理矩形 **[1843,135,717,1260]**；收起条为四行分类标签（行高 315 物理 px）：图片 y135 / 复盘 y450 / 文本2 y765 / 待分类 y1080。**每次动作前用工具实测窗口 bounds，不盲信常数**（收起条位置随默认分类行变化）。

## 录制与编码

两段式：先录无损中间文件，再离线转 GIF（时序敏感序列的表演写成**单脚本**一次进程跑完，逐命令 spawn 每次约漂移 1.3s）。

```powershell
# 1) 录制（X/Y/W/H 物理像素；FFV1 中间文件写仓库外，录完即删）
ffmpeg -y -f gdigrab -framerate 25 -draw_mouse 1 -offset_x X -offset_y Y -video_size WxH `
  -i desktop -c:v ffv1 -pix_fmt bgr0 -t T D:\fts-demo\raw-NN.mkv

# 2) 两遍调色板（S/E 为保留起止秒；fps 按资产 10–12；scale 只缩不放）
ffmpeg -y -i raw-NN.mkv -vf "trim=start=S:end=E,setpts=PTS-STARTPTS,fps=12,scale=600:-2:flags=lanczos,palettegen=max_colors=256:stats_mode=diff:reserve_transparent=0" -frames:v 1 -update 1 palette.png
ffmpeg -y -i raw-NN.mkv -i palette.png -filter_complex "[0:v]trim=start=S:end=E,setpts=PTS-STARTPTS,fps=12,scale=600:-2:flags=lanczos[x];[x][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle" -gifflags +transdiff -loop 0 out.gif

# 3) 断言（宽高/帧率/帧数；节奏类资产加时长偏差 <5%）
ffprobe -v error -select_streams v:0 -count_frames -show_entries stream=width,height,avg_frame_rate,nb_read_frames raw-NN.mkv
```

静态图（02/07/08）用 gdigrab 单帧（`-frames:v 1`）。02 的标注用 System.Drawing 合成：900×1290 画布，panel 截图贴在 x183/y15，左侧留白画彩色标签与引线（浅色底 `RGB(247,248,250)`）；标注文字放 UTF-8 附属文件读入——**PowerShell 5.1 脚本内不写中文字面量**（无 BOM 会被当 ANSI）。

## 逐资产配方

| # | 录制区域（物理 px） | 表演要点 |
|---|---|---|
| 01 | 面板+左侧浏览器窗口的横幅区（约 1310×1130） | 浏览器复制文字 → 指针移入右侧标签展开（复制本身不触发展开，必须拍到「移入→展开」）→ 新卡片已在默认分类 → 拖卡片回浏览器落下；输出缩 730 宽 |
| 02 | 面板整矩形 [1843,135,717,1260] 单帧 | 展开态（待分类）；标注六条：分类标签、复盘标签、卡片图钉、顶部图钉与垃圾桶、保持展开按钮、设置齿轮 |
| 03 | 面板矩形 | 资源管理器一张图拖到「图片」标签悬停（真实反馈形态以实测为准）→ 松手落入 |
| 04 | 面板矩形 | `Ctrl+单击` 多选 3 张（面板内 Ctrl 注入可靠）→ 顶部图钉批量置顶 → 拖一张到「文本2」 |
| 05 | 面板矩形 | 移开 → 收起 → 移入 → 展开 → 点保持展开 → 移开仍保持 → 再点恢复收起；**原速基准资产：禁 `setpts`/缩时长，ffprobe 断言输出时长 ≈ 录制时长（<5%）**，输出 12fps |
| 06 | 面板矩形 | 复盘标签内 IME 旁路逐字输入（KEYEVENTF_UNICODE，勿用剪贴板粘贴）→ 切前一天 → 切回，内容仍在 |
| 07 | 设置窗口矩形单帧 | 齿轮打开设置；数据目录必须显示 `D:\fts-demo\data` 中性路径 |
| 08 | 设置窗口插件区块单帧 | 「整理空白」默认关闭如实呈现 |

「面板内选择」与拖拽用 SendInput 表演脚本（move/click/drag，ease 曲线插值、`-DwellMs 420` 悬停）；打字用 KEYEVENTF_UNICODE 旁路输入法。已验证的边界：对资源管理器的 Shift 注入无效、Ctrl 多选跨次残留（先点空白清选中）、合成「多选+复制」不可靠（batch-copy 类资产须真人演示）。

## 已知坑

1. gdigrab 软件光标恒画普通箭头——光标形态不是拖拽证据，拖拽幽灵才是。
2. SendInput 的 INPUT 结构体在 x64 必须补齐填充（`KEYBDINPUT` 的 padA/padB）；PowerShell 方法名大小写不敏感，避免 `KeyUp`/`Up` 撞名。
3. 向真实窗口发按键前先确认前台正确；焦点在编辑器期间面板不自动收起。
4. 资源管理器窗口会被系统/用户挪动——每条资产开拍前重新定位并实测文件项坐标。
5. GitHub 相对路径大小写敏感：文件名全小写 kebab-case，引用与磁盘一致。
6. 入库前逐帧隐私抽查：用户名与本机绝对路径、通知横幅、任务栏、浏览器标签标题、输入法候选窗；发现问题重拍，不修补画面。

## 预算口径

单项：01 ≤2.5MB、02/07/08 ≤200KB、03/04/06 ≤1.5MB、05 ≤2.0MB、10 ≤300KB、11 ≤1.2MB；总量目标 ≤11MB、硬上限 12MB。降级顺序（05 除外）：缩时长 → 缩宽度 → 降帧率（≥10fps）→ 改静态图 → 删除（仅限低优先级资产）。

规划推导与选型调研见 [research/2026-09-25-readme-visual-plan.md](../research/2026-09-25-readme-visual-plan.md) 等三份材料；跨窗口交接见 [handoff/](../handoff/)。
