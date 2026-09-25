# Windows 11 无人值守演示 GIF 录制管线选型（2026-09-25）

> 交接材料之一，由 README 视觉化规划窗口产出。正文由 WBX 外部算力（DeepSeek V4.1 Flash，无联网，仅凭已有知识）生成，主窗口对时效性结论做了抽样核实；标注「需核实 / UNKNOWN」处以执行窗口现场验证为准。配套材料：[README 视觉资产规划](2026-09-25-readme-visual-plan.md)、[GIF 录制管线选型](2026-09-25-gif-recording-pipeline.md)、[README 媒体惯例](2026-09-25-readme-media-conventions.md)、[执行提示词](../handoff/2026-09-25-readme-visuals-execution-prompt.md)。

# Windows 11 无人值守「GUI 演示 GIF」录制工具链选型报告

## 0. 结论摘要

- 主链路推荐：**ffmpeg（gdigrab 区域抓取）→ FFV1 无损中间文件 → 两遍 palettegen/paletteuse（diff_mode=rectangle）→ GIF**，体积超标时用 **gifsicle -O3 --lossy** 二次压缩。
- GPU 合成/DDA 环境下 gdigrab 出现黑帧或撕裂时，切换 **ffmpeg ddagrab**（同一套下游滤镜，命令只换输入段）。
- 画质优先（每字节质量最高）的备选是 **gifski**（但它不做屏幕抓取，必须由 ffmpeg 出 PNG 帧序列喂给它，且许可证是 AGPL 系，需注意合规）。
- **ScreenToGif / ShareX 不适合本场景作为核心环节**：它们是交互式桌面工具，CLI 面窄且非稳定契约，无法保证「无人工交互 + 可重复」。可作为人工排查/对照工具，不作为产线。
- 最大工程风险不在编码，而在**无人值守的会话/焦点/DPI/遮挡/启停同步**五件事（见第 4 节）。

---

## 1. 候选工具对比表

