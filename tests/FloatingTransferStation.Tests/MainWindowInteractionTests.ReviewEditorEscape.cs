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
/// 复盘编辑器 Esc 退出编辑态（1.25.0）：编辑器持焦时按 Esc 释放键盘焦点（移交宿主
/// 窗口），指针仍在面板内时面板保持展开——焦点链经 Root_PreviewLostKeyboardFocus
/// 统一清算编辑保持原因并重估表面（指针在面板内维持展开，否则恢复收起节奏）。
/// IME 组合期的 Esc 属输入法操作：STA 无法构造真实组合窗，按组合标记判定逻辑锁定
/// （标记存在时 Esc 分支不触发、焦点不移交）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void ReviewEditor_EscapeReleasesKeyboardFocusToEndEditing()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsTrue(editor.Focus());
            Assert.AreEqual(editor, Keyboard.FocusedElement);
            var state = GetPrivateField<PanelStateMachine>(window, "_panelState");
            Assert.IsTrue(state.IsTextEditingActive);
            CompleteLayout(window);

            var escape = NewEditorKeyDown(editor, Key.Escape);
            editor.RaiseEvent(escape);
            CompleteLayout(window);

            Assert.AreEqual(
                window,
                Keyboard.FocusedElement,
                $"handled={escape.Handled}, focused={Keyboard.FocusedElement?.GetType().Name ?? "null"}");            Assert.IsFalse(state.IsTextEditingActive, "焦点释放后编辑保持原因应由既有链清算。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewEditor_EscapeKeepsPanelExpandedWhilePointerInside()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var editor = (TextBox)window.FindName("ReviewEditor");
            HoverRealAt(
                window,
                editor,
                new Point(editor.ActualWidth / 2, editor.ActualHeight / 2),
                settleMilliseconds: 0);
            Assert.IsTrue(window.IsMouseOver, "前置条件：真实光标已悬停在复盘编辑器上。");
            Assert.IsTrue(editor.Focus());
            CompleteLayout(window);

            editor.RaiseEvent(NewEditorKeyDown(editor, Key.Escape));
            CompleteLayout(window);

            Assert.AreNotEqual(editor, Keyboard.FocusedElement);
            Assert.IsTrue(viewModel.IsPanelExpanded, "指针在面板内时，退出编辑态不得收起面板。");
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsFalse(collapseTimer.IsEnabled, "指针在面板内时不得启动收起计时。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewEditor_EscapeDuringCompositionKeepsEditingUntilCompositionEnds()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var editor = (TextBox)window.FindName("ReviewEditor");
            Assert.IsTrue(editor.Focus());
            var state = GetPrivateField<PanelStateMachine>(window, "_panelState");
            CompleteLayout(window);

            var composition = new TextComposition(InputManager.Current, editor, "复");
            editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputStartEvent,
                Source = editor
            });

            var escapeWhileComposing = NewEditorKeyDown(editor, Key.Escape);
            editor.RaiseEvent(escapeWhileComposing);
            CompleteLayout(window);

            Assert.IsFalse(escapeWhileComposing.Handled, "IME 组合期的 Esc 属输入法操作，不触发退出编辑。");
            Assert.AreEqual(editor, Keyboard.FocusedElement);
            Assert.IsTrue(state.IsTextEditingActive);

            editor.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
            {
                RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
                Source = editor
            });

            var escapeAfterComposition = NewEditorKeyDown(editor, Key.Escape);
            editor.RaiseEvent(escapeAfterComposition);
            CompleteLayout(window);

            Assert.AreEqual(window, Keyboard.FocusedElement, "组合结束后 Esc 应正常退出编辑态。");
            Assert.IsFalse(state.IsTextEditingActive);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    private static KeyEventArgs NewEditorKeyDown(
        TextBox editor,
        Key key) =>
        new(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(editor)!,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
            Source = editor
        };
}
