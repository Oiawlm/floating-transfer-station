using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 记录编辑器（ReviewEditor）viewport 几何与滚动条可见性解耦契约（#23 扩展至文本输入表面，
/// 2026-11-06）：打字途中滚动条出现/消失不得改变排版宽度。根因与 BoardList/SettingsScrollHost
/// 同族（「从未治疗」而非复发——2026-10-12 契约 #23 修复当刻把记录编辑器划出范围）：自绘
/// ThemedTextBoxStyle 的 PART_ContentHost 仍是默认模板 ScrollViewer（Aero2 两列布局，垂直
/// 滚动条占独立 Auto 列），VerticalScrollBarVisibility=Auto 在内容打满时滚动条列突然占宽
/// ≈17 DIP（主题 ScrollBar 系统尺寸下限压过瘦条 Width=8，150% 缩放实测 17.33）——Wrap
/// 排版宽骤减即全文重排（用户报障「输入到一定程度滚动条出现，已输入文字突然整体换行重排」）。
/// 修法同 #23 机制：keyed overlay ScrollViewer 模板（单格叠加）让条带悬浮文本之上、永不占
/// 布局宽；8 DIP 条带落在编辑器 Padding=12 右带内（见 OverlayScrollViewerStyle 的 Padding
/// 透传：presenter 内缩、条带贴满宽右缘），与文字零相交（冻结锚 §3 几何论证同源：条带宽 8
/// &lt; 右带 12，条带处命中本就应归滚动条）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>向编辑器灌入足量多行文本，使其内容高度必然溢出视口（滚动条在场）。</summary>
    private static string OverflowingReviewText(int lines) =>
        string.Join("\n", Enumerable.Repeat("占位文字行", lines));

    private static (TextBox Editor, ScrollViewer Viewer) ReviewEditorScrollSurface(MainWindow window)
    {
        var editor = (TextBox)window.FindName("ReviewEditor");
        Assert.IsNotNull(editor, "ReviewEditor 必须存在。");
        var viewer = FindDescendant<ScrollViewer>(editor);
        Assert.IsNotNull(viewer, "编辑器模板内必须存在 PART_ContentHost ScrollViewer。");
        return (editor, viewer);
    }

    private static bool ReviewEditorVerticalScrollBarVisible(TextBox editor) =>
        FindDescendants<ScrollBar>(editor).Any(candidate =>
            candidate.Orientation == Orientation.Vertical &&
            candidate.Visibility == Visibility.Visible);

    [STATestMethod]
    public void ReviewViewport_EditorViewportWidthIsIndependentOfVerticalScrollBarVisibility()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var (editor, viewer) = ReviewEditorScrollSurface(window);
            editor.Text = "短";
            CompleteLayout(window);
            Assert.IsFalse(
                ReviewEditorVerticalScrollBarVisible(editor),
                "前置条件：内容不足一屏时垂直滚动条离场。");
            var widthWithoutScrollBar = viewer.ViewportWidth;

            // 用户场景：持续输入到打满一屏，滚动条出现。
            editor.Text = OverflowingReviewText(400);
            CompleteLayout(window);
            Assert.IsTrue(viewer.ScrollableHeight > 0, "前置条件：内容可滚动。");
            Assert.IsTrue(
                ReviewEditorVerticalScrollBarVisible(editor),
                "前置条件：垂直滚动条在场。");

            Assert.AreEqual(
                widthWithoutScrollBar,
                viewer.ViewportWidth,
                ScrollGeometryTolerance,
                "打字途中滚动条出现不得收窄排版宽（默认两列模板会让 viewport 骤减 ≈17 DIP 触发全文重排）。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewViewport_OverlayScrollBarRemainsInteractive()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var (editor, viewer) = ReviewEditorScrollSurface(window);
            editor.Text = OverflowingReviewText(400);
            CompleteLayout(window);
            Assert.IsTrue(viewer.ScrollableHeight > 0, "前置条件：内容可滚动。");
            var scrollBar = FindDescendants<ScrollBar>(editor)
                .Single(candidate => candidate.Orientation == Orientation.Vertical &&
                    candidate.Visibility == Visibility.Visible);
            viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
            CompleteLayout(window);
            var offsetAtBottom = viewer.VerticalOffset;
            Assert.IsTrue(offsetAtBottom > 0, "前置条件：视口已滚动到底部。");

            // overlay 模板接管后 Track 必须仍然接线：点击上页按钮（Track 的
            // DecreaseRepeatButton）视口上移，防「样式替换后 Track 失联」。
            var repeatButtons = FindDescendants<RepeatButton>(scrollBar).ToArray();
            Assert.HasCount(2, repeatButtons, "滚动条 Track 的增/减 RepeatButton 必须已实现。");
            repeatButtons[0].RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent, repeatButtons[0]));
            repeatButtons[0].RaiseEvent(NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonUpEvent, repeatButtons[0]));
            CompleteLayout(window);
            Assert.IsTrue(
                viewer.VerticalOffset < offsetAtBottom - 1,
                $"overlay 后滚动条仍可交互（上页前 {offsetAtBottom}，上页后 {viewer.VerticalOffset}）。");

            // 零遮挡契约（同 #23 BoardList 口径）：条带 8 DIP（隐式瘦条样式，MinWidth=0 压掉
            // 系统尺寸下限）必须整体落在编辑器 Padding=12 右带内——条带左缘不得侵入文本洞
            // （presenter 右缘），条带右缘不得越出编辑器内沿（边框内沿）。文字（TextBoxView）
            // 填满 presenter，条带与文字零相交。
            var presenter = FindDescendant<ScrollContentPresenter>(viewer);
            Assert.IsNotNull(presenter, "ScrollViewer 模板必须实现 PART_ScrollContentPresenter。");
            var strip = new Rect(
                scrollBar.TranslatePoint(new Point(), editor),
                new Size(scrollBar.ActualWidth, scrollBar.ActualHeight));
            Assert.IsTrue(strip.Width > 0 && strip.Height > 0, "前置条件：滚动条条带已布局。");
            Assert.AreEqual(
                8d, strip.Width, ScrollGeometryTolerance,
                "条带宽应为瘦条样式的 8 DIP（不得回落系统滚动条宽度下限，须落在 Padding=12 右带内）。");
            var textHoleRight = presenter.TranslatePoint(
                new Point(presenter.ActualWidth, 0), editor).X;
            Assert.IsTrue(
                strip.Left >= textHoleRight - ScrollGeometryTolerance,
                $"条带左缘 {strip.Left} 侵入文本洞（右缘 {textHoleRight}）：条带必须整体落在右内边距带内。");
            var interiorRight = editor.ActualWidth - editor.BorderThickness.Right;
            Assert.IsTrue(
                strip.Right <= interiorRight + ScrollGeometryTolerance,
                $"条带右缘 {strip.Right} 越出编辑器内沿 {interiorRight}。");

            // 条带处的真实命中归属滚动条（不只合成事件可达）：沿视觉树向上必须抵达垂直
            // ScrollBar，编辑器文字层不得盖住条带。
            var stripCenter = new Point(strip.Left + strip.Width / 2, strip.Top + strip.Height / 2);
            var hit = editor.InputHitTest(stripCenter);
            Assert.IsNotNull(hit, "条带中心必须命中元素。");
            var walker = (DependencyObject?)hit;
            while (walker is not null && !ReferenceEquals(walker, scrollBar))
            {
                walker = VisualTreeHelper.GetParent(walker);
            }

            Assert.AreSame(
                scrollBar, walker,
                $"条带中心 {stripCenter} 命中了 {hit.GetType().Name}：滚动条条带处的命中必须归属滚动条。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    /// <summary>字宽边界例：恰好在无滚动条状态下放满一行的行（宽度距排版宽不足一个字宽），
    /// 在滚动条出现后必须仍不换行——这是「全文重排」用户报障的最小复现单元。以 ExtentHeight
    /// 推算显示行数（同一字体下行高恒定：n 个硬换行行、无折行时 ExtentHeight = 单行基点 +
    /// (n−1) × 行高；空文本仍占一行高度，故以一行文本为基点），先二分找到最长不换行的
    /// 「记」重复行并断言 n+1 字恰好换行（边界成立），再灌满触发滚动条，断言全文显示行数
    /// 与硬换行行数一致（边界行未被滚动条出现挤压换行）。</summary>
    [STATestMethod]
    public void ReviewViewport_BoundaryLineDoesNotRewrapWhenScrollBarAppears()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var (editor, viewer) = ReviewEditorScrollSurface(window);

            // 行高校准：同一字体单行/两行的 ExtentHeight 差即行高（显示行数线性叠加的基）。
            editor.Text = "记";
            CompleteLayout(window);
            var oneLineExtent = viewer.ExtentHeight;
            editor.Text = "记\n记";
            CompleteLayout(window);
            var lineHeight = viewer.ExtentHeight - oneLineExtent;
            Assert.IsTrue(lineHeight > 0, $"行高校准必须为正（单行 {oneLineExtent}）。");
            editor.Text = string.Empty;
            CompleteLayout(window);
            Assert.IsTrue(
                Math.Abs(viewer.ExtentHeight - oneLineExtent) <= 1,
                "前置条件：空文本与单行文本的 ExtentHeight 同为一行高度（以一行文本为基点）。");

            double ExtentOfSingleLine(int count)
            {
                editor.Text = new string('记', count);
                CompleteLayout(window);
                return viewer.ExtentHeight;
            }

            // 二分最长不换行的「记」重复行：n 字单显示行、n+1 字两显示行。
            var good = 1;
            var bad = 8;
            while (Math.Abs(ExtentOfSingleLine(bad) - oneLineExtent) <= 1)
            {
                good = bad;
                bad *= 2;
                Assert.IsTrue(bad <= 2048, "前置条件：编辑器应能放下至少 8 个全角字。");
            }

            while (bad - good > 1)
            {
                var middle = (good + bad) / 2;
                if (Math.Abs(ExtentOfSingleLine(middle) - oneLineExtent) <= 1)
                {
                    good = middle;
                }
                else
                {
                    bad = middle;
                }
            }

            Assert.IsTrue(good >= 8, $"边界行应明显宽于几个字（找到 {good} 字），否则前置失效。");

            // 灌满触发滚动条：边界行 + 大量短行。断言全文 ExtentHeight 仍等于单行基点 +
            // (硬换行行数−1) × 行高（边界行未换行）；两列模板收窄 ≈17 DIP 时边界行
            // （距右缘不足一个全角字宽 ≈14 DIP）必然折行、ExtentHeight 多出一行行高。
            const int fillerLines = 400;
            editor.Text = new string('记', good) + "\n" + OverflowingReviewText(fillerLines);
            CompleteLayout(window);
            Assert.IsTrue(
                ReviewEditorVerticalScrollBarVisible(editor),
                "前置条件：垂直滚动条在场。");
            Assert.AreEqual(
                oneLineExtent + fillerLines * lineHeight,
                viewer.ExtentHeight,
                lineHeight / 2,
                $"边界行（{good} 个全角字）在滚动条出现后必须仍占一行显示高度。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }
}