| 工具（抓取方式） | CLI 可自动化程度 | 区域裁剪能力 | 帧率控制 | GIF 优化质量 | 许可证 | 维护状态 | 本场景结论 |
|---|---|---|---|---|---|---|---|
| ffmpeg · gdigrab（`-f gdigrab -i desktop` 或 `-i title=...`） | 极高：纯 CLI，参数全可脚本化，退出码可判定 | 原生 `-offset_x/-offset_y/-video_size`；另可 `-show_region 1` 可视化调试 | `-framerate` 抓取帧率 + 滤镜 `fps=` 精确重采样 | 需自行接 palettegen/paletteuse，可做到很好 | FFmpeg 主体 LGPL-2.1+，含 libx264 的 GPL 构建为 GPL-2.0+（具体构建需核实） | 活跃（可能过时，需核实） | **主方案**。区域小、CPU 可控、无 GUI 依赖 |
| ffmpeg · dshow（`-f dshow -i video="screen-capture-recorder"`） | 中高：ffmpeg 参数同样脚本化，但依赖第三方 DirectShow 源滤镜 | 由滤镜暴露（如 `-start_x/-start_y/-video_size`，随滤镜实现而异，UNKNOWN 需核实） | 同 ffmpeg | 同 ffmpeg | ffmpeg 部分同上；滤镜自身许可 UNKNOWN | 滤镜项目多为个人维护，活跃度 UNKNOWN（可能过时，需核实） | **不推荐**：多一层 32/64 位匹配和对齐坑（ffmpeg 64 位必须配 64 位滤镜） |
| ffmpeg · ddagrab（Desktop Duplication，滤镜源） | 极高：`-filter_complex "ddagrab=..."` 全脚本化 | 规范做法 `hwdownload,format=bgra,crop=w:h:x:y`；是否支持 `video_size/offset_x/offset_y` 取决于版本（UNKNOWN，需核实：`ffmpeg -h filter=ddagrab`） | `framerate=` 选项 | 同 ffmpeg 链条 | 同 ffmpeg | 较新特性（FFmpeg 5.1/6.0 附近引入，可能过时，需核实） | **GPU/游戏化渲染窗口的备选**；RDP/虚拟 GPU/无 DDA 环境可能失败（`allow_fallback` 需核实） |
| ffmpeg · palettegen/paletteuse（编码内核） | 极高 | 承接上游 crop | 承接上游 fps | GIF 质量天花板（全局调色板 + dither + 帧间透明差分） | 同 ffmpeg | 活跃 | **必用**，与抓取方式解耦 |
| ScreenToGif | 低—中：存在命令行参数，但面向「打开项目/启动录制器」等交互场景，参数集非稳定契约，具体旗标 UNKNOWN（需核实） | GUI 强（编辑器可逐帧框选裁剪） | GUI 内可设；CLI 侧 UNKNOWN | 好（自带优化/删帧编辑器） | 可能为 Ms-PL（可能过时，需核实） | 仍维护但节奏与版本 UNKNOWN（需核实） | **不适合无人值守产线**；可作人工对照 |
| ShareX | 低—中：有命令行参数（如静默/自动开始录制类旗标，具体 UNKNOWN 需核实），录制后端即 ffmpeg | GUI 强（区域/窗口/自定义区域） | GUI 内可设，CLI 侧 UNKNOWN | 一般依赖 ffmpeg 或自带转换，GIF 直出能力 UNKNOWN | 可能为 GPL-3.0（需核实） | 较活跃 | **不推荐**：录制→GIF 的无人值守闭环不可靠 |
| gifski（CLI） | 高：单命令、纯参数化（喂 PNG 帧序列或视频，视频输入是否内置取决于构建/版本，UNKNOWN 需核实） | 不做抓取，裁剪由 ffmpeg 完成 | `--fps` | **极好**：时间抖动+跨帧调色板，同体积下观感最佳 | 可能为 AGPL-3.0（libimagequant 为 GPL-3.0/商业双许可），若对外分发需核实 | 活跃（需核实） | **画质优先备选**，注意许可 |
| gifsicle | 高：单命令 | 不做抓取 | 有限（可 `--delay`、`--change-speed` 类操作，细节需核实） | 作为**后处理压缩器**很强：`-O3 --lossy=N --colors N` | 可能为 GPL-2.0（需核实） | 低频维护、稳定 | **体积兜底必装** |
| ImageMagick（`magick -delay ... -layers Optimize`） | 高 | 不做抓取 | `-delay` | 一般偏差（GIF 编码器优化弱，不调参容易体积爆炸） | ImageMagick License（Apache-2.0 兼容，需核实） | 活跃 | 不推荐作主链，仅作图像处理备用 |
| OBS Studio（+ obs-websocket） | 中高：有 `--startrecording`/`--minimize-to-tray` 类启动参数（具体需核实），停止与状态查询靠 obs-websocket | 强（窗口捕获 + 裁剪滤镜，且**不受遮挡影响**） | 强 | 无 GIF 直出（录 mp4 后仍靠 ffmpeg/gifski） | 可能为 GPL-2.0（需核实） | 活跃 | 重型备选：窗口捕获抗遮挡是唯一亮点，但依赖过重 |
| LICEcap / GifCam / Captura | 基本无（GUI 导向；Captura 命令行支持 UNKNOWN） | GUI | GUI | 弱—中 | LICEcap 可能 GPL-2.0；Captura 许可 UNKNOWN | LICEcap 停滞；Captura 已归档停止维护（可能过时，需核实） | **淘汰** |
| 自研 Windows.Graphics.Capture（C#/WinRT 小工具） | 极高（自己写契约） | 天然**按窗口**抓取，不需要区域坐标 | 自己控时钟 | 仍需交给 ffmpeg/gifski 编码 | 自研 | 取决于自研 | 高成本兜底：需要「抗遮挡 + 按窗口」且不想上 OBS 时选用 |

补充说明（关键差异）：

- **gdigrab** 从屏幕 DC 做 BitBlt，**受遮挡影响**：若有窗口盖住面板，录到的是盖在上面的窗口；`-i title=<窗口标题>` 只是用窗口矩形做裁剪框，不保证只抓到该窗口内容（需核实版本行为）。优点是零依赖、CPU 可预期、区域抓取成本低。
- **ddagrab** 走 Desktop Duplication，对 GPU 合成内容更稳，且光标是「画」在帧上的清晰光标；但依赖显卡/会话，且 DDA 在部分 RDP 或虚拟显示环境不可用。
- **dshow** 路径里的 `screen-capture-recorder` 类滤镜不是 ffmpeg 的一部分，多一个安装与位宽匹配失败点，本场景无收益。
- **ScreenToGif/ShareX** 的价值在人工剪辑界面，不在无人值守。

