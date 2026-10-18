using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

public partial class MainWindow
{
    private readonly IDailyReviewStore? _dailyReviews;
    private readonly DispatcherTimer _reviewSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly SemaphoreSlim _reviewSaveGate = new(1, 1);
    private DateOnly _reviewDate = DateOnly.FromDateTime(DateTime.Now);
    private string _reviewBaseContent = string.Empty;
    private bool _reviewDirty;
    private bool _reviewLoading;
    private bool _reviewLoaded;

    private void InitializeDailyReviewEditing()
    {
        _reviewSaveTimer.Tick += ReviewSaveTimer_Tick;
        if (_dailyReviews is not null)
        {
            _dailyReviews.Changed += DailyReviews_Changed;
        }
    }

    private bool IsReviewCategoryEnabled() =>
        _settings.ReviewMigrationVersion >= DailyReviewMigration.CurrentVersion;

    private bool IsReviewActive() =>
        IsReviewCategoryEnabled() &&
        _viewModel.ActivePanel?.Category == DailyReviewMigration.ReviewCategory;

    /// <summary>
    /// 表面三态分发（#30）：按激活标签角色切换 BoardList / ReviewContentHost /
    /// LauncherContentHost 三者互斥可见；批量置顶/删除按钮只属于内容桶表面
    /// （复盘与启动隐藏——启动的排除由瓦片表面结构性承载）。复盘表面额外持有
    /// 文档装载与文件监视生命周期。
    /// </summary>
    private void UpdatePanelSurface()
    {
        var activeCategory = _viewModel.ActivePanel?.Category;
        var isReview = IsReviewActive();
        var isLauncher = activeCategory == QuickLaunchMigration.LauncherCategory;
        ReviewContentHost.Visibility = isReview ? Visibility.Visible : Visibility.Collapsed;
        LauncherContentHost.Visibility = isLauncher ? Visibility.Visible : Visibility.Collapsed;
        BoardList.Visibility = isReview || isLauncher ? Visibility.Collapsed : Visibility.Visible;
        InsertionIndicator.Visibility = isReview || isLauncher ? Visibility.Collapsed : Visibility.Visible;
        if (isReview || isLauncher)
        {
            BatchPinButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateBatchPinButton();
        }
        DeleteContentButton.Visibility = isReview || isLauncher ? Visibility.Collapsed : Visibility.Visible;
        if (isLauncher)
        {
            // 启动表面空态/网格与工具行状态随激活刷新（绑定集合变化另行驱动）；
            // 垃圾桶随多选进入「删除已选 N 项」语义（#30 修订 2026-10-18，Issue #186）。
            UpdateLauncherSurfaceState();
            UpdateLauncherDeleteButtonForSelection();
            DeleteContentButton.Visibility = LauncherSelectedCount() > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            // 切标签：改名会话提交、多选清空；提示条与撤销记录随切标签一起销毁
            // （#30 §三.7；2026-10-18 起「编辑模式自动退出」由前两者承接）。
            CommitLauncherTileRename();
            ClearLauncherSelection();
            DestroyLauncherUndoToast();
        }

        if (!isLauncher)
        {
            // ＋菜单不随宿主隐藏自动关闭，切表面时显式收起（#30 切片 4）。
            LauncherAddMenu.IsOpen = false;
        }

        if (!isReview)
        {
            _reviewLoaded = false;
            return;
        }

        if (_dailyReviews is null)
        {
            ReviewEditor.IsEnabled = false;
            ReviewStatus.Text = "复盘存储不可用。";
            return;
        }

        ReviewEditor.IsEnabled = true;
        _dailyReviews.StartWatching();
        if (!_reviewLoaded)
        {
            _reviewLoaded = true;
            TrackPendingOperation(LoadReviewDateAsync(_reviewDate));
        }
    }

    private async Task LoadReviewDateAsync(DateOnly date)
    {
        if (_dailyReviews is null)
        {
            return;
        }

        try
        {
            var document = await _dailyReviews.LoadAsync(date);
            _reviewDate = date;
            _reviewBaseContent = document.Content;
            if (_reviewDirty)
            {
                // Switching back (or a stale load) must not discard unsaved editor input.
                ReviewStatus.Text = "有未保存的修改";
                UpdateReviewDateControls();
                return;
            }

            _reviewLoading = true;
            ReviewEditor.Text = document.Content;
            _reviewLoading = false;
            ReviewStatus.Text = document.Exists ? "已加载" : "今天还没有复盘内容。";
            UpdateReviewDateControls();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _reviewLoading = false;
            ReviewStatus.Text = "复盘读取失败：" + exception.Message;
        }
    }

