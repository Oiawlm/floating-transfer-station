using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 空状态引导（契约 #33）：内容桶（待分类/重要）为空时以居中「图标＋标题＋副文案」
/// 告知内容进入方式（复用启动表面 LauncherEmptyState 先例，无 CTA——内容桶没有
/// 显式添加动作）。可见性单点＝<see cref="UpdateEmptyStates"/>：一次求值并应用
/// SearchEmptyHint 与 BoardEmptyState 两元素（2026-11-06 收敛修订，契约 #33），
/// 触发路径＝<see cref="UpdatePanelSurface"/> 表面切换＋激活桶集合变化＋
/// IsDefaultCapture 迁移＋搜索态进出＋过滤单点 ApplyBoardListFilter；文案按
/// (BoardCategory, IsDefaultCapture) 二维矩阵派生：默认接收桶＝复制自动收集
/// （剪贴板采集只落默认接收分类），非默认桶＝拖入收集。
/// 元素为纯信息性（IsHitTestVisible=False），命中穿透回 BoardList，拖放语义不变。
/// 记录表面同语言空态（ReviewEmptyState，宿主内覆盖编辑器行）由
/// <see cref="UpdateReviewEmptyState"/> 单点管理，文案复用同一矩阵的记录臂。
/// </summary>
public partial class MainWindow
{
    /// <summary>空态文案二维矩阵：穷尽具名值、无兜底臂——内容桶两分类×默认接收
    /// 布尔四臂给出文案；记录臂给记录空态文案（第二维与记录无关，忽略——进入路径
    /// 是直接输入，无默认接收语义）；启动具名值不可达（调用点已按 IsContentBucket
    /// 守卫，启动空态归 LauncherEmptyState），抛
    /// <see cref="ArgumentOutOfRangeException"/> 属不可达防御。未具名枚举值
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
            (BoardCategory.Reference, _) => (
                "还没有记录内容", "直接输入即可开始记录，内容会自动保存"),
            (BoardCategory.Prompt, _) =>
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

