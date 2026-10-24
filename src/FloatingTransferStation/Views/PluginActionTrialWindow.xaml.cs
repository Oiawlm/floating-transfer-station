using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Views;

/// <summary>
/// 插件动作试运行对话框（1.51.0，契约「插件动作协议」诊断口径）：输入 ≤10k 字符
/// → 运行 → 输出＋总耗时＋规则命中计数。校验与执行复用正式加载器——每次运行
/// 经 <see cref="PluginCatalog.TryBuildActionPipeline"/> 按当前设置现建管线（设置
/// 改动即时反映），只读演练、不改任何卡片内容。壳技术与 URL 输入框同族（D8）。
/// </summary>
internal partial class PluginActionTrialWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly string _pluginId;
    private readonly string _actionId;
    private bool _isRunning;
    private bool _dialogShellApplied;

    public PluginActionTrialWindow(ISettingsHost host, string pluginId, string actionId)
    {
        _host = host;
        _pluginId = pluginId;
        _actionId = actionId;
        InitializeComponent();
        var theme = host.CurrentTheme;
        DesignThemeManager.Apply(this, theme);
        // 构造期应用进程级当前界面字体（偏好 InterfaceFont；null=默认），与主题应用同点。
        InterfaceFontManager.Apply(this);
        if (host.PluginCatalog is { } catalog)
        {
            var handle = catalog.EnabledActions.FirstOrDefault(candidate =>
                candidate.PluginId == pluginId && candidate.Action.Id == actionId);
            var pluginName = handle?.PluginName
                ?? catalog.Entries.FirstOrDefault(entry => entry.Id == pluginId)?.Manifest?.Name
                ?? pluginId;
            var actionTitle = handle?.Action.Title ?? actionId;
            TrialTitleText.Text = $"试运行 · {actionTitle}（{pluginName}）";
        }

        SourceInitialized += (_, _) =>
        {
            _dialogShellApplied = DwmWindowEffects.TryApplyDialogShell(
                new System.Windows.Interop.WindowInteropHelper(this).Handle,
                theme == DesignTheme.Dark);
            DialogShellFallback.Apply(this, _dialogShellApplied);
        };
        Loaded += (_, _) => TrialInputBox.Focus();
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

    private void TrialCloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TrialWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private async void TrialRunButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_isRunning)
        {
            return;
        }

        var input = TrialInputBox.Text;
        if (input.Length == 0 || input.Length > PluginActionLimits.TrialRunInputCharacters)
        {
            ShowTrialStatus($"输入需为 1–{PluginActionLimits.TrialRunInputCharacters} 字符（当前 {input.Length}）。", isError: true);
            return;
        }

        // 每次运行按当前设置现建管线（复用正式加载器口径，反映最新设置）。
        var pipeline = _host.PluginCatalog?.TryBuildActionPipeline(_pluginId, _actionId);
        if (pipeline is null)
        {
            ShowTrialStatus("当前设置下没有生效的规则（检查 when 门控）。", isError: true);
            return;
        }

        _isRunning = true;
        TrialRunButton.IsEnabled = false;
        TrialRunButton.Content = "运行中…";
        TrialStatusText.Text = string.Empty;
        try
        {
            var result = await Task.Run(() => pipeline.Apply(
                [new PluginActionCardInput(Guid.Empty, input)]));
            if (!result.IsSuccess)
            {
                ShowTrialStatus(result.FailureReason ?? "执行失败。", isError: true);
                return;
            }

            var output = result.Cards is { Count: > 0 } ? result.Cards[0].NewText : input;
            var hits = result.Cards?.Sum(card => card.RuleHits) ?? 0;
            TrialOutputBox.Text = output;
            TrialOutputBox.Visibility = Visibility.Visible;
            ShowTrialStatus(
                $"耗时 {result.Elapsed.TotalMilliseconds:0} ms · 规则命中 {hits} 处"
                + (result.SkippedEmptyCount > 0 ? " · 结果为空（正式执行将保留原文）" : string.Empty),
                isError: false);
        }
        finally
        {
            _isRunning = false;
            TrialRunButton.IsEnabled = true;
            TrialRunButton.Content = "运行";
        }
    }

    private void ShowTrialStatus(string message, bool isError)
    {
        TrialStatusText.Text = message;
        TrialStatusText.SetResourceReference(
            TextElement.ForegroundProperty,
            isError ? "DangerBrush" : "SecondaryTextBrush");
    }
}