---

## 2. 推荐端到端管线

### 2.1 阶段总览

    [0] 环境自检 → [1] 定位并冻结面板物理矩形 → [2] 无损录制中间文件
    → [3] 裁剪/删除废帧/调速 → [4] 两遍调色板生成 GIF
    → [5] 体积兜底压缩 → [6] 断言校验（帧数/尺寸/体积）

设计原则：**「抓取」只做一次且无损**，所有裁剪、提速、降帧、调色板都放在后续滤镜链里，这样调整构图与体积无需重录（重录是最贵的步骤）。

### 2.2 阶段 0：环境自检（决定走 gdigrab 还是 ddagrab）

    ffmpeg -hide_banner -version
    ffmpeg -hide_banner -devices
    ffmpeg -hide_banner -h filter=ddagrab
    ffmpeg -hide_banner -h filter=paletteuse
    ffmpeg -hide_banner -h filter=crop

若 `-h filter=ddagrab` 报未知滤镜，则本机只走 gdigrab 路线。

### 2.3 阶段 1：把面板钉在确定的物理像素矩形上

用 PowerShell P/Invoke（`SetWindowPos` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`）拿到**物理像素**矩形，并固定位置（例：`x=1560, y=140`）。理由与坑见第 4.3 节。

### 2.4 阶段 2：无损录制中间文件

主方案（gdigrab 直接区域抓取，最省 CPU/磁盘）：

    ffmpeg -hide_banner -y -f gdigrab -framerate 15 -draw_mouse 0 ^
      -offset_x 1560 -offset_y 140 -video_size 400x800 ^
      -thread_queue_size 1024 -i desktop -an ^
      -c:v ffv1 -level 3 -g 1 -pix_fmt bgr0 -t 20 ^
      -f matroska raw_rec.mkv

要点：
- `-t 20` 给一个**确定时长的上限**（无人值守时比「等信号再停」更可重复），真正时长靠阶段 3 裁剪。
- `-pix_fmt bgr0` 保住屏幕原色不经过有损色度抽样；若本机构建的 ffv1 拒绝 `bgr0`（需核实），退化为 `-pix_fmt gbrp`，或换 `-c:v libx264rgb -qp 0 -preset ultrafast -pix_fmt bgr24`（后者需 x264 版 GPL 构建）。
- `-draw_mouse 0` 是否要光标见 4.4。
- 双保险：`-show_region 1` 只在调试时加，会在屏幕上画红框，正式录制务必去掉。

备选方案（ddagrab，GPU 渲染窗口/黑帧时切换）：

    ffmpeg -hide_banner -y -filter_complex ^
      "ddagrab=output_idx=0:framerate=15:draw_mouse=0,hwdownload,format=bgra,crop=400:800:1560:140" ^
      -c:v ffv1 -level 3 -pix_fmt bgr0 -t 20 -f matroska raw_rec.mkv

若该版本 ddagrab 支持 `video_size/offset_x/offset_y`（UNKNOWN，需核实），可直接把区域写进 ddagrab 选项、省掉 crop。（`crop=400:800:1560:140` 的 x/y 是相对被捕获帧左上角的坐标。）

### 2.5 阶段 3：裁剪 / 删帧 / 提速

参数顺序建议：`crop → trim → setpts → fps → scale`。

- 裁掉面板边框/阴影：`crop=384:784:8:8`（在阶段 2 已按区域抓取时可省略）
- 删掉手势前后的死时间：`trim=start=0.4:end=6.9,setpts=PTS-STARTPTS`
- 整体提速 1.5×（观感更紧凑、顺带降帧数）：`setpts=PTS/1.5`
- 统一到目标帧率：`fps=10`
- 缩放到目标宽度（保持 1:2 比例，用 `-2` 保证偶数）：`scale=550:-2:flags=lanczos`

### 2.6 阶段 4：两遍调色板 → GIF（推荐做法）

第一遍（生成全局调色板，只吃**最终构图**的像素）：

    ffmpeg -hide_banner -y -i raw_rec.mkv ^
      -vf "crop=400:800:0:0,trim=start=0.4:end=6.9,setpts=PTS-STARTPTS,fps=10,scale=550:-2:flags=lanczos,palettegen=max_colors=256:stats_mode=diff:reserve_transparent=0" ^
      -frames:v 1 -update 1 palette.png

第二遍（用该调色板量化并编码 GIF）：

    ffmpeg -hide_banner -y -i raw_rec.mkv -i palette.png -filter_complex ^
      "[0:v]crop=400:800:0:0,trim=start=0.4:end=6.9,setpts=PTS-STARTPTS,fps=10,scale=550:-2:flags=lanczos[x];[x][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle" ^
      -gifflags +transdiff -loop 0 -fps_mode passthrough out.gif

要点：
- `stats_mode=diff` 要配 `diff_mode=rectangle`（只对变化区域重绘，未变区域走透明复用，屏幕录制场景常能省 30–60% 体积）。
- `-gifflags +transdiff` 保留帧间透明差分；若发现播放器兼容性异常再考虑去掉（会显著变大）。
- `-fps_mode passthrough`（旧写法 `-vsync 0`，已过时需核实）避免编码器插入重复帧。
- `-loop 0` 无限循环，README 演示的标准做法。
- `-update 1` 是较新版本单图输出的正确写法，老版本可能只需 `-frames:v 1`（需核实）。
- 单命令便捷版（便于快速迭代预览，质量略低）：

      ffmpeg -hide_banner -y -i raw_rec.mkv -filter_complex "crop=400:800:0:0,fps=10,scale=550:-2:flags=lanczos,split[a][b];[a]palettegen=max_colors=256:stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a:diff_mode=rectangle" -gifflags +transdiff -loop 0 -fps_mode passthrough preview.gif

### 2.7 阶段 5（可选）：gifski 画质路径

    ffmpeg -hide_banner -y -i raw_rec.mkv ^
      -vf "crop=400:800:0:0,trim=start=0.4:end=6.9,setpts=PTS-STARTPTS,fps=10,scale=550:-2:flags=lanczos,format=rgb24" ^
      -compression_level 1 frames\f_%05d.png

    gifski --fps 10 --quality 90 --motion-quality 80 --lossy-quality 80 -o out_gifski.gif frames\*.png

注意：`gifski` 的精确旗标集合与默认循环行为 UNKNOWN（需核实 `gifski --help`）；PNG 帧序列在 `--width` 上已在 ffmpeg 侧完成，不必重复缩放。用此路径前先确认 AGPL 许可是否可接受（见 4.7）。

### 2.8 阶段 6：体积兜底与验收断言

    gifsicle -O3 --lossy=40 --colors 200 -o out.opt.gif out.gif

    ffprobe -v error -select_streams v:0 -count_frames -show_entries stream=width,height,avg_frame_rate,nb_read_frames -of default=nw=1 raw_rec.mkv
    ffprobe -v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,nb_frames -of default=nw=1 out.opt.gif
    (Get-Item .\out.opt.gif).Length

断言建议：`nb_read_frames` 与「录制时长 × 抓取帧率」偏差 > 5% 即判定掉帧并重录；GIF 宽高与体积必须落在 500–800px、2–8MB 区间，否则回到阶段 3 调参重出（**不要重录**）。

---

## 3. 体积控制策略与质量权衡

### 3.1 体积模型（先算再做）

    GIF 体积 ≈ 宽 × 高 × 输出帧率 × 时长 × bpp

`bpp`（每像素-帧平均字节数）经验区间（**经验估算，需实测校准**）：

| 内容/设置 | bpp 参考值 |
|---|---|
| 扁平 UI、小范围变化、diff_mode=rectangle、bayer/none | 0.02 – 0.06 |
| 中等动态（拖放、面板动效）、sierra2_4a | 0.06 – 0.12 |
| 大面积运动/渐变/阴影、floyd_steinberg | 0.12 – 0.25 |

按 550×1100 @10fps、5 秒（50 帧 = 3025 万像素-帧）换算：0.05 bpp ≈ 1.5MB，0.12 bpp ≈ 3.6MB，0.25 bpp ≈ 7.6MB。**结论：本场景 2–8MB 预算在 550 宽 / 10–12fps / 4–6 秒 这一档基本可稳定命中。**

**必须提前正视的构型矛盾**：面板是 400×800（1:2 竖长条），若严格执行「宽 500–800px」，输出高度将是 **1000–1600px**，单帧像素 50–128 万，这是预算压力的唯一主因。三个可行化解法：
1. 宽度取区间下限（500–560）而非上限；
2. 只录面板内的**有效交互区**（例如 400×500 的上半部），把内容聚焦而非全面板；
3. 把一个长流程**拆成多个短视频 GIF**（每个手势一个，各 3–5 秒），README 里并排展示——这也比单个 15 秒大 GIF 更利于阅读。

### 3.2 旋钮对照表

| 旋钮 | 收紧体积的方向 | 质量代价 |
|---|---|---|
| 输出帧率 `fps=` | 15 → 12 → 10（再低开始有「卡顿感」） | 拖放类动作流畅度下降 |
| 时长/调速 | `trim` 砍死时间；`setpts=PTS/1.5` 提速 | 过快会让观众看不清悬停/菜单 |
| 尺寸 `scale=` | 550 → 500（对 1:2 面板等于高度 1000） | 文字发虚，低于 1:1 更明显 |
| 颜色数 `max_colors=` | 256 → 128 → 64 | 渐变/图标出现色带 |
| dither | `floyd_steinberg` → `sierra2_4a` → `bayer:bayer_scale=3~4` → `none` | 有序抖动是规则网纹、`none` 会硬色带；但**体积下降明显** |
| 帧间差分 | `diff_mode=rectangle` + `stats_mode=diff` + `-gifflags +transdiff` | 几乎无视觉代价，**优先动这个** |
| 后处理 | `gifsicle -O3 --lossy=20~60`、`--colors 200` | lossy 会轻微破坏高频细节（细字/细线） |

### 3.3 三套可直接落地的预设

- 紧凑（目标 ≤2.5MB）：

      -vf "...,fps=10,scale=520:-2:flags=lanczos,palettegen=max_colors=128:stats_mode=diff:reserve_transparent=0"
      paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle
      gifsicle -O3 --lossy=60 --colors 160

- 均衡（目标 3–5MB，推荐默认）：

      -vf "...,fps=12,scale=550:-2:flags=lanczos,palettegen=max_colors=256:stats_mode=diff:reserve_transparent=0"
      paletteuse=dither=sierra2_4a:diff_mode=rectangle
      gifsicle -O3 --lossy=25

- 高质量（目标 ≤8MB，用于关键交互）：

      -vf "...,fps=15,scale=700:-2:flags=lanczos,palettegen=max_colors=256:stats_mode=diff:reserve_transparent=0"
      paletteuse=dither=floyd_steinberg:diff_mode=rectangle
      或改走 gifski --quality 90（不上 gifsicle --lossy）

### 3.4 清晰度相关的额外权衡

- **分辨率策略优于放大**：若面板逻辑尺寸 400×800，把系统缩放设为 150%/200% 让物理像素变成 600×1200 / 800×1600，再 `scale` 到 550–700 宽，是「原生高分辨率素材 + 下采样」，文字最锐利；直接把 400 宽放大到 550 只会变糊。
- **缩放用 `flags=lanczos`**，避免 `bilinear` 在细字上产生糊边。
- **抖动选择看内容**：纯色面板 + 图标 + 文字 → `bayer` 甚至 `none` 观感已可接受且体积最小；面板里有头像/缩略图/渐变 → 必须 `sierra2_4a` 或 `floyd_steinberg`。
- 避免在面板里使用大面积半透明、模糊、阴影动效（dither 在渐变上效率极低，是体积杀手）。
- GitHub 端的渲染/上传体积硬上限 UNKNOWN（需核实）；工程经验上 **<5MB 加载体验明显更好**，2–3MB 最稳，可同时附 mp4/webm 链接作为高清备选。

---

## 4. 全程无人值守的注意点

### 4.1 会话、电源与遮挡（最容易致命）

- 屏幕抓取**只能在已解锁的交互式会话**中工作：锁定屏幕、切换用户、Session 0 服务、无显示器/无活动桌面的 runner 上，gdigrab 会拿到黑帧或直接失败（ddagrab 在此类环境更脆弱）。
- 电源与屏保：`powercfg /change monitor-timeout-ac 0`、`powercfg /change standby-timeout-ac 0`，并关闭屏保；禁用锁屏策略的具体键值 UNKNOWN（需核实，可用域策略或 `PresentationSettings` 类手段）。
- 遮挡治理清单：最小化/移走其它窗口；临时 `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)` 置顶（录制后恢复原 Z 序）；关闭通知与专注助手弹窗（注册表/设置项 UNKNOWN，需核实）；任务栏设为自动隐藏或把面板放在任务栏之外的位置；关闭「贴靠布局」「窗口动画」「透明效果」以稳定画面与体积。
- 多显示器：gdigrab 的 `offset_x/offset_y` 在副屏/负坐标下的语义 UNKNOWN（需核实）。**最稳做法：把面板固定到主显示器，抓取窗口用主屏区域，必要时抓整屏 + `crop` 滤镜。**
- 若应用设置了 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`（防截屏），抓取会得到黑帧/空洞——这是无法绕过的，必须在应用侧关闭。
- 磁盘：无损中间文件体积可观（400×800 的 bgr0 原始帧约 1.28MB/帧），确认临时目录剩余空间与写盘带宽，避免丢帧。

