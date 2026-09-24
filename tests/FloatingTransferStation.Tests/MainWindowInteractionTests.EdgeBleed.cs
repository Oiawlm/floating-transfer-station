using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    private static MainWindow CreateWindowWithRightEdgeBleed(
        TestDirectory directory,
        Func<Window, double> rightEdgeBleedProvider)
    {
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new NeverReadClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate);
        window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                operationGate),
            rightEdgeBleedProvider: rightEdgeBleedProvider);
        return window;
    }

    [STATestMethod]
    public void ExpandedGeometry_IncludesTheRightEdgeBleedBeyondTheWorkArea()
    {
        using var directory = new TestDirectory();
        var window = CreateWindowWithRightEdgeBleed(
            directory,
            _ => WindowSettings.EdgeBleed);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var visibleWidth = WindowSettings.Default.PanelWidth + WindowSettings.TabWidth;

            Assert.AreEqual(visibleWidth + WindowSettings.EdgeBleed, window.Width, 0.5);
            Assert.AreEqual(work.Right - visibleWidth, window.Left, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width - WindowSettings.EdgeBleed, 0.5);
            Assert.AreEqual(
                work.Right + WindowSettings.EdgeBleed,
                window.Left + window.Width,
                0.5);

            var rail = (Border)window.FindName("CategoryRail");
            Assert.IsNotNull(rail);
            Assert.AreEqual(WindowSettings.TabWidth, rail.ActualWidth, 0.5);
            var railLeftInWindow = rail.TranslatePoint(new Point(), window).X;
            Assert.AreEqual(
                window.Width - WindowSettings.EdgeBleed - WindowSettings.TabWidth,
                railLeftInWindow,
                0.5);
            Assert.AreEqual(
                work.Right,
                window.Left + railLeftInWindow + WindowSettings.TabWidth,
                0.5,
                "The rail must end at the work-area right edge; only the bleed region stays offscreen.");
            Assert.IsTrue(
                window.Left + railLeftInWindow >= work.Left - 0.5,
                "The full 58px rail must stay visible inside the work area.");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CollapsedGeometry_IncludesTheRightEdgeBleedBeyondTheWorkArea()
    {
        using var directory = new TestDirectory();
        var window = CreateWindowWithRightEdgeBleed(
            directory,
            _ => WindowSettings.EdgeBleed);

        try
        {
            window.Show();
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var rowHeight = WindowSettings.Default.WindowHeight / BoardCategoryCatalog.Ordered.Count;

            Assert.AreEqual(rowHeight, window.Height, 0.5);
            Assert.AreEqual(WindowSettings.TabWidth + WindowSettings.EdgeBleed, window.Width, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width - WindowSettings.EdgeBleed, 0.5);
            Assert.AreEqual(
                work.Right + WindowSettings.EdgeBleed,
                window.Left + window.Width,
                0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void WidthResize_KeepsTheVisibleRightEdgeAtTheWorkAreaWithBleed()
    {
        using var directory = new TestDirectory();
        var window = CreateWindowWithRightEdgeBleed(
            directory,
            _ => WindowSettings.EdgeBleed);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            InvokePrivate(
                window,
                "WidthThumb_DragDelta",
                null,
                new DragDeltaEventArgs(-40, 0));
            CompleteLayout(window);

            var normalized = new WindowSettings(
                WindowSettings.Default.PanelWidth + 40,
                WindowSettings.Default.WindowHeight,
                WindowSettings.Default.Top).Normalize(work.Width, work.Height);
            var visibleWidth = normalized.PanelWidth + WindowSettings.TabWidth;
            Assert.AreEqual(visibleWidth + WindowSettings.EdgeBleed, window.Width, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width - WindowSettings.EdgeBleed, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DisplayChange_ReevaluatesTheEdgeBleedAndReappliesPlacement()
    {
        using var directory = new TestDirectory();
        var currentBleed = WindowSettings.EdgeBleed;
        var window = CreateWindowWithRightEdgeBleed(
            directory,
            _ => currentBleed);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;
            var visibleWidth = WindowSettings.Default.PanelWidth + WindowSettings.TabWidth;

            Assert.AreEqual(visibleWidth + WindowSettings.EdgeBleed, window.Width, 0.5);

            currentBleed = 0d;
            InvokePrivate(window, "WndProc", nint.Zero, 0x007E, nint.Zero, nint.Zero, false);
            CompleteLayout(window);

            Assert.AreEqual(visibleWidth, window.Width, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ResetAction_WithBleedKeepsTheVisibleGeometryAtTheWorkArea()
    {
        using var directory = new TestDirectory();
        var window = CreateWindowWithRightEdgeBleed(
            directory,
            _ => WindowSettings.EdgeBleed);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var reset = window.FindName("ResetWindowButton") as Button;
            Assert.IsNotNull(reset);

            reset.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CompleteLayout(window);

            var work = SystemParameters.WorkArea;
            var expected = WindowSettings.Default.ResetToDefault(work.Width, work.Height);
            var visibleWidth = expected.PanelWidth + WindowSettings.TabWidth;
            Assert.AreEqual(visibleWidth + WindowSettings.EdgeBleed, window.Width, 0.5);
            Assert.AreEqual(work.Right, window.Left + window.Width - WindowSettings.EdgeBleed, 0.5);
            Assert.AreEqual(expected.WindowHeight, window.Height, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CollapsedContent_EndsAtTheWorkAreaRightEdge_WhenBleedIsActive()
    {
        using var directory = new TestDirectory();
        var window = CreateWindowWithRightEdgeBleed(
            directory,
            _ => WindowSettings.EdgeBleed);

        try
        {
            window.Show();
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            var handle = window.FindName("CollapsedCategoryHandle") as ContentControl;
            Assert.IsNotNull(handle);
            Assert.AreEqual(WindowSettings.TabWidth, handle.ActualWidth, 0.5);
            var handleLeftInWindow = handle.TranslatePoint(new Point(), window).X;
            var handleRightInWindow =
                handle.TranslatePoint(new Point(handle.ActualWidth, 0), window).X;
            Assert.AreEqual(
                work.Right,
                window.Left + handleRightInWindow,
                0.5,
                "The collapsed handle must end at the work-area right edge; the bleed region stays offscreen.");
            Assert.AreEqual(
                window.Width - WindowSettings.EdgeBleed,
                handleRightInWindow,
                0.5,
                "The content layer must be inset by the bleed so the visible window part carries the layout.");
            Assert.IsTrue(
                window.Left + handleLeftInWindow >= work.Left - 0.5,
                "The full 58px handle must stay visible inside the work area.");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ExpandedContent_EndsAtTheWorkAreaRightEdgeAndKeepsPanelWidth_WhenBleedIsActive()
    {
        using var directory = new TestDirectory();
        var window = CreateWindowWithRightEdgeBleed(
            directory,
            _ => WindowSettings.EdgeBleed);
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            var rail = (Border)window.FindName("CategoryRail");
            Assert.IsNotNull(rail);
            var railRightInWindow = rail.TranslatePoint(new Point(rail.ActualWidth, 0), window).X;
            Assert.AreEqual(
                work.Right,
                window.Left + railRightInWindow,
                0.5,
                "The expanded rail must end at the work-area right edge; the bleed region stays offscreen.");

            var panel = window.FindName("PanelContentHost") as Grid;
            Assert.IsNotNull(panel);
            Assert.AreEqual(
                WindowSettings.Default.PanelWidth,
                panel.ActualWidth,
                0.5,
                "The bleed must be carried by the shell, not by widening the panel column.");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ContentLayout_WithoutBleedMatchesTheWindowAndWorkAreaEdges()
    {
        using var directory = new TestDirectory();
        var window = CreateWindowWithRightEdgeBleed(directory, _ => 0d);

        try
        {
            window.Show();
            CompleteLayout(window);
            var work = SystemParameters.WorkArea;

            var handle = window.FindName("CollapsedCategoryHandle") as ContentControl;
            Assert.IsNotNull(handle);
            var handleRightInWindow =
                handle.TranslatePoint(new Point(handle.ActualWidth, 0), window).X;
            Assert.AreEqual(window.Width, handleRightInWindow, 0.5);
            Assert.AreEqual(work.Right, window.Left + handleRightInWindow, 0.5);

            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            var rail = (Border)window.FindName("CategoryRail");
            Assert.IsNotNull(rail);
            var railRightInWindow = rail.TranslatePoint(new Point(rail.ActualWidth, 0), window).X;
            Assert.AreEqual(window.Width, railRightInWindow, 0.5);
            Assert.AreEqual(work.Right, window.Left + railRightInWindow, 0.5);

            var panel = window.FindName("PanelContentHost") as Grid;
            Assert.IsNotNull(panel);
            Assert.AreEqual(WindowSettings.Default.PanelWidth, panel.ActualWidth, 0.5);
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
