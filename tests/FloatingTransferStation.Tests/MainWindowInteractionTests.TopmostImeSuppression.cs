using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 置顶重申的 IME 输入敏感期抑制：键盘焦点在面板内任一文本编辑器（复盘/卡片）
/// 或 IME 组合进行中时，重申不得发起任何 z 序变更调用——SetWindowPos(HWND_TOPMOST)
/// 会把主窗重新插回置顶带顶端，遮挡 IME 候选窗。观察口径沿用契约 #13 的 Win32
/// 直驱断言：先手动降级（HWND_NOTOPMOST），再调用 ReassertTopmost，仍非置顶
/// 即证明未发生 z 序变更调用；非编辑态同一序列必须恢复置顶（行为保持原样）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>停掉真实重申定时器，排除测试进行中 2 秒周期 Tick 对断言的竞态。</summary>
    private static void StopTopmostReassertTimer(MainWindow window) =>
        GetPrivateField<DispatcherTimer>(window, "_topmostTimer").Stop();

    private static bool DemoteFromTopmostBand(MainWindow window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (SetWindowPos(
                handle,
                HwndNotopmost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoMove | NativeMethods.SwpNoActivate))
        {
            return !IsWindowTopmost(handle);
        }

        return false;
    }

    private static void ReassertTopmostNow(MainWindow window) =>
        InvokePrivate(window, "ReassertTopmost");

    [STATestMethod]
    public void TopmostReassert_SuppressedWhileFocusInReviewEditor()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            StopTopmostReassertTimer(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var editor = (TextBox)window.FindName("ReviewEditor");

            // 基线：非编辑态的重申行为保持原样——降级后重申必须恢复置顶。
            Assert.IsTrue(DemoteFromTopmostBand(window), "前置条件：手动降级后应不在置顶层。");
            ReassertTopmostNow(window);
            Assert.IsTrue(IsWindowTopmost(handle), "非编辑态重申应把窗口抬回置顶层。");

            // 编辑态：焦点进入复盘编辑器后，同一序列不得发起 z 序变更。
            Assert.IsTrue(editor.Focus());
            Assert.AreEqual(editor, Keyboard.FocusedElement);
            Assert.IsTrue(DemoteFromTopmostBand(window));
            ReassertTopmostNow(window);
            Assert.IsFalse(
                IsWindowTopmost(handle),
                "键盘焦点在复盘编辑器内时，重申必须被抑制（不发起 z 序变更调用）。");

            // 焦点释放回宿主窗口后，重申恢复原样。
            ReleaseEditorFocusToWindow(window);
            ReassertTopmostNow(window);
            Assert.IsTrue(IsWindowTopmost(handle), "焦点离开编辑器后重申应恢复置顶层级。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void TopmostReassert_SuppressedWhileFocusInCardEditor()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("卡片编辑器遮挡候选窗回归", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            EnterCardEditingWithDoubleClick(window, item);
            CompleteLayout(window);
            StopTopmostReassertTimer(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var editor = EditorInput(window, item);
            Assert.IsTrue(editor.IsKeyboardFocused, "前置条件：双击进入编辑后卡片编辑器持焦。");

            Assert.IsTrue(DemoteFromTopmostBand(window));
            ReassertTopmostNow(window);
            Assert.IsFalse(
                IsWindowTopmost(handle),
                "键盘焦点在卡片编辑器内时，重申必须被抑制（不发起 z 序变更调用）。");

            // Esc 取消编辑（焦点回列表，非文本编辑器）后，重申恢复原样。
            editor.RaiseEvent(NewEditorKeyDown(editor, Key.Escape));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(300));
            CompleteLayout(window);
            Assert.IsFalse(
                editor.IsKeyboardFocused,
                "前置条件：Esc 取消编辑后编辑器不再持有键盘焦点。");
            Assert.IsTrue(DemoteFromTopmostBand(window));
            ReassertTopmostNow(window);
            Assert.IsTrue(IsWindowTopmost(handle), "退出卡片编辑后重申应恢复置顶层级。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void TopmostReassert_SuppressedDuringImeComposition()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            StopTopmostReassertTimer(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsTrue(editor.Focus());

            // STA 无法构造真实组合窗：按既有口径（ReviewEditor Esc 契约测试同法）
            // 以 TextComposition 直驱事件锁定组合标记判定逻辑。
            var composition = new TextComposition(InputManager.Current, editor, "复");
            editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent,
                Source = editor
            });
            Assert.IsTrue(
                GetPrivateField<bool>(window, "_isPanelEditorComposing"),
                "组合开始后面板编辑器组合标记应置位。");

            Assert.IsTrue(DemoteFromTopmostBand(window));
            ReassertTopmostNow(window);
            Assert.IsFalse(
                IsWindowTopmost(handle),
                "IME 组合期重申必须被抑制（不发起 z 序变更调用）。");

            // 组合完成：标记复位，但键盘焦点仍在编辑器内——抑制由焦点原因继续持有。
            editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
                Source = editor
            });
            Assert.IsFalse(
                GetPrivateField<bool>(window, "_isPanelEditorComposing"),
                "组合完成后面板编辑器组合标记应复位。");
            Assert.IsTrue(DemoteFromTopmostBand(window));
            ReassertTopmostNow(window);
            Assert.IsFalse(
                IsWindowTopmost(handle),
                "焦点仍在编辑器内时，组合结束不得单独解除抑制。");

            // 焦点释放后抑制整体解除，重申恢复原样。
            ReleaseEditorFocusToWindow(window);
            ReassertTopmostNow(window);
            Assert.IsTrue(IsWindowTopmost(handle), "焦点离开编辑器后重申应恢复置顶层级。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void TopmostReassert_CompositionFlagClearedWhenEditorFocusLeaves()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            StopTopmostReassertTimer(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsTrue(editor.Focus());

            var composition = new TextComposition(InputManager.Current, editor, "挡");
            editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent,
                Source = editor
            });
            Assert.IsTrue(GetPrivateField<bool>(window, "_isPanelEditorComposing"));

            // IME 取消组合不一定补发 TextInput 完成事件；焦点整体离开面板编辑器
            // 必须同时清算组合标记，防重申抑制滞留（滞留会让主窗失去置顶恢复能力）。
            ReleaseEditorFocusToWindow(window);
            Assert.IsFalse(
                GetPrivateField<bool>(window, "_isPanelEditorComposing"),
                "焦点离开面板编辑器时应清算组合标记。");
            Assert.IsTrue(DemoteFromTopmostBand(window));
            ReassertTopmostNow(window);
            Assert.IsTrue(
                IsWindowTopmost(handle),
                "组合标记随焦点离开清算后，重申应恢复置顶层级。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    /// <summary>
    /// 与复盘编辑器 Esc 退出同法的焦点释放：先清域内记录再聚焦宿主窗口，
    /// 键盘焦点才会真正离开编辑器。
    /// </summary>
    private static void ReleaseEditorFocusToWindow(MainWindow window)
    {
        FocusManager.SetFocusedElement(window, null);
        Assert.IsTrue(window.Focus());
    }
}
