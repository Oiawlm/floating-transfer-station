using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 记录 Markdown 阅读视图（实验性，默认关闭；批次 2026-11-03 W4）。实验开关开启后
/// 记录工具栏出现「阅读｜编辑」分段切换：编辑态＝现状 TextBox（保存/防抖/合并/Esc
/// 契约 #4/#20 零改变——阅读态只是另一个只读呈现面，编辑器仍是唯一事实源）；阅读态
/// ＝Core 文档模型映射的只读 FlowDocument（排版全走设计 token 与主题语义键）。
/// 模式记忆实现（自选）：会话内记忆（切日期/切标签/开关重开实验开关均保持上次模式），
/// 重启回落编辑态——不新增偏好字段，避免为实验性功能扩大持久化面。
/// </summary>
public partial class MainWindow
{
    private bool _reviewReadingMode;

    /// <summary>实验开关下发（构造期＋ApplyPreferences 唯一管线）：重估切换可见性
    /// 与呈现面；关闭时与现状逐像素一致（切换控件与阅读视图均 Collapsed）。</summary>
    private void ApplyReviewMarkdownViewPreference()
    {
        if (IsReviewActive())
        {
            UpdateReviewModeControls();
        }
    }

    /// <summary>模式切换单点：由分段 RadioButton 的 Checked 驱动（解析期初检跳过）。</summary>
    private void ReviewReadingModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (ReviewReadingView is null || _isClosing)
        {
            return;
        }

