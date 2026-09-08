using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Mac;

public sealed partial class MainWindow
{
    private static readonly DataFormat<string> InternalItems = DataFormat.CreateStringApplicationFormat("FloatingTransferStation.items");
    // Only this live drag session can request moves; external lookalike payloads cannot mutate the board.
    private string? _dragToken;
    private Guid[] _dragIds = [];

    private void WireCardDrag(Border card, BoardItem item)
    {
        PointerPressedEventArgs? pressed = null;
        Point origin = default;
        card.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any()) return;
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            var toggle = (e.KeyModifiers & (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;
            var range = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (toggle || range || !_selection.Ids.Contains(item.Id)) SelectItem(item, toggle, range);
            pressed = e;
            origin = e.GetPosition(card);
            e.Pointer.Capture(card);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        card.PointerReleased += (_, e) => { pressed = null; e.Pointer.Capture(null); };
        card.PointerMoved += async (_, e) =>
        {
            if (pressed is null || _dragging || _closing || _busy) return;
            var delta = e.GetPosition(card) - origin;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8) return;
            var trigger = pressed;
            pressed = null;
            e.Pointer.Capture(null);
            _dragging = true;
            try
            {
                var plan = BatchDragPlanner.Create(_board.Items(_active).ToArray(), _selection.Ids, item.Id);
                _dragIds = plan.Items.Select(i => i.Id).ToArray();
                _dragToken = Guid.NewGuid().ToString("N");
                using var data = await CreateTransferAsync(plan.Items, _dragToken);
                await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy | DragDropEffects.Move);
                // A Move returned by another application never deletes our source cards.
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or KeyNotFoundException)
            {
                ShowStatus("无法拖出这些内容：" + exception.Message);
            }
            finally { _dragging = false; _dragIds = []; _dragToken = null; }
        };
    }

    private async Task<DataTransfer> CreateTransferAsync(IReadOnlyList<BoardItem> items, string? token = null)
    {
        var data = new DataTransfer();
        try
        {
            if (items.All(i => i.Kind == BoardItemKind.Image))
            {
                foreach (var item in items)
                {
                    if (item.ImageAbsolutePath is not { } path || !ManagedImagePath.IsAllowed(_store.ImagesDirectory, path))
                        throw new IOException("图片路径无效。");
                    var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(path))
                        ?? throw new IOException("图片文件不存在。");
                    data.Add(DataTransferItem.CreateFile(file));
                }
            }
            else if (items.Count == 1 && items[0].Kind == BoardItemKind.Text)
            {
                data.Add(DataTransferItem.CreateText(items[0].Text ?? ""));
            }
            else if (token is null)
            {
                throw new InvalidOperationException("请选择一段文字或一组图片。");
            }
            if (token is not null)
            {
                var internalItem = new DataTransferItem();
                internalItem.Set(InternalItems, token);
                data.Add(internalItem);
            }
            return data;
        }
        catch { ((IDisposable)data).Dispose(); throw; }
    }

    private async Task CopySelectionAsync()
    {
        var items = _board.Items(_active).Where(i => _selection.Ids.Contains(i.Id)).ToArray();
        if (items.Length == 0 || Clipboard is null) return;
        try
        {
            var data = await CreateTransferAsync(items);
            try { await Clipboard.SetDataAsync(data); }
            catch { ((IDisposable)data).Dispose(); throw; }
            _monitor.SuppressCurrentChange();
            ShowStatus("已复制选中内容。");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            ShowStatus("复制未完成：" + exception.Message);
        }
    }

    private bool IsOwnDrag(IDataTransfer data) => _dragToken is not null && data.TryGetValue(InternalItems) == _dragToken;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var isOwnDrag = IsOwnDrag(e.DataTransfer);
        var targetIndex = sender is Button ? 0 : FindDropIndex(e);
        e.DragEffects = _closing || _busy || !_initialized.Task.IsCompletedSuccessfully || !_initialized.Task.Result ? DragDropEffects.None
            : isOwnDrag ? (_board.CanMoveMany(_dragIds, _active, targetIndex) ? DragDropEffects.Move : DragDropEffects.None)
            : e.DataTransfer.Formats.Any(f => f == DataFormat.Text || f == DataFormat.File || f == DataFormat.Bitmap || f.Kind == DataFormatKind.Platform)
                ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        await DropAsync(e, _active, FindDropIndex(e));
    }

    private int FindDropIndex(DragEventArgs e)
    {
        var targetIndex = _board.Items(_active).Count;
        if (e.Source is Visual source)
        {
            var row = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
            if (row?.DataContext is BoardItem item)
                targetIndex = _board.Items(_active).IndexOf(item) + (e.GetPosition(row).Y > row.Bounds.Height / 2 ? 1 : 0);
        }
        return targetIndex;
    }

    private async Task DropAsync(DragEventArgs e, BoardCategory category, int index)
    {
        e.Handled = true;
        if (_busy || _closing || !_initialized.Task.IsCompletedSuccessfully || !_initialized.Task.Result) { e.DragEffects = DragDropEffects.None; return; }
        try
        {
            if (IsOwnDrag(e.DataTransfer))
            {
                var ids = _dragIds.ToArray();
                if (!_board.CanMoveMany(ids, category, index)) { e.DragEffects = DragDropEffects.None; return; }
                e.DragEffects = DragDropEffects.Move;
                await RunMutationAsync(async () => await _mutations.MoveManyAsync(ids, category, index));
            }
            else
            {
                // Freeze the native drop data while the native event is still valid.
                var payload = await _reader.ReadAsync(e.DataTransfer);
                if (payload is null) { e.DragEffects = DragDropEffects.None; return; }
                e.DragEffects = DragDropEffects.Copy;
                await _imports.ImportAsync(payload, category);
                SyncSelection();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowStatus("拖入未完成：" + exception.Message);
        }
    }
}
