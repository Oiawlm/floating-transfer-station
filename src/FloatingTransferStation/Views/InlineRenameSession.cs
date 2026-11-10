using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace FloatingTransferStation.Views;

/// <summary>
/// 共享改名会话登记缝（契约 #49 修订）：「点击编辑器外部＝提交」隧道与「面板切换冲刷」
/// 遍历同一登记清单——两类改名会话（分类名、子分组 chip 名）由此共用同一台会话机器
/// （契约 #57 修订⑤），后续新增改名编辑位＝登记一个协调器实例。
/// </summary>
internal interface IInlineRenameSession
{
    /// <summary>当前改名会话的编辑器（协调查询缝）：会话不存在，或会话在而编辑器未实现
    ///（模板未挂）时为 null——调用方据此跳过本次触发，会话交回失焦等既有通道处置。</summary>
    TextBox? ActiveEditor();

    /// <summary>终结当前会话走提交路径：读编辑器即时文本提交（WPF 组合期文本已在
    /// <see cref="TextBox.Text"/> 中，IME 组合文本一并保存不丢失）、组合标记随终结清理。
    /// 无会话零动作＝幂等。</summary>
    void CommitActiveSession();
}

/// <summary>
/// 泛型改名会话协调器（<see cref="IInlineRenameSession"/> 的机器实现）：把分类名与子分组
/// chip 名两套逐字镜像的会话机器（Begin 聚焦全选→草稿限长→IME 组合守卫→Enter 提交／
/// Esc 取消丢弃／失焦提交）收敛为单一实现，subject 经构造参数注入（会话态/限长/组合集/
/// 查询缝）；乐观保存留在各调用面（<see cref="Commit"/> 只回调，Save*Async 仍是各面单点）。
/// 事件入口（Handle*）由各调用面沿 InitializeCardEditSessionTracking 先例做窗口级挂接
///（编辑器在 DataTemplate 内，事件不经 XAML 特性散挂，按 OriginalSource 过滤编辑器）。
/// </summary>
internal sealed class InlineRenameSession<TSubject> : IInlineRenameSession where TSubject : class
{
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<TextBox> _compositions;
    private readonly Func<IEnumerable<TSubject>> _subjects;
    private readonly Func<TSubject, bool> _isEditing;
    private readonly Func<TSubject, TextBox?> _findEditor;
    private readonly Func<DependencyObject, TextBox?> _findDescendantEditor;
    private readonly Func<string, string> _limitDraft;
    private readonly Action<TSubject> _beginSession;
    private readonly Func<TSubject, string, string> _endSession;
    private readonly Action<TSubject> _cancelSession;
    private readonly Action<TSubject, string> _saveIfChanged;
    private readonly Action _afterSessionEnd;

    public InlineRenameSession(
        Dispatcher dispatcher,
        HashSet<TextBox> compositions,
        Func<IEnumerable<TSubject>> subjects,
        Func<TSubject, bool> isEditing,
        Func<TSubject, TextBox?> findEditor,
        Func<DependencyObject, TextBox?> findDescendantEditor,
        Func<string, string> limitDraft,
        Action<TSubject> beginSession,
        Func<TSubject, string, string> endSession,
        Action<TSubject> cancelSession,
        Action<TSubject, string> saveIfChanged,
        Action afterSessionEnd)
    {
        _dispatcher = dispatcher;
        _compositions = compositions;
        _subjects = subjects;
        _isEditing = isEditing;
        _findEditor = findEditor;
        _findDescendantEditor = findDescendantEditor;
        _limitDraft = limitDraft;
        _beginSession = beginSession;
        _endSession = endSession;
        _cancelSession = cancelSession;
        _saveIfChanged = saveIfChanged;
        _afterSessionEnd = afterSessionEnd;
    }

    /// <summary>进入会话（双击等入口）：会话态置位（草稿＝显示名）后聚焦宿主内编辑器并
    /// 全选（Dispatcher Input 优先级——模板触发器先落定编辑器再聚焦）。</summary>
    public void Begin(TSubject subject, FrameworkElement host)
    {
        _beginSession(subject);
        _dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!_isEditing(subject) ||
                    _findDescendantEditor(host) is not { } editor)
                {
                    return;
                }

                editor.Focus();
                editor.SelectAll();
            }));
    }

    /// <summary>提交会话（Enter／失焦／点外／切走冲刷共用）：会话态终结读取草稿→表面
    /// 重估→乐观保存回调（等值短路在回调内——各调用面单点）。无会话＝幂等零动作。</summary>
    public void Commit(TSubject subject, string draftName)
    {
        if (!_isEditing(subject))
        {
            return;
        }

        var name = _endSession(subject, draftName);
        _afterSessionEnd();
        _saveIfChanged(subject, name);
    }

    /// <summary>取消会话（Esc＝取消丢弃不变）：草稿丢弃、名称保持、组合标记清理。</summary>
    public void Cancel(TSubject subject, TextBox editor)
    {
        _compositions.Remove(editor);
        _cancelSession(subject);
        _afterSessionEnd();
    }

    public TextBox? ActiveEditor()
    {
        var subject = _subjects().FirstOrDefault(_isEditing);
        return subject is null ? null : _findEditor(subject);
    }

    public void CommitActiveSession()
    {
        var subject = _subjects().FirstOrDefault(_isEditing);
        if (subject is null ||
            _findEditor(subject) is not { } editor)
        {
            return;
        }

        // 组合标记随终结清理（镜像失焦路径）：会话终结后组合完成事件不再到来，
        // 残留会使同一编辑器下次 Enter/Esc 失效。
        _compositions.Remove(editor);
        Commit(subject, editor.Text);
    }

    /// <summary>编辑器键位：Enter 提交、Esc 取消丢弃；IME 组合期属输入法操作，不触发。</summary>
    public void HandleKeyDown(KeyEventArgs e)
    {
        if (e.OriginalSource is not TextBox { DataContext: TSubject subject } editor ||
            _compositions.Contains(editor))
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Commit(subject, editor.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancel(subject, editor);
        }
    }

    /// <summary>失焦＝保存（与 Enter 同一提交路径；组合标记一并清理）。</summary>
    public void HandleLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is not TextBox { DataContext: TSubject subject } editor)
        {
            return;
        }

        _compositions.Remove(editor);
        Commit(subject, editor.Text);
    }

    /// <summary>草稿限长归一（IME 组合中的限长交由组合终结后统一收敛——组合标记期跳过）。</summary>
    public void HandleTextChanged(TextChangedEventArgs e)
    {
        if (e.OriginalSource is not TextBox { DataContext: TSubject subject } editor ||
            !_isEditing(subject) ||
            _compositions.Contains(editor))
        {
            return;
        }

        var limited = _limitDraft(editor.Text);
        if (limited == editor.Text)
        {
            return;
        }

        editor.Text = limited;
        editor.CaretIndex = editor.Text.Length;
    }

    /// <summary>IME 组合守卫：组合中的会话编辑器标记（组合期 Enter/Esc 属输入法操作）。</summary>
    public void HandleCompositionStartedOrUpdated(TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox { DataContext: TSubject subject } editor &&
            _isEditing(subject))
        {
            _compositions.Add(editor);
        }
    }

    public void HandleCompositionCompleted(TextCompositionEventArgs e)
    {
        if (e.OriginalSource is TextBox editor)
        {
            _compositions.Remove(editor);
        }
    }
}
