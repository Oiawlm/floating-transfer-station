using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 「点击编辑器外部＝提交」共享协调（单一定义点）：窗口级 Preview 左键按下隧道
/// 挂接，命中在「当前编辑位编辑器」之外的按下 → 调用该编辑位既有提交/冲刷实现。
/// 只补触发通道：不改各编辑位既有提交/取消语义（Enter/Esc/失焦路径原样保留，
/// 仍各自生效），不吞事件（e.Handled 不置位——按下继续原旅程：选择、拖拽起手、
/// 按钮点击照常），不触碰独立对话框类编辑窗（弹层/菜单在独立 HWND，不经本窗
/// 隧道，天然不受影响）。四处接入：分类名（CategoryEditing）、瓦片名
/// （QuickLaunch）、卡片正文/备注（CardTextEditing）、复盘编辑器（DailyReview
/// 700ms 防抖的立即冲刷）。编辑器内的按下不触发（交给编辑器放光标/选择）；
/// 卡片编辑会话对「正在编辑的卡片容器」内的按下不在此触发（由卡片手势层按
/// 契约 #22 管辖：容器内非编辑器按下＝提交并整卡吞掉）；滚动条例外沿卡片
/// 既有语义保持（拖动滚动条属滚动语义，不终结会话）。
/// </summary>
public partial class MainWindow
{
    private void InitializeEditingCommit()
    {
        AddHandler(
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(Root_EditingCommitPointerPressed));
    }

    private void Root_EditingCommitPointerPressed(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing ||
            e.ChangedButton != MouseButton.Left ||
            e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        CommitEditingOnPointerPressOutside(source);
    }

    /// <summary>四处编辑位共享的按压协调入口（窗口隧道最早点，先于各表面既有处理器）。</summary>
    private void CommitEditingOnPointerPressOutside(DependencyObject pressSource)
    {
        FlushReviewEditingOnPointerPressOutside(pressSource);
        CommitCardEditingOnPointerPressOutside(pressSource);
        CommitCategoryNameEditingOnPointerPressOutside(pressSource);
        CommitLauncherRenameOnPointerPressOutside(pressSource);
    }

    /// <summary>复盘编辑器：编辑器之外按下且有待存内容 → 立即冲刷保存（不等
    /// 700ms 防抖）。干净时冲刷在 SaveReviewAsync 内自短路，但这里先行判脏，
    /// 避免每次按压都空排队异步操作。</summary>
    private void FlushReviewEditingOnPointerPressOutside(DependencyObject pressSource)
    {
        if (!IsReviewActive() ||
            !_reviewDirty ||
            IsPointerPressWithin(ReviewEditor, pressSource))
        {
            return;
        }

        TrackPendingOperation(FlushReviewAsync());
    }

    /// <summary>卡片正文/备注：正在编辑的卡片容器之外按下 → 经既有
    /// CommitCardTextEditing 冲刷会话（容器之内的按下由卡片手势层按契约 #22
    /// 管辖：非编辑器按下＝提交并整卡吞掉，编辑器内按下＝编辑器原生）。滚动条
    /// 按压沿列表既有例外跳过（滚动语义不终结会话）。列表内部的此类按压此前
    /// 已由 BoardList 处理器冲刷——本通道把同一语义扩到列表之外的整个面板
    /// （头部、标签栏、面板空白），重复冲刷由会话锚点幂等吸收。</summary>
    private void CommitCardEditingOnPointerPressOutside(DependencyObject pressSource)
    {
        if (_editingCardItemId is null ||
            FindAncestor<ScrollBar>(pressSource) is not null)
        {
            return;
        }

        if (TryGetEditingCardContainer() is { } editingContainer &&
            IsPointerPressWithin(editingContainer, pressSource))
        {
            return;
        }

        CommitCardTextEditing();
    }

    /// <summary>分类名：改名编辑器之外按下 → 与失焦路径同一提交（读 editor.Text
    /// 即时值——WPF 组合期文本已在 Text 中，组合文本随提交一并保存不丢失；
    /// 组合标记同步清理，防会话终结后组合完成事件不再到来导致标记滞留）。</summary>
    private void CommitCategoryNameEditingOnPointerPressOutside(DependencyObject pressSource)
    {
        if (TryGetActiveCategoryNameEditor() is not { } editor ||
            IsPointerPressWithin(editor, pressSource))
        {
            return;
        }

        _activeCategoryNameCompositions.Remove(editor);
        CommitCategoryNameEdit(
            (CategoryViewModel)editor.DataContext,
            editor.Text);
    }

    /// <summary>瓦片改名：改名编辑器之外按下 → 与失焦路径同一提交（Explicit
    /// 绑定在 Commit 内显式推回，DraftName 取到编辑器即时文本，组合文本一并
    /// 保存；编辑器找不到的会话按列表外按压处理，Commit 自会跳过推回）。</summary>
    private void CommitLauncherRenameOnPointerPressOutside(DependencyObject pressSource)
    {
        if (_launcherRenamingItem is null)
        {
            return;
        }

        if (TryGetLauncherRenameEditor() is { } editor &&
            IsPointerPressWithin(editor, pressSource))
        {
            return;
        }

        CommitLauncherTileRename();
    }

    /// <summary>按压是否落在指定元素（含后代）内。ReferenceEquals 兼容命中源
    /// 即元素自身的按压（透明背景命中 Border/Grid 本体）。</summary>
    private static bool IsPointerPressWithin(System.Windows.Media.Visual scope, DependencyObject source) =>
        ReferenceEquals(scope, source) || scope.IsAncestorOf(source);
}
