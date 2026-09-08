using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public interface IBoardStore
{
    string ImagesDirectory { get; }
    Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default);
    Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default);
    bool TryDeleteImage(string? absolutePath);
}
