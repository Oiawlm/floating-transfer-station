using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Mac;

public sealed partial class MainWindow
{
    private readonly Grid _review = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), IsVisible = false };
    private readonly Button _reviewPrevious = new() { Content = "‹", Width = 32, Height = 28 };
    private readonly Button _reviewNext = new() { Content = "›", Width = 32, Height = 28 };
    private readonly ComboBox _reviewDates = new() { MinWidth = 132, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _reviewDateState = new() { Foreground = MacThemeBrushes.Light.SecondaryText, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _reviewEditor = new()
    {
        AcceptsReturn = true,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Background = MacThemeBrushes.Light.Card,
        BorderBrush = MacThemeBrushes.Light.BorderLine,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12),
        FontSize = 14,
        Watermark = "写下今天的复盘……"
    };
    private readonly TextBlock _reviewStatus = new() { FontSize = 12, Foreground = MacThemeBrushes.Light.SecondaryText };
    private readonly DispatcherTimer _reviewSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly SemaphoreSlim _reviewSaveGate = new(1, 1);
    private DateOnly _reviewDate = DateOnly.FromDateTime(DateTime.Now);
    private string _reviewBaseContent = string.Empty;
    private bool _reviewDirty;
    private bool _reviewLoading;
    private bool _reviewDateSelectionUpdating;
    private bool _reviewLoaded;
    private Task? _reviewLoadTask;

    private void BuildReviewSurface()
    {
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 5, Margin = new Thickness(0, 0, 0, 8) };
        controls.Children.Add(_reviewPrevious);
        Grid.SetColumn(_reviewNext, 1);
        controls.Children.Add(_reviewNext);
        Grid.SetColumn(_reviewDates, 2);
        controls.Children.Add(_reviewDates);
        Grid.SetColumn(_reviewDateState, 3);
        controls.Children.Add(_reviewDateState);
        _review.Children.Add(controls);
        Grid.SetRow(_reviewEditor, 1);
        _review.Children.Add(_reviewEditor);
        ScrollViewer.SetVerticalScrollBarVisibility(_reviewEditor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_reviewEditor, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        Grid.SetRow(_reviewStatus, 2);
        _review.Children.Add(_reviewStatus);
        Grid.SetRow(_review, 2);
        _panel.Children.Add(_review);
    }

    internal void ApplyReviewTheme()
    {
        _reviewEditor.Background = _brushes.Card;
        _reviewEditor.BorderBrush = _brushes.BorderLine;
        _reviewDateState.Foreground = _brushes.SecondaryText;
        _reviewStatus.Foreground = _brushes.SecondaryText;
    }

    private void InitializeDailyReviewEditing()
    {
        _reviewSaveTimer.Tick += async (_, _) =>
        {
            _reviewSaveTimer.Stop();
            try
            {
                await SaveReviewAsync();
            }
            catch (Exception)
            {
                _reviewStatus.Text = "复盘自动保存失败。";
            }
        };
        _reviewEditor.TextChanged += (_, _) =>
        {
            if (_reviewLoading || !_review.IsVisible || _closing) return;
            _reviewDirty = true;
            _reviewStatus.Text = "未保存";
            _reviewSaveTimer.Stop();
            _reviewSaveTimer.Start();
        };
        _reviewPrevious.Click += async (_, _) => await SwitchReviewDateAsync(_reviewDate.AddDays(-1));
        _reviewNext.Click += async (_, _) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_reviewDate < today) await SwitchReviewDateAsync(_reviewDate.AddDays(1));
        };
        _reviewDates.SelectionChanged += async (_, _) =>
        {
            if (_reviewDateSelectionUpdating || !_review.IsVisible || _reviewDates.SelectedItem is not DateOnly date || date == _reviewDate) return;
            await SwitchReviewDateAsync(date);
        };
        _dailyReviews.Changed += (_, change) =>
            Dispatcher.UIThread.Post(async () => await HandleDailyReviewChangeAsync(change));
    }

    private bool IsReviewCategoryEnabled() =>
        _settings.ReviewMigrationVersion >= DailyReviewMigration.CurrentVersion;

    private bool IsReviewActive() => IsReviewCategoryEnabled() && _active == DailyReviewMigration.ReviewCategory;

    private void UpdateReviewSurface()
    {
        var isReview = IsReviewActive();
        _review.IsVisible = isReview;
        _list.IsVisible = !isReview;
        foreach (var button in _mutationButtons) button.IsVisible = !isReview;
        if (!isReview)
        {
            _reviewLoaded = false;
            _reviewLoadTask = null;
            _count.Text = $"{_board.Items(_active).Count} 项内容";
            return;
        }

        _count.Text = "按天保存的 Markdown 复盘";
        _dailyReviews.StartWatching();
        if (!_reviewLoaded)
        {
            _reviewLoaded = true;
            _reviewLoadTask = LoadReviewDateAsync(_reviewDate);
        }
    }

    private async Task LoadReviewDateAsync(DateOnly date)
    {
        try
        {
            var document = await _dailyReviews.LoadAsync(date);
            _reviewDate = date;
            _reviewBaseContent = document.Content;
            if (_reviewDirty)
            {
                // Switching back (or a stale load) must not discard unsaved editor input.
                _reviewStatus.Text = "有未保存的修改";
                await RefreshReviewDatesAsync();
                UpdateReviewDateControls();
                return;
            }

            _reviewLoading = true;
            _reviewEditor.Text = document.Content;
            _reviewLoading = false;
            _reviewStatus.Text = document.Exists ? "已加载" : "今天还没有复盘内容。";
            await RefreshReviewDatesAsync();
            UpdateReviewDateControls();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _reviewLoading = false;
            _reviewStatus.Text = "复盘读取失败：" + exception.Message;
        }
    }

    private async Task<bool> SaveReviewAsync()
    {
        if (_closing && !_initialized.Task.IsCompleted) return true;
        if (!_reviewDirty && string.Equals(_reviewEditor.Text ?? string.Empty, _reviewBaseContent, StringComparison.Ordinal)) return true;
        _reviewDirty = true;
        await _reviewSaveGate.WaitAsync();
        try
        {
            if (!_reviewDirty)
            {
                return true;
            }
            var date = _reviewDate;
            var content = _reviewEditor.Text ?? string.Empty;
            _reviewStatus.Text = "正在保存…";
            try
            {
                await _dailyReviews.SaveAsync(date, content);
                if (_reviewDate == date && _reviewEditor.Text == content)
                {
                    var saved = await _dailyReviews.LoadAsync(date);
                    _reviewBaseContent = saved.Content;
                    _reviewDirty = false;
                    _reviewStatus.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
                }
                else
                {
                    // Typing during the save left newer text in the editor; keep saving it.
                    _reviewStatus.Text = "未保存";
                    _reviewSaveTimer.Start();
                }

                await RefreshReviewDatesAsync();
                return true;
            }
            catch (IOException exception)
            {
                _reviewStatus.Text = "复盘保存失败：" + exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                _reviewStatus.Text = "复盘保存失败：" + exception.Message;
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

    private async Task SwitchReviewDateAsync(DateOnly date)
    {
        if (date > DateOnly.FromDateTime(DateTime.Now) || !await FlushReviewAsync())
        {
            UpdateReviewDateControls();
            return;
        }

        await LoadReviewDateAsync(date);
    }

    private async Task RefreshReviewDatesAsync()
    {
        var dates = (await _dailyReviews.ListDatesAsync()).ToList();
        if (!dates.Contains(_reviewDate)) dates.Add(_reviewDate);
        dates.Sort((left, right) => right.CompareTo(left));
        _reviewDateSelectionUpdating = true;
        try
        {
            _reviewDates.ItemsSource = dates;
            _reviewDates.SelectedItem = _reviewDate;
        }
        finally
        {
            _reviewDateSelectionUpdating = false;
        }
    }

    private void UpdateReviewDateControls()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _reviewPrevious.IsEnabled = _reviewDate > DateOnly.MinValue;
        _reviewNext.IsEnabled = _reviewDate < today;
        _reviewDateState.Text = _reviewDate == today ? "今天" : _reviewDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private async Task HandleDailyReviewChangeAsync(DailyReviewFileChangedEventArgs change)
    {
        try
        {
            await RefreshReviewDatesAsync();
            if (!IsReviewActive() || (change.Date is { } date && date != _reviewDate)) return;
            var remote = await _dailyReviews.LoadAsync(_reviewDate);
            if (!_reviewDirty)
            {
                _reviewLoading = true;
                _reviewEditor.Text = remote.Content;
                _reviewLoading = false;
                _reviewBaseContent = remote.Content;
                _reviewStatus.Text = remote.Exists ? "已从文件刷新" : "文件已删除。";
                return;
            }

            var choice = await ShowReviewConflictAsync();
            if (choice == "load")
            {
                _reviewLoading = true;
                _reviewEditor.Text = remote.Content;
                _reviewLoading = false;
                _reviewBaseContent = remote.Content;
                _reviewDirty = false;
                _reviewStatus.Text = "已载入文件版本";
            }
            else if (choice == "merge")
            {
                var merged = DailyReviewMerge.Merge(_reviewBaseContent, _reviewEditor.Text ?? string.Empty, remote.Content);
                _reviewLoading = true;
                _reviewEditor.Text = merged.Content;
                _reviewLoading = false;
                _reviewBaseContent = remote.Content;
                _reviewDirty = merged.Content != remote.Content || merged.HasConflicts;
                _reviewStatus.Text = merged.HasConflicts ? "已合并，请整理冲突后保存" : "已合并，等待保存";
            }
            else
            {
                // Closing the dialog without a choice keeps the local content pending save.
                _reviewStatus.Text = "保留本地内容，等待保存";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _reviewStatus.Text = "复盘刷新失败：" + exception.Message;
        }
    }

    private async Task<string?> ShowReviewConflictAsync()
    {
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window
        {
            Title = "复盘文件冲突",
            Width = 440,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (label, value) in new[] { ("保留本地", "keep"), ("载入文件", "load"), ("合并", "merge") })
        {
            var button = new Button { Content = label, MinWidth = 80 };
            button.Click += (_, _) => { result.TrySetResult(value); dialog.Close(); };
            buttons.Children.Add(button);
        }
        var content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 18,
            Children =
        {
            new TextBlock { Text = "当天复盘文件已在应用外修改，请选择如何处理。", TextWrapping = TextWrapping.Wrap },
            buttons
        }
        };
        dialog.Content = content;
        dialog.Closing += (_, _) => result.TrySetResult(null);
        _ = dialog.ShowDialog(this);
        return await result.Task;
    }
}
