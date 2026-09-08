using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Mac.Services;

/// <summary>UI-thread clipboard polling with one bounded capture in flight and explicit shutdown draining.</summary>
public sealed class ClipboardMonitorService
{
    private readonly Func<Task<IAsyncDataTransfer?>> _readClipboard;
    private readonly Func<BoardCategory> _category;
    private readonly AvaloniaTransferReader _reader;
    private readonly TransferImportService _importer;
    private readonly Action<string> _showStatus;
    private readonly IPasteboardStateReader? _stateReader;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Task<bool> _pending = Task.FromResult(false);
    private long? _lastChangeCount;
    private bool _stopping;

    public ClipboardMonitorService(Func<IClipboard?> clipboard, Func<BoardCategory> category,
        AvaloniaTransferReader reader, TransferImportService importer, Action<string> showStatus,
        IPasteboardStateReader? stateReader = null,
        Func<Task<IAsyncDataTransfer?>>? readClipboard = null)
    {
        _readClipboard = readClipboard ?? (() => clipboard()?.TryGetDataAsync() ?? Task.FromResult<IAsyncDataTransfer?>(null));
        _category = category;
        _reader = reader;
        _importer = importer;
        _showStatus = showStatus;
        _stateReader = stateReader ?? (OperatingSystem.IsMacOS() ? new MacPasteboard() : null);
        _timer.Tick += (_, _) =>
        {
            if (!_stopping && _pending.IsCompleted)
            {
                _pending = CaptureCoreAsync(automatic: true);
            }
        };
    }

    public bool AutomaticCaptureAvailable => _stateReader is not null;

    public void Start()
    {
        _stopping = false;
        if (_stateReader is null)
        {
            return; // Windows preview uses explicit paste; it never records the host clipboard in the background.
        }

        try
        {
            _lastChangeCount = _stateReader.ReadState().ChangeCount;
            _timer.Start();
        }
        catch
        {
            _showStatus("无法启动剪贴板监测；仍可手动粘贴或拖入内容。");
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        _timer.Stop();
        await _pending;
    }

    public Task<bool> CaptureNowAsync()
    {
        if (_stopping)
        {
            return Task.FromResult(false);
        }

        if (_pending.IsCompleted)
        {
            _pending = CaptureCoreAsync(automatic: false);
        }

        return _pending;
    }

    public void SuppressCurrentChange()
    {
        if (_stateReader is not null)
        {
            _lastChangeCount = _stateReader.ReadState().ChangeCount;
        }
    }

    private async Task<bool> CaptureCoreAsync(bool automatic)
    {
        try
        {
            var before = _stateReader?.ReadState();
            if (automatic && (before is null || before.ChangeCount == _lastChangeCount))
            {
                return false;
            }

            _lastChangeCount = before?.ChangeCount;
            if (before?.IsPrivate == true)
            {
                if (!automatic)
                {
                    _showStatus("来源禁止记录这份剪贴板内容，已跳过。");
                }

                return false;
            }

            var target = _category();
            TransferPayload? payload = null;
            var hasNativeImage = before is not null && !before.HasFiles &&
                before.Types.Any(AvaloniaTransferReader.IsEncodedImageFormat) && _stateReader is MacPasteboard;
            if (hasNativeImage)
            {
                payload = ((MacPasteboard)_stateReader!).ReadImages(before!);
            }
            else
            {
                using var data = await _readClipboard();
                if (data is not null)
                {
                    payload = await _reader.ReadAsync(data);
                }
            }

            // A clipboard read may yield while the owner changes. Never persist a mixed/private generation.
            if (before is not null && _stateReader?.ReadState() is { } after &&
                (after.ChangeCount != before.ChangeCount || after.IsPrivate))
            {
                return false;
            }

            if (payload is null)
            {
                if (!automatic)
                {
                    _showStatus("剪贴板中没有可导入的文字或静态图片。");
                }

                return false;
            }

            return await _importer.ImportAsync(payload, target);
        }
        catch
        {
            _showStatus("剪贴板读取失败或内容超出容量限制，请稍后重新复制。");
            return false;
        }
    }
}
