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

    /// <summary>
    /// 原始 FileDrop 文件清单（#30 r2-M1）：读取端保留未解释的文件列表，解释权归
    /// 目标角色——启动标签/表面按 ∈{*.exe,*.lnk} 分流导入；内容桶表面不消费本载荷
    /// （既有图片/文本判定零变化）。
    /// </summary>
    public sealed record FileList(IReadOnlyList<string> Paths) : ExternalDropPayload;
}
