using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 子分组槽序消费面锁定（契约 #57 修订，照 <see cref="MainWindowInteractionTests.CategoryOrder"/>
/// 族镜像）：chip 轨按每分类槽序渲染、切分类换序；宿主采纳即重排并落 settings.json；
/// 乐观提交契约（同步前缀/失败双回滚/旧失败不踩新提交）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void Subgroup_ChipRailFollowsCustomSubgroupOrderPerCategory()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var settings = WindowSettings.Default
            .WithSubgroupName(BoardCategory.Inbox, 1, "急件")
            .WithSubgroupOrder(BoardCategory.Inbox, [3, 2, 1, 0])
            .WithSubgroupOrder(BoardCategory.Reference, [1, 0, 2, 3]);
        var window = CreateSubgroupWindow(directory, board, SubgroupDisplayMode.ChipRail, settings);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            CollectionAssert.AreEqual(
                new[] { 3, 2, 1, 0 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "chip 轨必须按该分类的自定义槽序渲染。");
            Assert.AreEqual(
                "急件",
                viewModel.SubgroupChips.Single(chip => chip.Slot == 1).DisplayName,
                "显示名随槽走（按槽寻位，不按轨位）。");

            // 切分类换序：Reference 的清单独立生效；切回 Inbox 恢复原序。
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            CollectionAssert.AreEqual(
                new[] { 1, 0, 2, 3 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "切分类必须换用该分类的槽序。");
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            CollectionAssert.AreEqual(
                new[] { 3, 2, 1, 0 },
                viewModel.SubgroupChips.Select(chip => chip.Slot).ToArray(),
                "切回原分类必须恢复该分类槽序。");
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
