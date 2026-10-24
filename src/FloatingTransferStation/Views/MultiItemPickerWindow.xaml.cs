using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 通用多选选择器（#45 泛化自 #30 切片 4 的已安装应用选择器）：行模型＝标题/
/// 副题/已添加标记/勾选态，<see cref="PickerRow.Payload"/> 由调用方携带业务对象
/// 映射喂入——已安装应用与启动·自动发现清单共用同一窗口（多选复选 + 搜索过滤，
/// 区域文化不区分大小写包含；确认回传勾选的行清单，取消/关闭/Esc 回传空）。
/// 已添加条目仅展示灰字标记，重复添加由调用方目录去重路径收敛。
/// </summary>
internal partial class MultiItemPickerWindow : Window
{
    /// <summary>行视图态：勾选与已添加标记（UI 线程读写）。Payload 为不可变业务对象。</summary>
    internal sealed class PickerRow(object payload, string title, string? subtitle, bool alreadyAdded)
    {
        public object Payload { get; } = payload;

        public string Title { get; } = title;

        public string? Subtitle { get; } = subtitle;

        public bool AlreadyAdded { get; } = alreadyAdded;

        public bool IsChecked { get; set; }

        public System.Windows.Visibility AlreadyAddedTextVisibility =>
            AlreadyAdded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public System.Windows.Visibility SubtitleVisibility =>
            string.IsNullOrWhiteSpace(Subtitle)
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
    }

    private const string NoMatchHintText = "没有匹配的应用";

    private readonly List<PickerRow> _rows;
    private readonly string _emptySourceHint;
    private bool _shownAsDialog;

    /// <summary>确认结果（空 = 取消；勾选行的 Payload 保留原对象身份）。ShowDialog 后读取。</summary>
    public IReadOnlyList<object> Selection { get; private set; } = [];

    /// <summary>批量动作结果（#45 发现清单「全部忽略」）：经批量钮关闭时为 true，
    /// 此时 <see cref="Selection"/> 为空——批量语义由调用方解释（对整份源生效，
    /// 不止当前过滤视图）。</summary>
    public bool BulkActionChosen { get; private set; }

    /// <summary>模态标记（DialogResult 只在 ShowDialog 路径合法；测试以 Show 驱动）。</summary>
    public new bool? ShowDialog()
    {
        _shownAsDialog = true;
        return base.ShowDialog();
    }

    public MultiItemPickerWindow(
        DesignTheme theme,
        string title,
        string searchPlaceholder,
        string emptySourceHint,
        IReadOnlyList<PickerRow> rows,
        string? bulkActionText = null)
    {
        InitializeComponent();
        _emptySourceHint = emptySourceHint;
        _rows = [.. rows];
        Title = title;
        PickerTitleText.Text = title;
        PickerSearchHint.Text = searchPlaceholder;
        AutomationProperties.SetName(CloseButton, $"关闭{title}");
        AutomationProperties.SetName(PickerSearchBox, searchPlaceholder);
        AutomationProperties.SetName(PickerConfirmButton, $"确认{title}");
        if (bulkActionText is null)
        {
            PickerBulkActionButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            PickerBulkActionButton.Content = bulkActionText;
            AutomationProperties.SetName(PickerBulkActionButton, bulkActionText);
        }

        DesignThemeManager.Apply(this, theme);
        // 构造期应用进程级当前界面字体（偏好 InterfaceFont；null=默认），与主题应用同点。
        InterfaceFontManager.Apply(this);
        // D8：DWM 圆角＋系统阴影唯一来源（失败由 DialogShellFallback 回退直角矩形）。
        SourceInitialized += (_, _) =>
        {
            _dialogShellApplied = DwmWindowEffects.TryApplyDialogShell(
                new System.Windows.Interop.WindowInteropHelper(this).Handle,
                theme == DesignTheme.Dark);
            DialogShellFallback.Apply(this, _dialogShellApplied);
        };

        RebuildVisibleRows();
    }

    private bool _dialogShellApplied;

    /// <summary>圆角/阴影 DWM 接入探针（D8 坑清单；测试与诊断用）。</summary>
    internal bool DialogShellAppliedForTests => _dialogShellApplied;

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void PickerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void PickerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RebuildVisibleRows();
    }

    /// <summary>按搜索词重建可见行（勾选状态保留在行对象上，过滤不丢已选）。</summary>
    private void RebuildVisibleRows()
    {
        var keyword = PickerSearchBox.Text.Trim();
        PickerSearchHint.Visibility = keyword.Length == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        IEnumerable rows = _rows;
        if (keyword.Length > 0)
        {
            rows = _rows.Where(row => row.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        }

        PickerList.Items.Clear();
        foreach (var row in rows)
        {
            PickerList.Items.Add(row);
        }

        var visibleCount = PickerList.Items.Count;
        PickerEmptyHint.Text = _rows.Count == 0 ? _emptySourceHint : NoMatchHintText;
        PickerEmptyHint.Visibility = visibleCount == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        UpdateConfirmButton();
    }

    private void UpdateConfirmButton()
    {
        var checkedCount = _rows.Count(row => row.IsChecked);
        PickerConfirmButton.Content = checkedCount > 0 ? $"添加（{checkedCount}）" : "添加";
        PickerConfirmButton.IsEnabled = checkedCount > 0;
    }

    /// <summary>行勾选变化经路由事件从模板回传（CheckBox 绑定行对象，无 VM 层）。</summary>
    private void PickerList_Checked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is CheckBox { DataContext: PickerRow })
        {
            UpdateConfirmButton();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) =>
        CloseWithResult(_rows.Where(row => row.IsChecked).Select(row => row.Payload).ToList());

    private void BulkActionButton_Click(object sender, RoutedEventArgs e)
    {
        BulkActionChosen = true;
        CloseWithResult([]);
    }

    private void CloseWithResult(IReadOnlyList<object> selection)
    {
        Selection = selection;
        if (_shownAsDialog)
        {
            // DialogResult 赋值本身即关闭对话框窗口。
            DialogResult = true;
        }
        else
        {
            Close();
        }
    }
}
