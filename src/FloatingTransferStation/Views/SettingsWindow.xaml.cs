using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    // 内容滚动区最高点让窗口整体矮于工作区 WorkAreaHeightMargin；
    // 头部行高与 SettingsWindow.xaml 的 RowDefinition 保持一致。
    private const double WorkAreaHeightMargin = 24;
    private const double HeaderRowHeight = 46;
    private const double MinContentHeight = 160;

    private readonly ISettingsHost _host;
    private bool _isSynchronizingControls;
    private bool _micaApplied;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        DesignThemeManager.Apply(this, host.CurrentTheme);

        // 限高：内容高于工作区时滚动区收缩并由 ScrollViewer 滚动，而不是顶满整屏、
        // 把下缘推出屏幕且垂直拖不动。上限必须落在 ScrollViewer 上——窗口 MaxHeight
        // 只裁剪 HWND，内部布局仍按内容全高排布，滚动区拿不到受限视口。
        var contentCap = Math.Max(
            MinContentHeight,
            SystemParameters.WorkArea.Height - WorkAreaHeightMargin - HeaderRowHeight);
        SettingsScrollHost.MaxHeight = contentCap;
        MaxHeight = contentCap + HeaderRowHeight;

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
        GlobalHotkeyToggle.IsChecked = host.CurrentPreferences.GlobalHotkeyEnabled;
        RightClickCopyToggle.IsChecked = host.CurrentPreferences.RightClickCardCopyEnabled;
        CtrlCCopyToggle.IsChecked = host.CurrentPreferences.CopySelectionWithCtrlCEnabled;
        TrashLeftClickComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "清空非置顶", Tag = TrashNoSelectionLeftClickAction.ClearNonPinned },
            new ComboBoxItem { Content = "清空全部", Tag = TrashNoSelectionLeftClickAction.ClearAll }
        };
        TrashLeftClickComboBox.SelectedItem =
            ((ComboBoxItem[])TrashLeftClickComboBox.ItemsSource)[(int)host.CurrentPreferences.TrashNoSelectionLeftClick];
        TrashRightClickComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "清空全部", Tag = TrashNoSelectionRightClickAction.ClearAll },
            new ComboBoxItem { Content = "清空非置顶", Tag = TrashNoSelectionRightClickAction.ClearNonPinned },
            new ComboBoxItem { Content = "无操作", Tag = TrashNoSelectionRightClickAction.NoAction }
        };
        TrashRightClickComboBox.SelectedItem = ((ComboBoxItem[])TrashRightClickComboBox.ItemsSource)
            .Single(item => Equals(item.Tag, host.CurrentPreferences.TrashNoSelectionRightClick));
        _isSynchronizingControls = false;

        DataDirectoryText.Text = host.DataDirectory;
        AppNameRun.Text = ProductIdentity.DisplayName;
        VersionRun.Text = $"版本 {ProductIdentity.Version}";
        PopulatePluginSection();
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
        SizeChanged += SettingsWindow_SizeChanged;
    }

    /// <summary>
    /// SizeToContent 定稿实际高度后（首帧渲染前的布局期），把窗口上下缘钳回工作区。
    /// 初始放置时实际高度未测量，只能先按头部可见放置；内容较高时窗口下缘会探出
    /// 工作区，此处收口，同时保证垂直拖拽钳制在矮屏上仍有向上空间。
    /// </summary>
    private void SettingsWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - ActualWidth));
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>头部拖拽移动窗口；与主窗一致限制在工作区内。</summary>
    private void HeaderDragRegion_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        Left = Math.Clamp(Left + e.HorizontalChange, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top + e.VerticalChange, work.Top, Math.Max(work.Top, work.Bottom - Height));
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

    private void GlobalHotkeyToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyGlobalHotkeyFromToggle(enable: true);

    private void GlobalHotkeyToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyGlobalHotkeyFromToggle(enable: false);

    private void ApplyGlobalHotkeyFromToggle(bool enable)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        if (!_host.TryApplyGlobalHotkeyPreference(enable))
        {
            GlobalHotkeyStatusText.Text = "全局快捷键注册失败，可能被其他软件占用。";
            GlobalHotkeyStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            GlobalHotkeyStatusText.Text = string.Empty;
            GlobalHotkeyStatusText.Visibility = Visibility.Collapsed;
        }

        // 回显实际状态：注册失败的开启请求不落偏好，开关退回关闭。
        _isSynchronizingControls = true;
        GlobalHotkeyToggle.IsChecked = _host.CurrentPreferences.GlobalHotkeyEnabled;
        _isSynchronizingControls = false;
    }

    private void RightClickCopyToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyCopyGesturePreference(rightClickCardCopy: true);

    private void RightClickCopyToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyCopyGesturePreference(rightClickCardCopy: false);

    private void ApplyCopyGesturePreference(bool rightClickCardCopy)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            RightClickCardCopyEnabled = rightClickCardCopy
        });
    }

    private void CtrlCCopyToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyCtrlCCopyPreference(enabled: true);

    private void CtrlCCopyToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyCtrlCCopyPreference(enabled: false);

    private void ApplyCtrlCCopyPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            CopySelectionWithCtrlCEnabled = enabled
        });
    }

    private void TrashLeftClickComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            TrashLeftClickComboBox.SelectedItem is not ComboBoxItem { Tag: TrashNoSelectionLeftClickAction action })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { TrashNoSelectionLeftClick = action });
    }

    private void TrashRightClickComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            TrashRightClickComboBox.SelectedItem is not ComboBoxItem { Tag: TrashNoSelectionRightClickAction action })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { TrashNoSelectionRightClick = action });
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

    /// <summary>
    /// 填充插件区块：每个插件一行（名称/版本 + 描述或加载错误 + 启用开关）。
    /// 清单非法的插件保留在列表中并显示原因，开关禁用。
    /// </summary>
    private void PopulatePluginSection()
    {
        var catalog = _host.PluginCatalog;
        if (catalog is null)
        {
            PluginDirectoryText.Text = string.Empty;
            OpenPluginDirectoryButton.Visibility = Visibility.Collapsed;
            PluginHelpText.Text = "插件系统在本次运行中不可用。";
            return;
        }

        PluginDirectoryText.Text = catalog.UserPluginsDirectory;
        foreach (var entry in catalog.Entries)
        {
            PluginListPanel.Children.Add(CreatePluginRow(entry));
        }

        if (catalog.Entries.Count == 0)
        {
            PluginHelpText.Text = "未发现插件。把插件文件夹放入下面的目录后重启应用即可出现。";
            return;
        }

        PluginHelpText.Text = "插件默认关闭；启用后立即生效并自动保存。放入用户目录的同名插件会覆盖内建版本。";
    }

    private Grid CreatePluginRow(PluginEntry entry)
    {
        var row = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = entry.Manifest is { } manifest
            ? $"{manifest.Name}  {manifest.Version}"
            : $"{System.IO.Path.GetFileName(entry.Directory)}（无法加载）";
        var detail = entry.Manifest is { } valid
            ? (valid.Description ?? (valid.Kind == PluginKind.TextCleaner ? "文本整理插件" : string.Empty))
            : entry.LoadError ?? "清单无效。";

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
        var titleBlock = new TextBlock
        {
            Text = title,
            Foreground = TryFindResource("PrimaryTextBrush") as System.Windows.Media.Brush,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var detailBrush = entry.Manifest is null
            ? TryFindResource("DangerBrush") as System.Windows.Media.Brush
            : TryFindResource("SecondaryTextBrush") as System.Windows.Media.Brush;
        var detailBlock = new TextBlock
        {
            Text = detail,
            Foreground = detailBrush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        textPanel.Children.Add(titleBlock);
        textPanel.Children.Add(detailBlock);
        Grid.SetColumn(textPanel, 0);
        row.Children.Add(textPanel);

        var toggle = new CheckBox
        {
            Style = TryFindResource("ToggleSwitchStyle") as Style,
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = entry.Enabled,
            IsEnabled = entry.Manifest is not null,
            Tag = entry.Id
        };
        System.Windows.Automation.AutomationProperties.SetName(
            toggle,
            $"{title}插件开关");
        toggle.Checked += PluginToggle_StateChanged;
        toggle.Unchecked += PluginToggle_StateChanged;
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

        return row;
    }

    private async void PluginToggle_StateChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingControls ||
            sender is not CheckBox { Tag: string pluginId } toggle)
        {
            return;
        }

        // await 前读取开关状态，续延不再依赖控件访问。
        var enabled = toggle.IsChecked == true;
        try
        {
            await _host.ApplyPluginEnabledAsync(pluginId, enabled);
            PluginStatusText.Text = string.Empty;
            PluginStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PluginStatusText.Text = "插件设置暂未保存，请稍后重试。";
            PluginStatusText.Visibility = Visibility.Visible;
        }
    }

    private void OpenPluginDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var directory = _host.PluginCatalog?.UserPluginsDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            using var _ = Process.Start(new ProcessStartInfo(directory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            PluginStatusText.Text = "无法打开插件目录。";
            PluginStatusText.Visibility = Visibility.Visible;
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
