using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Views;

/// <summary>
/// 「文字卡 AI 整理」的窗口编排（1.41.0，契约 #29）：截断文字卡悬停出现 AI 整理
/// 钮 → 一键调 DeepSeek flash 按内置提示词整理全文 → 直接替换正文 → Ctrl+Z
/// 恢复原文。入口守卫（忙态防抖/编辑/搜索态）→取 Key（空则状态条提示零请求）→
/// 在途状态与排空（TrackPendingOperation）→失败/成功/并发放弃三分支；成功经
/// <see cref="BoardMutationService.ReplaceItemTextUndoableAsync"/> 落板（撤销栈
/// 泛化的文本替换批）。变更全部在 UI 线程落板（快照构建状态所属线程契约）；
/// 无自动重试（用户重点即重发）、无取消面（退出排空由 TrackPendingOperation 承接）。
/// 手势/合成/UIA 三入口的路由见 MainWindow.CardGestures 与
/// MainWindow.SelectionAndDeletion 的 AiOrganizeCommand 分支。
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan AiBusyRotationPeriod = TimeSpan.FromSeconds(1.2);

    private readonly AiTextOrganizer _aiTextOrganizer;

    /// <summary>
    /// AI 整理一张卡片：守卫→取 Key→在途态→整理→三分支落板。真实输入（手势层）
    /// 与合成/UIA 路径（BoardList_ButtonClick）共用本入口；每卡单飞（IsAiOrganizing
    /// 重入丢弃），跨卡允许并发。编排体恒经 Dispatcher.InvokeAsync 启动：帧内
    /// SynchronizationContext 是 Dispatcher 上下文，所有 await 续体（完成/失败/
    /// finally）都回投 UI 线程（快照构建状态所属线程契约），不依赖调用方的
    /// 线程或上下文（合成/UIA 调用可能来自无上下文的 STA 或线程池线程）。
    /// </summary>
    private Task BeginCardAiOrganizeAsync(BoardItem item) =>
        Dispatcher.InvokeAsync(() => BeginCardAiOrganizeCoreAsync(item))
            .Task
            .Unwrap();

    private async Task BeginCardAiOrganizeCoreAsync(BoardItem item)
    {
        // 入口守卫：忙态防抖（按钮保持启用防点击穿透，重入在此丢弃）与
        // 编辑/搜索态（与按钮可见性条件同族，守卫兜底合成/UIA 路径）。
        if (item.IsAiOrganizing ||
            item.IsEditing ||
            item.IsEditingNote ||
            _viewModel.IsSearchActive)
        {
            return;
        }

        var apiKey = _secretProtector.Unprotect(
            _preferences.DeepSeekApiKeyProtected ?? string.Empty);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ShowStatus("尚未配置 DeepSeek API Key：设置 → AI 设置。");
            return;
        }

        var originalText = item.Text ?? string.Empty;
        item.BeginAiOrganize();
        UpdateCardAiBusyGlyph(item, isBusy: true);
        ShowStatus("正在整理这张卡片…");
        var operation = _aiTextOrganizer.OrganizeAsync(
            originalText,
            apiKey,
            _windowOperationCancellation.Token);
        TrackPendingOperation(operation);
        try
        {
            var outcome = await operation;
            if (!outcome.IsSuccess)
            {
                ShowStatus($"AI 整理失败：{outcome.ErrorMessage}");
                return;
            }

            // 乐观并发守卫：整理期间条目被删或原文已变（手工编辑/另一次整理完成）
            // → 放弃本次结果，不覆盖用户的新状态。
            if (_board.FindItem(item.Id) is null ||
                !string.Equals(item.Text, originalText, StringComparison.Ordinal))
            {
                ShowStatus("卡片内容已变化，整理结果已放弃。");
                return;
            }

            if (await _mutations.ReplaceItemTextUndoableAsync(item.Id, outcome.OrganizedText!))
            {
                ShowStatus("整理完成，Ctrl + Z 可恢复原文。");
            }
        }
        catch (OperationCanceledException)
        {
            // 退出排空：取消即静默结束（状态由 finally 清理），不打扰收尾。
        }
        finally
        {
            item.EndAiOrganize();
            UpdateCardAiBusyGlyph(item, isBusy: false);
        }
    }

    /// <summary>
    /// 忙态弧线图标的旋转：动效开启时线性匀速循环，「界面动效」关闭＝静态弧线
    /// （与 FadeAnimation 同一开关口径 ClientAreaAnimationsEnabled）。图标本体
    /// 的显隐由模板 DataTrigger 承载，这里只驱动其 RenderTransform；变换实例由
    /// 代码创建——DataTemplate 内联声明的 Freezable 会在模板密封期被冻结并跨
    /// 容器共享，冻结对象上 BeginAnimation 会抛异常。
    /// </summary>
    private void UpdateCardAiBusyGlyph(BoardItem item, bool isBusy)
    {
        if (BoardList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container ||
            FindDescendantByName(container, "CardAiBusyIcon") is not { } indicator)
        {
            return;
        }

        if (isBusy && ClientAreaAnimationsEnabled)
        {
            var rotate = new RotateTransform { CenterX = 8d, CenterY = 8d };
            indicator.RenderTransform = rotate;
            rotate.BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(0d, 360d, AiBusyRotationPeriod)
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }
        else if (indicator.RenderTransform is RotateTransform rotate)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = 0d;
        }
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject parent, string name)
    {
        if (parent is FrameworkElement element && element.Name == name)
        {
            return element;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (FindDescendantByName(child, name) is { } match)
            {
                return match;
            }
        }

        return null;
    }
}
