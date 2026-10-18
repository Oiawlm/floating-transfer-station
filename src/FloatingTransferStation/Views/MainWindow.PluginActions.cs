using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 插件 L1 文本动作（1.51.0，契约「插件动作协议」）的窗口编排：卡片右键菜单
/// 「插件动作」分组（唯一入口）→ 按选集组成判定可见与可用（single=选集恰为一张
/// 文字卡；multi=文字卡 ≥2，图片卡不阻断）→ 后台执行动作管线（UI 不冻结，
/// 运行中禁再入）→ 经 <see cref="BoardMutationService.ReplaceItemsTextUndoableAsync"/>
/// 统一提交（全有或全无，Ctrl+Z 逐卡恢复）。宿主统一提交、插件只提议：本类不
/// 直接写板。右键命中卡不在选集内时先单选该卡（资源管理器惯例）；复制手势路径
/// 的选择语义不变（仍只复制不改选择）。
/// </summary>
public partial class MainWindow
{
    private bool _isPluginActionRunning;

    /// <summary>右键卡片：构建并打开「插件动作」菜单；无可执行项不弹菜单。</summary>
    private void TryOpenPluginActionsMenu(BoardItem clickedItem)
    {
        if (_isClosing ||
            _isPluginActionRunning ||
            _pluginCatalog is not { } catalog ||
            _viewModel.IsSearchActive)
        {
            return;
        }

        // 命中卡不在选集 → 先单选它；已在选集 → 保持当前多选。
        if (!BoardList.SelectedItems.Contains(clickedItem))
        {
            BoardList.SelectedItems.Clear();
            BoardList.SelectedItems.Add(clickedItem);
        }

        var available = BuildAvailablePluginActions();
        if (available.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu
        {
            Style = TryFindResource("LauncherTileContextMenuStyle") as Style
        };
        var caption = new MenuItem
        {
            Header = "插件动作",
            IsEnabled = false,
            Style = TryFindResource("LauncherContextMenuItemStyle") as Style
        };
        menu.Items.Add(caption);
        foreach (var handle in available)
        {
            var item = new MenuItem
            {
                Header = handle.Action.Title,
                ToolTip = $"{handle.PluginName} · {handle.Entry.Manifest?.Description ?? handle.Action.Id}",
                Style = TryFindResource("LauncherContextMenuItemStyle") as Style,
                Tag = handle
            };
            item.Click += PluginActionMenuItem_Click;
            menu.Items.Add(item);
        }

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 按当前选集组成解析可执行动作：single=选集恰为一张文字卡（含图片的选集不满足）；
    /// multi=文字卡 ≥2（图片卡忽略）。
    /// </summary>
    private List<PluginActionHandle> BuildAvailablePluginActions()
    {
        var selected = OrderedSelectedBoardItems();
        var textCount = selected.Count(item => item.Kind == BoardItemKind.Text);
        var result = new List<PluginActionHandle>();
        foreach (var handle in _pluginCatalog!.EnabledActions)
        {
            var enabled = handle.Action.Selection switch
            {
                PluginActionSelection.Single => textCount == 1 && selected.Length == 1,
                PluginActionSelection.Multi => textCount >= 2,
                _ => false
            };
            if (enabled)
            {
                result.Add(handle);
            }
        }

        return result;
    }

    private async void PluginActionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: PluginActionHandle handle } ||
            _isPluginActionRunning ||
            _isClosing)
        {
            return;
        }

        // 菜单打开期间选集可能已变（点击目标卡已先单选/保持），执行前按菜单可见性
        // 同一判据重取快照；多选动作的输入恒为板序文字卡快照。
        var textCards = OrderedSelectedBoardItems()
            .Where(item => item.Kind == BoardItemKind.Text)
            .ToArray();
        var single = handle.Action.Selection == PluginActionSelection.Single;
        if (single ? textCards.Length != 1 : textCards.Length < 2)
        {
            ShowStatus("选集已变化，插件动作未执行。");
            return;
        }

        var inputs = textCards
            .Select(item => new PluginActionCardInput(item.Id, item.Text ?? string.Empty))
            .ToArray();
        var expectedTexts = textCards.ToDictionary(item => item.Id, item => item.Text ?? string.Empty);

        _isPluginActionRunning = true;
        ShowStatus($"正在执行插件动作「{handle.Action.Title}」…");
        try
        {
            // 正则执行是 CPU 密集（预算 ≤2s），放后台跑避免冻结 UI；结果回到
            // UI 线程后经批量替换管线统一提交（快照构建状态所属线程契约）。
            var result = await Task.Run(() => handle.Pipeline.Apply(inputs));
            if (!result.IsSuccess)
            {
                ShowStatus($"插件动作「{handle.Action.Title}」失败：{result.FailureReason}");
                return;
            }

            var replacements = result.Cards!.Select(card => new BoardMutationService.BoardTextReplacement(
                card.ItemId,
                card.NewText,
                expectedTexts[card.ItemId])).ToArray();
            if (await _mutations.ReplaceItemsTextUndoableAsync(replacements))
            {
                ShowStatus(result.SkippedEmptyCount > 0
                    ? $"插件动作「{handle.Action.Title}」已替换 {result.Cards!.Count} 张卡片（{result.SkippedEmptyCount} 张结果为空已跳过），Ctrl + Z 可恢复。"
                    : $"插件动作「{handle.Action.Title}」已替换 {result.Cards!.Count} 张卡片，Ctrl + Z 可恢复。");
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException)
        {
            // 退出排空：取消即静默结束（状态由 finally 清理）。
        }
        catch (Exception)
        {
            // 纯崩溃护栏：与 AI 整理同族——管线对预算/超时已映射为可读失败，这里
            // 只防未知异常击穿为进程崩溃。
            ShowStatus($"插件动作「{handle.Action.Title}」执行失败，未做任何修改。");
        }
        finally
        {
            _isPluginActionRunning = false;
        }
    }
}
