using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 复盘阅读视图映射器的语法子集矩阵（契约 #4 增补锁定面）：支持的语法逐类断言
/// 映射结果；超集/未知语法断言按原文降级（Raw/字面文本）——不丢失内容、不抛错。
/// </summary>
[TestClass]
public sealed class MarkdownDocumentMapperTests
{
    [TestMethod]
    public void Map_NullOrWhitespace_ReturnsEmptyDocument()
    {
        Assert.AreEqual(0, MarkdownDocumentMapper.Map("   \n\t ").Blocks.Count);
        Assert.AreEqual(0, MarkdownDocumentMapper.Map(string.Empty).Blocks.Count);
    }

    [TestMethod]
    public void Map_Headings_CarryLevelsOneToSix()
    {
        var document = MarkdownDocumentMapper.Map(
            "# 一级\n\n## 二级\n\n### 三级\n\n#### 四级\n\n##### 五级\n\n###### 六级");

        var levels = document.Blocks.OfType<MarkdownBlock.Heading>().Select(heading => heading.Level).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, levels);
    }

    [TestMethod]
    public void Map_ParagraphPlainText_MapsSingleTextNode()
    {
        var document = MarkdownDocumentMapper.Map("一段普通文本。");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var text = (MarkdownInline.Text)paragraph.Inlines.Single();
        Assert.AreEqual("一段普通文本。", text.Content);
    }

    [TestMethod]
    public void Map_Emphasis_DistinguishesBoldAndItalic()
    {
        var document = MarkdownDocumentMapper.Map("前 **加粗** 中 *斜体* 后");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var emphasis = paragraph.Inlines.OfType<MarkdownInline.Emphasis>().ToArray();
        Assert.AreEqual(2, emphasis.Length);
        Assert.IsTrue(emphasis[0].Strong, "** 必须映射为粗体（Strong）。");
        Assert.IsFalse(emphasis[1].Strong, "* 必须映射为斜体。");
        Assert.AreEqual(
            "加粗",
            ((MarkdownInline.Text)emphasis[0].Inlines.Single()).Content);
        Assert.AreEqual(
            "斜体",
            ((MarkdownInline.Text)emphasis[1].Inlines.Single()).Content);
    }

    [TestMethod]
    public void Map_NestedEmphasis_RecursesInsideOut()
    {
        var document = MarkdownDocumentMapper.Map("**加粗里有 *斜体* 收尾**");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var bold = (MarkdownInline.Emphasis)paragraph.Inlines.OfType<MarkdownInline.Emphasis>().Single();
        Assert.IsTrue(bold.Strong);
        var italic = (MarkdownInline.Emphasis)bold.Inlines.OfType<MarkdownInline.Emphasis>().Single();
        Assert.IsFalse(italic.Strong);
        Assert.AreEqual("斜体", ((MarkdownInline.Text)italic.Inlines.Single()).Content);
    }

    [TestMethod]
    public void Map_InlineCode_KeepsVerbatimContent()
    {
        var document = MarkdownDocumentMapper.Map("行内 `a  b_c` 码");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var code = paragraph.Inlines.OfType<MarkdownInline.Code>().Single();
        Assert.AreEqual("a  b_c", code.Content);
    }

    [TestMethod]
    public void Map_FencedCodeBlock_KeepsLanguageAndVerbatimLines()
    {
        var document = MarkdownDocumentMapper.Map("```csharp\n第一行\n  缩进与  空格 \n第三行\n```");

        var code = (MarkdownBlock.Code)document.Blocks.Single();
        Assert.AreEqual("csharp", code.Language);
        Assert.AreEqual("第一行\n  缩进与  空格 \n第三行", code.Content);
    }

    [TestMethod]
    public void Map_IndentedCodeBlock_MapsWithoutLanguage()
    {
        var document = MarkdownDocumentMapper.Map("    缩进代码行");

        var code = (MarkdownBlock.Code)document.Blocks.Single();
        Assert.AreEqual(string.Empty, code.Language);
        Assert.AreEqual("缩进代码行", code.Content);
    }

    [TestMethod]
    public void Map_BlockQuote_ContainsChildParagraphsAndNests()
    {
        var document = MarkdownDocumentMapper.Map("> 外层\n> > 内层");

        var quote = (MarkdownBlock.Quote)document.Blocks.Single();
        var outer = (MarkdownBlock.Paragraph)quote.Blocks[0];
        Assert.AreEqual("外层", ((MarkdownInline.Text)outer.Inlines.Single()).Content);
        var nested = (MarkdownBlock.Quote)quote.Blocks[1];
        var inner = (MarkdownBlock.Paragraph)nested.Blocks.Single();
        Assert.AreEqual("内层", ((MarkdownInline.Text)inner.Inlines.Single()).Content);
    }

    [TestMethod]
    public void Map_UnorderedList_CarriesItemsWithNestedList()
    {
        var document = MarkdownDocumentMapper.Map("- 项目一\n- 项目二\n  - 嵌套项");

        var list = (MarkdownBlock.List)document.Blocks.Single();
        Assert.IsFalse(list.IsOrdered);
        Assert.AreEqual(1, list.StartNumber);
        Assert.AreEqual(2, list.Items.Count);
        var nested = (MarkdownBlock.List)list.Items[1].Blocks.OfType<MarkdownBlock.List>().Single();
        Assert.IsFalse(nested.IsOrdered);
        var nestedItem = (MarkdownBlock.Paragraph)nested.Items[0].Blocks[0];
        Assert.AreEqual("嵌套项", ((MarkdownInline.Text)nestedItem.Inlines.Single()).Content);
    }

    [TestMethod]
    public void Map_OrderedList_CarriesStartNumber()
    {
        var document = MarkdownDocumentMapper.Map("3. 第三\n4. 第四");

        var list = (MarkdownBlock.List)document.Blocks.Single();
        Assert.IsTrue(list.IsOrdered);
        Assert.AreEqual(3, list.StartNumber, "有序列表起始序号（3.）必须进入模型。");
        Assert.AreEqual(2, list.Items.Count);
    }

    [TestMethod]
    public void Map_ThematicBreak_VariantsAllMap()
    {
        foreach (var marker in new[] { "---", "***", "___" })
        {
            var document = MarkdownDocumentMapper.Map(marker);
            Assert.IsInstanceOfType<MarkdownBlock.ThematicBreak>(document.Blocks.Single(), $"分隔线 {marker} 必须映射为 ThematicBreak。");
        }
    }

    [TestMethod]
    public void Map_Link_CarriesUrlTitleAndChildren()
    {
        var document = MarkdownDocumentMapper.Map("[文字 **加粗**](https://example.com/a?b=1 \"提示\")");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var link = paragraph.Inlines.OfType<MarkdownInline.Link>().Single();
        Assert.AreEqual("https://example.com/a?b=1", link.Url);
        Assert.AreEqual("提示", link.Title);
        Assert.AreEqual("文字 ", ((MarkdownInline.Text)link.Inlines[0]).Content);
        Assert.IsTrue(((MarkdownInline.Emphasis)link.Inlines[1]).Strong);
    }

    [TestMethod]
    public void Map_Autolink_MapsToLinkWithUrlText()
    {
        var document = MarkdownDocumentMapper.Map("见 <https://auto.example.com/x>");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var link = paragraph.Inlines.OfType<MarkdownInline.Link>().Single();
        Assert.AreEqual("https://auto.example.com/x", link.Url);
        Assert.AreEqual(
            "https://auto.example.com/x",
            ((MarkdownInline.Text)link.Inlines.Single()).Content);
    }

    [TestMethod]
    public void Map_LineBreaks_SoftAndHardBothMapToLineBreakNode()
    {
        // Obsidian 阅读惯例：软换行也按换行呈现（复盘按行记录）。
        var document = MarkdownDocumentMapper.Map("第一行\n第二行  \n第三行");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var breaks = paragraph.Inlines.OfType<MarkdownInline.LineBreak>().Count();
        Assert.AreEqual(2, breaks, "软换行与硬换行（行尾两空格）都必须映射为换行节点。");
    }

    [TestMethod]
    public void Map_EntityAndBackslashEscape_DecodeToPlainText()
    {
        var document = MarkdownDocumentMapper.Map("实体 &amp; 转义 \\*非斜体\\*");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var joined = string.Concat(paragraph.Inlines.OfType<MarkdownInline.Text>().Select(text => text.Content));
        Assert.AreEqual("实体 & 转义 *非斜体*", joined);
    }

    [TestMethod]
    public void Map_Image_DegradesToRawSourceSlice()
    {
        var document = MarkdownDocumentMapper.Map("![替代文字](https://example.com/pic.png)");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var raw = paragraph.Inlines.OfType<MarkdownInline.Raw>().Single();
        Assert.AreEqual("![替代文字](https://example.com/pic.png)", raw.Content);
    }

    [TestMethod]
    public void Map_HtmlBlock_DegradesToRawSourceVerbatim()
    {
        const string html = "<div>\n原始 HTML 块\n</div>";
        var document = MarkdownDocumentMapper.Map(html);

        var raw = (MarkdownBlock.Raw)document.Blocks.Single();
        Assert.AreEqual(html, raw.Content);
    }

    [TestMethod]
    public void Map_InlineHtml_DegradesToRawSourceSlice()
    {
        // CommonMark 把行内 HTML 拆成首尾两个标签节点，中间文本保留——
        // 三段合起来＝原文逐字呈现。
        var document = MarkdownDocumentMapper.Map("前 <b>加粗</b> 后");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var raws = paragraph.Inlines.OfType<MarkdownInline.Raw>().Select(raw => raw.Content).ToArray();
        CollectionAssert.AreEqual(new[] { "<b>", "</b>" }, raws);
        Assert.AreEqual(
            "加粗",
            ((MarkdownInline.Text)paragraph.Inlines[2]).Content);
    }

    [TestMethod]
    public void Map_TableSyntax_NotSupported_RendersAsLiteralParagraphText()
    {
        // 表格不在锁定子集内：CommonMark 核心管线不激活表格扩展，管道符按
        // 字面文本呈现（安全降级＝按原文呈现，不丢内容）。
        var document = MarkdownDocumentMapper.Map("| 列一 | 列二 |\n|---|---|\n| 甲 | 乙 |");

        var paragraph = (MarkdownBlock.Paragraph)document.Blocks.Single();
        var lines = paragraph.Inlines
            .Aggregate(
                new List<string>(),
                (lines, inline) =>
                {
                    if (inline is MarkdownInline.Text text)
                    {
                        lines.Add(text.Content);
                    }

                    return lines;
                });
        CollectionAssert.AreEqual(
            new[] { "| 列一 | 列二 |", "|---|---|", "| 甲 | 乙 |" },
            lines);
    }

    [TestMethod]
    public void Map_FootnoteSyntax_NotSupported_RendersAsLiteralText()
    {
        // 脚注不在子集内且无扩展：未定义的 [^1] 保持字面文本；带定义的
        // "[^1]: 内容" 是合法链接引用定义（定义块不可见），正文 [^1] 按引用链接
        // 解析（CommonMark 核心语义），两处都不抛错、不丢内容。
        var unresolved = MarkdownDocumentMapper.Map("脚注[^1] 未定义");
        var paragraph = (MarkdownBlock.Paragraph)unresolved.Blocks.Single();
        Assert.AreEqual(
            "脚注[^1] 未定义",
            string.Concat(paragraph.Inlines.OfType<MarkdownInline.Text>().Select(text => text.Content)));

        var resolved = MarkdownDocumentMapper.Map("见[^1]\n\n[^1]: 定义内容");
        var body = (MarkdownBlock.Paragraph)resolved.Blocks.Single();
        Assert.AreEqual(1, body.Inlines.OfType<MarkdownInline.Link>().Count());
    }

    [TestMethod]
    public void Map_MixedDocument_PreservesBlockOrder()
    {
        var document = MarkdownDocumentMapper.Map("# 标题\n\n段落\n\n---\n\n- 项\n\n> 引用\n\n```\n码\n```");

        var kinds = document.Blocks.Select(block => block.GetType().Name).ToArray();
        CollectionAssert.AreEqual(
            new[] { "Heading", "Paragraph", "ThematicBreak", "List", "Quote", "Code" },
            kinds);
    }
}
