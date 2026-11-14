namespace FloatingTransferStation.Services;

/// <summary>
/// Core 服务状态条消息的语义种类（Issue #248 候选 C1）：Core 只递送结果语义，
/// 中文文案的唯一映射在窗口层（App 工程 StatusMessageTexts）。一个值对应一种
/// 用户可感知消息；同文案的不同触发点共用同值。新增值必须同步窗口层映射与
/// 锁定测试（枚举全覆盖断言会红）。
/// </summary>
public enum StatusMessageKind
{
    // —— BoardMutationService ——
    /// <summary>批量移动保存失败，已回滚到原位置。</summary>
    MoveSaveFailedRestored,
    /// <summary>批量置顶保存失败，已回滚。</summary>
    PinSaveFailedRestored,
    /// <summary>删除保存失败，已回滚（可撤销/永久两模式共用）。</summary>
    DeleteSaveFailedRestored,
    /// <summary>文字卡正文保存失败，已还原原文本（手工编辑/AI 单卡/批量替换共用）。</summary>
    ItemTextSaveFailedRestored,
    /// <summary>备注保存失败，已还原原备注。</summary>
    ItemNoteSaveFailedRestored,
    /// <summary>图片 AI 命名标题保存失败，已还原原标题。</summary>
    ImageTitleSaveFailedRestored,
    /// <summary>AI 整理替换前复检发现正文已变化，本次替换放弃。</summary>
    AiReplaceAbandonedTextChanged,
    /// <summary>批量替换门内复检：条目不存在或已非文字卡，整批放弃。</summary>
    BatchReplaceAbandonedCardMissing,
    /// <summary>批量替换门内复检：正文与期望原文不符，整批放弃。</summary>
    BatchReplaceAbandonedTextChanged,
    /// <summary>子分组指派保存失败，已恢复原分组。</summary>
    SubgroupAssignSaveFailedRestored,
    /// <summary>子分组分区保存失败，已恢复原顺序。</summary>
    SubgroupPartitionSaveFailedRestored,
    /// <summary>撤销时撤销栈为空。</summary>
    UndoEmpty,
    /// <summary>删除批图片副本清理部分失败（永久删除或驱逐时）。</summary>
    ImageCleanupPartiallyFailed,
    /// <summary>清空保存失败，已回滚（清空分类/跨分类清空非置顶共用）。</summary>
    ClearSaveFailedRestored,
    /// <summary>撤销删除批成功。</summary>
    UndoDeleteRestored,
    /// <summary>撤销删除批保存失败，内容保持删除状态。</summary>
    UndoDeleteSaveFailedKeptDeleted,
    /// <summary>撤销 AI 文本批保存失败，卡片保持整理后的内容。</summary>
    AiTextUndoSaveFailedKeptReplaced,
    /// <summary>撤销 AI 单卡文本批成功。</summary>
    AiTextUndoRestored,
    /// <summary>撤销多卡批时没有可恢复的原文。</summary>
    MultiTextUndoNothingToRestore,
    /// <summary>撤销多卡批成功（RestoredCount 携带恢复张数）。</summary>
    MultiTextUndoRestored,
    /// <summary>撤销多卡批保存失败，卡片保持动作后的内容。</summary>
    MultiTextUndoSaveFailedKeptReplaced,
    /// <summary>撤销子分组指派批保存失败，卡片保持指派后的分组。</summary>
    SubgroupAssignUndoSaveFailedKept,
    /// <summary>撤销子分组指派批成功。</summary>
    SubgroupAssignUndoRestored,
    /// <summary>撤销分区批保存失败，列表保持分组整理后的顺序。</summary>
    SubgroupPartitionUndoSaveFailedKept,
    /// <summary>撤销分区批成功。</summary>
    SubgroupPartitionUndoRestored,

    // —— ClipboardCaptureService ——
    /// <summary>待处理剪贴板队列容量超限，本次未收集。</summary>
    ClipboardCapacityExceeded,
    /// <summary>剪贴板读取重试全部失败。</summary>
    ClipboardReadFailed,
    /// <summary>采集被去重门抑制（文本/单图共用）。</summary>
    DuplicateSuppressed,
    /// <summary>文本采集保存失败，请重新复制。</summary>
    ClipboardTextSaveFailed,
    /// <summary>图片采集失败，请重新复制（剪贴板图片与文件图片共用）。</summary>
    ClipboardImageSaveFailed,

    // —— ExternalDropImportService ——
    /// <summary>拖入文本被启用的整理插件清空，本次未保存。</summary>
    DropTextClearedByPlugin,
    /// <summary>拖入内容保存失败（图片副本已清理），请重试。</summary>
    DropSaveFailedRetry,
    /// <summary>拖入内容保存失败且部分图片副本暂时无法删除。</summary>
    DropSaveFailedCleanupIncomplete
}

/// <summary>
/// 三服务（板变更/剪贴板采集/外部拖入）经回调递送的状态条结果消息：语义种类＋
/// 必要上下文载荷（当前仅 <see cref="MultiTextUndoRestored"/> 的恢复张数）。
/// 取代经 Core 直递中文字符串的旧通道——文案逐字不变，只换递送载体。
/// </summary>
public readonly record struct StatusMessage(StatusMessageKind Kind, int RestoredCount = 0);
