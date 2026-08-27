namespace FloatingTransferStation.Models;

public abstract record ExternalDropPayload
{
    private ExternalDropPayload()
    {
    }

    public sealed record ImageFiles(IReadOnlyList<string> Paths) : ExternalDropPayload;
    public sealed record ImageCandidates(
        IReadOnlyList<ClipboardImageCandidate> Candidates) : ExternalDropPayload;
    public sealed record Text(string Value) : ExternalDropPayload;
}
