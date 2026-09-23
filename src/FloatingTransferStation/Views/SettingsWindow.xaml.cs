using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using FloatingTransferStation;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 设置首版：主题（跟随系统/浅色/深色）、动效、开机自启、数据目录与关于。
/// 所有改动立即经宿主生效并异步原子持久化，没有确认按钮；
/// 退出按钮走主窗既有 Closing 冲刷序列。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private bool _isSynchronizingControls;
    private bool _micaApplied;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        DesignThemeManager.Apply(this, host.CurrentTheme);

        _isSynchronizingControls = true;
        ThemeComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "跟随系统", Tag = ThemePreference.FollowSystem },
            new ComboBoxItem { Content = "浅色", Tag = ThemePreference.Light },
            new ComboBoxItem { Content = "深色", Tag = ThemePreference.Dark }
        };
        ThemeComboBox.SelectedIndex = (int)host.CurrentPreferences.ThemeMode;
        AnimationsToggle.IsChecked = host.CurrentPreferences.AnimationsEnabled;
        StartupToggle.IsChecked = host.StartupManager.IsEnabled();
        _isSynchronizingControls = false;

        DataDirectoryText.Text = host.DataDirectory;
        AppNameRun.Text = ProductIdentity.DisplayName;
        VersionRun.Text = $"版本 {ProductIdentity.Version}";
        Owner = host.HostWindow;
        SourceInitialized += SettingsWindow_SourceInitialized;
    }

    /// <summary>主窗主题变化时同步设置窗的主题字典与沉浸式深色。</summary>
    internal void ApplyTheme(DesignTheme theme)
    {
        DesignThemeManager.Apply(this, theme);
        DwmWindowEffects.UpdateImmersiveDarkMode(
            new System.Windows.Interop.WindowInteropHelper(this).Handle,
            theme == DesignTheme.Dark);
    }

    private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _micaApplied = DwmWindowEffects.TryApplyMaterial(
            handle,
            _host.CurrentTheme == DesignTheme.Dark);
        if (!_micaApplied)
        {
            WindowShell.Background =
                TryFindResource("WindowShellOpaqueBrush") as System.Windows.Media.Brush
                ?? WindowShell.Background;
        }

        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, Math.Min(Owner.Left - Width - 12, work.Right - Width));
        Top = Math.Clamp(Owner.Top, work.Top, Math.Max(work.Top, work.Bottom - 44));
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: ThemePreference mode })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { ThemeMode = mode });
    }

    private void AnimationsToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyAnimationsFromToggle(true);

    private void AnimationsToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyAnimationsFromToggle(false);

    private void ApplyAnimationsFromToggle(bool animationsEnabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            AnimationsEnabled = animationsEnabled
        });
    }

    private void StartupToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyStartupFromToggle(enable: true);

    private void StartupToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyStartupFromToggle(enable: false);

    private void ApplyStartupFromToggle(bool enable)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        try
        {
            if (enable)
            {
                _host.StartupManager.Enable();
            }
            else
            {
                _host.StartupManager.Disable();
            }

            StartupStatusText.Visibility = Visibility.Collapsed;
            StartupStatusText.Text = string.Empty;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            StartupStatusText.Text = "自启设置暂未写入注册表，请稍后重试。";
            StartupStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            _isSynchronizingControls = true;
            StartupToggle.IsChecked = _host.StartupManager.IsEnabled();
            _isSynchronizingControls = false;
        }
    }

    private void OpenDataDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (string.IsNullOrWhiteSpace(_host.DataDirectory))
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_host.DataDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StartupStatusText.Text = "无法打开数据目录。";
            StartupStatusText.Visibility = Visibility.Visible;
        }
    }

    private void RepoLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 打不开浏览器时保持链接可见即可，不再打扰。
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _host.RequestApplicationExit();
    }

    private void WindowShell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WindowShell.Clip = WindowShellClip.Create(
            e.NewSize.Width,
            e.NewSize.Height,
            FloatingTransferStation.Design.DesignTokens.DwmCornerRadius);
    }
}
