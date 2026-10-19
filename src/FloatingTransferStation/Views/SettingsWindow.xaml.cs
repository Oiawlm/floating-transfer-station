using System.Diagnostics;
using System.Security.Cryptography;
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
    private readonly ISettingsHost _host;
    private bool _isSynchronizingControls;
    private bool _micaApplied;
    private bool _isChangingDataDirectory;
    private bool _isDeepSeekKeyVisible;
    private bool _isTestingDeepSeekConnection;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        InitializeComponent();
        DesignThemeManager.Apply(this, host.CurrentTheme);

        // 固定视口（契约 #24，Issue #106）：窗口高度不再跟随内容（SizeToContent=Manual），
        // 溢出由 SettingsScrollHost 滚动。窗口不声明 MaxWidth/MaxHeight（1.50.0 教训：
        // 属性会在 HWND 超宽/最大化时把根视觉钳出死区）；交互缩放区间与最大化几何
        // 全部经 WM_GETMINMAXINFO 轨道尺寸强制（SettingsWindowPlacement），恢复
        // 持久化几何时（SourceInitialized）再由 Normalize 按目标显示器工作区钳制。

        _isSynchronizingControls = true;
        ThemeComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "跟随系统", Tag = ThemePreference.FollowSystem },
            new ComboBoxItem { Content = "浅色", Tag = ThemePreference.Light },
            new ComboBoxItem { Content = "深色", Tag = ThemePreference.Dark }
        };
        ThemeComboBox.SelectedIndex = (int)host.CurrentPreferences.ThemeMode;
        PinnedDividerComboBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "无", Tag = PinnedDividerStyle.None },
            new ComboBoxItem { Content = "实线", Tag = PinnedDividerStyle.Solid },
            new ComboBoxItem { Content = "虚线", Tag = PinnedDividerStyle.Dashed },
            new ComboBoxItem { Content = "强调条", Tag = PinnedDividerStyle.AccentBar }
        };
        PinnedDividerComboBox.SelectedIndex = (int)host.CurrentPreferences.PinnedDivider;
        AnimationsToggle.IsChecked = host.CurrentPreferences.AnimationsEnabled;
        StartupToggle.IsChecked = host.StartupManager.IsEnabled();
        GlobalHotkeyToggle.IsChecked = host.CurrentPreferences.GlobalHotkeyEnabled;
        AutoUpdateCheckToggle.IsChecked = host.CurrentPreferences.AutoUpdateCheckEnabled;
        RightClickCopyToggle.IsChecked = host.CurrentPreferences.RightClickCardCopyEnabled;
        CtrlCCopyToggle.IsChecked = host.CurrentPreferences.CopySelectionWithCtrlCEnabled;
        AutoCleanupToggle.IsChecked = host.CurrentPreferences.AutoCleanupEnabled;
        // 档位显示文案由唯一映射派生（全仓小时数字面量只允许出现在映射与测试期望），
        // 枚举序≠显示序（零值是默认档 24h），选中项一律按 Tag 匹配、不依赖下标；
        // 总开关关闭时下拉置灰（档位仍照常持久化）。
        var retentionItems = new[]
        {
            AutoCleanupRetention.TwoHours,
            AutoCleanupRetention.FourHours,
            AutoCleanupRetention.SixHours,
            AutoCleanupRetention.EightHours,
            AutoCleanupRetention.TenHours,
            AutoCleanupRetention.TwelveHours,
            AutoCleanupRetention.TwentyFourHours
        }.Select(tier => new ComboBoxItem
        {
            Content = $"{AutoCleanupSchedule.GetRetentionDuration(tier).TotalHours:0} 小时",
            Tag = tier
        }).ToArray();
        AutoCleanupRetentionComboBox.ItemsSource = retentionItems;
        AutoCleanupRetentionComboBox.SelectedItem =
            retentionItems.FirstOrDefault(item =>
                item.Tag is AutoCleanupRetention tier &&
                tier == host.CurrentPreferences.AutoCleanupRetention)
            ?? retentionItems.Single(item =>
                item.Tag is AutoCleanupRetention tier &&
                tier == AutoCleanupRetention.TwentyFourHours);
        AutoCleanupRetentionComboBox.IsEnabled = host.CurrentPreferences.AutoCleanupEnabled;
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

        // AI 设置（1.27.0）：仅配置 API Key；模型恒为 DeepSeekClient.DefaultModel
        // （deepseek-flash，契约 #28），无选择 UI。
        // 存量 Key 解密后入框（明文只在内存）；解密失败按未配置降级并就地提示。
        var storedDeepSeekKey = host.CurrentPreferences.DeepSeekApiKeyProtected;
        if (!string.IsNullOrEmpty(storedDeepSeekKey))
        {
            var decryptedKey = _host.SecretProtector.Unprotect(storedDeepSeekKey);
            if (decryptedKey is not null)
            {
                DeepSeekKeyBox.Password = decryptedKey;
            }
            else
            {
                ShowDeepSeekStatus("API Key 无法解密，请重新填写。");
            }
        }

        // 整理提示词（1.52.0）：null 态不预填默认全文（保住「是否默认」判定
        // 基线），状态标签随回填显式当前口径。
        AiOrganizePromptBox.Text =
            host.CurrentPreferences.AiOrganizeSystemPromptOverride ?? string.Empty;

        _isSynchronizingControls = false;

        DataDirectoryText.Text = host.DataDirectory;
        AppNameRun.Text = ProductIdentity.DisplayName;
        VersionRun.Text = $"版本 {ProductIdentity.Version}";
        // repo 地址单一来源（契约 #26）：页脚链接与更新服务共用 ProductIdentity 常量，
        // XAML 不再维护字面量。
        RepoLink.NavigateUri = new Uri(ProductIdentity.GitHubRepoHtmlUrl);
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

    // ---- 双栏导航（契约 #24，Issue #106）----

    private static readonly string[] PageNames =
        ["AppearancePage", "PanelPage", "GeneralPage", "DataPage", "AiPage"];

    private void SettingsNavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string pageName })
        {
            SelectSettingsPage(pageName);
        }
    }

    private void SelectSettingsPage(string pageName)
    {
        foreach (var name in PageNames)
        {
            // InitializeComponent 解析中途首项 Checked 已触发，此时后续页尚未解析；
            // 各页初始可见性已在 XAML 声明，此处只补运行期切换。
            if (FindName(name) is not StackPanel page)
            {
                continue;
            }

            var selected = string.Equals(name, pageName, StringComparison.Ordinal);
            page.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (selected)
            {
                AnimatePageIn(page);
            }
        }

        // 共享视口的滚动偏移不跨页残留（切页回到页首）；构造期首项 Checked 时视口尚未解析。
        SettingsScrollHost?.ScrollToHome();
    }

    /// <summary>
    /// 页切换淡入（167ms 档，契约 #24 设计语言）：只动 Opacity，可见性已即时生效，
    /// UIA/命中不依赖动画收尾；「界面动效」关闭或构造期首项 Checked（未加载）直接就位。
    /// </summary>
    private void AnimatePageIn(StackPanel page)
    {
        page.BeginAnimation(UIElement.OpacityProperty, null);
        page.Opacity = 1d;
        if (!IsLoaded || !_host.CurrentPreferences.AnimationsEnabled)
        {
            return;
        }

        page.BeginAnimation(
            UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(
                0d,
                1d,
                TimeSpan.FromMilliseconds(FloatingTransferStation.Design.DesignTokens.NormalDurationMs)));
    }

    /// <summary>
    /// ↑/↓/Home/End 确定性切换导航项（契约 #24 键盘判据）。在隧道期消费，
    /// 压掉 RadioButton 的原生方向键导航，保证一次按键只前进一步。
    /// </summary>
    private void SettingsNavList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var offset = e.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.Home => int.MinValue,
            Key.End => int.MaxValue,
            _ => 0
        };
        if (offset == 0)
        {
            return;
        }

        e.Handled = true;
        var items = SettingsNavList.Children.OfType<RadioButton>().ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var current = Array.FindIndex(items, item => item.IsChecked == true);
        if (current < 0)
        {
            current = 0;
        }

        var next = offset switch
        {
            int.MinValue => 0,
            int.MaxValue => items.Length - 1,
            _ => (current + offset + items.Length) % items.Length
        };
        items[next].IsChecked = true;
        items[next].Focus();
    }

    private void SettingsWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _micaApplied = DwmWindowEffects.TryApplyMaterial(
            handle,
            _host.CurrentTheme == DesignTheme.Dark);
        if (!_micaApplied)
        {
            ApplyWindowShellOpaqueFallback();
        }

        // 系统级缩放/最大化（契约 #24，Issue #106）：WM_EXITSIZEMOVE 是「结束才落盘」
        // 的唯一可靠信号，WM_GETMINMAXINFO 修正无边框窗口的最大化几何。
        if (System.Windows.Interop.HwndSource.FromHwnd(handle) is { } source)
        {
            source.AddHook(WndProc);
        }

        RestorePlacementOrDefault();
    }

    /// <summary>
    /// 材质不可用（旧系统）时回退到不透明壳，避免透明像素露出黑底；
    /// 动态资源引用让兜底随主题活切换跟随新字典（对齐全仓 DynamicResource 语义），
    /// 不定格在兜底瞬间的旧主题实例（探针实证一次性赋值会过期）。
    /// </summary>
    private void ApplyWindowShellOpaqueFallback() =>
        WindowShell.SetResourceReference(Border.BackgroundProperty, "WindowShellOpaqueBrush");

    // ---- 几何持久化与恢复（契约 #24，Issue #106）----

    private bool _isInSystemSizing;

    /// <summary>
    /// 系统最大化意图（WM_SYSCOMMAND SC_MAXIMIZE → WM_SIZE 收口）：轨道尺寸是窗口
    /// 管理器对一切尺寸变更的统一钳制边界，最大化时须让位给工作区（见 Placement）。
    /// </summary>
    private bool _isSystemMaximizing;

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmSize = 0x0005;
        const int WmSysCommand = 0x0112;
        const int WmGetMinMaxInfo = 0x0024;
        const int WmEnterSizeMove = 0x0231;
        const int WmExitSizeMove = 0x0232;
        const int ScMaximize = 0xF030;
        const int SizeRestored = 0;
        switch (message)
        {
            case WmSize:
                if (wParam.ToInt64() == SizeRestored)
                {
                    _isSystemMaximizing = false;
                }

                break;
            case WmSysCommand:
                // 只观察不消费：让系统/WPF 正常处理命令本身。最大化期间保持让位
                //（WPF/系统可能多次重申最大化矩形），其他命令（恢复/移动等）即清除。
                _isSystemMaximizing = (wParam.ToInt64() & 0xFFF0) == ScMaximize;
                break;
            case WmEnterSizeMove:
                _isInSystemSizing = true;
                break;
            case WmExitSizeMove:
                _isInSystemSizing = false;
                ClampIntoCurrentWorkArea();
                CommitPlacement();
                break;
            case WmGetMinMaxInfo:
                // 冻结数值唯一来源 Design.SettingsWindowMetrics；最大轨道高由
                // Placement 按窗口所在显示器工作区推导（40 字节结构体，契约 #24）。
                // 最大化意图双检测：系统路径（Win+Up 等）经 WM_SYSCOMMAND SC_MAXIMIZE
                //（WPF 在 WM_SIZE 后才更新 WindowState，须靠标志）；WPF WindowState
                // 赋值路径（如测试）在消息到达前 DP 已是 Maximized，直接可见。
                var maximizeIntent = _isSystemMaximizing || WindowState == WindowState.Maximized;
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                handled = SettingsWindowPlacement.TryApplyMaximizeWorkArea(
                    hwnd,
                    lParam,
                    FloatingTransferStation.Design.SettingsWindowMetrics.MinWidth,
                    FloatingTransferStation.Design.SettingsWindowMetrics.MinHeight,
                    FloatingTransferStation.Design.SettingsWindowMetrics.MaxWidth,
                    dpi.PixelsPerInchX / 96d,
                    dpi.PixelsPerInchY / 96d,
                    maximizeIntent);
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 恢复持久化几何（契约 #24 §四.3）：整组非法（缺失/NaN/±∞/非正尺寸）或完全落在
    /// 所有显示器之外 → 回落 owner 相对默认定位；否则按持久化矩形中心解析目标显示器，
    /// 以其工作区按冻结式（工作区高 − 48，矮屏抬到最小高）归一矩形（部分越界钳入、
    /// 负坐标合法）后应用。
    /// </summary>
    private void RestorePlacementOrDefault()
    {
        var preferences = _host.CurrentPreferences;
        var (left, top, width, height) = (
            preferences.SettingsWindowLeft,
            preferences.SettingsWindowTop,
            preferences.SettingsWindowWidth,
            preferences.SettingsWindowHeight);
        if (!SettingsWindowPlacement.IsValid(left, top, width, height)
            || !SettingsWindowPlacement.IntersectsVirtualScreen(left!.Value, top!.Value, width!.Value, height!.Value))
        {
            PlaceAtDefaultPosition();
            return;
        }

        var workArea = ResolveWorkAreaFor(left!.Value + width!.Value / 2, top!.Value + height!.Value / 2);
        var maxHeight = Math.Max(
            FloatingTransferStation.Design.SettingsWindowMetrics.MinHeight,
            workArea.Height - FloatingTransferStation.Design.SettingsWindowMetrics.MaxHeightMargin);
        var rect = SettingsWindowPlacement.Normalize(
            left.Value, top.Value, width.Value, height.Value,
            FloatingTransferStation.Design.SettingsWindowMetrics.MinWidth,
            FloatingTransferStation.Design.SettingsWindowMetrics.MaxWidth,
            FloatingTransferStation.Design.SettingsWindowMetrics.MinHeight,
            maxHeight,
            workArea);
        Width = rect.Width;
        Height = rect.Height;
        Left = rect.Left;
        Top = rect.Top;
    }

    /// <summary>owner 相对默认定位（首次运行或几何不可用的回落路径）。</summary>
    private void PlaceAtDefaultPosition()
    {
        var work = SystemParameters.WorkArea;
        if (Owner is { } owner)
        {
            Left = Math.Max(work.Left, Math.Min(owner.Left - Width - 12, work.Right - Width));
            Top = Math.Clamp(owner.Top, work.Top, Math.Max(work.Top, work.Bottom - ActualHeight));
            return;
        }

        // Owner 不可用的理论边界：主屏工作区居中。
        Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = work.Top + Math.Max(0, (work.Height - ActualHeight) / 2);
    }

    private SettingsWindowPlacement.WorkArea ResolveWorkAreaFor(double dipX, double dipY)
    {
        if (SettingsWindowPlacement.WorkAreaResolverOverride is { } resolver)
        {
            return resolver(dipX, dipY) ?? PrimaryWorkArea();
        }

        return SettingsWindowPlacement.QueryWorkArea(this, dipX, dipY) ?? PrimaryWorkArea();
    }

    private static SettingsWindowPlacement.WorkArea PrimaryWorkArea() => new(
        SystemParameters.WorkArea.Left,
        SystemParameters.WorkArea.Top,
        SystemParameters.WorkArea.Right,
        SystemParameters.WorkArea.Bottom);

    /// <summary>把当前位置钳进所在显示器工作区（恢复/SizeChanged/系统缩放结束共用，
    /// 不留第二套钳制）。</summary>
    private void ClampIntoCurrentWorkArea()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        var workArea = ResolveWorkAreaFor(Left + (double.IsNaN(ActualWidth) ? 0 : ActualWidth / 2),
            Top + (double.IsNaN(ActualHeight) ? 0 : ActualHeight / 2));
        Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ActualHeight));
        Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - ActualWidth));
    }

    /// <summary>
    /// 拖动/系统缩放结束（Normal 态）把几何写回偏好：复用 ApplyPreferences 原子管线
    /// （契约 #5 同路）；最大化/最小化状态不落盘（恢复的是 Normal 几何）。
    /// </summary>
    private void CommitPlacement()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            SettingsWindowLeft = Left,
            SettingsWindowTop = Top,
            SettingsWindowWidth = ActualWidth,
            SettingsWindowHeight = ActualHeight
        });
    }

    private void HeaderDragRegion_DragCompleted(object sender, DragCompletedEventArgs e) => CommitPlacement();

    /// <summary>
    /// 尺寸定稿或打开后再变化（如运行时改缩放、显示器断开）时，把窗口钳回所在
    /// 显示器工作区（与恢复共用同一钳制）。探针实测 SizeToContent 的测量与尺寸事件
    /// 先于 SourceInitialized 发生，该处理器必须在构造函数挂接才覆盖初始放置；
    /// 系统交互缩放期间（WM_SIZEMOVE）不逐帧回写位置，由 EXITSIZEMOVE 收口一次。
    /// </summary>
    private void SettingsWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInSystemSizing)
        {
            return;
        }

        ClampIntoCurrentWorkArea();
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

    private void PinnedDividerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            PinnedDividerComboBox.SelectedItem is not ComboBoxItem { Tag: PinnedDividerStyle style })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { PinnedDivider = style });
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
        // 总开关关闭时保留时长下拉置灰；开启恢复（窗口每次打开都重建，初值在构造同步块）。
        AutoCleanupRetentionComboBox.IsEnabled = enabled;
    }

    private void AutoCleanupRetentionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSynchronizingControls ||
            AutoCleanupRetentionComboBox.SelectedItem is not ComboBoxItem { Tag: AutoCleanupRetention retention })
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { AutoCleanupRetention = retention });
    }

    private void AutoUpdateCheckToggle_Checked(object sender, RoutedEventArgs e) =>
        ApplyAutoUpdateCheckPreference(enabled: true);

    private void AutoUpdateCheckToggle_Unchecked(object sender, RoutedEventArgs e) =>
        ApplyAutoUpdateCheckPreference(enabled: false);

    private void ApplyAutoUpdateCheckPreference(bool enabled)
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with
        {
            AutoUpdateCheckEnabled = enabled
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

    // ---- AI 设置（1.27.0，DeepSeek 接入地基）----

    private void DeepSeekKeyBox_LostFocus(object sender, RoutedEventArgs e) => CommitDeepSeekKey();

    private void DeepSeekKeyPlainBox_LostFocus(object sender, RoutedEventArgs e) => CommitDeepSeekKey();

    /// <summary>
    /// Key 的生效点是失焦（对齐卡片就地编辑失焦提交先例）：非空提交密文，空输入清除（存 null）；
    /// 明文只在内存中出现，存储侧只落密文。加密失败（DPAPI 异常）就地提示且不落任何值。
    /// </summary>
    private void CommitDeepSeekKey()
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        var plaintext = ReadDeepSeekKeyInput();
        string? protectedKey;
        try
        {
            protectedKey = string.IsNullOrEmpty(plaintext)
                ? null
                : _host.SecretProtector.Protect(plaintext);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException)
        {
            ShowDeepSeekStatus("API Key 加密失败，本次未保存，请重试。");
            return;
        }

        _host.ApplyPreferences(_host.CurrentPreferences with { DeepSeekApiKeyProtected = protectedKey });
    }

    private void DeepSeekKeyVisibilityButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isDeepSeekKeyVisible)
        {
            DeepSeekKeyBox.Password = DeepSeekKeyPlainBox.Text;
            DeepSeekKeyPlainBox.Visibility = Visibility.Collapsed;
            DeepSeekKeyBox.Visibility = Visibility.Visible;
            DeepSeekKeyVisibilityButton.Content = "显示";
        }
        else
        {
            DeepSeekKeyPlainBox.Text = DeepSeekKeyBox.Password;
            DeepSeekKeyBox.Visibility = Visibility.Collapsed;
            DeepSeekKeyPlainBox.Visibility = Visibility.Visible;
            DeepSeekKeyVisibilityButton.Content = "隐藏";
        }

        _isDeepSeekKeyVisible = !_isDeepSeekKeyVisible;
    }

    private async void DeepSeekTestButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isTestingDeepSeekConnection)
        {
            return;
        }

        _isTestingDeepSeekConnection = true;
        DeepSeekTestButton.IsEnabled = false;
        DeepSeekTestButton.Content = "测试中…";
        try
        {
            // 直接测当前输入框内容（含未保存的新输入）；探活为 GET /models
            // （零 token 消耗），成功文案模型名恒为客户端默认（deepseek-flash，
            // 契约 #28），无选择状态。
            var result = await _host.TestDeepSeekConnectionAsync(ReadDeepSeekKeyInput());
            ShowDeepSeekStatus(result.Message, isError: !result.IsSuccess);
        }
        catch (OperationCanceledException)
        {
            // 宿主/客户端未来若引入可取消令牌：取消不按失败反馈，只恢复按钮。
        }
        catch (Exception)
        {
            // 纯崩溃护栏：客户端边界已把网络阶段全部映射为结果，常规失败不经过
            // 这里；仅防 async void 的未知异常击穿为进程崩溃，不带任何请求细节。
            ShowDeepSeekStatus("测试失败，请重试。");
        }
        finally
        {
            _isTestingDeepSeekConnection = false;
            DeepSeekTestButton.IsEnabled = true;
            DeepSeekTestButton.Content = "测试连接";
        }
    }

    private string ReadDeepSeekKeyInput() => (_isDeepSeekKeyVisible
        ? DeepSeekKeyPlainBox.Text
        : DeepSeekKeyBox.Password).Trim();

    private void ShowDeepSeekStatus(string message, bool isError = true)
    {
        DeepSeekStatusText.Text = message;
        DeepSeekStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "DangerBrush" : "AccentBrush");
        DeepSeekStatusText.Visibility = Visibility.Visible;
    }

    // ---- AI 整理提示词（1.52.0，契约 #29 修订）----

    private void AiOrganizePromptBox_LostFocus(object sender, RoutedEventArgs e) =>
        CommitAiOrganizePrompt();

    private void AiOrganizePromptBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateAiOrganizePromptState();

    private void AiOrganizePromptFillDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        // 点击已先触发编辑框失焦提交（若持有焦点）；以默认全文为微调起点填入
        // 框内——填入≠生效，保存点仍是下一次失焦，用户可先改再保存。
        e.Handled = true;
        AiOrganizePromptBox.Text = AiOrganizePrompt.SystemPrompt;
        AiOrganizePromptBox.CaretIndex = AiOrganizePromptBox.Text.Length;
    }

    private void AiOrganizePromptResetButton_Click(object sender, RoutedEventArgs e)
    {
        // 点击已先触发失焦提交（即便刚保存了旧值也会被下一步覆盖）：清空并
        // 立即提交 null——「恢复默认」不需要再失焦一次才生效，最终态恒为 null。
        e.Handled = true;
        AiOrganizePromptBox.Text = string.Empty;
        CommitAiOrganizePrompt();
    }

    /// <summary>
    /// 生效点是失焦（对齐 Key 框失焦提交先例）：Trim 后空白＝null（内置默认，
    /// 绝不发送空白 system）；超长（1 万字）就地红字提示且不保存；否则保存
    /// Trim 后文本。生效值未变化时跳过提交（按钮可重复点击，不产生无谓写盘）。
    /// </summary>
    private void CommitAiOrganizePrompt()
    {
        if (_isSynchronizingControls)
        {
            return;
        }

        var trimmed = AiOrganizePromptBox.Text.Trim();
        if (trimmed.Length > AiOrganizePrompt.MaxOverrideLength)
        {
            AiOrganizePromptErrorText.Text = "整理提示词超过 1 万字，未保存。";
            AiOrganizePromptErrorText.Visibility = Visibility.Visible;
            return;
        }

        AiOrganizePromptErrorText.Visibility = Visibility.Collapsed;
        var effective = trimmed.Length == 0 ? null : trimmed;
        if (string.Equals(
                effective,
                _host.CurrentPreferences.AiOrganizeSystemPromptOverride,
                StringComparison.Ordinal))
        {
            return;
        }

        _host.ApplyPreferences(
            _host.CurrentPreferences with { AiOrganizeSystemPromptOverride = effective });
    }

    /// <summary>状态显式可见（禁止静默回退）：空框＝内置默认，非空＝已自定义。</summary>
    private void UpdateAiOrganizePromptState() =>
        AiOrganizePromptStateText.Text = string.IsNullOrWhiteSpace(AiOrganizePromptBox.Text)
            ? "当前使用内置默认。"
            : "已自定义。";

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

    private Border CreatePluginRow(PluginEntry entry)
    {
        var content = new StackPanel();

        var row = new Grid { Background = System.Windows.Media.Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 卡片行语言与「标签顺序」行同族：Card 底 + Border 描边 + 圆角 4，颜色走主题键；
        // 画刷经动态资源引用解析（对齐全仓 DynamicResource 语义），主题活切换即跟随。
        var surface = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = content
        };
        surface.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var title = entry.Manifest is { } manifest
            ? $"{manifest.Name}  {manifest.Version}"
            : $"{System.IO.Path.GetFileName(entry.Directory)}（无法加载）";
        var detail = entry.Manifest is { } valid
            ? (valid.Description ?? (valid.Kind == PluginKind.TextCleaner ? "文本整理插件" : "文本动作插件"))
            : entry.LoadError ?? "清单无效。";

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBlock.SetResourceReference(TextElement.ForegroundProperty, "PrimaryTextBrush");
        titleLine.Children.Add(titleBlock);
        // 来源徽章（1.51.0）：内置/用户覆盖；ToolTip=插件实际目录（含用户覆盖路径）。
        var badge = new TextBlock
        {
            Text = entry.Origin == PluginOrigin.User ? "用户覆盖" : "内置",
            FontSize = 11,
            Margin = new Thickness(6, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = entry.Directory
        };
        badge.SetResourceReference(TextElement.ForegroundProperty, "SecondaryTextBrush");
        titleLine.Children.Add(badge);
        textPanel.Children.Add(titleLine);
        var detailBlock = new TextBlock
        {
            Text = detail,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        // 描述键随行状态选定（清单非法=DangerBrush），值经动态引用随字典换装。
        detailBlock.SetResourceReference(
            TextElement.ForegroundProperty,
            entry.Manifest is null ? "DangerBrush" : "SecondaryTextBrush");
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

        content.Children.Add(row);
        if (entry.Manifest is { Kind: PluginKind.TextActions } actionsManifest)
        {
            content.Children.Add(BuildPluginActionsPanel(actionsManifest, entry));
        }

        return surface;
    }

    /// <summary>
    /// text-actions 插件行的动作区：逐动作标题＋选型摘要＋声明式设置字段（改动
    /// 即时经宿主原子持久化并重建管线）＋「试运行…」入口（诊断对话框复用正式
    /// 加载器口径）。所有画刷走动态资源，主题活切换即跟随。
    /// </summary>
    private FrameworkElement BuildPluginActionsPanel(PluginManifest manifest, PluginEntry entry)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var action in manifest.Actions!)
        {
            var actionPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

            var headerLine = new Grid { Background = System.Windows.Media.Brushes.Transparent };
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var actionTitle = new TextBlock
            {
                Text = $"动作：{action.Title}（{(action.Selection == PluginActionSelection.Multi ? "多选" : "单选")}）",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            actionTitle.SetResourceReference(TextElement.ForegroundProperty, "SecondaryTextBrush");
            Grid.SetColumn(actionTitle, 0);
            headerLine.Children.Add(actionTitle);
            var trialButton = new Button
            {
                Style = TryFindResource("ThemedActionButtonStyle") as Style,
                Content = "试运行…",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                ToolTip = "用当前设置试跑该动作（不改卡片内容）",
                Tag = (entry.Id, action.Id)
            };
            System.Windows.Automation.AutomationProperties.SetName(trialButton, $"{action.Title}试运行"); trialButton.Click += PluginActionTrialButton_Click;
            Grid.SetColumn(trialButton, 1);
            headerLine.Children.Add(trialButton);
            actionPanel.Children.Add(headerLine);

            foreach (var setting in action.Settings)
            {
                actionPanel.Children.Add(BuildPluginSettingRow(entry, action, setting));
            }

            panel.Children.Add(actionPanel);
        }

        return panel;
    }

    /// <summary>单个声明式设置字段的标签＋控件行；值经宿主按插件 id 原子持久化。</summary>
    private FrameworkElement BuildPluginSettingRow(PluginEntry entry, PluginActionDefinition action, PluginActionSetting setting)
    {
        var line = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var overrides = _host.PluginCatalog?.GetActionSettingOverrides(entry.Id, action.Id)
            ?? new Dictionary<string, string>();
        var currentValue = action.ResolveSettingValue(overrides, setting.Key);

        var label = new TextBlock
        {
            Text = setting.Label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        label.SetResourceReference(TextElement.ForegroundProperty, "PrimaryTextBrush");
        Grid.SetColumn(label, 0);
        line.Children.Add(label);

        FrameworkElement control;
        switch (setting.Type)
        {
            case PluginSettingType.Bool:
                {
                    var toggle = new CheckBox
                    {
                        Style = TryFindResource("ToggleSwitchStyle") as Style,
                        IsChecked = string.Equals(currentValue, "true", StringComparison.Ordinal),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Tag = (entry.Id, action.Id, setting.Key)
                    };
                    toggle.Checked += PluginSetting_ValueChanged;
                    toggle.Unchecked += PluginSetting_ValueChanged;
                    control = toggle;
                    break;
                }

            case PluginSettingType.Choice:
                {
                    var combo = new ComboBox
                    {
                        Style = TryFindResource("ThemedComboBoxStyle") as Style,
                        ItemsSource = setting.Options,
                        MinWidth = 140,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Tag = (entry.Id, action.Id, setting.Key)
                    };
                    combo.SelectedItem = setting.Options.FirstOrDefault(option => option == currentValue) ?? setting.Options[0];
                    combo.SelectionChanged += PluginSetting_ValueChanged;
                    control = combo;
                    break;
                }

            default:
                {
                    var box = new TextBox
                    {
                        Text = currentValue,
                        MaxLength = 200,
                        MinWidth = 140,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Style = TryFindResource("ThemedTextBoxStyle") as Style,
                        Tag = (entry.Id, action.Id, setting.Key)
                    };
                    box.LostFocus += PluginSetting_ValueChanged;
                    control = box;
                    break;
                }
        }

        Grid.SetColumn(control, 1);
        line.Children.Add(control);
        return line;
    }

    private async void PluginSetting_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingControls ||
            sender is not FrameworkElement { Tag: ValueTuple<string, string, string> tag })
        {
            return;
        }

        var (pluginId, actionId, key) = tag;
        var value = sender switch
        {
            CheckBox toggle => (toggle.IsChecked == true).ToString().ToLowerInvariant(),
            ComboBox combo => combo.SelectedItem as string ?? string.Empty,
            TextBox box => box.Text,
            _ => string.Empty
        };

        try
        {
            await _host.SetPluginActionSettingAsync(pluginId, actionId, key, value);
            PluginStatusText.Text = string.Empty;
            PluginStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            PluginStatusText.Text = "插件设置暂未保存，请稍后重试。";
            PluginStatusText.Visibility = Visibility.Visible;
        }
    }

    private void PluginActionTrialButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: ValueTuple<string, string> tag })
        {
            return;
        }

        var (pluginId, actionId) = tag;
        var trial = new PluginActionTrialWindow(_host, pluginId, actionId);
        trial.Owner = this;
        trial.ShowDialog();
    }

    private async void RescanPluginsButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_host.PluginCatalog is null)
        {
            return;
        }

        RescanPluginsButton.IsEnabled = false;
        try
        {
            await _host.RescanPluginsAsync();
            PluginListPanel.Children.Clear();
            PopulatePluginSection();
            PluginStatusText.Text = string.Empty;
            PluginStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PluginStatusText.Text = "重新扫描失败，请稍后重试。";
            PluginStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            RescanPluginsButton.IsEnabled = true;
        }
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
