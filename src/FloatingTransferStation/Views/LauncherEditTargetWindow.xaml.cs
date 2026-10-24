using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>编辑目标回传（#30 修订 2026-10-18，Issue #186 D7）：确认后的目标三元组；
/// Url 已是规范形（LauncherUrlInput 唯一 ingress），取消/关闭/Esc 不回传。</summary>
internal sealed record LauncherEditedTarget(
    LauncherKind Kind,
    string TargetPath,
    string? Arguments,
    string? WorkingDirectory);

/// <summary>
/// 编辑目标对话框（D7）：目标创建后可改。Url=地址字段（复用创建入口归一化与
/// 内联报错）；Application=目标路径＋「浏览…」＋可折叠高级区（参数/起始位置）；
/// PackagedApp=只读展示 AUMID＋「重新选择…」走已安装应用选择器预选。壳=D8 统一
/// 对话框壳（不透明表面＋DWM 圆角/阴影唯一来源）；主按钮「保存」，Esc=取消。
/// 重复拒绝在目录 UpdateTargetAsync（Duplicate→状态条，对话框不突变）。
/// </summary>
internal partial class LauncherEditTargetWindow : Window
{
    private readonly LauncherKind _kind;
    private bool _shownAsDialog;
    private bool _dialogShellApplied;

    /// <summary>确认结果（null = 取消/未确认）。</summary>
    public LauncherEditedTarget? Result { get; private set; }

    /// <summary>选择器对话框显示（测试缝：同创建入口 _showPickerDialog）。</summary>
    internal Func<MultiItemPickerWindow, bool?> _showPickerDialog = static picker => picker.ShowDialog();

    /// <summary>模态标记（DialogResult 只在 ShowDialog 路径合法；测试以 Show 驱动）。</summary>
    public new bool? ShowDialog()
    {
        _shownAsDialog = true;
        return base.ShowDialog();
    }

    public LauncherEditTargetWindow(DesignTheme theme, LauncherItem item)
    {
        InitializeComponent();
        _kind = item.Kind;
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
        TargetInputBox.Text = item.TargetPath;
        ArgumentsBox.Text = item.Arguments ?? string.Empty;
        WorkingDirectoryBox.Text = item.WorkingDirectory ?? string.Empty;
        switch (item.Kind)
        {
            case LauncherKind.Url:
                DialogTitle.Text = "编辑网页地址";
                TargetInputHint.Text = "例如 github.com";
                break;
            case LauncherKind.Application:
                DialogTitle.Text = "编辑应用目标";
                TargetInputBox.SetValue(AutomationProperties.NameProperty, "目标路径");
                TargetInputHint.Text = string.Empty;
                BrowseButton.Visibility = Visibility.Visible;
                AdvancedSection.Visibility = Visibility.Visible;
                break;
            case LauncherKind.PackagedApp:
                DialogTitle.Text = "编辑打包应用";
                TargetInputBox.SetValue(AutomationProperties.NameProperty, "应用标识（AUMID）");
                TargetInputHint.Text = string.Empty;
                BrowseButton.Visibility = Visibility.Visible;
                BrowseButton.Content = "重新选择…";
                break;
        }

        UpdatePlaceholderVisibility();
        Loaded += (_, _) => TargetInputBox.Focus();
    }

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

    private void EditTargetWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void TargetInputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdatePlaceholderVisibility();
        if (ErrorText.Visibility != Visibility.Collapsed)
        {
            // 输入变化即失效既有内联错误（错误只描述上一次确认尝试）。
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdatePlaceholderVisibility()
    {
        TargetInputHint.Visibility = TargetInputBox.Text.Length == 0 && TargetInputHint.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_kind == LauncherKind.PackagedApp)
        {
            OpenPackagedAppPicker();
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择应用",
            Filter = "应用程序|*.exe;*.lnk"
        };
        if (dialog.ShowDialog(this) == true)
        {
            TargetInputBox.Text = dialog.FileName;
        }
    }

    private void OpenPackagedAppPicker()
    {
        var currentAumid = TargetInputBox.Text;
        var picker = new MultiItemPickerWindow(
            DesignThemeManager.DetectSystemTheme(),
            "添加已安装应用",
            "搜索应用",
            "未枚举到已安装应用",
            InstalledAppsEnumerator.Enumerate()
                .Select(app => new MultiItemPickerWindow.PickerRow(
                    app,
                    app.DisplayName,
                    subtitle: null,
                    string.Equals(app.Aumid, currentAumid, StringComparison.Ordinal)))
                .ToList())
        { Owner = this };
        if (_showPickerDialog(picker) == true &&
            picker.Selection.OfType<InstalledApp>().FirstOrDefault() is { } chosen)
        {
            TargetInputBox.Text = chosen.Aumid;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetInputBox.Text;
        if (_kind == LauncherKind.Url)
        {
            // Url 唯一 ingress：确认前归一化（拒绝=内联报错不关窗，与创建入口同语义）。
            var normalization = LauncherUrlInput.Normalize(target);
            if (!normalization.Success)
            {
                ErrorText.Text = normalization.Error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            target = normalization.Url;
        }
        else if (_kind == LauncherKind.Application && string.IsNullOrWhiteSpace(target))
        {
            ErrorText.Text = "目标路径不能为空。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new LauncherEditedTarget(
            _kind,
            target.Trim(),
            _kind == LauncherKind.Application ? NullIfEmpty(ArgumentsBox.Text) : null,
            _kind == LauncherKind.Application ? NullIfEmpty(WorkingDirectoryBox.Text) : null);
        if (_shownAsDialog)
        {
            DialogResult = true;
        }
        else
        {
            Close();
        }
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>测试入口：与 SaveButton_Click 同一确认管线（内部方法直呼）。</summary>
    internal void SaveButton_ClickForTests() =>
        SaveButton_Click(SaveButton, new RoutedEventArgs(Button.ClickEvent, SaveButton));
}