### 4.2 窗口焦点与输入注入

- 面板必须处于**前台且可见**：`ShowWindow(SW_RESTORE)` → `SetWindowPos` 固定矩形 → `SetForegroundWindow`，然后**轮询 `GetForegroundWindow()` 确认成功**再开始执行手势；`SetForegroundWindow` 会因前台锁失败，需要 `AttachThreadInput` 技巧或 `AllowSetForegroundWindow`。
- 更稳的做法是**尽量不用坐标输入**：优先用 UI Automation 的 `InvokePattern`/`SetFocus`/`ValuePattern`；只有在「拖放、悬停」这类必须注入指针的场景才用 `SendInput`（鼠标拖放需要中间移动帧 + `Sleep(30~60ms)`，否则应用可能识别为点击而非拖拽）。
- `UIPI`：若目标应用以管理员运行，自动化进程必须同级，否则 `SendInput` 被静默丢弃；录制用的 ffmpeg 进程无需提权。
- 操作前把鼠标预移到面板内的中性位置，避免录到从屏幕别处「飞入」的光标轨迹（除非那正是你要展示的）。

### 4.3 DPI 与坐标系（裁剪错位的头号原因）

- 自动化进程必须声明 **Per-Monitor DPI Aware v2**（`SetProcessDpiAwarenessContext`）或等价清单；否则 `GetWindowRect` 返回被虚拟化缩放的逻辑坐标，录制区域会整体偏移。
- 用 `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS)` 取**真实物理边界**，而不是 `GetWindowRect`：在 125%/150% 缩放下两者常差 7–8 物理像素（不可见边框），直接导致录到阴影或多切 1 列像素。
- 建议在录制前把「面板物理矩形」写入一个中间 JSON/日志，抓取参数与自动化参数**来自同一个来源**，避免两处硬编码漂移。

