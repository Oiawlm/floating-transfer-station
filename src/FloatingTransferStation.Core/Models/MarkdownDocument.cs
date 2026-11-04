namespace FloatingTransferStation.Models;

/// <summary>
/// 复盘阅读视图的渲染文档模型（实验性，2026-11-03）：与 WPF 无关的中间表示——
/// Markdown 原文经 <c>MarkdownDocumentMapper</c>（Markdig/CommonMark 核心管线）
/// 映射为本模型，UI 侧再映射为只读渲染。支持子集（锁定，见设计契约 #4 增补）：
/// 标题、段落、粗体/斜体、行内代码、围栏与缩进代码块、块引用、有序/无序列表、
/// 分隔线、链接。不在子集内的语法（HTML 块、图片、行内 HTML 等）以 <see cref="Blocks"/>
/// 中的 <c>Raw</c> 节点按原文切片降级呈现——绝不丢失内容，也绝不执行。
/// </summary>
public sealed record MarkdownDocument(IReadOnlyList<MarkdownBlock> Blocks)
{
    public static MarkdownDocument Empty { get; } = new(Array.Empty<MarkdownBlock>());
}

/// <summary>块级节点（一级成员按渲染职责互斥）。</summary>
public abstract record MarkdownBlock
{
    /// <summary>标题（level 1–6）。</summary>
    public sealed record Heading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

    /// <summary>段落（软换行按 Obsidian 阅读惯例映射为换行节点）。</summary>
    public sealed record Paragraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

    /// <summary>代码块（围栏或缩进；Language 为围栏信息串，可为空）。</summary>
    public sealed record Code(string Language, string Content) : MarkdownBlock;

    /// <summary>块引用（含子块，可嵌套）。</summary>
    public sealed record Quote(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

    /// <summary>列表（有序/无序；项内可嵌套子块，含子列表）。</summary>
    public sealed record List(bool IsOrdered, int StartNumber, IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

    /// <summary>分隔线（thematic break）。</summary>
    public sealed record ThematicBreak : MarkdownBlock;

    /// <summary>不支持语法的原文降级：按源文切片原样呈现（安全出口，防内容丢失）。</summary>
    public sealed record Raw(string Content) : MarkdownBlock;
}

/// <summary>列表项（承载项内块，支持嵌套列表/引用等）。</summary>
public sealed record MarkdownListItem(IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>行内节点。</summary>
public abstract record MarkdownInline
{
    /// <summary>普通文本（已含 CommonMark 反斜杠转义解码）。</summary>
    public sealed record Text(string Content) : MarkdownInline;

    /// <summary>强调：<paramref name="Strong"/>=true 为粗体，false 为斜体（嵌套内容递归）。</summary>
    public sealed record Emphasis(bool Strong, IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

    /// <summary>行内代码（内容为反引号内原文）。</summary>
    public sealed record Code(string Content) : MarkdownInline;

    /// <summary>链接（UI 渲染为可点击，仅 http/https 打开默认浏览器；本地离线，不加载远程资源）。</summary>
    public sealed record Link(string Url, string Title, IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

    /// <summary>换行（硬换行与 Obsidian 惯例的软换行共用一个节点；渲染为行终止）。</summary>
    public sealed record LineBreak : MarkdownInline;

    /// <summary>不支持行内语法的原文降级（图片、行内 HTML、未知节点）：按源文切片呈现。</summary>
    public sealed record Raw(string Content) : MarkdownInline;
}
