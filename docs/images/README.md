# README 演示资产与再生步骤

本目录存放 README 引用的演示资产（真实应用画面，非生成图）。2026-11-04 起整批资产在**新录制环境**重制/新增：所有 GIF 与面板类静态图改为从**源码构建的预览实例**（含标签通用化改名、删除钮右键永久删除、左半送达、双层日期箭头、Markdown 阅读视图、W6 视觉修复后的真实画面）录制；标注图解（`*-anatomy`/`card-gesture-zones`/`card-delete-buttons`/`update-flow-diagram`）为真实截图＋脚本绘制标注。UI 变化后按本文步骤重录对应资产，不要用截图工具随手补图。

## 资产清单（README / usage.md 引用位）

| 文件 | 尺寸 | 体积 | 内容 | 引用位置 |
|---|---|---|---|---|
| `hero-loop.gif` | 720×364 | 84 KB | 网页三击选段 → `Ctrl+C` 入库 → 悬停展开 → 拖卡片回网页投放框（角标：三击/Ctrl+C/悬停/拖动） | 首屏 hero |
| `panel-anatomy.png` | 1100×1090 | 93 KB | 展开态面板结构标注（头部按钮组/四标签/靶心默认接收标记/卡片右上按钮/状态条） | README 与 usage「面板结构」 |
| `card-gesture-zones.png` | 940×430 | 26 KB | 卡片左右两半手势分区图解（左半编辑/送达/拖出、右半复制、右键全卡面选中、边缘环带归左半） | README 速查引言＋usage「手势分区」 |
| `card-deliver-input.gif` | 720×338 | 28 KB | 左半单击＝全文送达外部输入框（浏览器框内可见粘贴结果；角标：悬停/单击左半） | README 速查＋usage「复制出去」 |
| `card-delete-permanent.gif` | 540×1040 | 27 KB | 删除钮右键＝永久删除（角标：悬停/右键） | README 图集＋usage「删除与清空」 |
| `card-delete-buttons.png` | 860×470 | 32 KB | 删除钮双语义图解（左键可撤销/右键永久，右上按钮网格 4× 放大） | README 速查链接＋usage「删除与清空」 |
| `drag-reorder.gif` | 540×700 | 28 KB | 分类内拖动重排：换位显示插入指示线、同位松手无指示线（角标：悬停/拖动/同位） | README 速查＋usage「再拖出去」 |
| `multi-select-batch-pin.gif` | 540×1040 | 45 KB | 右键多选三卡 → 顶部图钉批量置顶 → 整批拖到「待分类」（角标：右键/左键/拖动） | README 图集＋usage「选中与多选」 |
| `batch-copy-images.gif` | 720×322 | 136 KB | 资源管理器 `Ctrl+A` 全选＋`Ctrl+C`，面板按来源顺序出卡（角标：Ctrl+A/Ctrl+C/悬停） | README 图集＋usage「批量复制」 |
| `drag-into-category.gif` | 720×286 | 99 KB | 资源管理器拖图到「重要」标签落入（角标：拖动/悬停） | README 图集＋usage「指定位置放入」 |
| `edge-collapse-hold.gif` | 190×1040 | 30 KB | 移开收起 → 移入展开 → 保持展开开/关（原速基准；角标：悬停/移开/左键） | README 一览＋usage「不挡工作区」 |
| `review-tab.gif` | 540×880 | 30 KB | 复盘输入自动保存 → `‹` 切前一天 → `›` 切回内容仍在（角标：悬停/输入/单击） | README 图集＋usage「复盘标签」 |
| `review-date-jump.gif` | 540×760 | 26 KB | `«»` 双层箭头跳有记录日（11-04→11-03→11-01 跳过空白日→回 11-03；角标：单击） | README 图集＋usage「复盘标签」 |
| `review-markdown-toggle.gif` | 540×880 | 22 KB | 「阅读/编辑」切换 Markdown 渲染视图（角标：悬停/单击） | README 图集＋usage「复盘标签」 |
| `update-flow-diagram.png` | 1020×560 | 38 KB | 自动更新流程静帧图解（常态帧＋点亮帧为真实画面裁剪） | README 图集＋usage「更新」 |
| `theme-light-dark.png` | 880×848 | 96 KB | 同一展开态面板浅色/深色双联（新标签名；悬停态按钮可见） | README 一览＋usage「主题」 |
| `quicklaunch-anatomy.png` | 1100×1090 | 78 KB | 启动页布局图解（工具行/发现预留位/瓦片/名称带/勾选钮/排序） | README 图集＋usage「快捷启动」 |
| `settings-category-order-dark.png` | 660×400 | 17 KB | 设置「标签顺序」节深色主题（新默认名四行） | README 图集＋usage「设置」 |
| `pinned-divider-solid/dashed/accent-bar/none.png` | 418×640 | 各 ~32 KB | 置顶分隔四样式（内容区裁剪，未受改名影响） | usage「置顶与分隔」 |
| `settings-window.png` / `-dark` / `-resized` | 1020×900 等 | 51/51/36 KB | 设置窗口双栏布局（外观页；不受改名影响） | README 图集＋usage「设置」 |
| `settings-pinned-divider-section.png` | 680×600 | 30 KB | 设置 → 外观「置顶分隔」节 | usage「置顶与分隔」 |
| `plugins-section.png` / `settings-plugins-dark.png` | — | 94/43 KB | 插件区块（默认关闭） | README 图集＋usage「插件」 |
| `settings-ai-*`（test-success/test-failure/dark） | 680×600 | 41/39/39 KB | AI 设置节三态 | usage「AI 设置」 |
| `settings-ai-prompt-light/dark.png` | 680×600 | 53/63 KB | 整理提示词卡 | usage「AI 设置」 |
| `card-ai-organize-light/dark/before/after.png` | 418×640 | 35/35/33/46 KB | AI 整理入口与前后对比（卡片区未受改名影响） | usage「AI 整理」 |
| `quicklaunch-surface-light/dark.png` | 627×960 | 30/31 KB | 启动瓦片表面 | README 图集＋usage |
| `quicklaunch-selection-dark.png` | 552×960 | 29 KB | 启动多选＋「删除已选 N 项」 | 同上 |
| `quicklaunch-contextmenu-light/dark.png` | 120×127 | 2 KB | 瓦片右键菜单 | 同上 |
| `quicklaunch-url-dialog-light/dark.png` | 540×267 | 13/12 KB | 网页地址对话框 | 同上 |
| `quicklaunch-favicon-light/dark.png` | 418×640 | 13/13 KB | favicon 两态 | usage |
| `quicklaunch-add-menu.png` | 103×66 | 2 KB | ＋添加菜单 | 图集链接 |
| `quicklaunch-system-locations-light/dark.png` + 菜单双联 | — | 16-17/3 KB | 系统位置瓦片与菜单 | 图集/PR 引用 |
| `launcher-discovery-button-light/dark.png` | 627×960 | 26/26 KB | 「发现 N」工具行入口 | README 图集 |
| `launcher-discovery-picker-light/dark.png` | 570×720 | 56/57 KB | 发现待确认清单 | 同上 |
| `launcher-discovery-settings-light/dark.png` | 1020×900 / 680×600 | 88/51 KB | 设置发现节 | 同上 |
| `update-button-available.png` / `update-button-default.png` | 418×640 | 21/12 KB | 更新按钮点亮/常态（`update-flow-diagram` 的裁剪源） | usage「更新」 |
| `settings-auto-cleanup-retention-tiers/disabled.png` | 1020×900 | 90/88 KB | 自动清理保留时长两态 | 本表登记（PR 引用） |
| `wechat-qr.png` | 1568×1568 | 12 KB | 开发者微信二维码（品牌深紫 #463A9E、EC-H；规划窗口重绘资产，非应用画面；内容与 `ProductIdentity.WeChatId` 一致） | README「联系与反馈」 |
| `card-gesture-swap-evidence/`（4 PNG） | — | 共 72 KB | 2026-10-23 手势交换窗口证据 | 本表登记（PR 引用） |