    private async Task<bool> SaveReviewAsync()
    {
        if (_dailyReviews is null)
        {
            return true;
        }

        if (!_reviewDirty && string.Equals(ReviewEditor.Text, _reviewBaseContent, StringComparison.Ordinal))
        {
            return true;
        }

        _reviewDirty = true;

        await _reviewSaveGate.WaitAsync();
        try
        {
            if (!_reviewDirty)
            {
                return true;
            }

            var date = _reviewDate;
            var content = ReviewEditor.Text;
            ReviewStatus.Text = "正在保存…";
            try
            {
                await _dailyReviews.SaveAsync(date, content);
                if (_reviewDate == date && ReviewEditor.Text == content)
                {
                    var saved = await _dailyReviews.LoadAsync(date);
                    _reviewBaseContent = saved.Content;
                    _reviewDirty = false;
                    ReviewStatus.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
                }
                else
                {
                    // Typing during the save left newer text in the editor; keep saving it.
                    ReviewStatus.Text = "未保存";
                    _reviewSaveTimer.Start();
                }

                return true;
            }
            catch (IOException exception)
            {
                ReviewStatus.Text = "复盘保存失败：" + exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                ReviewStatus.Text = "复盘保存失败：" + exception.Message;
                return false;
            }
        }
        finally
        {
            _reviewSaveGate.Release();
        }
    }

    private async Task<bool> FlushReviewAsync()
    {
        _reviewSaveTimer.Stop();
        return await SaveReviewAsync();
    }

    private async void ReviewSaveTimer_Tick(object? sender, EventArgs e)
    {
        _reviewSaveTimer.Stop();
        try
        {
            await SaveReviewAsync();
        }
        catch (Exception)
        {
            ReviewStatus.Text = "复盘自动保存失败。";
        }
    }

    private void ReviewEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_reviewLoading || !IsReviewActive() || _isClosing)
        {
            return;
        }

        _reviewDirty = true;
        ReviewStatus.Text = "未保存";
        _reviewSaveTimer.Stop();
        _reviewSaveTimer.Start();
    }

    private async void ReviewPreviousButton_Click(object sender, RoutedEventArgs e) =>
        await SwitchReviewDateAsync(_reviewDate.AddDays(-1));

    private async void ReviewNextButton_Click(object sender, RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_reviewDate < today)
        {
            await SwitchReviewDateAsync(_reviewDate.AddDays(1));
        }
    }

    private async Task SwitchReviewDateAsync(DateOnly date)
    {
        if (_dailyReviews is null || date > DateOnly.FromDateTime(DateTime.Now))
        {
            return;
        }

        if (!await FlushReviewAsync())
        {
            UpdateReviewDateControls();
            return;
        }

        await LoadReviewDateAsync(date);
    }

    private void UpdateReviewDateControls()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        ReviewPreviousButton.IsEnabled = _reviewDate > DateOnly.MinValue;
        ReviewNextButton.IsEnabled = _reviewDate < today;
        ReviewDateState.Text = _reviewDate == today ? "今天" : _reviewDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void DailyReviews_Changed(object? sender, DailyReviewFileChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(async () => await HandleDailyReviewChangeAsync(e)));
    }

    private async Task HandleDailyReviewChangeAsync(DailyReviewFileChangedEventArgs change)
    {
        if (_dailyReviews is null)
        {
            return;
        }

        try
        {
            if (!IsReviewActive() || (change.Date is { } date && date != _reviewDate))
            {
                return;
            }

            var remote = await _dailyReviews.LoadAsync(_reviewDate);
            if (!_reviewDirty)
            {
                _reviewLoading = true;
                ReviewEditor.Text = remote.Content;
                _reviewLoading = false;
                _reviewBaseContent = remote.Content;
                ReviewStatus.Text = remote.Exists ? "已从文件刷新" : "文件已删除。";
                return;
            }

            var choice = MessageBox.Show(
                "当天复盘文件已在应用外修改。\n是：载入文件\n否：保留本地\n取消：合并两边内容",
                "复盘文件冲突",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Information);
            if (choice == MessageBoxResult.Yes)
            {
                _reviewLoading = true;
                ReviewEditor.Text = remote.Content;
                _reviewLoading = false;
                _reviewBaseContent = remote.Content;
                _reviewDirty = false;
                ReviewStatus.Text = "已载入文件版本";
                return;
            }

            if (choice == MessageBoxResult.Cancel)
            {
                var merged = DailyReviewMerge.Merge(_reviewBaseContent, ReviewEditor.Text, remote.Content);
                _reviewLoading = true;
                ReviewEditor.Text = merged.Content;
                _reviewLoading = false;
                _reviewBaseContent = remote.Content;
                _reviewDirty = merged.Content != remote.Content || merged.HasConflicts;
                ReviewStatus.Text = merged.HasConflicts ? "已合并，请整理冲突后保存" : "已合并，等待保存";
            }
            else if (choice == MessageBoxResult.No)
            {
                ReviewStatus.Text = "保留本地内容，等待保存";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ReviewStatus.Text = "复盘刷新失败：" + exception.Message;
        }
    }
}
