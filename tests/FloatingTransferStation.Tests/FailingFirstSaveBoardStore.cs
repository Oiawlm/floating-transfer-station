using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

internal sealed class FailingFirstSaveBoardStore(string root, Exception? firstFailure = null) : IBoardStore
{
    private readonly TaskCompletionSource _failFirstSave = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _saveCount;

    public TaskCompletionSource FirstSaveStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource SuccessfulSaveCompleted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    public BoardSnapshot? LastPersistedSnapshot { get; private set; }
    public string ImagesDirectory { get; } = Path.Combine(root, "images");

    public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new BoardSnapshot());

    public async Task SaveBoardAsync(
        BoardSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _saveCount) == 1)
        {
            FirstSaveStarted.TrySetResult();
            await _failFirstSave.Task.WaitAsync(cancellationToken);
            throw firstFailure ?? new IOException("Injected first-save failure.");
        }

        LastPersistedSnapshot = snapshot;
        SuccessfulSaveCompleted.TrySetResult();
    }

    public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(WindowSettings.Default);

    public Task SaveSettingsAsync(
        WindowSettings settings,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public bool TryDeleteImage(string? absolutePath) => true;

    public void FailFirstSave() => _failFirstSave.TrySetResult();
}
