using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置保存门控存储替身（记录语义与各设置测试私有的 RecordingSettingsBoardStore
/// 一致）：SaveSettingsAsync 进入即计数并记录快照；HoldNextSave 后在预设门上挂起
/// （模拟磁盘写往返），ReleaseSave 放行、可注入失败异常（保存管线按失败完成）；
/// 未挂门时直接完成。用于确定性断言「保存挂起期间」与「失败回滚」的界面状态。
/// </summary>
internal sealed class GatedSettingsBoardStore(string root) : IBoardStore
{
    private TaskCompletionSource? _hold;
    private Exception? _pendingFailure;

    public int SettingsSaveCount { get; private set; }
    public WindowSettings? LastSettings { get; private set; }
    public string ImagesDirectory { get; } = Path.Combine(root, "images");

    /// <summary>下一次 SaveSettingsAsync 在记录后挂起，直到 ReleaseSave。</summary>
    public void HoldNextSave()
    {
        _pendingFailure = null;
        _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>放行挂起的保存；注入的失败异常在放行后抛出（无挂起时下一次保存直接失败）。</summary>
    public void ReleaseSave(Exception? failure = null)
    {
        _pendingFailure = failure;
        if (_hold is { } hold)
        {
            _hold = null;
            hold.TrySetResult();
        }
    }

    public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new BoardSnapshot());

    public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(WindowSettings.Default);

    public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default)
    {
        SettingsSaveCount++;
        LastSettings = settings;
        return CompleteSaveAsync();
    }

    public bool TryDeleteImage(string? absolutePath) => true;

    private async Task CompleteSaveAsync()
    {
        var hold = _hold;
        if (hold is not null)
        {
            // 不在进入时清空 _hold：放行职责归 ReleaseSave——断言失败（红路径）
            // 时兜底 ReleaseSave 仍能放行已挂起中的保存；放行后重复等待同一
            // 已完成的门也不会再挂起。
            await hold.Task;
        }

        var failure = Interlocked.Exchange(ref _pendingFailure, null);
        if (failure is not null)
        {
            throw failure;
        }
    }
}
