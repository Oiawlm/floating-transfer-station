using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatingTransferStation.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 卡片双击编辑光标锚点（caret anchor）守护：双击进入正文编辑，光标落在
/// 双击点的就近插入边界且无选区（「从该处编辑」语义，多行正文全选有整段
/// 误删风险）；横向语义与编辑器内直接点击同规（原生就近边界）；无锚点入口
/// （备注单击）保持全选默认；编辑态与显示态行距同源（20/行，映射的确定性
/// 前提）。真实输入路径用 Win32 消息直驱（契约 #13 口径），合成序列只作
/// 「不全选」补充回归。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>多行正文卡（换行定行，行数与宽度无关；4 行 × 行距 20 = 80 &lt; MaxHeight 100）。</summary>
    private const string CaretAnchorCardText = "第一行内容\n第二行内容\n第三行内容\n第四行内容";

    /// <summary>卡片显示态正文 TextBlock（TrimAwareTextBlock 承载，1.28.0 起结构）。</summary>
    private static TextBlock CardDisplayTextOf(ListBoxItem container, BoardItem item) =>
        (TextBlock)FindDescendants<TrimAwareTextBlock>(container)
            .Single(candidate => ReferenceEquals(candidate.DataContext, item)).Child;

    /// <summary>
    /// 编辑器第 lineIndex 行的纵向行带（Top 含、Bottom 不含）：带上沿=该行首
    /// 字符矩形 Top，带下沿=下一行首字符矩形 Top（行高均匀时即 行高；末行带
    /// 用上一行 Top 外推）。全部取直接测量（非循环断言）——行带定义不依赖
    /// 行距对齐结果，锚点映射与行距度量由不同测试分别锁定。
    /// </summary>
    private static (double Top, double Bottom) CaretLineBand(TextBox editor, int lineIndex)
    {
        Assert.IsTrue(
            editor.LineCount >= 2,
            $"前置：行带测量要求编辑器已排版出至少两行（实际 LineCount={editor.LineCount}）。");
        var top = editor.GetRectFromCharacterIndex(editor.GetCharacterIndexFromLineIndex(lineIndex)).Top;
        if (lineIndex + 1 < editor.LineCount)
        {
            return (top, editor.GetRectFromCharacterIndex(editor.GetCharacterIndexFromLineIndex(lineIndex + 1)).Top);
        }

        var previousTop = editor.GetRectFromCharacterIndex(editor.GetCharacterIndexFromLineIndex(lineIndex - 1)).Top;
        return (top, top + (top - previousTop));
    }

    /// <summary>
    /// 真实输入双击（Win32 消息直驱两连击）：调用前几何须已定稿且双击窗已
    /// 拉开（预热一击 + 泵过系统双击时限），两击同位落在时限内构成双击。
    /// </summary>
    private static void DoubleClickRealAt(
        MainWindow window,
        ListBoxItem container,
        Point positionInContainer)
    {
        ClickRealAt(window, container, positionInContainer);
        ClickRealAt(window, container, positionInContainer);
    }

    /// <summary>
    /// 真实输入几何预热：置顶稳定窗口、预热一击（左半，无语义）、泵过系统
    /// 双击时限——正式双击的两击不被此前痕迹拼窗，几何不再因悬停重排漂移。
    /// </summary>
    private static void WarmUpForRealDoubleClick(MainWindow window, ListBoxItem container)
    {
        window.Topmost = true;
        CompleteLayout(window);
        ClickRealAt(window, container, new Point(24, container.ActualHeight / 2));
        PumpDispatcherFor(
            window.Dispatcher,
            TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));
    }

    [STATestMethod]
    public void CardCaretAnchor_DoubleClickPlacesCaretAtClickedLineRealInput()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(CaretAnchorCardText, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var display = CardDisplayTextOf(container, item);
            var origin = display.TransformToAncestor(container).Transform(new Point(0, 0));
            var savedCursor = SaveCursorPosition();

            try
            {
                // 第一行行中心双击：光标必须落在第一行行带内且无选区。
                WarmUpForRealDoubleClick(window, container);
                DoubleClickRealAt(
                    window,
                    container,
                    new Point(origin.X + 20, origin.Y + 10));
                CompleteLayout(window);

                var editor = CardTextEditorOf(container);
                Assert.IsTrue(editor.IsVisible, "双击必须进入就地编辑。");
                Assert.IsTrue(editor.IsKeyboardFocused, "编辑器必须持有键盘焦点。");
                Assert.AreEqual(
                    0,
                    editor.SelectionLength,
                    "双击进入正文编辑不得全选（多行正文全选有整段误删风险）。");
                var band = CaretLineBand(editor, 0);
                var caretRect = editor.GetRectFromCharacterIndex(editor.CaretIndex);
                Assert.IsTrue(
                    caretRect.Top >= band.Top && caretRect.Top < band.Bottom,
                    $"光标必须落在第一行行带内（[{band.Top}, {band.Bottom})，实际 Top={caretRect.Top}）。");
                Assert.AreEqual(
                    0,
                    editor.GetLineIndexFromCharacterIndex(editor.CaretIndex),
                    "光标行号必须为第一行。");

                // 编辑表面会吞掉并终结会话：先提交退出，再在第二行行中心双击——
                // 跨行偏移验证映射走的确实是点击坐标而非固定文首。
                window.CommitCardTextForTest();
                CompleteLayout(window);
                PumpDispatcherFor(
                    window.Dispatcher,
                    TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));
                DoubleClickRealAt(
                    window,
                    container,
                    new Point(origin.X + 20, origin.Y + 30));
                CompleteLayout(window);

                editor = CardTextEditorOf(container);
                Assert.IsTrue(editor.IsVisible);
                Assert.AreEqual(0, editor.SelectionLength);
                band = CaretLineBand(editor, 1);
                caretRect = editor.GetRectFromCharacterIndex(editor.CaretIndex);
                Assert.IsTrue(
                    caretRect.Top >= band.Top && caretRect.Top < band.Bottom,
                    $"第二行双击的光标必须落在第二行行带内（[{band.Top}, {band.Bottom})，实际 Top={caretRect.Top}）。");
                Assert.AreEqual(
                    1,
                    editor.GetLineIndexFromCharacterIndex(editor.CaretIndex),
                    "光标行号必须为第二行。");

                // 横向下界：显示文本左缘（原点 +1px）双击 → 光标就近落在文首。
                window.CommitCardTextForTest();
                CompleteLayout(window);
                PumpDispatcherFor(
                    window.Dispatcher,
                    TimeSpan.FromMilliseconds(GetDoubleClickTime() + 100));
                DoubleClickRealAt(
                    window,
                    container,
                    new Point(origin.X + 1, origin.Y + 10));
                CompleteLayout(window);

                editor = CardTextEditorOf(container);
                Assert.AreEqual(0, editor.SelectionLength);
                Assert.IsTrue(
                    editor.CaretIndex <= 1,
                    $"左缘双击的光标必须落在文首（实际 CaretIndex={editor.CaretIndex}）。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardCaretAnchor_TypingStartsAtClickedPosition()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(CaretAnchorCardText, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var display = CardDisplayTextOf(container, item);
            var origin = display.TransformToAncestor(container).Transform(new Point(0, 0));
            var savedCursor = SaveCursorPosition();

            try
            {
                // 第二行行中心双击进入编辑，选区空时 SelectedText 赋值 = 光标处
                // 插入：插入字符的纵向落点必须仍在双击行（「从该处开始编辑」，
                // 非文首/文末）。
                WarmUpForRealDoubleClick(window, container);
                DoubleClickRealAt(
                    window,
                    container,
                    new Point(origin.X + 20, origin.Y + 30));
                CompleteLayout(window);

                var editor = CardTextEditorOf(container);
                Assert.IsTrue(editor.IsVisible);
                Assert.AreEqual(
                    0,
                    editor.SelectionLength,
                    "前置：选区必须为空（空选区时 SelectedText 赋值才是插入）。");
                editor.SelectedText = "✎";
                CompleteLayout(window);

                Assert.AreEqual(
                    1,
                    editor.GetLineIndexFromCharacterIndex(editor.CaretIndex - 1),
                    "插入点必须位于双击的第二行（非文首/文末）。");
                var band = CaretLineBand(editor, 1);
                var insertedRect = editor.GetRectFromCharacterIndex(editor.CaretIndex - 1);
                Assert.IsTrue(
                    insertedRect.Top >= band.Top && insertedRect.Top < band.Bottom,
                    $"插入字符必须落在第二行行带内（[{band.Top}, {band.Bottom})，实际 Top={insertedRect.Top}）。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardCaretAnchor_EditorLineMetricsMatchDisplay()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(CaretAnchorCardText, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var display = CardDisplayTextOf(container, item);

            // 显示态先行锁定：4 行 × 行距 20（TrimAwareTextBlock 内层 TextBlock
            // 的自然高度），显示态行距漂移同样使本测试红——两态同源是整条
            // 锚点映射正确性的前提，缺一侧即锁不住。
            Assert.AreEqual(
                80d,
                display.ActualHeight,
                0.5,
                "显示态 4 行自然高度必须为 4×20（显示态行距锁定）。");

            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);

            var editor = CardTextEditorOf(container);
            Assert.IsTrue(
                editor.LineCount >= 2,
                $"前置：编辑器必须排版出多行（实际 LineCount={editor.LineCount}）。");
            for (var k = 1; k < Math.Min(editor.LineCount, 5); k++)
            {
                var actual = editor.GetRectFromCharacterIndex(editor.GetCharacterIndexFromLineIndex(k)).Top
                    - editor.GetRectFromCharacterIndex(0).Top;
                Assert.AreEqual(
                    20d * k,
                    actual,
                    0.5,
                    $"编辑态第 {k + 1} 行行距必须与显示态同源（行距 20，第 {k + 1} 行 Top 差应 ≈ {20 * k}）。");
            }

            // 两态总高一致（同一换行数 × 同一行距的复合证据）。
            Assert.AreEqual(
                display.ActualHeight,
                editor.ActualHeight,
                0.5,
                "编辑态与显示态总高必须一致（同行距同换行数的逐像素对齐前提）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CardCaretAnchor_DoubleClickKeepsSelectionEmptySynthetic()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(CaretAnchorCardText, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);

            // 合成双击序列（锚点取真实光标位置）：只守「不全选」——行级精度
            // 由真实输入测试负责，合成事件无法指定落点行。
            RaiseCardDoubleClick(ZoneHitSource(window, item, CardGestureZones.ContentZone));
            CompleteLayout(window);

            var editor = CardTextEditorOf(ContainerOf(window, item));
            Assert.IsTrue(editor.IsVisible);
            Assert.IsTrue(editor.IsKeyboardFocused);
            Assert.AreEqual(
                0,
                editor.SelectionLength,
                "合成双击序列同样不得回退为全选。");
            Assert.IsTrue(
                editor.CaretIndex >= 0 && editor.CaretIndex <= editor.Text.Length,
                $"光标索引必须落在文本范围内（实际 {editor.CaretIndex}/{editor.Text.Length}）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>
    /// 自动换行（无显式换行符）场景的锚点映射：显示态与编辑态换行宽度一致
    /// （两态总高一致=同一换行数×同行距），第二视觉行行中心双击 → 光标落在
    /// 编辑态同一视觉行。换行宽度若不一致（两态可用宽度差哪怕 1px），总行数
    /// 随之不同、总高断言即红——这是换行卡误映射的直接防线。
    /// </summary>
    [STATestMethod]
    public void CardCaretAnchor_WrappedLineDoubleClickMapsToSameVisualLine()
    {
        const string wrappedText = "这是一段没有换行符的长文本，依靠自动换行折行成多个视觉行，" +
            "用来验证显示态与编辑态的换行宽度一致、光标锚点在换行卡上同样落在双击的那一行。";
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText(wrappedText, BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var container = ContainerOf(window, item);
            var display = CardDisplayTextOf(container, item);
            Assert.IsTrue(
                display.ActualHeight >= 60,
                $"前置：正文必须自动换行出至少 3 个视觉行（实际高度 {display.ActualHeight}）。");
            var origin = display.TransformToAncestor(container).Transform(new Point(0, 0));
            var savedCursor = SaveCursorPosition();

            try
            {
                WarmUpForRealDoubleClick(window, container);
                DoubleClickRealAt(
                    window,
                    container,
                    new Point(origin.X + 20, origin.Y + 30));
                CompleteLayout(window);

                var editor = CardTextEditorOf(container);
                Assert.IsTrue(editor.IsVisible);
                Assert.AreEqual(
                    0,
                    editor.SelectionLength,
                    "换行卡双击同样不得全选。");
                Assert.AreEqual(
                    display.ActualHeight,
                    editor.ActualHeight,
                    0.5,
                    "编辑态与显示态总高必须一致（换行宽度一致的直接证据：同一文本换行数相同）。");
                var band = CaretLineBand(editor, 1);
                var caretRect = editor.GetRectFromCharacterIndex(editor.CaretIndex);
                Assert.IsTrue(
                    caretRect.Top >= band.Top && caretRect.Top < band.Bottom,
                    $"换行卡第二视觉行双击的光标必须落在同一视觉行（[{band.Top}, {band.Bottom})，实际 Top={caretRect.Top}）。");
                Assert.AreEqual(
                    1,
                    editor.GetLineIndexFromCharacterIndex(editor.CaretIndex),
                    "光标行号必须为第二视觉行。");
            }
            finally
            {
                RestoreCursorPosition(savedCursor);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
