using System.Globalization;
using System.Windows;
using System.Windows.Media.Animation;
using FloatingTransferStation.Design;

namespace FloatingTransferStation.Views;

/// <summary>
/// 通用淡入淡出附加行为：以 Opacity 过渡表达悬停配色或显隐变化（分层交叉淡入），
/// 入场用减速曲线、出场用加速曲线；系统「减弱动效」关闭时立即定格，
/// 新状态到来时直接替换运行中的动画（可中断）。
/// </summary>
public static class FadeAnimation
{
    internal static readonly HandoffBehavior AnimationHandoffBehavior = HandoffBehavior.SnapshotAndReplace;

    internal static readonly SplineEase EnterEasing = SplineEase.FromToken(DesignTokens.EnterEasingKeySpline);
    internal static readonly SplineEase ExitEasing = SplineEase.FromToken(DesignTokens.ExitEasingKeySpline);

    private static readonly DependencyProperty HideGenerationProperty = DependencyProperty.RegisterAttached(
        "HideGeneration",
        typeof(int),
        typeof(FadeAnimation),
        new PropertyMetadata(0));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(FadeAnimation),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.RegisterAttached(
        "AnimationsEnabled",
        typeof(bool),
        typeof(FadeAnimation),
        new PropertyMetadata(true, OnAnimationsEnabledChanged));

    public static readonly DependencyProperty DurationMsProperty = DependencyProperty.RegisterAttached(
        "DurationMs",
        typeof(int),
        typeof(FadeAnimation),
        new PropertyMetadata(DesignTokens.FadeVisibilityMs));

    public static readonly DependencyProperty ManageVisibilityProperty = DependencyProperty.RegisterAttached(
        "ManageVisibility",
        typeof(bool),
        typeof(FadeAnimation),
        new PropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) =>
        (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) =>
        element.SetValue(IsActiveProperty, value);

    public static bool GetAnimationsEnabled(DependencyObject element) =>
        (bool)element.GetValue(AnimationsEnabledProperty);

    public static void SetAnimationsEnabled(DependencyObject element, bool value) =>
        element.SetValue(AnimationsEnabledProperty, value);

    public static int GetDurationMs(DependencyObject element) =>
        (int)element.GetValue(DurationMsProperty);

    public static void SetDurationMs(DependencyObject element, int value) =>
        element.SetValue(DurationMsProperty, value);

    public static bool GetManageVisibility(DependencyObject element) =>
        (bool)element.GetValue(ManageVisibilityProperty);

    public static void SetManageVisibility(DependencyObject element, bool value) =>
        element.SetValue(ManageVisibilityProperty, value);

    internal static DoubleAnimation CreateOpacityAnimation(double from, double to, int durationMs) =>
        new(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = to < from ? ExitEasing : EnterEasing,
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
        var targetOpacity = isActive ? 1d : 0d;
        var currentOpacity = element.Opacity;
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetValue(UIElement.OpacityProperty, targetOpacity);

        if (GetManageVisibility(element) && isActive)
        {
            element.Visibility = Visibility.Visible;
        }

        if (!animationsEnabled)
        {
            if (GetManageVisibility(element) && !isActive)
            {
                element.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var animation = CreateOpacityAnimation(currentOpacity, targetOpacity, GetDurationMs(element));
        if (GetManageVisibility(element) && !isActive)
        {
            var generation = (int)element.GetValue(HideGenerationProperty) + 1;
            element.SetValue(HideGenerationProperty, generation);
            animation.Completed += (_, _) =>
            {
                if ((int)element.GetValue(HideGenerationProperty) == generation &&
                    !GetIsActive(element))
                {
                    element.Visibility = Visibility.Collapsed;
                }
            };
        }

        element.BeginAnimation(UIElement.OpacityProperty, animation, AnimationHandoffBehavior);
    }

    private static void ApplyImmediateState(UIElement element, bool isActive)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetValue(UIElement.OpacityProperty, isActive ? 1d : 0d);
        if (GetManageVisibility(element))
        {
            element.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

/// <summary>
/// 以三次贝塞尔控制点表达的缓动函数（等价其他框架的 KeySpline/SplineEase）。
/// </summary>
internal sealed class SplineEase : IEasingFunction
{
    private readonly double _x1;
    private readonly double _y1;
    private readonly double _x2;
    private readonly double _y2;

    private SplineEase(double x1, double y1, double x2, double y2)
    {
        _x1 = x1;
        _y1 = y1;
        _x2 = x2;
        _y2 = y2;
    }

    public static SplineEase FromToken(string token)
    {
        var parts = token.Split(',');
        return new SplineEase(
            double.Parse(parts[0], CultureInfo.InvariantCulture),
            double.Parse(parts[1], CultureInfo.InvariantCulture),
            double.Parse(parts[2], CultureInfo.InvariantCulture),
            double.Parse(parts[3], CultureInfo.InvariantCulture));
    }

    public double Ease(double normalizedTime)
    {
        if (normalizedTime <= 0d)
        {
            return 0d;
        }

        if (normalizedTime >= 1d)
        {
            return 1d;
        }

        return SampleY(SolveParameter(normalizedTime));
    }

    private double SolveParameter(double x)
    {
        // 合法 KeySpline 的 x(u) 单调，二分求解 24 次已足够精确。
        var low = 0d;
        var high = 1d;
        for (var index = 0; index < 24; index++)
        {
            var mid = (low + high) / 2d;
            if (SampleX(mid) < x)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2d;
    }

    private double SampleX(double u) =>
        (3d * (1d - u) * (1d - u) * u * _x1) + (3d * (1d - u) * u * u * _x2) + (u * u * u);

    private double SampleY(double u) =>
        (3d * (1d - u) * (1d - u) * u * _y1) + (3d * (1d - u) * u * u * _y2) + (u * u * u);
}