        // 绑定视图自身的变化事件（源变化经视图转发、Filter 刷新、ItemsSource 切换
        // 都会触发）：空集臂读 BoardList.Items.IsEmpty 是视图口径，而源集合事件与
        // 视图处理序＝订阅序（ItemCollection 随 ItemsSource 晚于本窗口挂接），源
        // 事件里同步求值会读到尚未消化变化的过期视图（搜索态删最后一张匹配卡时
        // 空提示缺席的同源时序）。视图事件在视图内部状态更新完成后才发出，恰是
        // 本单点想要的求值时机；视图恒在 UI 线程处理变化，无需封送。
        ((INotifyCollectionChanged)BoardList.Items).CollectionChanged += ActivePanelItems_EmptyStateChanged;
    }

    private void ActivePanelItems_EmptyStateChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyStates();
        // 激活桶内容增删/归组/GroupedView 纯重排（Move）同拍重评零指派引导条
        //（2026-11-06，#57 可用性扩展）：谓词读源集合，与空态单点共用本触发。
        UpdateGroupedViewGuidance();
        // 增删卡改变槽计数（契约 #62 驻留提示口径）：与空态/引导条同拍喂数。
        RefreshSubgroupChipHints();
    }

    private void ActivePanel_EmptyStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryViewModel.IsDefaultCapture))
        {
            UpdateEmptyStates();
        }
    }

    /// <summary>空态可见性唯一求值点（契约 #33，2026-11-06 收敛修订；2026-11-07
    /// 谓词分化修订；2026-11-08 槽过滤空态去 CTA 修订）：从当前状态
    /// 一次算出并应用 SearchEmptyHint（含分支互补三件套：标题文案＋搜索分支
    /// 「清空筛选」按钮可见/槽过滤分支副文案引导可见，两者互斥）与
    /// BoardEmptyState 两元素——同格（Grid.Row=2）
    /// 设计互斥（搜索态「无匹配结果」与槽过滤空结果「该分组暂无卡片」的空集
    /// 语义由 SearchEmptyHint 承载、文案按来源切换，#57 扩展；内容桶空态让位——
    /// 让位谓词＝仅槽过滤：「全部」全量态与根态内容等价，空分类走 BoardEmptyState），
    /// 收敛前的两分离更新点触发路径不成并集——chip 单击等纯过滤路径只重评空集臂、
    /// 从不重评内容桶臂，空面板上点
    /// 分组按钮即两段空态文字同格叠加（用户实证）；收敛后全部状态迁移路径（表面
    /// 切换、集合 CollectionChanged、IsDefaultCapture 迁移、搜索进出、过滤单点
    /// ApplyBoardListFilter——chip 单击/取消/清筛选/Esc/切分类全经此）汇聚调用本
    /// 单点，结构上不存在漏路径；同路径重复调用无害（纯求值幂等），求「求值点
    /// 唯一」不求「调用次数最少」。BoardEmptyState 文案随激活桶 (Category,
    /// IsDefaultCapture) 派生。集合变化可能来自后台导入路径（外部拖入的落板回调
    /// 在线程池线程触发 CollectionChanged——CI 实证曾因此跨线程访问崩掉测试宿主），
    /// 非 UI 线程进入时统一封送回 UI 线程再碰元素（与 MoveMany 批量路径的
    /// Dispatcher.InvokeAsync 先例同法）。</summary>
    private void UpdateEmptyStates()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateEmptyStates);
            return;
        }

        var panel = _viewModel.ActivePanel;
        var boardVisible = panel is not null &&
            BoardCategoryCatalog.IsContentBucket(panel.Category) &&
            !_viewModel.IsSearchActive &&
            _subgroupFilterSlot is null &&
            panel.Items.Count == 0;
        BoardEmptyState.SetCurrentValue(VisibilityProperty, boardVisible ? Visibility.Visible : Visibility.Collapsed);
        if (boardVisible && panel is not null)
        {
            // 文案派生只在可见时进行：矩阵对启动具名值抛防御异常（不可达），
            // 非内容桶表面不得进入；不可见期残留文案无意义，下一可见轮会重算。
            var (title, hint) = BoardEmptyStateCopy(panel.Category, panel.IsDefaultCapture);
            BoardEmptyStateTitle.SetCurrentValue(TextBlock.TextProperty, title);
            BoardEmptyStateHint.SetCurrentValue(TextBlock.TextProperty, hint);
        }

        var searchVisible = _viewModel.IsSearchActive && BoardList.Items.IsEmpty;
        // 让位谓词＝仅槽过滤（契约 #33 修订口径）：「全部」全量态与根态内容等价——
        // 空分类走 BoardEmptyState，不让位「该分组暂无卡片」。
        var subgroupVisible = !_viewModel.IsSearchActive &&
            _subgroupFilterSlot is not null &&
            BoardList.Items.IsEmpty;
        SearchEmptyHint.SetCurrentValue(VisibilityProperty, searchVisible || subgroupVisible
            ? Visibility.Visible
            : Visibility.Collapsed);
        SearchEmptyHintTitle.SetCurrentValue(
            TextBlock.TextProperty,
            _viewModel.IsSearchActive ? "无匹配结果" : "该分组暂无卡片");
        // 分支互补三件套（契约 #33 2026-11-08 修订）：搜索＝「清空筛选」按钮在场
        //（#37 语义零变化）、副文案退位；槽过滤＝无 CTA——按钮退位、副文案承载
        // 退出引导（对齐内容桶空态的纯文字引导范式：该空态是「视图空」而非
        // 「内容空」，轨在场且被过滤 chip 高亮，Esc/切分类/悬停其他 chip 通道充足）。
        SearchClearFilterButton.SetCurrentValue(
            VisibilityProperty,
            searchVisible ? Visibility.Visible : Visibility.Collapsed);
        SearchEmptyHintHint.SetCurrentValue(
            VisibilityProperty,
            subgroupVisible ? Visibility.Visible : Visibility.Collapsed);
        SearchEmptyHintHint.SetCurrentValue(
            TextBlock.TextProperty,
            "按 Esc 查看该分类全部卡片，或移到其他分组");
    }

    /// <summary>记录空态唯一更新点：记录表面激活、编辑器可用（存储在位）且当前
    /// 编辑内容为空时可见——「当日无内容」以编辑器文本为唯一事实（装载/外部刷新
    /// 都经文本落位）。触发路径＝<see cref="UpdatePanelSurface"/> 表面切换、
    /// ReviewEditor_TextChanged 输入与清空、LoadReviewDateAsync 日期装载完成、
    /// HandleDailyReviewChangeAsync 外部变更后的文本替换；全部 UI 线程进入（外部
    /// 文件 Changed 已由 DailyReviews_Changed 封送），无需封送分支。文案复用
    /// <see cref="BoardEmptyStateCopy"/> 记录臂（第二维与记录无关，传 false）；无
    /// CTA——记录的进入路径是直接输入，编辑器本身就是动作面，空态
    /// IsHitTestVisible=False 让点击穿透到编辑器取得焦点。内容桶空态（#33）与
    /// 本空态各属专属宿主，表面互斥由 UpdatePanelSurface 三态分发结构性保证。</summary>
    private void UpdateReviewEmptyState()
    {
        var visible = IsReviewActive() &&
            ReviewEditor.IsEnabled &&
            ReviewEditor.Text.Length == 0;
        ReviewEmptyState.SetCurrentValue(VisibilityProperty, visible ? Visibility.Visible : Visibility.Collapsed);
        if (!visible)
        {
            return;
        }

        var (title, hint) = BoardEmptyStateCopy(DailyReviewMigration.ReviewCategory, isDefaultCapture: false);
        ReviewEmptyStateTitle.SetCurrentValue(TextBlock.TextProperty, title);
        ReviewEmptyStateHint.SetCurrentValue(TextBlock.TextProperty, hint);
    }
}
