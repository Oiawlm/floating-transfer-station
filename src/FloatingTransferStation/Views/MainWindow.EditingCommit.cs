using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Views;

/// <summary>
/// 编辑会话终结共享协调（单一定义点，契约 #49 修订：点外即提交＋切走即冲刷）：
/// ①窗口级 Preview 左键按下隧道——命中在「当前编辑位编辑器」之外的按下 → 调用
/// 该编辑位既有提交/冲刷实现；②面板切换冲刷——<see cref="FlushEditingSessionsForPanelSwitch"/>
/// 在 ActivatePanel 顶部单点终结全部在途编辑会话。五处就地编辑位接入：分类名与子分组
/// chip 名（两类改名会话经共享改名会话协调器登记——<see cref="_renameSessions"/>）＋
/// 瓦片名（QuickLaunch）＋卡片正文/备注（CardTextEditing 单会话机器）＋记录编辑器
///（DailyReview 700ms 防抖的立即冲刷）。只补触发通道：不改各编辑位既有提交/取消语义
///（Enter/Esc/失焦路径原样保留，仍各自生效），不吞事件（e.Handled 不置位——按下继续
/// 原旅程：选择、拖拽起手、按钮点击照常），不触碰独立对话框类编辑窗（弹层/独立窗口
/// 天然不经本窗隧道）。编辑器内的按下不触发（交给编辑器放光标/选择）；卡片编辑会话对
///「正在编辑的卡片容器」内的按下不在此触发（由卡片手势层按契约 #22 管辖）；滚动条
/// 例外沿列表既有语义保持（滚动语义不终结会话）。
/// </summary>
public partial class MainWindow
{
    /// <summary>改名会话协调器登记清单（契约 #49 修订）：分类名在前、chip 名在后（面板
    /// 切换冲刷按此序提交）；按压隧道与切换冲刷遍历同一清单——「五处」与「切走冲刷」是
    /// 同一登记的两类消费者。</summary>
    private IReadOnlyList<IInlineRenameSession> _renameSessions = [];

    private void InitializeEditingCommit()
    {
        _renameSessions = [_categoryRenameSession, _subgroupRenameSession];
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

    /// <summary>五处编辑位共享的按压协调入口（窗口隧道最早点，先于各表面既有处理器）。</summary>
    private void CommitEditingOnPointerPressOutside(DependencyObject pressSource)
    {
        FlushReviewEditingOnPointerPressOutside(pressSource);
        CommitCardEditingOnPointerPressOutside(pressSource);
        CommitRenameSessionsOnPointerPressOutside(pressSource);
        CommitLauncherRenameOnPointerPressOutside(pressSource);
    }

    /// <summary>面板切换统一冲刷（契约 #49 修订）：ActivatePanel 顶部单点终结全部在途
    /// 编辑会话——序＝卡片会话终结（提交＋清手势记忆）→改名会话按登记序提交（分类名→
    /// chip 名；IME 组合文本一并保存、组合标记随终结清理）→记录按角色冲刷。冲刷先于
    /// 视图突变（_viewModel.Activate 之前编辑器仍在可视树、提交读即时文本可行）、只走
    /// 提交路径（Esc＝取消丢弃语义不受影响）、幂等吸收重复触发；瓦片沿
    /// UpdatePanelSurface 既有表面切换提交，不在此迁入。</summary>
    private void FlushEditingSessionsForPanelSwitch(BoardCategory target)
    {
        // 卡片会话锚定当前面板视图：换面板即终结（提交+清手势记忆），早于搜索退出
        // 与焦点转移——它们的中间状态不应干扰提交流径。
        EndCardEditSessionForViewChange();

        foreach (var session in _renameSessions)
        {
            session.CommitActiveSession();
        }

        // 冲刷仍是记录特有行为，键在角色（#30）：离开记录表面即冲刷未保存记录。
        if (IsReviewActive() && BoardCategoryCatalog.RoleOf(target) != CategoryRole.Review)
        {
            TrackPendingOperation(FlushReviewAsync());
        }
    }

    /// <summary>记录编辑器：编辑器之外按下且有待存内容 → 立即冲刷保存（不等
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

    /// <summary>两类改名会话（协调器登记清单：分类名＋子分组 chip 名，契约 #57 修订⑤）：
    /// 改名编辑器之外按下 → 与失焦路径同一提交（读 editor.Text 即时值——WPF 组合期文本
    /// 已在 Text 中，组合文本随提交一并保存不丢失；组合标记随终结清理，防会话终结后
    /// 组合完成事件不再到来导致标记滞留）。编辑器内按压不触发。</summary>
    private void CommitRenameSessionsOnPointerPressOutside(DependencyObject pressSource)
    {
        foreach (var session in _renameSessions)
        {
            if (session.ActiveEditor() is { } editor &&
                !IsPointerPressWithin(editor, pressSource))
            {
                session.CommitActiveSession();
            }
        }
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