总量约 2.5 MB（预算目标 ≤11 MB、硬上限 12 MB，大幅富余）。引用宽度一律 ≤ 源宽；GIF 帧率 12 fps。

## 录制环境（2026-11-04 起）

- Windows 11，单屏 **1920×1080 物理、系统缩放 125%**（1 DIP＝1.25 物理像素）；所有录制/裁剪坐标用**物理像素**、每次动作前实测（本机与旧 2560×1600@150% 环境不通用）。
- 应用画面来自**源码构建的预览实例**：worktree（main 或含待发布改动的分支）`dotnet build src/FloatingTransferStation -c Release`，产物拷到 `D:\fts-demo\app\Release\`。本机无全局 .NET 10，启动脚本必须设 `DOTNET_ROOT`/`DOTNET_ROOT_X64` 指向仓库 `.tools\dotnet`（否则 apphost 弹「You must install .NET」）。
- 浅色为主（`FTS_PREVIEW_THEME=light`），深色帧用 `-Theme dark` 重启同种子实例。
- 录制前：`recording-env.ps1 -Mode on`（隐藏桌面图标＋关闭通知横幅，录完 `-Mode off` 还原）；优雅关闭其他预览实例；告知用户键鼠避让、剪贴板会被覆盖。

## 隔离启动与种子（D:\fts-demo\）

预览模式用环境变量隔离（`src/FloatingTransferStation/App.xaml.cs` 的 `FTS_PREVIEW_DATA_DIR`/`FTS_PREVIEW_THEME`；预览互斥体按数据目录哈希独立，不影响已装实例）：

```powershell
. D:\fts-demo\launch-preview.ps1            # 默认 light；-Theme dark 切深色
. D:\fts-demo\reset-working.ps1             # robocopy /MIR seed -> data（并删 discoveries）
```

种子 `D:\fts-demo\seed\`（2026-11-04 更新）：

- `board.json`：「重要」5 卡（含置顶文字/图、长文截断卡）＋「待分类」3 卡；`images\img-01..04.png` 低饱和渐变（`make-assets.ps1` 生成）。
- `launchers.json`：**信封格式 `{ "version": 1, "items": [...] }`**（裸数组会被判损坏自动隔离）——记事本/计算器/画图/资源管理器/示例网站/回收站/此电脑 7 瓦片。
- `settings.json`：`panelWidth 360 / windowHeight 800 / top 20 / reviewMigrationVersion 1 / categoryNameMigrationVersion 2`。
- `preferences.json`：`launcherDiscoveryEnabled=false`（**隐私：防止本机真实应用名进画面**）、`autoUpdateCheckEnabled=false`（防录屏中真实更新重启）、`reviewMarkdownViewEnabled=true`（阅读视图演示）、`quickLaunchMigrationVersion=1`。
- `reviews\`：10-31、11-01、11-03、11-04 四天有内容、**11-02 留空**（双层箭头跳空白日的演示素材）。
- 资源管理器源图 `D:\fts-demo\assets\demo-01..05.png`；浏览器侧 `hero-source.html`（三击选段＋投放框）与 `drop-target.html`，用 Edge `--app=file:///... --window-size --window-position` 打开（无标签栏、本地文件、零隐私面）。

