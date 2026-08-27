using System.Windows;
using System.Windows.Media.Animation;

namespace FloatingTransferStation.Views;

public static class CategoryFeedbackAnimation
{
    internal static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(120);
    internal static readonly HandoffBehavior AnimationHandoffBehavior = HandoffBehavior.SnapshotAndReplace;

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(CategoryFeedbackAnimation),
        new PropertyMetadata(false, OnIsActiveChanged));

    public static readonly DependencyProperty AnimationsEnabledProperty = DependencyProperty.RegisterAttached(
        "AnimationsEnabled",
        typeof(bool),
        typeof(CategoryFeedbackAnimation),
        new PropertyMetadata(true, OnAnimationsEnabledChanged));

    public static bool GetIsActive(DependencyObject element) =>
        (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) =>
        element.SetValue(IsActiveProperty, value);

    public static bool GetAnimationsEnabled(DependencyObject element) =>
        (bool)element.GetValue(AnimationsEnabledProperty);

    public static void SetAnimationsEnabled(DependencyObject element, bool value) =>
        element.SetValue(AnimationsEnabledProperty, value);

    internal static DoubleAnimation CreateOpacityAnimation(double from, double to) =>
        new(from, to, new Duration(TransitionDuration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
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

        if (animationsEnabled)
        {
            element.BeginAnimation(
                UIElement.OpacityProperty,
                CreateOpacityAnimation(currentOpacity, targetOpacity),
                AnimationHandoffBehavior);
        }
    }

    private static void ApplyImmediateState(UIElement element, bool isActive)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.SetValue(UIElement.OpacityProperty, isActive ? 1d : 0d);
    }
}
