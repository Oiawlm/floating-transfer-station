using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Mac;

public sealed partial class MainWindow
{
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_rename.IsVisible || e.Source is TextBox) return;
        var command = (e.KeyModifiers & (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;
        if (command && e.Key == Key.Q) { e.Handled = true; await CloseSafelyAsync(); }
        else if (!_expanded) return;
        else if (e.Key == Key.F2) { e.Handled = true; BeginRename(); }
        else if (e.Key == Key.Escape) { e.Handled = true; _selection.Clear(); SyncSelection(); }
        else if (command && e.Key == Key.A)
        {
            e.Handled = true;
            _selection.SelectAll(_board.Items(_active).Select(i => i.Id));
            SyncSelection();
        }
        else if (command && e.Key == Key.P) { e.Handled = true; await PinSelectionAsync(); }
        else if (command && e.Key == Key.V) { e.Handled = true; await CaptureAsync(); }
        else if (command && e.Key == Key.C) { e.Handled = true; await CopySelectionAsync(); }
        else if (e.Key is Key.Delete or Key.Back) { e.Handled = true; await DeleteAsync(false); }
    }

    private Task PinSelectionAsync()
    {
        var items = _board.Items(_active).Where(i => _selection.Ids.Contains(i.Id)).ToArray();
        if (items.Length == 0) return Task.CompletedTask;
        return RunMutationAsync(async () => await _mutations.SetPinnedAsync(items.Select(i => i.Id).ToArray(), items.Any(i => !i.IsPinned)));
    }

    private Task DeleteAsync(bool clearWhenEmpty)
    {
        var ids = _selection.Ids.ToArray();
        var category = _active;
        if (ids.Length == 0 && !clearWhenEmpty) return Task.CompletedTask;
        return RunMutationAsync(async () =>
        {
            if (ids.Length > 0) await _mutations.DeleteManyAsync(ids);
            else await _mutations.ClearCategoryAsync(category);
        });
    }

    private async Task RunMutationAsync(Func<Task> operation)
    {
        if (_busy || _closing || !_initialized.Task.IsCompletedSuccessfully || !_initialized.Task.Result) return;
        _busy = true;
        foreach (var button in _mutationButtons) button.IsEnabled = false;
        try { await operation(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowStatus("操作未完成：" + exception.Message);
        }
        finally
        {
            _busy = false;
            foreach (var button in _mutationButtons) button.IsEnabled = true;
            SyncSelection();
        }
    }

    private async Task CaptureAsync()
    {
        if (_closing || !_initialized.Task.IsCompletedSuccessfully || !_initialized.Task.Result) return;
        try { await _monitor.CaptureNowAsync(); SyncSelection(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowStatus("暂时无法读取剪贴板：" + exception.Message);
        }
    }

    private void BeginRename()
    {
        if (_busy || _closing || !_initialized.Task.IsCompletedSuccessfully || !_initialized.Task.Result) return;
        _collapseTimer.Stop();
        _rename.Text = _settings.CategoryName(_active);
        _rename.IsVisible = true;
        _rename.Focus();
        _rename.SelectAll();
    }

    private void CancelRename()
    {
        _rename.IsVisible = false;
        _list.Focus();
    }

    private async Task CommitRenameAsync()
    {
        if (_busy) return;
        var name = _rename.Text ?? "";
        if (!BoardCategoryCatalog.IsValidDisplayName(name))
        {
            ShowStatus("分类名称最多 6 个可见文字单元，请缩短后保存。");
            return;
        }
        var next = _settings.WithCategoryName(_active, name);
        await RunMutationAsync(async () => await _gate.RunAsync(async () =>
        {
            await _store.SaveSettingsAsync(next);
            _settings = next;
            _title.Text = name;
            UpdateTabs();
            CancelRename();
            return true;
        }));
    }
}