每条资产开拍前 `reset-working.ps1` 后重启预览实例；重录同一段文案间隔 >5 秒（产品 5 秒去重规则）。

## 录制与编码（两段式＋角标）

两段式：gdigrab 25fps FFV1 中间文件（仓库外 `D:\fts-demo\raw\`）→ 离线调色板 GIF；表演写成单脚本一次跑完。**输入注入用 `D:\fts-demo\input.ps1`**：本机过滤 SendInput 合成鼠标**移动**（绝对/相对均被吞，按钮与键盘正常），指针移动全部走 `SetCursorPos` 缓动插值（10ms/步、smoothstep；同样产生真实 WM_MOUSEMOVE 流，悬停/拖拽/送达全部可用）。

```powershell
# 1) 录制（X/Y/W/H 物理像素；FFV1 中间文件）
ffmpeg -y -f gdigrab -framerate 25 -draw_mouse 1 -offset_x X -offset_y Y -video_size WxH `
  -i desktop -c:v ffv1 -pix_fmt bgr0 -t T D:\fts-demo\raw\NN.mkv
# 2) 两遍调色板（trim 剪头尾；fps 12；scale 只缩不放；drawtext 角标在调色板前叠加）
ffmpeg -y -i raw\NN.mkv -vf "trim=…,setpts=PTS-STARTPTS,fps=12[,scale=W:-2:flags=lanczos],drawtext=…,palettegen=…" palette.png
ffmpeg -y -i raw\NN.mkv -i palette.png -filter_complex "[0:v]<同链>[x];[x][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle" -gifflags +transdiff -loop 0 out.gif
# 3) 断言（宽高/帧率/帧数/体积）
ffprobe -v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,nb_frames out.gif
```