### 4.4 光标是否入镜

- 记录型演示通常关光标：gdigrab `-draw_mouse 0`、ddagrab `draw_mouse=0`（两者的默认值不同，**务必显式写上**）。
- 若为展示「悬停」必须带光标：用 ddagrab 的光标（DDA 画的是清晰光标位图）；gdigrab 的光标在部分缩放下会偏位或模糊（需核实）。
- 折中方案：录制时关光标，后期用 `drawbox`/`overlay` 在目标控件位置合成高亮圈或点击指示标记——但注意这会引入每帧变化区域，使 `diff_mode=rectangle` 收益下降、体积上升。

### 4.5 开始/停止录制的同步方式（按可靠性排序）

1. **固定时长 + 固定时标（最可重复，推荐）**：ffmpeg 端 `-t <T>`；自动化脚本内部按「T 之内完成」的预算编排；录制开头加入约 0.5 秒的静置（用于后期 `trim` 校准），结尾同理。产物时长完全确定。
2. **进度握手**：ffmpeg 加 `-progress pipe:1`，父进程读到首个 `frame=` 行（且 `frame>=2`）后再触发表演，避免「ffmpeg 还没真正开始写文件就动作了」竞态。
3. **优雅停止**：优先让 ffmpeg 自然结束（到达 `-t` 或 `-frames:v`），这样 trailer 一定完整；尝试向 stdin 写 `q` 在 Windows 管道场景下的有效性 UNKNOWN（需核实 ffmpeg 的非控制台 stdin 键盘处理），因此**不要把优雅停止当成主机制**。
4. **绝不要用 `Stop-Process -Force`/`taskkill /F`**：会丢失 trailer，产出损坏或不可读的中间文件。
5. **视觉时标兜底**：在无损中间文件上用 `drawtext=timecode` 烧一条时间码（仅调试版），事后能精确定位手势起止帧，用于自动计算 `trim` 的 `start/end`。
6. 若改用 OBS 路线：`--startrecording` 启动、obs-websocket 查询 `Recording` 状态并 `StopRecord`，确认输出文件存在且非 0 字节后再进入后处理（具体参数名需核实）。

