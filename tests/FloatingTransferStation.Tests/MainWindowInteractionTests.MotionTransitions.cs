using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
            var card = FindDescendants<Border>(container)
                .Single(candidate => ReferenceEquals(
                    candidate.Style,
                    window.FindResource("CardContainerStyle")));
            LiftAnimation.SetIsActive(card, true);
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

    [STATestMethod]
    public void NewItems_PlayStaggeredEntranceAnimations()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var existing = board.AddText("existing card");
        var window = CreateWindow(directory, board);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            var first = board.AddText("新条目一");
            var second = board.AddText("新条目二");
            CompleteLayout(window);

            var existingContainer = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(existing);
            var firstContainer = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(first);
            var secondContainer = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(second);
            Assert.IsNotNull(existingContainer);
            Assert.IsNotNull(firstContainer);
            Assert.IsNotNull(secondContainer);
            Assert.IsFalse(existingContainer.HasAnimatedProperties);
            Assert.IsTrue(firstContainer.HasAnimatedProperties);
            Assert.IsTrue(secondContainer.HasAnimatedProperties);
            Assert.AreEqual(
                1d,
                firstContainer.GetAnimationBaseValue(UIElement.OpacityProperty));
            Assert.AreEqual(
                1d,
                secondContainer.GetAnimationBaseValue(UIElement.OpacityProperty));
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "card-entrance-stagger.png",
                "FTS_MOTION_TRANSITIONS_EVIDENCE_DIR");

            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(1d, firstContainer.Opacity, 0.001);
            Assert.AreEqual(1d, secondContainer.Opacity, 0.001);
            Assert.AreEqual(0d, ((TranslateTransform)firstContainer.RenderTransform!).Y, 0.001);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void NewItems_AppearImmediatelyWhenSystemAnimationsAreDisabled()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var window = CreateWindow(directory, board);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");

            var added = board.AddText("新条目");
            CompleteLayout(window);

            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(added);
            Assert.IsNotNull(container);
            Assert.IsFalse(container.HasAnimatedProperties);
            Assert.AreEqual(1d, container.Opacity);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void PinnedMove_ReplaysCardEntranceAnimation()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var plain = board.AddText("plain card");
        board.AddText("another card");
        var window = CreateWindow(directory, board);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var containerBefore =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(plain);

            board.SetPinnedMany([plain.Id], true);
            CompleteLayout(window);

            var container =
                (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(plain);
            Assert.IsNotNull(container);
            Assert.IsTrue(container.HasAnimatedProperties);
            if (ReferenceEquals(container, containerBefore))
            {
                Assert.AreEqual(
                    1d,
                    container.GetAnimationBaseValue(UIElement.OpacityProperty));
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DeleteCardFade_PlaysAlongsideThePipelineAndCompletes()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("delete me");
        var store = new RecordingBoardStore(directory.Root) { SaveDelay = TimeSpan.FromMilliseconds(400) };
        var window = CreateWindow(board, store, WindowSettings.Default);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            var method = typeof(MainWindow).GetMethod(
                "DeleteContentAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            var task = (Task)method.Invoke(window, [new[] { item.Id }, BoardCategory.Inbox, false])!;
            Assert.IsTrue(container.HasAnimatedProperties);
            Assert.AreEqual(
                0d,
                container.GetAnimationBaseValue(UIElement.OpacityProperty));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(180));
            Assert.AreEqual(0d, container.Opacity, 0.001);

            PumpDispatcherUntil(window.Dispatcher, task);
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
            Assert.AreEqual(1, store.SaveCount);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DeleteCardFade_IsSkippedWhenSystemAnimationsAreDisabled()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("delete me");
        var store = new RecordingBoardStore(directory.Root) { SaveDelay = TimeSpan.FromMilliseconds(400) };
        var window = CreateWindow(board, store, WindowSettings.Default);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            var method = typeof(MainWindow).GetMethod(
                "DeleteContentAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            var task = (Task)method.Invoke(window, [new[] { item.Id }, BoardCategory.Inbox, false])!;
            Assert.IsFalse(container.HasAnimatedProperties);
            Assert.AreEqual(1d, container.Opacity);

            PumpDispatcherUntil(window.Dispatcher, task);
            Assert.AreEqual(0, board.Items(BoardCategory.Inbox).Count);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DeleteCardFade_RestoresCardsWhenSaveFailsAndRollsBack()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("delete me");
        var store = new RecordingBoardStore(directory.Root)
        {
            SaveFailure = new IOException("Injected failure."),
        };
        var window = CreateWindow(board, store, WindowSettings.Default);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(container);
            var method = typeof(MainWindow).GetMethod(
                "DeleteContentAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            var task = (Task)method.Invoke(window, [new[] { item.Id }, BoardCategory.Inbox, false])!;
            PumpDispatcherUntil(window.Dispatcher, task);
            CompleteLayout(window);

            Assert.AreEqual(1, board.Items(BoardCategory.Inbox).Count);
            var restored = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(item);
            Assert.IsNotNull(restored);
            // 回滚重插入的卡片播放入场动画；结束后必须回到完全不透明。
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(500));
            Assert.AreEqual(1d, restored.Opacity, 0.001);
        }
        finally
        {
            store.SaveFailure = null;
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Cards_CarrySharedFrozenElevationShadow()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("elevated card");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var effect = window.FindResource("CardShadowEffect");
            Assert.IsInstanceOfType(effect, typeof(DropShadowEffect));
            Assert.IsTrue(((DropShadowEffect)effect).IsFrozen);

            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(
                board.Items(BoardCategory.Inbox).Single());
            Assert.IsNotNull(container);
            var card = FindDescendants<Border>(container)
                .Single(candidate => ReferenceEquals(
                    candidate.Style,
                    window.FindResource("CardContainerStyle")));
            Assert.AreSame(effect, card.Effect);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardLift_AnimatesOnHoverActivation()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("lifted card");
        var window = CreateWindow(directory, board);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = true;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(
                board.Items(BoardCategory.Inbox).Single());
            Assert.IsNotNull(container);
            var card = FindDescendants<Border>(container)
                .Single(candidate => ReferenceEquals(
                    candidate.Style,
                    window.FindResource("CardContainerStyle")));

            LiftAnimation.SetIsActive(card, true);
            Assert.IsInstanceOfType(card.RenderTransform, typeof(TranslateTransform));
            var transform = (TranslateTransform)card.RenderTransform;
            Assert.IsTrue(transform.HasAnimatedProperties);
            Assert.AreEqual(
                LiftAnimation.LiftedOffsetPx,
                transform.GetAnimationBaseValue(TranslateTransform.YProperty));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(LiftAnimation.LiftedOffsetPx, transform.Y, 0.001);

            LiftAnimation.SetIsActive(card, false);
            Assert.AreEqual(
                0d,
                transform.GetAnimationBaseValue(TranslateTransform.YProperty));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            Assert.AreEqual(0d, transform.Y, 0.001);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ScrollThumb_HoverLayerWidensTheThumbVisually()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        AddScrollableItems(board, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var scrollBar = FindDescendants<ScrollBar>(list)
                .Single(candidate => candidate.Orientation == Orientation.Vertical &&
                    candidate.Visibility == Visibility.Visible);
            var thumb = FindDescendants<Thumb>(scrollBar).Single();
            var hover = FindDescendants<Border>(thumb)
                .Single(candidate => candidate.Name == "ThumbHoverLayer");
            Assert.AreEqual(6d, hover.Width);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void Icons_UseFluentGeometryWithUniformStrokeWeights()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("pinned card");
        board.SetPinnedMany([pinned.Id], true);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(pinned);
            InvokePrivate(window, "SetHeaderActionsVisible", true);
            CompleteLayout(window);
            var container = (ListBoxItem?)list.ItemContainerGenerator.ContainerFromItem(pinned);
            Assert.IsNotNull(container);

            // Fluent pin_20_regular 几何（20×20 外轮廓 + 内轮廓）。
            var pinPath = FindDescendant<System.Windows.Shapes.Path>(
                FindDescendants<Button>(container)
                    .Single(candidate => Equals(candidate.CommandParameter, "TogglePin")));
            Assert.IsNotNull(pinPath);
            StringAssert.StartsWith(pinPath.Data.ToString(), "M10.1221");

            var batchPinPath = FindDescendant<System.Windows.Shapes.Path>(
                (Button)window.FindName("BatchPinButton"));
            Assert.IsNotNull(batchPinPath);
            StringAssert.StartsWith(batchPinPath.Data.ToString(), "M10.1221");

            // Fluent delete_20_regular 几何（盖沿、外圈、桶身与两条竖线）。
            var trashPath = FindDescendant<System.Windows.Shapes.Path>(
                (Button)window.FindName("DeleteContentButton"));
            Assert.IsNotNull(trashPath);
            StringAssert.StartsWith(trashPath.Data.ToString(), "M8.5,4");

            var resetPath = FindDescendant<System.Windows.Shapes.Path>(
                (Button)window.FindName("ResetWindowButton"));
            Assert.IsNotNull(resetPath);
            Assert.AreEqual(1.5d, resetPath.StrokeThickness);

            var selection = FindDescendants<Button>(container)
                .Single(candidate => Equals(candidate.CommandParameter, "ToggleSelection"));
            var checkPath = FindDescendant<System.Windows.Shapes.Path>(selection);
            Assert.IsNotNull(checkPath);
            Assert.AreEqual(1.5d, checkPath.StrokeThickness);
            var surface = FindDescendants<Border>(selection)
                .Single(candidate => candidate.Name == "SelectionSurface");
            Assert.AreEqual(new Thickness(1.5), surface.BorderThickness);

            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "unified-icons.png",
                "FTS_MOTION_TRANSITIONS_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
