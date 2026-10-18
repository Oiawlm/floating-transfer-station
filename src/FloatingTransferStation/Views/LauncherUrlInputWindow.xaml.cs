using System.Windows;
using System.Windows.Input;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 网页地址输入对话框（#30 修订，Issue #179）：单行输入＋内联错误，零网络。
/// 归一化唯一经 Core <see cref="LauncherUrlInput"/>（创建入口 ingress）；确认回传
/// 规范形结果，取消/关闭/Esc 回传 null。Enter=确定（确认钮 IsDefault）、Esc=取消。
/// </summary>
internal partial class LauncherUrlInputWindow : Window
{
    private bool _shownAsDialog;

    /// <summary>确认结果（null = 取消/未确认；Success=true 时含规范形网址与默认名）。</summary>
    public LauncherUrlNormalization? Result { get; private set; }

    /// <summary>模态标记（DialogResult 只在 ShowDialog 路径合法；测试以 Show 驱动）。</summary>
    public new bool? ShowDialog()
    {
        _shownAsDialog = true;
        return base.ShowDialog();
    }

    public LauncherUrlInputWindow(DesignTheme theme)
    {
        InitializeComponent();
        DesignThemeManager.Apply(this, theme);
        // D8：DWM 圆角＋系统阴影唯一来源（失败由 DialogShellFallback 回退直角矩形）。
        SourceInitialized += (_, _) =>
        {
            _dialogShellApplied = DwmWindowEffects.TryApplyDialogShell(
                new System.Windows.Interop.WindowInteropHelper(this).Handle,
                theme == DesignTheme.Dark);
            DialogShellFallback.Apply(this, _dialogShellApplied);
        };
        Loaded += (_, _) => UrlInputBox.Focus();
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

    private void UrlInputWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void UrlInputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UrlInputHint.Visibility = UrlInputBox.Text.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (UrlErrorText.Visibility != Visibility.Collapsed)
        {
            // 输入变化即失效既有内联错误（错误只描述上一次确认尝试）。
            UrlErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        var normalization = LauncherUrlInput.Normalize(UrlInputBox.Text);
        if (!normalization.Success)
        {
            // 拒绝=就地内联报错，不关窗、不建条目。
            UrlErrorText.Text = normalization.Error;
            UrlErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = normalization;
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
