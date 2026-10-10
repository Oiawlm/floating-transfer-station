using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 测试助手：统一改写板面条目的 CreatedAt（init-only，经快照回合重建）。
/// 供逐卡 24h TTL（1.25.0）到期判定测试构造超龄/边界/未来时间戳卡片。
/// </summary>
internal static class BoardCreatedAtTestHelpers
{
    public static void SetAllItemsCreatedAt(this BoardService board, DateTimeOffset createdAt)
    {
        var snapshot = board.CreateSnapshot();
        board.Restore(new BoardSnapshot
        {
            Items = snapshot.Items
                .Select(item => CloneWithCreatedAt(item, createdAt))
                .ToList()
        });
    }

    public static void SetItemCreatedAt(
        this BoardService board,
        Guid itemId,
        DateTimeOffset createdAt)
    {
        var snapshot = board.CreateSnapshot();
        board.Restore(new BoardSnapshot
        {
            Items = snapshot.Items
                .Select(item => item.Id == itemId ? CloneWithCreatedAt(item, createdAt) : item)
                .ToList()
        });
    }

    public static void AgeAllItems(this BoardService board, DateTimeOffset now, double hoursOld) =>
        board.SetAllItemsCreatedAt(now - TimeSpan.FromHours(hoursOld));

    private static BoardItem CloneWithCreatedAt(BoardItem item, DateTimeOffset createdAt) => new()
    {
        Id = item.Id,
        Kind = item.Kind,
        Category = item.Category,
        Order = item.Order,
        CreatedAt = createdAt,
        Text = item.Text,
        ImageRelativePath = item.ImageRelativePath,
        IsPinned = item.IsPinned,
        ImageAbsolutePath = item.ImageAbsolutePath
    };
}
