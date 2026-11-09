using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FloatingTransferStation.Design;

namespace FloatingTransferStation.Views;

/// <summary>
/// 悬停上浮附加行为：以渲染位移（不参与布局、不影响命中测试）表达层级抬升，
/// 入场减速、回落加速；系统「减弱动效」关闭时立即定格，动画可中断。
/// </summary>
public static class LiftAnimation
{
    internal static readonly HandoffBehavior AnimationHandoffBehavior = HandoffBehavior.SnapshotAndReplace;
    internal static readonly TimeSpan LiftDuration = TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs);
    /// <summary>数值唯一来源=DesignTokens.CardHoverLiftPx（2026-11-08 视觉语言收敛）。</summary>
    public const double LiftedOffsetPx = DesignTokens.CardHoverLiftPx;

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(LiftAnimation),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.RegisterAttached(
        "AnimationsEnabled",
        typeof(bool),
        typeof(LiftAnimation),
        new PropertyMetadata(true, OnAnimationsEnabledChanged));

    public static bool GetIsActive(DependencyObject element) =>
        (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) =>
        element.SetValue(IsActiveProperty, value);

    public static bool GetAnimationsEnabled(DependencyObject element) =>
        (bool)element.GetValue(AnimationsEnabledProperty);

    public static void SetAnimationsEnabled(DependencyObject element, bool value) =>
        element.SetValue(AnimationsEnabledProperty, value);

    internal static DoubleAnimation CreateLiftAnimation(double from, double to) =>
        new(from, to, LiftDuration)
        {
            // 缓动按语义不按数值方向（2026-11-08 视觉语言收敛）：目的地=抬升态→
            // EnterEasing 减速，回落常态→ExitEasing 加速。位移向负值，原「to<from」
            // 数值判据把抬升命中加速曲线，与类注释/设计规范 §六宣称相反。
            EasingFunction = to == LiftedOffsetPx ? FadeAnimation.EnterEasing : FadeAnimation.ExitEasing,
            FillBehavior = FillBehavior.Stop,
        };

    private static void OnIsActiveChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is UIElement element)
        {
            ApplyState(element, (bool)args.NewValue, GetAnimationsEnabled(element));
        }
    }

    private static void OnAnimationsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is UIElement element &&
            (bool)args.OldValue &&
            !(bool)args.NewValue)
        {
            ApplyImmediateState(element, GetIsActive(element));
        }
    }

    private static void ApplyState(UIElement element, bool isActive, bool animationsEnabled)
    {
        var target = isActive ? LiftedOffsetPx : 0d;
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        var current = transform.Y;
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.SetValue(TranslateTransform.YProperty, target);

        if (!animationsEnabled)
        {
            return;
        }

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            CreateLiftAnimation(current, target),
            AnimationHandoffBehavior);
    }

    private static void ApplyImmediateState(UIElement element, bool isActive)
    {
        if (element.RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.SetValue(TranslateTransform.YProperty, isActive ? LiftedOffsetPx : 0d);
    }
}
