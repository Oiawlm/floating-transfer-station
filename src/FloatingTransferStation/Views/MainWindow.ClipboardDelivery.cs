using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片复制到剪贴板（交付第二通道，解决游戏聊天窗等无法拖入的目标）：
/// 右键任意卡片复制那一张（不依赖、不改变当前选择，搜索过滤态同样可用）；
/// 面板持有键盘焦点、存在选择且不在文字编辑态时 Ctrl+C 复制选中项。
/// 复制不删除源卡片、不进撤销栈；负载与拖出同构并携带内部条目标记，
/// 自动采集据此跳过，复制旧卡片不会凭空生成重复条目。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 剪贴板写入缝：测试注入捕获函数以断言负载与提示，不触碰系统剪贴板；
    /// null 时走真实 Clipboard.SetDataObject。
    /// </summary>
    internal Func<DataObject, bool>? ClipboardWriterOverride { get; set; }

    /// <summary>
    /// 右键卡片复制（1.15.0 起，设置可关闭；1.17.0 起限于右区操作列）：
    /// 不进入选择、不打开菜单。左区右键无操作、不拦截（内容区不承载复制手势）。
    /// </summary>
    private void BoardList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            !_preferences.RightClickCardCopyEnabled ||
            e.OriginalSource is not DependencyObject source ||
            FindAncestor<ListBoxItem>(source) is not { } container ||
            container.DataContext is not BoardItem item)
        {
            return;
        }

        if (ResolveCardHitZone(source, container) != CardHitZone.Operations)
        {
            return;
        }

        e.Handled = true;
        CopyBoardItemsToClipboard([item]);
    }

    /// <summary>
    /// Ctrl+C 复制选中项：面板展开、无文字编辑焦点（TextBox 让路）且存在选择时
    /// 生效；无选择时不改剪贴板、不拦截按键。返回是否已处理该按键。
    /// </summary>
    private bool TryCopySelectionWithCtrlC(KeyEventArgs e)
    {
        if (e.Key != Key.C ||
            e.KeyboardDevice.Modifiers != ModifierKeys.Control ||
            _isClosing ||
            !_preferences.CopySelectionWithCtrlCEnabled ||
            Keyboard.FocusedElement is TextBoxBase ||
            !_viewModel.IsPanelExpanded)
        {
            return false;
        }

        var selected = OrderedSelectedBoardItems();
        if (selected.Length == 0)
        {
            return false;
        }

        e.Handled = true;
        CopyBoardItemsToClipboard(selected);
        return true;
    }

    /// <summary>选中项按板内顺序（置顶区在前、各自排序）交付，与批量置顶的取序一致。</summary>
    private BoardItem[] OrderedSelectedBoardItems()
    {
        if (_viewModel.ActivePanel is not { } panel)
        {
            return [];
        }

        var selectedIds = BoardList.SelectedItems
            .OfType<BoardItem>()
            .Select(item => item.Id)
            .ToHashSet();
        return panel.Items.Where(item => selectedIds.Contains(item.Id)).ToArray();
    }

    /// <summary>
    /// 打包并写入剪贴板，成功后经状态条提示；图片副本缺失或剪贴板暂时被占时
    /// 提示失败且不改变面板任何状态。
    /// </summary>
    private void CopyBoardItemsToClipboard(IReadOnlyList<BoardItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        DataObject data;
        try
        {
            data = _dragPayload.BuildClipboardPayload(items);
        }
        catch (FileNotFoundException)
        {
            ShowStatus("图片副本已缺失，无法复制。");
            return;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ShowStatus("无法复制这些内容：" + exception.Message);
            return;
        }

        if (!WriteClipboardData(data))
        {
            return;
        }

        ShowStatus(DescribeCopiedItems(items));
    }

    private bool WriteClipboardData(DataObject data)
    {
        try
        {
            if (ClipboardWriterOverride is { } writer)
            {
                return writer(data);
            }

            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            ShowStatus("剪贴板暂时不可用，请重试。");
            return false;
        }
    }

    private static string DescribeCopiedItems(IReadOnlyList<BoardItem> items)
    {
        var textCount = items.Count(item => item.Kind == BoardItemKind.Text);
        var imageCount = items.Count - textCount;
        return (textCount, imageCount) switch
        {
            ( > 0, 0) => $"已复制 {textCount} 条文字到剪贴板",
            (0, _) => $"已复制 {imageCount} 张图片到剪贴板",
            _ => $"已复制 {textCount} 条文字和 {imageCount} 张图片到剪贴板"
        };
    }
}