### 4.6 可重复性与自检

- 固定输入：分辨率、缩放比例、主题（浅/深）、壁纸、字体、语言、系统动画开关——CI 机器间差异会直接改变 GIF 体积与观感。
- 每次录制后必须跑第 2.8 节的 `ffprobe -count_frames` 断言；掉帧即重录（比事后修补便宜）。
- 把所有命令固化成一个脚本（如 `record.ps1 -OutDir ... -Region X,Y,W,H -TargetFps 15`），所有路径绝对化、时间戳命名中间产物，便于失败后取证。
- 依赖固定：用 winget/scoop 安装并**锁定版本**（`winget install --version`；scoop 的 manifest 版本号），避免上游滤镜/参数行为变化导致产物漂移。

### 4.7 许可与合规提示

- 对外分发工具或把规则产物打包进产品时，需确认：FFmpeg 构建是 LGPL 还是 GPL（含 x264 即 GPL），gifski 的 AGPL 系许可，gifsicle 的 GPL，ShareX 的 GPL，ScreenToGif 的 Ms-PL。本场景若只是**内部生成 GIF 资源并发布 GIF 本身**，通常不构成分发这些工具；一旦把工具链打包进产品/镜像分发，需逐个复核（以上均为「可能过时，需核实」）。

---

## 5. 风险与 UNKNOWN 清单

