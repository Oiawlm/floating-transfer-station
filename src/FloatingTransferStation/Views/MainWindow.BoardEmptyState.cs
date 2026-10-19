using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 内容桶空状态引导（契约 #33）：待分类/图片为空时以居中「图标＋标题＋副文案」
/// 告知内容进入方式（复用启动表面 LauncherEmptyState 先例，无 CTA——内容桶没有
/// 显式添加动作）。可见性单点＝<see cref="UpdatePanelSurface"/> 表面切换＋激活桶
/// 集合变化＋搜索态进出；文案按 (BoardCategory, IsDefaultCapture) 二维矩阵派生：
/// 默认接收桶＝复制自动收集（剪贴板采集只落默认接收分类），非默认桶＝拖入收集。
/// 元素为纯信息性（IsHitTestVisible=False），命中穿透回 BoardList，拖放语义不变。
/// </summary>
public partial class MainWindow
{
    /// <summary>空态文案二维矩阵：穷尽具名值、无兜底臂——内容桶两分类×默认接收
    /// 布尔四臂给出文案；复盘/启动具名值不可达（调用点已按 IsContentBucket 守卫），
    /// 抛 <see cref="ArgumentOutOfRangeException"/> 属不可达防御。未具名枚举值
    /// （CS8524）显式豁免：存储边界对分类做 IsDefined 守卫、越界按损坏条目丢弃，
    /// 与 <see cref="BoardCategoryCatalog.RoleOf"/> 同口径。</summary>
#pragma warning disable CS8524 // switch 表达式不处理未命名枚举值（装载边界已清洗；漏映射 CS8509 仍为编译错误）
    internal static (string Title, string Hint) BoardEmptyStateCopy(
        BoardCategory category,
        bool isDefaultCapture) => (category, isDefaultCapture) switch
        {
            (BoardCategory.Inbox, true) => (
                "还没有内容", "复制的文字或图片会自动收集到这里，也可以直接拖入"),
            (BoardCategory.Inbox, false) => (
                "还没有内容", "把文字或图片拖到这里即可收集"),
            (BoardCategory.CustomerOriginal, true) => (
                "还没有内容", "复制的图片或文字会自动收集到这里，也可以直接拖入"),
            (BoardCategory.CustomerOriginal, false) => (
                "还没有内容", "把图片或文字拖到这里即可收集"),
            (BoardCategory.Reference or BoardCategory.Prompt, _) =>
                throw new ArgumentOutOfRangeException(nameof(category), category, null),
        };
#pragma warning restore CS8524

    private void InitializeBoardEmptyState()
    {
        foreach (var panel in _viewModel.Categories)
        {
            // 集合变化（采集/拖入/删除/批量操作）与默认接收迁移都可能是空态翻转
            // 或文案换格的触发点；订阅全部桶而非仅激活桶——面板可在收起期变更。
            panel.Items.CollectionChanged += ActivePanelItems_EmptyStateChanged;
            panel.PropertyChanged += ActivePanel_EmptyStateChanged;
        }
    }

    private void ActivePanelItems_EmptyStateChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateBoardEmptyState();

    private void ActivePanel_EmptyStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryViewModel.IsDefaultCapture))
        {
            UpdateBoardEmptyState();
        }
    }

    /// <summary>空态可见性与文案的唯一更新点：仅内容桶表面、非搜索态且激活桶为空
    /// 时可见（搜索态的空集语义由 SearchEmptyHint 承载）；文案随激活桶
    /// (Category, IsDefaultCapture) 派生。surface/搜索/集合三触发路径共用。
    /// 集合变化可能来自后台导入路径（外部拖入的落板回调在线程池线程触发
    /// CollectionChanged——CI 实证曾因此跨线程访问崩掉测试宿主），非 UI 线程
    /// 进入时统一封送回 UI 线程再碰元素（与 MoveMany 批量路径的
    /// Dispatcher.InvokeAsync 先例同法）。</summary>
    private void UpdateBoardEmptyState()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateBoardEmptyState);
            return;
        }

        var panel = _viewModel.ActivePanel;
        var visible = panel is not null &&
            BoardCategoryCatalog.IsContentBucket(panel.Category) &&
            !_viewModel.IsSearchActive &&
            panel.Items.Count == 0;
        BoardEmptyState.SetCurrentValue(VisibilityProperty, visible ? Visibility.Visible : Visibility.Collapsed);
        if (!visible || panel is null)
        {
            return;
        }

        // 文案派生只在可见时进行：矩阵对复盘/启动具名值抛防御异常（不可达），
        // 非内容桶表面不得进入；不可见期残留文案无意义，下一可见轮会重算。
        var (title, hint) = BoardEmptyStateCopy(panel.Category, panel.IsDefaultCapture);
        BoardEmptyStateTitle.SetCurrentValue(TextBlock.TextProperty, title);
        BoardEmptyStateHint.SetCurrentValue(TextBlock.TextProperty, hint);
    }
}
