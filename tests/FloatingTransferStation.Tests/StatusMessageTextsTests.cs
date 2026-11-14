using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 状态条文案映射表锁定（Issue #248 候选 C1）：窗口层 StatusMessageTexts 是
/// 「枚举→中文文案」唯一映射，基线＝批次 2026-11-12-backlog-burndown 冻结文案表
/// （40 调用点/33 文案）。本套件锁三件事：①枚举全覆盖——任何新值不登记映射即红；
/// ②文案逐字——与冻结表基线逐字一致，防迁移/整理期漂移；③插值格式——
/// MultiTextUndoRestored 的张数插值形态锁定。
/// </summary>
[TestClass]
public sealed class StatusMessageTextsTests
{
    [TestMethod]
    public void EveryKindMapsToNonEmptyText()
    {
        foreach (var kind in Enum.GetValues<StatusMessageKind>())
        {
            var text = new StatusMessage(kind).ToDisplayText();
            Assert.IsFalse(string.IsNullOrWhiteSpace(text), $"枚举 {kind} 未登记文案映射。");
        }
    }

    [TestMethod]
    public void Texts_MatchFrozenBaseline_Verbatim()
    {
        // 冻结文案表逐字基线（docs/handoff/2026-11-12-backlog-burndown/evidence-w5）。
        var expected = new Dictionary<StatusMessageKind, string>
        {
            // —— BoardMutationService ——
            [StatusMessageKind.MoveSaveFailedRestored] = "移动未保存，内容已恢复到原位置。",
            [StatusMessageKind.PinSaveFailedRestored] = "置顶状态未保存，内容已恢复。",
            [StatusMessageKind.DeleteSaveFailedRestored] = "删除未保存，内容已恢复。",
            [StatusMessageKind.ItemTextSaveFailedRestored] = "内容未保存，已还原原文本。",
            [StatusMessageKind.ItemNoteSaveFailedRestored] = "备注未保存，已还原原备注。",
            [StatusMessageKind.ImageTitleSaveFailedRestored] = "图片命名未保存，已还原。",
            [StatusMessageKind.AiReplaceAbandonedTextChanged] = "卡片内容已变化，整理结果已放弃。",
            [StatusMessageKind.BatchReplaceAbandonedCardMissing] = "选中的卡片已变化，本次批量替换已放弃。",
            [StatusMessageKind.BatchReplaceAbandonedTextChanged] = "选中的卡片内容已变化，本次批量替换已放弃。",
            [StatusMessageKind.SubgroupAssignSaveFailedRestored] = "子分组未保存，已恢复原分组。",
            [StatusMessageKind.SubgroupPartitionSaveFailedRestored] = "分组整理未保存，已恢复原顺序。",
            [StatusMessageKind.UndoEmpty] = "没有可撤销的操作。",
            [StatusMessageKind.ImageCleanupPartiallyFailed] = "部分已删除的图片副本暂时无法清理。",
            [StatusMessageKind.ClearSaveFailedRestored] = "清空未保存，内容已恢复。",
            [StatusMessageKind.UndoDeleteRestored] = "已恢复最近删除的内容。",
            [StatusMessageKind.UndoDeleteSaveFailedKeptDeleted] = "撤销未保存，内容保持删除状态。",
            [StatusMessageKind.AiTextUndoSaveFailedKeptReplaced] = "撤销未保存，卡片保持整理后的内容。",
            [StatusMessageKind.AiTextUndoRestored] = "已恢复卡片原文。",
            [StatusMessageKind.MultiTextUndoNothingToRestore] = "卡片内容已变化，没有需要恢复的原文。",
            [StatusMessageKind.MultiTextUndoRestored] = "已恢复 3 张卡片的原文。",
            [StatusMessageKind.MultiTextUndoSaveFailedKeptReplaced] = "撤销未保存，卡片保持动作后的内容。",
            [StatusMessageKind.SubgroupAssignUndoSaveFailedKept] = "撤销未保存，卡片保持指派后的分组。",
            [StatusMessageKind.SubgroupAssignUndoRestored] = "已恢复卡片原分组。",
            [StatusMessageKind.SubgroupPartitionUndoSaveFailedKept] = "撤销未保存，列表保持分组整理后的顺序。",
            [StatusMessageKind.SubgroupPartitionUndoRestored] = "已恢复分组整理前的顺序。",
            // —— ClipboardCaptureService ——
            [StatusMessageKind.ClipboardCapacityExceeded] = "待处理剪贴板内容过多或过大，本次未收集，请稍后重新复制。",
            [StatusMessageKind.ClipboardReadFailed] = "本次剪贴板内容暂时无法读取，请重新复制。",
            [StatusMessageKind.DuplicateSuppressed] = "与最近收集的内容相同，未重复收集。",
            [StatusMessageKind.ClipboardTextSaveFailed] = "本次内容未保存，请重新复制。",
            [StatusMessageKind.ClipboardImageSaveFailed] = "本次图片未保存，请重新复制。",
            // —— ExternalDropImportService ——
            [StatusMessageKind.DropTextClearedByPlugin] = "拖入的文本被启用插件清空，本次未保存。",
            [StatusMessageKind.DropSaveFailedRetry] = "拖入内容未保存，请重试。",
            [StatusMessageKind.DropSaveFailedCleanupIncomplete] = "拖入内容未保存，且部分图片副本暂时无法删除。"
        };

        // 全覆盖对偶断言：映射表多出的值（expected 有、枚举无）与漏登的值
        // （枚举有、expected 无）都红——两侧集合必须恰好一致。
        CollectionAssert.AreEquivalent(
            Enum.GetValues<StatusMessageKind>().Cast<StatusMessageKind>().ToArray(),
            expected.Keys.ToArray(),
            "基线字典与枚举值集必须恰好一致（新增枚举值须同步本表与映射表）。");

        foreach (var (kind, text) in expected)
        {
            var actual = kind switch
            {
                StatusMessageKind.MultiTextUndoRestored =>
                    new StatusMessage(kind, RestoredCount: 3).ToDisplayText(),
                _ => new StatusMessage(kind).ToDisplayText()
            };
            Assert.AreEqual(text, actual, $"枚举 {kind} 文案与冻结表不一致。");
        }
    }

    [TestMethod]
    public void MultiTextUndoRestored_InterpolatesRestoredCount()
    {
        Assert.AreEqual(
            "已恢复 12 张卡片的原文。",
            new StatusMessage(StatusMessageKind.MultiTextUndoRestored, RestoredCount: 12).ToDisplayText());
    }
}
