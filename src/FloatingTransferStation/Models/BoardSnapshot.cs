namespace FloatingTransferStation.Models;

public sealed class BoardSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public List<BoardItem> Items { get; init; } = [];

    public static BoardSnapshot Empty { get; } = new();
}
