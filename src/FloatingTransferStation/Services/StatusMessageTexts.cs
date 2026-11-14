namespace FloatingTransferStation.Services;

/// <summary>
/// 状态条结果消息（<see cref="StatusMessage"/>）→ 中文文案的唯一窗口层映射
/// （Issue #248 候选 C1：Core 三服务只递语义，文案单点在此，不散落服务层）。
/// 文案基线＝批次 2026-11-12-backlog-burndown 冻结文案表（40 调用点/33 文案），
/// 逐字不变；调整文案或本地化只改本表。锁定测试 StatusMessageTextsTests 断言
/// 枚举全覆盖与本表逐字一致——新增枚举值不登记映射即红。
/// </summary>
public static class StatusMessageTexts
{
    /// <summary>把结果消息映射为状态条中文文案（含 RestoredCount 插值的唯一处）。</summary>
    public static string ToDisplayText(this StatusMessage message) => message.Kind switch
    {
        // —— BoardMutationService ——
        StatusMessageKind.MoveSaveFailedRestored => "移动未保存，内容已恢复到原位置。",
        StatusMessageKind.PinSaveFailedRestored => "置顶状态未保存，内容已恢复。",
        StatusMessageKind.DeleteSaveFailedRestored => "删除未保存，内容已恢复。",
        StatusMessageKind.ItemTextSaveFailedRestored => "内容未保存，已还原原文本。",
        StatusMessageKind.ItemNoteSaveFailedRestored => "备注未保存，已还原原备注。",
        StatusMessageKind.ImageTitleSaveFailedRestored => "图片命名未保存，已还原。",
        StatusMessageKind.AiReplaceAbandonedTextChanged => "卡片内容已变化，整理结果已放弃。",
        StatusMessageKind.BatchReplaceAbandonedCardMissing => "选中的卡片已变化，本次批量替换已放弃。",
        StatusMessageKind.BatchReplaceAbandonedTextChanged => "选中的卡片内容已变化，本次批量替换已放弃。",
        StatusMessageKind.SubgroupAssignSaveFailedRestored => "子分组未保存，已恢复原分组。",
        StatusMessageKind.SubgroupPartitionSaveFailedRestored => "分组整理未保存，已恢复原顺序。",
        StatusMessageKind.UndoEmpty => "没有可撤销的操作。",
        StatusMessageKind.ImageCleanupPartiallyFailed => "部分已删除的图片副本暂时无法清理。",
        StatusMessageKind.ClearSaveFailedRestored => "清空未保存，内容已恢复。",
        StatusMessageKind.UndoDeleteRestored => "已恢复最近删除的内容。",
        StatusMessageKind.UndoDeleteSaveFailedKeptDeleted => "撤销未保存，内容保持删除状态。",
        StatusMessageKind.AiTextUndoSaveFailedKeptReplaced => "撤销未保存，卡片保持整理后的内容。",
        StatusMessageKind.AiTextUndoRestored => "已恢复卡片原文。",
        StatusMessageKind.MultiTextUndoNothingToRestore => "卡片内容已变化，没有需要恢复的原文。",
        StatusMessageKind.MultiTextUndoRestored => $"已恢复 {message.RestoredCount} 张卡片的原文。",
        StatusMessageKind.MultiTextUndoSaveFailedKeptReplaced => "撤销未保存，卡片保持动作后的内容。",
        StatusMessageKind.SubgroupAssignUndoSaveFailedKept => "撤销未保存，卡片保持指派后的分组。",
        StatusMessageKind.SubgroupAssignUndoRestored => "已恢复卡片原分组。",
        StatusMessageKind.SubgroupPartitionUndoSaveFailedKept => "撤销未保存，列表保持分组整理后的顺序。",
        StatusMessageKind.SubgroupPartitionUndoRestored => "已恢复分组整理前的顺序。",
        // —— ClipboardCaptureService ——
        StatusMessageKind.ClipboardCapacityExceeded => "待处理剪贴板内容过多或过大，本次未收集，请稍后重新复制。",
        StatusMessageKind.ClipboardReadFailed => "本次剪贴板内容暂时无法读取，请重新复制。",
        StatusMessageKind.DuplicateSuppressed => "与最近收集的内容相同，未重复收集。",
        StatusMessageKind.ClipboardTextSaveFailed => "本次内容未保存，请重新复制。",
        StatusMessageKind.ClipboardImageSaveFailed => "本次图片未保存，请重新复制。",
        // —— ExternalDropImportService ——
        StatusMessageKind.DropTextClearedByPlugin => "拖入的文本被启用插件清空，本次未保存。",
        StatusMessageKind.DropSaveFailedRetry => "拖入内容未保存，请重试。",
        StatusMessageKind.DropSaveFailedCleanupIncomplete => "拖入内容未保存，且部分图片副本暂时无法删除。",
        _ => throw new InvalidOperationException($"未知状态消息种类：{message.Kind}。")
    };
}
