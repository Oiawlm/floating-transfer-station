using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 卡片复制到剪贴板（交付第二通道，解决游戏聊天窗等无法拖入的目标）：
/// 左键单击卡片右半复制那一张（手势交换，Issue #216；不依赖、不改变当前
/// 选择，搜索过滤态同样可用）；右键卡片右半承载选择手势（裸/Ctrl 右键
/// toggle、Shift/Ctrl+Shift 右键锚点范围选择）；左半右键打开「插件动作」
/// 菜单；面板持有键盘焦点、存在选择且不在文字编辑态时 Ctrl+C 复制选中项。
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
    /// 压制 ListBox 对卡片右键按下的默认选中：右键是选择手势（分发在 Up，
    /// 手势交换 Issue #216），不得让默认处理先行改写选择——否则 Up 的
    /// toggle 会在默认选中之上二次翻转、锚点语义也会被默认单选冲掉。
    /// 真实输入的右键 Down 若放行，ListBox 默认处理会先选中命中项——合成
    /// 测试只驱动 Up 事件，掩盖过这一实机偏差（B-007 同族）。列表空白与
    /// 滚动条不经过本压制，原生行为不变。
    /// </summary>
    private void BoardList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.OriginalSource is not DependencyObject source ||
            FindAncestor<ListBoxItem>(source) is null)
        {
            return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// 右键卡片分发（手势交换，Issue #216）：右半承载选择手势——Shift 走
    /// 锚点范围选择（Ctrl+Shift 保留既有选择并追加范围），裸右键与 Ctrl+右键
    /// 等效 toggle，均不清理其他已选条目；修饰键取 Up 时线程键盘态。左半
    /// （非复制路径）右键打开「插件动作」菜单（命中卡不在选集内时先单选）。
    /// </summary>
    private void BoardList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.OriginalSource is not DependencyObject source ||
            FindAncestor<ListBoxItem>(source) is not { } container ||
            container.DataContext is not BoardItem item)
        {
            return;
        }

        if (ResolveCardHitZone(source, container) == CardHitZone.Operations)
        {
            e.Handled = true;
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                SelectRange(item, modifiers.HasFlag(ModifierKeys.Control));
            }
            else
            {
                ToggleSelection(item);
            }

            return;
        }

        // 插件动作菜单入口＝左半右键（交换后右半右键不再出现菜单）。仅当
        // 动作菜单真正打开时才拦截右键；无插件/无可执行项维持既有语义
        //（ContentZone 左半无操作）。
        if (TryOpenPluginActionsMenu(item))
        {
            e.Handled = true;
        }
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
