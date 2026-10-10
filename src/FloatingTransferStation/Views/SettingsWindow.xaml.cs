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
    private bool _isChangingDataDirectory;

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
        AutoCleanupToggle.IsChecked = host.CurrentPreferences.AutoCleanupEnabled;
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
        PopulateCategoryOrderSection();
        Owner = host.HostWindow;
        // 安全网钳位必须挂在构造函数：SizeToContent 的测量与尺寸变化事件在
        // SourceInitialized 之前就已全部发生（探针实测 Loaded 先于 SourceInitialized），
        // 挂晚了整个初始放置过程没有任何钳位（1.15.1 缺陷）；此后尺寸再变化
        // （如运行时改缩放）仍由它持续兜底。
        SizeChanged += SettingsWindow_SizeChanged;
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
        // 整窗入屏：下缘约束用已定稿的实际高度（SizeToContent 在 HWND 创建前完成测量，
        // 探针实测 SourceInitialized 时 ActualHeight 已是定稿值），不再用 1.15.1 的
        // work.Bottom-44——它只保证顶部一条 44px 带可见，与真实高度无关，窗口高度接近
        // 整屏时下缘照样探出工作区。若个别环境此时尚未定稿，构造函数挂上的 SizeChanged
        // 钳位会在定稿后收口。本次只承诺主屏工作区（SystemParameters.WorkArea）；
        // 按窗口所在显示器取工作区（MonitorFromWindow）为后续增强。
        Top = Math.Clamp(Owner.Top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    /// <summary>
    /// 尺寸定稿或打开后再变化（如运行时改缩放）时，把窗口上下缘钳回工作区。
    /// 探针实测 SizeToContent 的测量与尺寸事件先于 SourceInitialized 发生，该处理器
    /// 必须在构造函数挂接才覆盖初始放置；Top/Left 尚为 NaN（未放置）时 Clamp 结果
    /// 仍为 NaN，赋回等于保持未设置。同时保证垂直拖拽钳制在矮屏上仍有向上空间。
    /// 只按主屏工作区（SystemParameters.WorkArea）钳制。
    /// </summary>
    private void SettingsWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - ActualWidth));
    }

    private void SettingsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _categoryOrderDrag is not null)
        {
            // 拖拽会话中的 Esc 属「标签顺序」节：回弹原序不提交，优先于关窗路径消费。
            e.Handled = true;
            CancelCategoryOrderDrag();
            return;
        }

        if (e.Key == Key.Escape && !_isChangingDataDirectory)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>头部拖拽移动窗口；与主窗一致限制在工作区内。</summary>
    private void HeaderDragRegion_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        // 垂直钳制用渲染定稿的 ActualHeight 而非 Height：Height 依赖 .NET 10 把
        // SizeToContent 定稿值写回的行为，写回缺失时 NaN 会让上界钳制静默失效。
        Left = Math.Clamp(Left + e.HorizontalChange, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top + e.VerticalChange, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!_isChangingDataDirectory)
        {
            Close();
        }
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

    private void AutoCleanupToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyAutoCleanupPreference(enabled: true);

    private void AutoCleanupToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyAutoCleanupPreference(enabled: false);

    private void ApplyAutoCleanupPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            AutoCleanupEnabled = enabled
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
            ShowDataDirectoryStatus("无法打开数据目录。");
        }
    }

    /// <summary>
    /// 更改数据目录：选父文件夹 → 预检（形状/可写/卷空间/目标不存在）→ 确认对话框
    /// （源→目标、数据体积、自动重启、旧目录处置）→ 迁移并自动重启。迁移期间
    /// 锁定设置内容并显示进度，失败恢复原状并提示。
    /// </summary>
    private async void ChangeDataDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isChangingDataDirectory)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择内容存储父文件夹（将在其中创建 悬浮中转站\\Data）"
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var preview = _host.DescribeDataDirectoryChange(dialog.FolderName);
        if (!preview.Valid || preview.TargetDataDirectory is null)
        {
            ShowDataDirectoryStatus(preview.Refusal ?? "无法使用所选位置。");
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"内容存储位置将变更：\n\n当前位置：{_host.DataDirectory}\n新位置：{preview.TargetDataDirectory}\n数据体积：约 {FormatByteSize(preview.SizeBytes)}\n\n迁移完成并登记后应用会自动重启；旧目录会在下次启动并通过安全校验后自动清理。\n\n现在开始迁移？",
            "更改内容存储位置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes)
        {
            return;
        }

        _isChangingDataDirectory = true;
        SettingsScrollHost.IsEnabled = false;
        ShowDataDirectoryStatus("正在准备迁移…");
        try
        {
            var progress = new Progress<DataDirectoryCopyProgress>(update =>
            {
                if (IsLoaded)
                {
                    ShowDataDirectoryStatus(
                        $"正在迁移内容… {FormatByteSize(update.CopiedBytes)} / {FormatByteSize(update.TotalBytes)}");
                }
            });
            var result = await _host.ChangeDataDirectoryAsync(dialog.FolderName, progress);
            if (result.ExitApplication)
            {
                if (result.Error is not null)
                {
                    MessageBox.Show(
                        this,
                        result.Error,
                        ProductIdentity.DisplayName,
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            ShowDataDirectoryStatus(result.Error ?? "迁移未完成，原目录未做改动。");
        }
        finally
        {
            _isChangingDataDirectory = false;
            if (IsLoaded)
            {
                SettingsScrollHost.IsEnabled = true;
                HideDataDirectoryStatus();
            }
        }
    }

    private void ShowDataDirectoryStatus(string message)
    {
        DataDirectoryStatusText.Text = message;
        DataDirectoryStatusText.Visibility = Visibility.Visible;
    }

    private void HideDataDirectoryStatus()
    {
        DataDirectoryStatusText.Text = string.Empty;
        DataDirectoryStatusText.Visibility = Visibility.Collapsed;
    }

    private static string FormatByteSize(long bytes) => bytes switch
    {
        < 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024d:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):F1} MB",
        _ => $"{bytes / (1024d * 1024 * 1024):F2} GB"
    };

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
            ChangePluginDirectoryButton.Visibility = Visibility.Collapsed;
            ResetPluginDirectoryButton.Visibility = Visibility.Collapsed;
            PluginDirectoryNoteText.Visibility = Visibility.Collapsed;
            PluginHelpText.Text = "插件系统在本次运行中不可用。";
            return;
        }

        RefreshPluginDirectoryDisplay();
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

    private void RefreshPluginDirectoryDisplay()
    {
        PluginDirectoryText.Text = _host.EffectivePluginsDirectory;
        PluginDirectoryNoteText.Text = _host.PluginsDirectoryIsDefault
            ? "当前使用默认插件目录（数据目录下 plugins）；更改后重启生效，启用状态仍保存在数据目录。"
            : "自定义插件目录；更改后重启生效，启用状态仍保存在数据目录。";
    }

    private async void ChangePluginDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择插件文件夹" };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        await ApplyPluginsDirectoryAsync(dialog.FolderName);
    }

    private async void ResetPluginDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await ApplyPluginsDirectoryAsync(null);
    }

    private async Task ApplyPluginsDirectoryAsync(string? directoryOverride)
    {
        var error = await _host.ApplyPluginsDirectoryAsync(directoryOverride);
        if (error is not null)
        {
            PluginStatusText.Text = error;
            PluginStatusText.Visibility = Visibility.Visible;
            return;
        }

        PluginStatusText.Text = string.Empty;
        PluginStatusText.Visibility = Visibility.Collapsed;
        RefreshPluginDirectoryDisplay();
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
