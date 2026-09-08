using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using FloatingTransferStation.Mac.Services;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Mac;

public sealed partial class MainWindow
{
    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            if (_beforeLoad is not null) await _beforeLoad;
            _settings = await _store.LoadSettingsAsync();
            _board.Restore(await _store.LoadBoardAsync());
            foreach (var category in BoardCategoryCatalog.Ordered)
                _board.Items(category).CollectionChanged += (_, _) =>
                    _count.Text = $"{_board.Items(_active).Count} 项内容";
            Expand(_active);
            _shell.IsEnabled = true;
            _initialized.TrySetResult(true);
            if (_smokeDirectory is not null) await RunSmokeTestAsync(_smokeDirectory);
            else if (!_closing)
            {
                ShowStatus(OperatingSystem.IsMacOS() ? "复制文字或图片，即可自动收集。" : "Mac 界面的 Windows 预览 · 使用粘贴按钮导入");
                _monitor.Start();
            }
        }
        catch (Exception exception)
        {
            _initialized.TrySetResult(false);
            _shell.IsEnabled = true;
            ShowStatus("启动未完成：" + exception.Message);
            if (_smokeDirectory is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(_smokeDirectory, "smoke-failed.txt"), exception.ToString());
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(1);
            }
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_canClose) return;
        e.Cancel = true;
        _ = CloseSafelyAsync();
    }

    private async Task CloseSafelyAsync()
    {
        if (_closing) return;
        _closing = true;
        _collapseTimer.Stop();
        try
        {
            if (!await _initialized.Task)
            {
                _canClose = true;
                Close();
                return;
            }
            await _monitor.StopAsync();
            RememberTop();
            await _mutations.SaveForShutdownAsync(() => _store.SaveSettingsAsync(_settings));
            _canClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _closing = false;
            ShowStatus("保存失败，窗口保持打开：" + exception.Message);
            if (_smokeDirectory is null) _monitor.Start();
        }
    }

    private async Task RunSmokeTestAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "smoke-complete.json");
        if (File.Exists(marker)) File.Delete(marker);
        await _imports.ImportAsync(new TransferPayload.Text("把灵感放在手边\n文字、图片，随手收集，随时拖出。"), _active);
        await _imports.ImportAsync(new TransferPayload.Text("Mac 与 Windows 共用分类、置顶和原子保存逻辑。"), _active);
        await _imports.ImportAsync(new TransferPayload.Text("⌘ + 单击多选 · Shift + 单击连续选择"), _active);
        var sourceImage = Path.Combine(directory, "sample.png");
        using (var sample = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(640, 280))
        {
            for (var y = 0; y < sample.Height; y++)
                for (var x = 0; x < sample.Width; x++)
                    sample[x, y] = x < 420
                        ? new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)(28 + y / 8), (byte)(93 + x / 7), (byte)(92 + y / 4))
                        : new SixLabors.ImageSharp.PixelFormats.Rgba32(228, (byte)(175 + y / 7), 117);
            await SixLabors.ImageSharp.ImageExtensions.SaveAsPngAsync(sample, sourceImage);
        }
        await _imports.ImportAsync(new TransferPayload.ImageFiles([sourceImage]), _active);
        var order = _board.Items(_active).ToArray();
        _selection.Select(order[0].Id, order.Select(i => i.Id).ToArray(), true, false);
        _selection.Select(order[1].Id, order.Select(i => i.Id).ToArray(), false, true);
        await PinSelectionAsync();
        if (_board.Items(_active).Count(i => i.IsPinned) != 2) throw new InvalidOperationException("Pin command did not persist two selected items.");
        _selection.Clear();
        SyncSelection();
        _rename.Text = "灵感";
        await CommitRenameAsync();
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.A, KeyModifiers = command });
        if (_selection.Ids.Count != order.Length) throw new InvalidOperationException("Select-all keyboard routing failed.");
        RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape });
        if (_selection.Ids.Count != 0) throw new InvalidOperationException("Escape keyboard routing failed.");
        var textItem = order.First(i => i.Kind == BoardItemKind.Text);
        using (var drag = await CreateTransferAsync([textItem], "smoke-drag"))
            if (drag.TryGetText() != textItem.Text) throw new InvalidOperationException("Text drag payload was not preserved.");
        using (var drag = await CreateTransferAsync([order.First(i => i.Kind == BoardItemKind.Image)], "smoke-drag"))
            if (drag.TryGetFiles()?.Count() != 1) throw new InvalidOperationException("File drag payload was not preserved.");
        var saved = await _store.LoadBoardAsync();
        if (saved.Items.Count != order.Length || saved.Items.Count(i => i.IsPinned) != 2)
            throw new InvalidOperationException("Board reload did not match saved state.");
        if ((await _store.LoadSettingsAsync()).CategoryName(_active) != "灵感")
            throw new InvalidOperationException("Category rename was not saved.");
        ShowStatus("已验证：收集、选择、置顶、改名、拖出载荷、保存恢复");
        _list.ScrollIntoView(_board.Items(_active).First());
        await Task.Delay(400);
        SaveWindowImage(Path.Combine(directory, "expanded.png"));
        Collapse();
        await Task.Delay(250);
        SaveWindowImage(Path.Combine(directory, "collapsed.png"));
        if (OperatingSystem.IsMacOS()) await RunNativeClipboardSmokeAsync(directory);
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new
        {
            version = ProductIdentity.Version,
            platform = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            checks = new[] { "text-import", "image-import", "range-selection", "batch-pin", "rename", "keyboard", "text-and-file-drag-payload", "persistence", "expanded-window", "collapsed-window" }
        }));
        await CloseSafelyAsync();
    }

    private void SaveWindowImage(string path)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(Bounds.Width), (int)Math.Ceiling(Bounds.Height)), new Vector(96, 96));
        image.Render(this);
        image.Save(path);
    }
}
