using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void ReviewCategory_ShowsEditorAndKeepsClipboardTargetUnchanged()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        using var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        var captureTarget = new DefaultCaptureCategoryState();
        captureTarget.Set(BoardCategory.Inbox);
        var settings = WindowSettings.Default with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNames = WindowSettings.Default.WithCategoryName(BoardCategory.Reference, "复盘").CategoryNames
        };
        var window = CreateWindow(board, store, settings, captureTarget);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);

            Assert.AreEqual(BoardCategory.Inbox, captureTarget.Current);
            Assert.AreEqual(Visibility.Visible, ((Border)window.FindName("ReviewContentHost")).Visibility);
            Assert.AreEqual(Visibility.Collapsed, ((ListBox)window.FindName("BoardList")).Visibility);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "daily-review-expanded.png",
                "FTS_DAILY_REVIEW_EVIDENCE_DIR");

            var editor = (TextBox)window.FindName("ReviewEditor");
            editor.Text = "今天完成了一个功能";
            var save = window.Dispatcher.InvokeAsync(
                () => (Task)typeof(MainWindow).GetMethod(
                    "FlushReviewAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!).Task.Unwrap();
            PumpDispatcherUntil(window.Dispatcher, save);

            var saved = store.LoadAsync(DateOnly.FromDateTime(DateTime.Now)).GetAwaiter().GetResult();
            Assert.AreEqual("今天完成了一个功能", saved.Content);
        }
        finally
        {
            GetPrivateField<IDailyReviewStore>(window, "_dailyReviews")?.StopWatching();
            CloseWindowWithoutSaving(window);
        }
    }

    private static void LoadReview(MainWindow window)
    {
        InvokePrivateTask(window, "LoadReviewDateAsync", DateOnly.FromDateTime(DateTime.Now));
    }
}