        _reviewReadingMode = true;
        UpdateReviewModeControls();
    }

    private void ReviewEditModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (ReviewReadingView is null || _isClosing)
        {
            return;
        }

        _reviewReadingMode = false;
        UpdateReviewModeControls();
    }

    /// <summary>
    /// 表面单点（UpdatePanelSurface 的记录臂在此重估）：切换控件可见＝实验开关开∧
    /// 记录激活；阅读视图可见＝其上再∧阅读态；否则编辑器独占。从阅读态离开时若
    /// 编辑器持有焦点，沿 Esc 契约（#20）同链释放焦点——面板去留给既有焦点链清算。
    /// </summary>
    private void UpdateReviewModeControls()
    {
        var enabled = _preferences.ReviewMarkdownViewEnabled;
        var reading = enabled && _reviewReadingMode && IsReviewActive();
        ReviewModeSwitch.Visibility = enabled && IsReviewActive() ? Visibility.Visible : Visibility.Collapsed;
        ReviewReadingView.Visibility = reading ? Visibility.Visible : Visibility.Collapsed;
        ReviewEditor.Visibility = reading ? Visibility.Collapsed : Visibility.Visible;
        if (reading)
        {
            // 呈现面换为阅读视图时把编辑器焦点按 #20 同链交还宿主窗（不引入新的
            // 焦点去向）；阅读视图不承载编辑保持原因。
            if (Keyboard.FocusedElement == ReviewEditor)
            {
                FocusManager.SetFocusedElement(this, null);
                Focus();
            }

            RefreshReviewReadingView();
        }
        else
        {
            ReviewReadingView.Document = null;
        }
    }

    /// <summary>
    /// 阅读视图内容刷新单点：编辑器文本（唯一事实源）→ Core 映射 → FlowDocument。
    /// 由模式切换、日期装载、外部变更/合并四处驱动（与 UpdateReviewEmptyState 同
    /// 挂点族）；阅读态未激活时为空操作（Document 保持 null，不浪费渲染）。
    /// </summary>
    private void RefreshReviewReadingView()
    {
        if (ReviewReadingView.Visibility != Visibility.Visible)
        {
            return;
        }

        ReviewReadingView.Document = ReadingViewRenderer.Render(
            MarkdownDocumentMapper.Map(ReviewEditor.Text));
    }

    /// <summary>模型 → FlowDocument 渲染映射（排版全走设计 token 与主题语义键，
    /// DynamicResource 引用随浅深两主题整本替换即时切换；链接仅 http/https 可点击，
    /// 经 UseShellExecute 打开默认浏览器——纯本地离线，不加载任何远程资源）。</summary>
    private static class ReadingViewRenderer
    {
        public static FlowDocument Render(MarkdownDocument document)
        {
            var flow = new FlowDocument
            {
                PagePadding = new Thickness(0),
                TextAlignment = TextAlignment.Left,
                // 正文字号唯一数值来源＝DesignTokens（卡片正文同源，不设第二份 14）。
                FontSize = DesignTokens.CardTextFontSize
            };
            flow.SetResourceReference(TextElement.FontFamilyProperty, "InterfaceFontFamily");
            flow.SetResourceReference(TextElement.ForegroundProperty, "PrimaryTextBrush");
            foreach (var block in document.Blocks)
            {
                flow.Blocks.Add(RenderBlock(block));
            }

            return flow;
        }

        private static Block RenderBlock(MarkdownBlock block) => block switch
        {
            MarkdownBlock.Heading heading => RenderHeading(heading),
            MarkdownBlock.Paragraph paragraph => RenderParagraph(paragraph.Inlines),
            MarkdownBlock.Code code => RenderCodeBlock(code),
            MarkdownBlock.Quote quote => RenderQuote(quote),
            MarkdownBlock.List list => RenderList(list),
            MarkdownBlock.ThematicBreak => RenderThematicBreak(),
            // 不支持语法的原文降级：按原文呈现为普通段落（不丢内容）。
            MarkdownBlock.Raw raw => RenderPlainText(raw.Content),
            _ => RenderPlainText(string.Empty)
        };

        private static Block RenderHeading(MarkdownBlock.Heading heading)
        {
            var paragraph = RenderParagraph(heading.Inlines);
            paragraph.FontSize = heading.Level switch
            {
                1 => DesignTokens.ReadingHeading1FontSize,
                2 => DesignTokens.ReadingHeading2FontSize,
                3 => DesignTokens.ReadingHeading3FontSize,
                // H4–H6＝正文字号＋SemiBold 权重区分（无第二份 14）。
                _ => DesignTokens.CardTextFontSize
            };
            paragraph.FontWeight = FontWeights.SemiBold;
            return paragraph;
        }

        private static Paragraph RenderParagraph(IReadOnlyList<MarkdownInline> inlines)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, DesignTokens.ReadingBlockGapPx, 0, 0) };
            AddInlines(paragraph.Inlines, inlines);
            return paragraph;
        }

        private static Paragraph RenderPlainText(string text)
        {
            var paragraph = RenderParagraph(Array.Empty<MarkdownInline>());
            paragraph.Inlines.Add(new Run(text));
            return paragraph;
        }

        private static Block RenderCodeBlock(MarkdownBlock.Code code)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, DesignTokens.ReadingBlockGapPx, 0, 0),
                Padding = new Thickness(DesignTokens.ReadingCodePaddingPx),
                FontFamily = new FontFamily(DesignTokens.ReadingCodeFontFamily),
                BorderThickness = new Thickness(1)
            };
            paragraph.SetResourceReference(Block.BackgroundProperty, "HoverSurfaceBrush");
            paragraph.SetResourceReference(Block.BorderBrushProperty, "BorderBrush");
            // 代码行间显式 LineBreak、行内空格以 NBSP 代换保住缩进对齐（FlowDocument
            // 会折叠普通空格序列）。
            var lines = code.Content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                if (lines[i].Length > 0)
                {
                    paragraph.Inlines.Add(new Run(lines[i].Replace(' ', '\u00A0')));
                }
            }

            return paragraph;
        }

        private static Block RenderQuote(MarkdownBlock.Quote quote)
        {
            var section = new Section
            {
                Margin = new Thickness(0, DesignTokens.ReadingBlockGapPx, 0, 0),
                Padding = new Thickness(DesignTokens.ReadingBlockGapPx + DesignTokens.ReadingQuoteBarPx, 0, 0, 0),
                BorderThickness = new Thickness(DesignTokens.ReadingQuoteBarPx, 0, 0, 0)
            };
            section.SetResourceReference(Block.BorderBrushProperty, "BorderBrush");
            foreach (var block in quote.Blocks)
            {
                section.Blocks.Add(RenderBlock(block));
            }

            return section;
        }

        private static Block RenderList(MarkdownBlock.List list)
        {
            var flowList = new System.Windows.Documents.List
            {
                Margin = new Thickness(DesignTokens.ReadingBlockGapPx, DesignTokens.ReadingBlockGapPx, 0, 0),
                MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                StartIndex = Math.Max(1, list.StartNumber)
            };
            foreach (var item in list.Items)
            {
                var listItem = new ListItem();
                foreach (var block in item.Blocks)
                {
                    listItem.Blocks.Add(RenderBlock(block));
                }

                flowList.ListItems.Add(listItem);
            }

            return flowList;
        }

        private static Block RenderThematicBreak()
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, DesignTokens.ReadingBlockGapPx, 0, 0),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            paragraph.SetResourceReference(Block.BorderBrushProperty, "BorderBrush");
            return paragraph;
        }

        private static void AddInlines(InlineCollection target, IReadOnlyList<MarkdownInline> inlines)
        {
            foreach (var inline in inlines)
            {
                AddInline(target, inline);
            }
        }

        private static void AddInline(InlineCollection target, MarkdownInline inline)
        {
            switch (inline)
            {
                case MarkdownInline.Text text:
                    target.Add(new Run(text.Content));
                    break;
                case MarkdownInline.Emphasis emphasis:
                    Span emphasisSpan = emphasis.Strong ? new Bold() : new Italic();
                    AddInlines(emphasisSpan.Inlines, emphasis.Inlines);
                    target.Add(emphasisSpan);
                    break;
                case MarkdownInline.Code code:
                    // 行内代码：代码字体＋HoverSurface 底（Span 承载背景，Run 背景
                    // 在 FlowDocument 不渲染）。
                    var codeSpan = new Span(new Run(code.Content.Replace(' ', '\u00A0')))
                    {
                        FontFamily = new FontFamily(DesignTokens.ReadingCodeFontFamily)
                    };
                    codeSpan.SetResourceReference(TextElement.BackgroundProperty, "HoverSurfaceBrush");
                    target.Add(codeSpan);
                    break;
                case MarkdownInline.Link link:
                    target.Add(RenderLink(link));
                    break;
                case MarkdownInline.LineBreak:
                    target.Add(new LineBreak());
                    break;
                case MarkdownInline.Raw raw:
                    target.Add(new Run(raw.Content));
                    break;
                default:
                    target.Add(new Run(string.Empty));
                    break;
            }
        }

        private static Inline RenderLink(MarkdownInline.Link link)
        {
            var clickable = Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            if (!clickable)
            {
                // 非 http(s)/相对目标：不当链接渲染（不做任何导航），按普通文本呈现。
                var plain = new Span();
                AddInlines(plain.Inlines, link.Inlines);
                return plain;
            }

            var hyperlink = new Hyperlink { NavigateUri = uri };
            hyperlink.RequestNavigate += OpenInDefaultBrowser;
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            hyperlink.TextDecorations = TextDecorations.Underline;
            hyperlink.Cursor = Cursors.Hand;
            AddInlines(hyperlink.Inlines, link.Inlines);
            return hyperlink;
        }

        private static void OpenInDefaultBrowser(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            e.Handled = true;
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or Win32Exception or System.ComponentModel.Win32Exception)
            {
                // 打开浏览器失败（无关联程序/策略限制）静默放弃：链接点击是可重试的
                // 非关键路径，不值得打断阅读。
            }
        }
    }
}