**操作指示角标（统一样式，2026-11-04 起）**：每条 GIF 在左下角以强调色 `#6D5DFB` 圆角色块＋白字标注当前手势（`左键/右键/双击/单击/按住左键拖动/单击左半/悬停标签/移开鼠标/输入文字/Ctrl+A 全选/Ctrl+C 复制/三击选中段落/同位松手：无指示线`），`drawtext` 用 `textfile=`（UTF-8 文案文件 `D:\fts-demo\badges\*.txt`，规避命令行中文转义）＋`fontfile='C\:/Windows/Fonts/msyh.ttc'`＋`enable='between(t,S,E)'` 按表演时间轴分段；调色板生成与应用走同一滤镜链（角标参与量化）。标注图解用 `D:\fts-demo\annotate.ps1 -Spec specs\*.json`（UTF-8 JSON spec：canvas/paste/label/line/box/dot；真实截图打底、强调色芯片＋引线）。

静态图单帧：`shot.ps1`（带光标）/`shot-nc.ps1`（无光标，主题对比与图解底图用）。

## 逐资产要点（2026-11-04 批次）

| 键 | 表演要点 |
|---|---|
| hero-loop | 三击选段（三次 Click 拼双击窗）→ `Ctrl+C` 等 1.3s → 悬停展开 1.2s → 新卡左半拖到网页投放框 |
| card-deliver-input | 先点浏览器输入框取前台 → 悬停展开 → 单击卡左半 → 等 1.9s（双击时限+粘贴+恢复剪贴板） |
| card-delete-permanent | 悬停展开 → 移到卡右上删除钮（悬停 0.65s 出钮）→ 右键 → 卡即逝 |
| drag-reorder | 拖 img-02 到列表尾（换位：指示线可见）→ 再按住另一卡回到原位松手（同位：全程无指示线） |
| multi-select-batch-pin | 右键三卡（选中）→ 点头部图钉（有选中时出现，实测 ≈(1656,42)）→ 拖选中卡到「待分类」标签（整批移动＋自动切面） |
| review-tab / review-date-jump / review-markdown-toggle | 悬停「记录」行展开 → 工具栏实测 `«(1405,75) ‹(1444,75) ›(1691,75) »(1712,75) 阅读(1765,75) 编辑(1796,75)`；输入用 `TypeText`（KEYEVENTF_UNICODE 绕输入法，无候选窗入画） |
| edge-collapse-hold | 收起→展开→点保持展开（头部 ≈(1610,45)）→移开不收→再点恢复；**原速基准：禁 setpts 提速** |
| drag-into-category / batch-copy-images | 资源管理器开 `D:\fts-demo\assets`（仅演示文件）；拖文件用 DragMouse；批量用 Click＋`Ctrl+A`＋`Ctrl+C`（键盘选择比 Ctrl+Click 注入可靠） |

## 已知坑（2026-11-04 批次新增）

1. **本机过滤 SendInput 合成鼠标移动**（绝对/相对均静默无效、返回成功）：指针一律 `SetCursorPos` 插值；按钮/键盘仍走 SendInput。
2. **PowerShell 变量大小写不敏感**：函数参数 `$X` 与循环局部 `$x` 互相覆盖会把目标坐标改写成起点（MoveMouse 假成功的根因）；参数与局部变量必须异名（`$ToX/$curX`）。
3. 预览实例主窗口是 no-activate 型：`Process.MainWindowHandle` 恒零，需 `EnumWindows` 按进程枚举取**最宽**可见窗口；收起态每行标签是独立 hwnd。
4. PS 5.1 脚本内禁中文字面量（无 BOM 按 ANSI）：角标/标注中文一律走 UTF-8 `textfile`/JSON spec；嵌套结构体数组要引号 `'Namespace+Type[]'`。
5. `New-Object RectangleF(a, b, c+5, d+2)` 的算术参数会被拆成多个位置参数——每个表达式显式加括号。
6. gdigrab 光标恒画普通箭头；主题对比与图解底图用 `-draw_mouse 0`，悬停按钮态靠真实悬停（光标隐藏但按钮可见）。
7. GIF 隐私抽查：入库前逐资产抽帧拼图（`fps=3, tile=5x4` contact sheet）核用户名/本机路径/通知/任务栏/输入法候选窗；演示路径固定 `D:\fts-demo\`（中性约定）。
8. GitHub 相对路径大小写敏感：文件名全小写 kebab-case，引用与磁盘一致。

## 预算口径

总量目标 ≤11 MB、硬上限 12 MB（当前约 2.5 MB）。单项：hero ≤2.5 MB、面板 GIF ≤1.5 MB、静态标注图 ≤200 KB、双联 ≤300 KB。降级顺序（原速基准资产除外）：缩时长 → 缩宽度 → 降帧率（≥10fps）→ 改静态图 → 删除（仅限低优先级资产）。

视觉规划的通用方法论以公开社区惯例为准；项目资产构成与再生方式以本台账为准。
