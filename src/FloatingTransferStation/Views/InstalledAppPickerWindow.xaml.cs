using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 已安装应用选择器（快捷启动 #30 切片 4）：AppsFolder 枚举结果的会话内挑选面。
/// 多选复选 + 搜索过滤（区域文化不区分大小写包含）；确认回传选中的 AUMID 清单，
/// 取消/关闭/Esc 回传空。已添加条目仅展示灰字标记，重复添加由目录去重路径收敛
/// （滚动至已有瓦片 + accent 脉冲）。
/// </summary>
internal partial class InstalledAppPickerWindow : Window
{
    /// <summary>行视图态：勾选与已添加标记（UI 线程读写）。App 为不可变记录。</summary>
    internal sealed class PickerRow(InstalledApp app, bool alreadyAdded)
    {
        public InstalledApp App { get; } = app;

        public bool AlreadyAdded { get; } = alreadyAdded;

        public bool IsChecked { get; set; }

        public System.Windows.Visibility AlreadyAddedTextVisibility =>
            AlreadyAdded ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    private readonly List<PickerRow> _rows;
    private bool _shownAsDialog;

    /// <summary>确认结果（空 = 取消）。ShowDialog 后读取。</summary>
    public IReadOnlyList<InstalledApp> Selection { get; private set; } = [];

    /// <summary>模态标记（DialogResult 只在 ShowDialog 路径合法；测试以 Show 驱动）。</summary>
    public new bool? ShowDialog()
    {
        _shownAsDialog = true;
        return base.ShowDialog();
    }

    public InstalledAppPickerWindow(
        DesignTheme theme,
        IReadOnlyList<InstalledApp> installedApps,
        IReadOnlySet<string> existingAumids)
    {
        InitializeComponent();
        DesignThemeManager.Apply(this, theme);

        var existing = existingAumids ?? throw new ArgumentNullException(nameof(existingAumids));
        _rows = installedApps
            .Select(app => new PickerRow(app, existing.Contains(app.Aumid)))
            .ToList();
        RebuildVisibleRows();
    }

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
            rows = _rows.Where(row => row.App.DisplayName.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
        }

        PickerList.Items.Clear();
        foreach (var row in rows)
        {
            PickerList.Items.Add(row);
        }

        var visibleCount = PickerList.Items.Count;
        PickerEmptyHint.Text = _rows.Count == 0
            ? "未枚举到已安装应用"
            : "没有匹配的应用";
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

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Selection = _rows.Where(row => row.IsChecked).Select(row => row.App).ToList();
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
