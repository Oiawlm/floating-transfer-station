using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using FloatingTransferStation.Design;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 按钮与交互状态锁定测试（2026-11-08 视觉语言收敛，h1/A20）：禁用两档 token、
/// hover 双轨（层淡入 vs 属性瞬切 vs 仅描边）、pressed 范式、LiftAnimation 缓动
/// 按语义，以及两处原裸控件（折叠 ToggleButton/原生 CheckBox）入主题体系后的
/// 主题键解析。类型学与范式叙述见 docs/design.md「按钮与交互状态」章。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void ButtonStates_LiftAnimationChoosesEasingByLiftSemantics()
    {
        // A16：缓动按语义（抬升=减速 EnterEasing、回落=加速 ExitEasing）不按数值
        // 方向——位移向负值，原「to<from」判据曾把抬升命中加速曲线。
        var lift = LiftAnimation.CreateLiftAnimation(0d, LiftAnimation.LiftedOffsetPx);
        Assert.AreSame(FadeAnimation.EnterEasing, lift.EasingFunction, "抬升（进入悬停态）用减速曲线。");
        Assert.AreEqual(TimeSpan.FromMilliseconds(DesignTokens.HoverFeedbackMs), lift.Duration);

        var fall = LiftAnimation.CreateLiftAnimation(LiftAnimation.LiftedOffsetPx, 0d);
        Assert.AreSame(FadeAnimation.ExitEasing, fall.EasingFunction, "回落（离开悬停态）用加速曲线。");

        // A3/A5/A6 收编锁（源文本）：抬升位移、脉冲峰值与瓦片入场位移以 token
        // 消费（防止字面量回流）；数值权威=DesignTokens＋design.md 映射叙述。
        var quickLaunch = File.ReadAllText(Path.Combine(
            FindRepositoryRootForStyles(),
            "src",
            "FloatingTransferStation",
            "Views",
            "MainWindow.QuickLaunch.cs"));
        StringAssert.Contains(quickLaunch, "DesignTokens.LaunchConfirmPulseScale", "确认脉冲峰值须以 token 消费（A5）。");
        StringAssert.Contains(quickLaunch, "DesignTokens.TileEntranceOffsetPx", "瓦片入场位移须以 token 消费（A6）。");
    }

    [STATestMethod]
    public void ButtonStates_DisabledOpacityConsolidatesToControlAndCardTiers()
    {
        // A12：禁用不透明度归一两档——控件族 ControlDisabledOpacity 0.4、
        // 整卡 CardDisabledOpacity 0.6（触发器逐个实例化求值，模板触发器
        // 需布局后应用）。
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            var settings = OpenSettingsWindowForStyles(window);
            var host = new StackPanel();
            ((Grid)window.FindName("LayoutRoot")).Children.Add(host);

            var controls = new List<Control>
            {
                new Button { Style = (Style)window.FindResource("ThemedActionButtonStyle") },
                new ToggleButton { Style = (Style)window.FindResource("ThemedFilterChipStyle") },
                new ToggleButton { Style = (Style)window.FindResource("ThemedToggleButtonStyle") },
                new TextBox { Style = (Style)window.FindResource("ThemedTextBoxStyle") },
                new CheckBox { Style = (Style)settings.FindResource("ToggleSwitchStyle") },
            };
            foreach (var control in controls)
            {
                host.Children.Add(control);
            }

            var card = new Border { Style = (Style)settings.FindResource("SettingsCardStyle") };
            host.Children.Add(card);
            CompleteLayout(window);

            foreach (var control in controls)
            {
                control.IsEnabled = false;
            }

            card.IsEnabled = false;
            CompleteLayout(window);

            foreach (var control in controls)
            {
                Assert.AreEqual(
                    DesignTokens.ControlDisabledOpacity,
                    control.Opacity,
                    $"{control.GetType().Name} 禁用不透明度必须归一 ControlDisabledOpacity。");
            }

            Assert.AreEqual(
                DesignTokens.CardDisabledOpacity,
                card.Opacity,
                "设置卡整卡灰化档=CardDisabledOpacity（#57 先例入 token）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ButtonStates_HoverAndPressedFollowSharedParadigms()
    {
        // B4/B5/A13/A14 成文范式的结构锁定：文字钮/chip 悬停=属性瞬切换底换边、
        // 按压=ActiveCategory；开关悬停=仅描边（HoverBorderBrush）；微信号行
        // 悬停=HoverSurface 瞬切底；头部图标钮悬停=FadeAnimation 层淡入（双轨）。
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            var settings = OpenSettingsWindowForStyles(window);

            var actionTemplate = GetControlTemplate((Style)window.FindResource("ThemedActionButtonStyle"));
            var actionHover = GetTriggerSetter(actionTemplate, UIElement.IsMouseOverProperty, "Surface", Control.BackgroundProperty);
            AssertDynamicResourceKey(actionHover, "HoverSurfaceBrush", "文字钮悬停换底=HoverSurfaceBrush。");
            AssertDynamicResourceKey(
                GetTriggerSetter(actionTemplate, UIElement.IsMouseOverProperty, "Surface", Control.BorderBrushProperty),
                "HoverBorderBrush",
                "文字钮悬停换边=HoverBorderBrush。");
            AssertDynamicResourceKey(
                GetTriggerSetter(actionTemplate, ButtonBase.IsPressedProperty, "Surface", Control.BackgroundProperty),
                "ActiveCategoryBrush",
                "文字钮按压=ActiveCategoryBrush（pressed 范式）。");

            var chipTemplate = GetControlTemplate((Style)window.FindResource("ThemedFilterChipStyle"));
            AssertDynamicResourceKey(
                GetTriggerSetter(chipTemplate, ButtonBase.IsPressedProperty, "Surface", Control.BackgroundProperty),
                "ActiveCategoryBrush",
                "chip 按压=ActiveCategoryBrush（pressed 范式）。");

            var switchTemplate = GetControlTemplate((Style)settings.FindResource("ToggleSwitchStyle"));
            AssertDynamicResourceKey(
                GetTriggerSetter(switchTemplate, UIElement.IsMouseOverProperty, "Track", Border.BorderBrushProperty),
                "HoverBorderBrush",
                "开关悬停=仅描边支系（Track 描边 HoverBorderBrush，A13）。");
            Assert.IsNull(
                FindTrigger(switchTemplate, UIElement.IsMouseOverProperty)?.Setters.OfType<Setter>()
                    .FirstOrDefault(setter => setter.TargetName is null),
                "开关悬停不换底不位移（仅描边支系）。");

            var weChatRowStyle = (Style)settings.FindResource("WeChatIdRowStyle");
            var rowHover = weChatRowStyle.Triggers.OfType<Trigger>()
                .First(trigger => trigger.Property == UIElement.IsMouseOverProperty);
            AssertDynamicResourceKey(
                rowHover.Setters.OfType<Setter>().First(setter => setter.Property == TextBlock.BackgroundProperty),
                "HoverSurfaceBrush",
                "微信号行悬停=HoverSurface 瞬切底（A14）。");

            // 双轨判别：头部图标钮模板无 IsMouseOver 触发器（零瞬切），悬停由
            // FadeAnimation 层（HoverLayer）承载——层淡入支系。
            var headerTemplate = GetControlTemplate((Style)window.FindResource("HeaderActionButtonStyle"));
            Assert.IsNull(
                FindTrigger(headerTemplate, UIElement.IsMouseOverProperty),
                "头部图标钮悬停不经属性触发器（层淡入支系，双轨判别）。");
            var headerButton = new Button { Style = (Style)window.FindResource("HeaderActionButtonStyle") };
            var host = new StackPanel();
            host.Children.Add(headerButton);
            ((Grid)window.FindName("LayoutRoot")).Children.Add(host);
            CompleteLayout(window);
            // CompleteLayout 已应用模板（ApplyTemplate 对已应用模板返回 false），
            // 直接按模板名取层断言。
            var hoverLayer = (Border?)headerTemplate.FindName("HoverLayer", headerButton);
            Assert.IsNotNull(hoverLayer, "头部图标钮模板含 HoverLayer（层淡入支系）。");
            Assert.IsNotNull(
                BindingOperations.GetBinding(hoverLayer, FadeAnimation.IsActiveProperty),
                "HoverLayer 悬停由 FadeAnimation.IsActive 绑定驱动。");
            Assert.AreEqual(
                DesignTokens.HoverFeedbackMs,
                FadeAnimation.GetDurationMs(hoverLayer),
                "层淡入时长=HoverFeedbackMs token。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ButtonStates_ThemedCheckBoxAndToggleWireBareControls()
    {
        // A15：两处原裸控件（Aero2 外观）入主题体系——折叠 ToggleButton 套
        // ThemedToggleButtonStyle、多选选择器 CheckBox 套 ThemedCheckBoxStyle；
        // 后者实例化后选中填充解析为主题键 AccentBrush（裸控件模板化后主题键
        // 解析的结构保证）。
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            // 关闭动画（既有惯例，同 DesignTokens 瓦片几何测试）：FadeAnimation
            // 即时定格终值，断言不受 120ms 淡入时序影响。
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            window.Show();
            var root = FindRepositoryRootForStyles();
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(root, "src", "FloatingTransferStation", "Views", "LauncherEditTargetWindow.xaml")),
                "ThemedToggleButtonStyle",
                "折叠 ToggleButton 必须套 ThemedToggleButtonStyle（脱离 Aero2）。");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(root, "src", "FloatingTransferStation", "Views", "MultiItemPickerWindow.xaml")),
                "ThemedCheckBoxStyle",
                "选择器 CheckBox 必须套 ThemedCheckBoxStyle（脱离 Aero2）。");

            var checkBox = new CheckBox
            {
                Style = (Style)window.FindResource("ThemedCheckBoxStyle"),
                IsChecked = true,
            };
            var host = new StackPanel();
            host.Children.Add(checkBox);
            ((Grid)window.FindName("LayoutRoot")).Children.Add(host);
            CompleteLayout(window);

            var template = GetControlTemplate((Style)window.FindResource("ThemedCheckBoxStyle"));
            var fill = (Border?)template.FindName("SelectionSelectedFill", checkBox);
            Assert.IsNotNull(fill, "模板含选中填充层。");
            Assert.AreEqual(1d, fill.Opacity, 0.01, "选中态填充层激活（FadeAnimation 已求值）。");
            Assert.AreSame(
                window.FindResource("AccentBrush"),
                fill.Background,
                "选中填充必须解析为主题键 AccentBrush 实例（随字典整本替换）。");
            var ring = (Border?)template.FindName("SelectionSurface", checkBox);
            Assert.IsNotNull(ring);
            Assert.AreSame(
                window.FindResource("SecondaryTextBrush"),
                ring.BorderBrush,
                "未激活环描边必须解析为主题键 SecondaryTextBrush。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    /// <summary>打开真实设置窗并取其资源树（ToggleSwitchStyle 等设置窗私有样式
    /// 的唯一来源）；用毕由调用方 CloseWindow 归属关闭（Owner 链随主窗关闭）。</summary>
    private static SettingsWindow OpenSettingsWindowForStyles(MainWindow window)
    {
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear, "SettingsButton 必须存在。");
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var settings = GetPrivateField<SettingsWindow>(window, "_settingsWindow");
        Assert.IsNotNull(settings, "点击齿轮应打开设置窗。");
        return settings;
    }

    private static ControlTemplate GetControlTemplate(Style style) =>
        (ControlTemplate)style.Setters.OfType<Setter>()
            .First(setter => setter.Property == Control.TemplateProperty).Value;

    private static Trigger? FindTrigger(ControlTemplate template, DependencyProperty property) =>
        template.Triggers.OfType<Trigger>()
            .FirstOrDefault(trigger => trigger.Property == property);

    private static Setter GetTriggerSetter(
        ControlTemplate template,
        DependencyProperty triggerProperty,
        string targetName,
        DependencyProperty setterProperty)
    {
        var trigger = FindTrigger(template, triggerProperty);
        Assert.IsNotNull(trigger, $"模板须含 {triggerProperty.Name} 触发器。");
        var setter = trigger.Setters.OfType<Setter>()
            .FirstOrDefault(candidate => candidate.TargetName == targetName && candidate.Property == setterProperty);
        Assert.IsNotNull(
            setter,
            $"{triggerProperty.Name} 触发器须含 {targetName}.{setterProperty.Name} Setter。");
        return setter;
    }

    private static void AssertDynamicResourceKey(Setter setter, string expectedKey, string message)
    {
        var resource = setter.Value as DynamicResourceExtension;
        Assert.IsNotNull(resource, $"{message}（Setter 值应是 DynamicResource）");
        Assert.AreEqual(expectedKey, resource.ResourceKey, message);
    }

    private static string FindRepositoryRootForStyles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "version.txt")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "未找到仓库根（version.txt 探针）。");
        return directory.FullName;
    }
}
