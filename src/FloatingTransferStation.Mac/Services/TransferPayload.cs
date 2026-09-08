namespace FloatingTransferStation.Mac.Services;

public abstract record TransferPayload
{
    public sealed record Text(string Value) : TransferPayload;
    public sealed record ImageFiles(IReadOnlyList<string> Paths) : TransferPayload;
    public sealed record ImageCandidates(IReadOnlyList<ReadOnlyMemory<byte>> Candidates) : TransferPayload;
    public sealed record ImageBatch(IReadOnlyList<ImageCandidates> Images) : TransferPayload;
}
