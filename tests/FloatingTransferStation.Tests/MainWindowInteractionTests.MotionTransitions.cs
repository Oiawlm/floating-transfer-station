using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void FadeAnimation_UsesLockedEasingCurvesAndInterruptibleHandoff()
    {
        var create = typeof(FadeAnimation).GetMethod(
            "CreateOpacityAnimation",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(create);

        var enter = (DoubleAnimation)create.Invoke(null, [0d, 1d, DesignTokens.FadeVisibilityMs])!;
        Assert.AreEqual(TimeSpan.FromMilliseconds(DesignTokens.FadeVisibilityMs), enter.Duration);
        Assert.AreEqual(FillBehavior.Stop, enter.FillBehavior);
        var enterField = typeof(FadeAnimation).GetField(
            "EnterEasing", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(enterField);
        Assert.AreSame(enterField.GetValue(null), enter.EasingFunction);
        Assert.IsNotNull(enter.EasingFunction);
        Assert.IsTrue(
            enter.EasingFunction.Ease(0.5) > 0.8,
            "入场曲线前半程应快速推进（减速曲线）。");

        var exit = (DoubleAnimation)create.Invoke(null, [1d, 0d, DesignTokens.HoverFeedbackMs])!;
        Assert.AreEqual(TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs), exit.Duration);
        var exitField = typeof(FadeAnimation).GetField(
            "ExitEasing", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(exitField);
        Assert.AreSame(exitField.GetValue(null), exit.EasingFunction);
        Assert.IsNotNull(exit.EasingFunction);
        Assert.IsTrue(
            exit.EasingFunction!.Ease(0.5) < 0.15,
            "出场曲线前半程应缓慢推进（加速曲线）。");
    }

    [STATestMethod]
    public void StatusOverlay_FadesInAndOutWithManagedVisibility()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var overlay = (Border)window.FindName("StatusOverlay");
            Assert.IsNotNull(overlay);

            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);

            window.ShowStatus("已保存");
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, overlay.Visibility);
            Assert.IsTrue(overlay.HasAnimatedProperties);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(1d, overlay.Opacity, 0.001);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "status-overlay-fade.png",
                "FTS_MOTION_TRANSITIONS_EVIDENCE_DIR");

            InvokePrivate(window, "StatusTimer_Tick", null, EventArgs.Empty);
            Assert.IsTrue(overlay.HasAnimatedProperties);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
            Assert.AreEqual(0d, overlay.GetAnimationBaseValue(UIElement.OpacityProperty));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void StatusOverlay_IsImmediateWhenSystemAnimationsAreDisabled()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var overlay = (Border)window.FindName("StatusOverlay");
            Assert.IsNotNull(overlay);

            window.ShowStatus("已保存");
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, overlay.Visibility);
            Assert.IsFalse(overlay.HasAnimatedProperties);
            Assert.AreEqual(1d, overlay.Opacity);

            InvokePrivate(window, "StatusTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
            Assert.IsFalse(overlay.HasAnimatedProperties);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardMotionLayers_AreWiredToHoverAndSelectionStates()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("pinned card");
        var plain = board.AddText("plain card");
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(plain);
            Assert.IsNotNull(container);

            var hoverFill = FindDescendants<Border>(container)
                .Single(candidate => candidate.Name == "CardHoverFillLayer");
            var hoverRing = FindDescendants<Border>(container)
                .Single(candidate => candidate.Name == "CardHoverRingLayer");
            var selectedFill = FindDescendants<Border>(container)
                .Single(candidate => candidate.Name == "CardSelectedFillLayer");
            Assert.AreEqual(DesignTokens.HoverFeedbackMs, FadeAnimation.GetDurationMs(hoverFill));
            Assert.AreEqual(DesignTokens.HoverFeedbackMs, FadeAnimation.GetDurationMs(hoverRing));
            Assert.AreEqual(DesignTokens.FadeVisibilityMs, FadeAnimation.GetDurationMs(selectedFill));

            container.IsSelected = true;
            CompleteLayout(window);

            Assert.IsTrue(selectedFill.HasAnimatedProperties);
            Assert.AreEqual(
                1d,
                selectedFill.GetAnimationBaseValue(UIElement.OpacityProperty));

            FadeAnimation.SetIsActive(hoverFill, true);
            FadeAnimation.SetIsActive(hoverRing, true);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "card-hover-selected.png",
                "FTS_MOTION_TRANSITIONS_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedCard_PinButtonFadesInWithoutPointerHover()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("pinned card");
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(pinned);
            Assert.IsNotNull(container);
            var pin = FindDescendants<Button>(container)
                .Single(candidate => Equals(candidate.CommandParameter, "TogglePin"));

            Assert.IsTrue(pin.HasAnimatedProperties);
            Assert.AreEqual(
                1d,
                pin.GetAnimationBaseValue(UIElement.OpacityProperty));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(180));
            Assert.AreEqual(1d, pin.Opacity, 0.001);
            Assert.IsTrue(pin.IsHitTestVisible);
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