- UNKNOWN：`ddagrab` 是否支持 `video_size/offset_x/offset_y` 区域参数（用 `ffmpeg -h filter=ddagrab` 现场确认）；`allow_fallback` 选项是否存在。
- UNKNOWN：各工具当前最新版本号、ScreenToGif/ShareX 的完整命令行参数集与录制→GIF 无人值守可行性（需核实官方文档）。
- UNKNOWN：`gifski` 精确旗标、默认循环行为、以及是否能直接接受视频输入而非仅帧序列。
- UNKNOWN：gdigrab `offset_x/offset_y` 在多显示器/负坐标下的语义；`-i title=` 在窗口被遮挡时的实际行为。
- UNKNOWN：Windows 上通过管道向 ffmpeg stdin 发送 `q` 的可靠停止能力。
- UNKNOWN：GitHub README 图片的硬性体积上限与是否需要 <5MB 的工程经验阈值。
- 风险：DDA 在 RDP/虚拟 GPU 环境不可用；应用启用防截屏亲和性导致黑帧；125%/150% 缩放下的 7–8 物理像素边框偏差造成裁剪错位。

（以上涉及具体版本、许可条款、维护活跃度的结论均标注为「可能过时，需核实」，请在落地前用 `ffmpeg -h`、`gifski --help`、`gifsicle --help` 与各项目仓库现场复核。）
## 主窗口核实补充（2026-09-25）

- 本机已装 ffmpeg 且在 PATH 中（`ffmpeg -version` 可直接用；具体安装目录不入库，避免泄露本机环境指纹）；gifsicle 未装，需 `winget install gifsicle` 或省略（ffmpeg palettegen/paletteuse 已可满足预算，gifsicle 仅作体积兜底）。
- 本机屏幕：2560×1600 物理分辨率，系统缩放 150%（AppliedDPI=144）——§4.3 的 DPI/物理像素警告在本机直接适用。
- 录制驱动方式补充：执行窗口有 ZCode 官方 computer-use 技能（GUI 观察/截图/输入注入），「表演」环节由它完成，ffmpeg 只负责区域抓取，两者按 §4.5 的固定时长 + 进度握手同步。
- 产品自身有 `FTS_PREVIEW_DATA_DIR` / `FTS_PREVIEW_THEME` 环境变量（`src/FloatingTransferStation/App.xaml.cs`）：隔离数据目录启动 + 强制亮/暗主题，专门为本机预览与截图取证设计，种子演示数据应走这条路，绝不动真实用户数据。
