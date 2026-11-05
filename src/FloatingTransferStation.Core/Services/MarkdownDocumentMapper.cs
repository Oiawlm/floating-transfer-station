using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using FloatingTransferStation.Models;
using MarkdownDocument = FloatingTransferStation.Models.MarkdownDocument;

namespace FloatingTransferStation.Services;

/// <summary>
/// Markdown 原文 → <see cref="MarkdownDocument"/> 映射器（记录阅读视图，实验性）。
/// 管线＝CommonMark 核心＋精确源定位（<c>UsePreciseSourceLocation</c>）：不激活任何
/// 扩展，表格/脚注等超集语法不被解析为结构（天然按普通文本呈现＝安全降级），
/// HTML 块、图片、行内 HTML 等不在子集内的节点按源文切片降级（Raw）。软换行与硬换行
/// 一并映射为 <see cref="MarkdownInline.LineBreak"/>（Obsidian 阅读惯例：源文单行
/// 换行即换行——记录内容按行记录，逐行呈现符合记录习惯）。链接保留 URL/标题/子节点，
/// 可点击性（仅 http/https 开默认浏览器）由 UI 侧判定——映射器不掺 UI 语义。
/// </summary>
public static class MarkdownDocumentMapper
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .Build();

    public static MarkdownDocument Map(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return MarkdownDocument.Empty;
        }

        return new MarkdownDocument(MapBlocks(Markdown.Parse(markdown, Pipeline), markdown));
    }

    private static List<MarkdownBlock> MapBlocks(ContainerBlock container, string source)
    {
        var blocks = new List<MarkdownBlock>();
        foreach (var block in container)
        {
            // 链接引用定义不可见（引用已在行内解析为 Link），定义块与其分组容器跳过。
            if (block is LinkReferenceDefinition or LinkReferenceDefinitionGroup)
            {
                continue;
            }

            var mapped = block switch
            {
                HeadingBlock heading => new MarkdownBlock.Heading(
                    heading.Level,
                    MapInlines(heading.Inline, source)),
                ParagraphBlock paragraph => new MarkdownBlock.Paragraph(
                    MapInlines(paragraph.Inline, source)),
                FencedCodeBlock fenced => new MarkdownBlock.Code(
                    fenced.Info ?? string.Empty,
                    ExtractCode(fenced)),
                CodeBlock code => new MarkdownBlock.Code(string.Empty, ExtractCode(code)),
                QuoteBlock quote => new MarkdownBlock.Quote(MapBlocks(quote, source)),
                ListBlock list => MapList(list, source),
                ThematicBreakBlock => new MarkdownBlock.ThematicBreak(),
                // HTML 块与未知块级语法：按原文切片原样呈现（安全降级）。
                _ => new MarkdownBlock.Raw(Slice(block, source))
            };
            blocks.Add(mapped);
        }

        return blocks;
    }

    private static MarkdownBlock MapList(ListBlock list, string source)
    {
        var items = new List<MarkdownListItem>();
        foreach (var item in list.OfType<ListItemBlock>())
        {
            items.Add(new MarkdownListItem(MapBlocks(item, source)));
        }

        return new MarkdownBlock.List(
            list.IsOrdered,
            ParseStartNumber(list.OrderedStart),
            items);
    }

    // OrderedStart 是源文起始序号串（Markdig 以 string 携带）；非法/缺失回落 1
    //（渲染层只取起始值，逐项递增与 CommonMark 序号语义一致）。
    private static int ParseStartNumber(string? orderedStart) =>
        int.TryParse(orderedStart, out var start) && start > 0 ? start : 1;

    private static string ExtractCode(CodeBlock code)
    {
        // StringLineGroup 按行承载围栏/缩进代码内容（已剥围栏与缩进前缀、保留行内
        // 空白），行间以换行连接、无结尾换行——直接作为代码内容。
        return code.Lines.ToString();
    }

    // SourceSpan 的 End 为含端（实测 Markdig 1.5.1：Length = End − Start + 1）。
    private static string Slice(MarkdownObject node, string source)
    {
        var span = node.Span;
        var length = span.End - span.Start + 1;
        return span.Start >= 0 && length >= 0 && span.Start + length <= source.Length
            ? source.Substring(span.Start, length)
            : string.Empty;
    }

    private static List<MarkdownInline> MapInlines(ContainerInline? container, string source)
    {
        var inlines = new List<MarkdownInline>();
        if (container is null)
        {
            return inlines;
        }

        foreach (var inline in container)
        {
            MapInline(inline, source, inlines);
        }

        return inlines;
    }

    private static void MapInline(Inline inline, string source, List<MarkdownInline> result)
    {
        switch (inline)
        {
            case LiteralInline literal:
                result.Add(new MarkdownInline.Text(literal.Content.ToString()));
                break;
            case CodeInline code:
                result.Add(new MarkdownInline.Code(code.Content));
                break;
            case EmphasisInline emphasis:
                result.Add(new MarkdownInline.Emphasis(
                    emphasis.DelimiterCount >= 2,
                    MapInlines(emphasis, source)));
                break;
            case LinkInline { IsImage: true } image:
                // 图片不在子集内（不加载远程资源）：按原文切片降级。
                result.Add(new MarkdownInline.Raw(Slice(image, source)));
                break;
            case LinkInline { Url: null or "" } unresolved:
                // 未解析的引用式链接（如脚注 [^1]）：没有目标地址，按原文降级。
                result.Add(new MarkdownInline.Raw(Slice(unresolved, source)));
                break;
            case LinkInline link:
                result.Add(new MarkdownInline.Link(
                    link.Url ?? string.Empty,
                    link.Title ?? string.Empty,
                    MapInlines(link, source)));
                break;
            case LineBreakInline:
                // 软/硬换行共用节点：阅读视图一律换行呈现（Obsidian 惯例，见类注释）。
                result.Add(new MarkdownInline.LineBreak());
                break;
            case AutolinkInline autolink:
                result.Add(new MarkdownInline.Link(
                    autolink.Url,
                    string.Empty,
                    new List<MarkdownInline> { new MarkdownInline.Text(autolink.Url) }));
                break;
            case HtmlEntityInline entity:
                result.Add(new MarkdownInline.Text(entity.Transcoded.ToString()));
                break;
            case ContainerInline container:
                result.AddRange(MapInlines(container, source));
                break;
            // 行内 HTML 与未知行内节点：原文降级。
            default:
                result.Add(new MarkdownInline.Raw(Slice(inline, source)));
                break;
        }
    }
}
