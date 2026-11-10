using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FloatingTransferStation.Views;

/// <summary>
/// Esc 宿主语义（关窗／退出搜索态）的 IME 组合期公共守卫（契约 #63）：组合进行中
/// 按 Esc 属输入法操作（取消候选），宿主语义不得触发；组合结束后 Esc 照常。结构
/// 对照 InlineRenameSession 组合标记范式——按窗口登记组合中的文本编辑器（组合
/// 开始/更新入集，组合终结或编辑器失焦出集，防标记滞留），消费方只读
/// <see cref="IsComposing"/> 单一判定。四个独立对话框（编辑目标/网址输入/多选
/// 选择器/插件试运行）、主窗搜索态退出（窗口级与搜索框内两条 Esc 分支）与设置窗
/// 关窗统一消费本守卫；就地编辑五处既有组合守卫（分类/chip 改名、卡片正文、记录
/// 编辑器、瓦片改名，契约 #20/#49）各随会话机器存续，不并入本守卫。
/// </summary>
internal sealed class ImeCompositionGuard
{
    private readonly HashSet<TextBoxBase> _compositions = [];

    private ImeCompositionGuard()
    {
    }

    /// <summary>把守卫挂接到窗口：组合事件窗口级登记（handledEventsToo——组合期
    /// 文本事件常被编辑器标记已处理）与编辑器失焦清理由此单点完成，消费方不再自建
    /// 组合标记。</summary>
    public static ImeCompositionGuard Attach(Window window)
    {
        var guard = new ImeCompositionGuard();
        window.AddHandler(
            TextCompositionManager.PreviewTextInputStartEvent,
            new TextCompositionEventHandler((_, e) => guard.TrackComposition(e, composing: true)),
            true);
        window.AddHandler(
            TextCompositionManager.PreviewTextInputUpdateEvent,
            new TextCompositionEventHandler((_, e) => guard.TrackComposition(e, composing: true)),
            true);
        window.AddHandler(
            TextCompositionManager.PreviewTextInputEvent,
            new TextCompositionEventHandler((_, e) => guard.TrackComposition(e, composing: false)),
            true);
        window.AddHandler(
            Keyboard.PreviewLostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(guard.Editor_LostKeyboardFocus),
            true);
        return guard;
    }

    /// <summary>组合进行中判定：窗口内仍有组合未终结的文本编辑器。</summary>
    public bool IsComposing => _compositions.Count > 0;

    private void TrackComposition(TextCompositionEventArgs e, bool composing)
    {
        if (e.OriginalSource is not TextBoxBase editor)
        {
            return;
        }

        if (composing)
        {
            _compositions.Add(editor);
        }
        else
        {
            // PreviewTextInput＝组合终结（与 InlineRenameSession.HandleCompositionCompleted
            // 同事件源）；非组合的普通文本输入不在集合中，移除为幂等零动作。
            _compositions.Remove(editor);
        }
    }

    private void Editor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 组合随焦点离开而中止：终结事件不再到来，标记滞留会让同一编辑器下次
        // Esc 永久失效（镜像 InlineRenameSession.HandleLostKeyboardFocus 清理）。
        if (e.OriginalSource is TextBoxBase editor)
        {
            _compositions.Remove(editor);
        }
    }
}
