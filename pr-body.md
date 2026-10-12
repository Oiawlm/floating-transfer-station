## 内容

按 Issue #103 落地卡片操作按钮区两列网格化（目标版本 1.32.0 的先行功能分支）：

- **操作区整件重构为固定两列网格**：四列横排（备注|置顶|选择|删除，备注列 Auto 塌缩）改为右上角锚定的两列 × 2 行网格——列与按钮行绝对 30 DIP（复用 `CardOperationColumnWidth` 单一来源），行间 4 DIP 独立间隔行，行优先序 **选择(0,0)|删除(0,1)|置顶(1,0)|备注(1,1)**；此后新增按钮按行优先向下占格。格子固定：按钮隐藏（备注钮七条件门槛、三信号矩阵显隐）时格子留空、其余按钮零漂移（绝对行列下 `Collapsed` 天然留空）；备注列 Auto 塌缩机制随之删除，所有按钮语义统一为「固定格子+显隐」。
- **避让内缩 124→64**（`CardTextReservedRightInset` = 两列占地 + 4 DIP 正文-网格水平缓冲，唯一派生来源不变）；`MinPanelWidth=310` 产品下限不变，推导注释按网格几何改述（右半区容纳 2×30）。
- **T7 双 lane 评审采纳**（wbx job `20261012-102427-jki`）：两 lane 收敛 P1——「30 行装不下 30 钮 + 4 边距」的 Margin 方案过约束、行间隙不可靠，改为独立间隔行根治（同为绝对值、格子算术不变）；另采纳行高复用资源、注释 +4 语义校正；Margin 覆盖/徽章重叠两条经核实不存在。
- **可见性语义零改动**：悬停/选中/置顶三信号矩阵（PinButtonStyle/DeleteButtonStyle/SelectionButtonStyle 触发器）与备注钮七条件门槛（NoteButtonStyle MultiDataTrigger + `FadeAnimation.ManageVisibility`）原样保留；左右对半分区（#14）、手势路由（CardGestureZones 常量与 CommandParameter 意图）、TrimAwareTextBlock 零改动。
- **契约修订随实现同一 PR**：#16（四列→两列网格、内缩 64、测量口径网格最大态）、#19（空间列序→行优先网格序、塌缩语义→格子固定预留/可见性塌缩）、#22（入口位置→网格右下格 (1,1)）；`docs/design.md` 数值段同步（94/280 滞后一并修正）；README 两处按钮位置描述；CHANGELOG 未发布条目。
- 布局副作用如实说明：按钮网格参与卡片期望高度，矮卡（单行文字）会生长到可容纳 64 DIP 网格——这是「置顶钮常显可点」（#19）在任意卡高下的必然要求；图片卡备注格 (1,1) 恒空。

## 失败先行与验证

- **失败先行实证**（79f1d2f）：`CardTextReservedInset_DerivesFromMeasuredOperationStrip`（60≠120）、`CardOperationStrip_HiddenNoteButtonKeepsGridCells`（60≠90）、`TextCard_ReservesFixedTwoColumnOperationGrid`（2 列≠4 列）三测全红，实现后转绿；行为语义测试（三信号矩阵、Win32 真实输入、备注七条件门槛）零放宽。
- 全量 Release 测试 **0 失败**（871 通过 / 2 跳过证据门控，2m28s，本机安静时段实跑；新增证据测试计入后 874 总计 0 失败一轮）。附注：本机为多窗口共居环境，个别全量轮次中「指针在外收起」族测试（PanelHold/DailyReview）会被并行窗口的前台/光标活动干扰而偶发失败——这些测试隔离运行稳定全绿（3 轮 2/2、3/3 实录），以 CI quality 门（安静 runner）为权威裁决。
- `dotnet format --verify-no-changes` 退出码 0；`build -c Release -warnaserror` 退出码 0（合并 1.30.0/1.31.0 后的 970ca08 复验）。
- 合并前已同步最新 origin/main（970ca08：CHANGELOG 未发布区保留双方条目，契约 #16/#19/#22 版本标注顺延 1.32.0）。

## UI 证据

真实渲染截图（真实光标悬停 + RenderTargetBitmap 实拍窗口壳层，新增 `Evidence_CaptureBtnGridHoverLayouts` 采集，复用既有 `FTS_NOTE_EVIDENCE_DIR` 门控、CI 零改动）：`btngrid-tab-image-hover.png`（备注格恒空）、`btngrid-tab-text-hover.png`、`btngrid-tab-inbox-hover.png`——两列网格 [选择|删除 / 置顶|备注] 随悬停淡入，行优先序与格子固定语义与规格一致。证据文件含合成种子数据、无任何用户内容，按仓库规约不入库，随最终交付报告留档于本地 `docs/handoff/GOAL-V1-BTNGRID-EVIDENCE/`（PR 描述附件受 GitHub API 上传限制，遵循 #101 先例如实留档）。

Closes #103
