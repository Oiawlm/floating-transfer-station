using System.Windows;
using System.Windows.Media;
using FloatingTransferStation.Design;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void DesignTokens_AreMirroredByWpfBrushResources()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        DesignThemeManager.Apply(window, DesignTheme.Light);

        try
        {
            AssertTokenBrush(window, "WindowShellBrush", DesignTokens.WindowShellTintHex);
            AssertTokenBrush(window, "WindowShellOpaqueBrush", DesignTokens.WindowShellHex);
            AssertTokenBrush(window, "TabRailBrush", DesignTokens.TabRailHex);
            AssertTokenBrush(window, "CardBrush", DesignTokens.CardHex);
            AssertTokenBrush(window, "BorderBrush", DesignTokens.BorderHex);
            AssertTokenBrush(window, "PrimaryTextBrush", DesignTokens.PrimaryTextHex);
            AssertTokenBrush(window, "SecondaryTextBrush", DesignTokens.SecondaryTextHex);
            AssertTokenBrush(window, "AccentBrush", DesignTokens.AccentHex);
            AssertTokenBrush(window, "DangerBrush", DesignTokens.DangerHex);
            AssertTokenBrush(window, "HeaderBrush", DesignTokens.HeaderHex);
            AssertTokenBrush(window, "HoverSurfaceBrush", DesignTokens.HoverSurfaceHex);
            AssertTokenBrush(window, "HoverBorderBrush", DesignTokens.HoverBorderHex);
            AssertTokenBrush(window, "SelectedCardBrush", DesignTokens.SelectedCardHex);
            AssertTokenBrush(window, "SelectedCardBorderBrush", DesignTokens.SelectedCardBorderHex);
            AssertTokenBrush(window, "ActiveCategoryBrush", DesignTokens.ActiveCategoryHex);
            AssertTokenBrush(window, "DropTargetBrush", DesignTokens.DropTargetHex);
            AssertTokenBrush(window, "ScrollThumbBrush", DesignTokens.ScrollThumbHex);
            AssertTokenBrush(window, "ScrollThumbHoverBrush", DesignTokens.ScrollThumbHoverHex);
            AssertTokenBrush(window, "StatusSurfaceBrush", DesignTokens.StatusSurfaceHex);
            AssertTokenBrush(window, "OnAccentBrush", DesignTokens.OnAccentHex);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void DesignTokens_AreMirroredByWpfCornerRadiusResources()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            Assert.AreEqual(
                new CornerRadius(DesignTokens.DwmCornerRadius),
                (CornerRadius)window.FindResource("ShellCornerRadius"));
            Assert.AreEqual(
                new CornerRadius(DesignTokens.CardCornerRadius),
                (CornerRadius)window.FindResource("CardCornerRadius"));
            Assert.AreEqual(
                new CornerRadius(DesignTokens.ControlCornerRadius),
                (CornerRadius)window.FindResource("HeaderActionCornerRadius"));
            Assert.AreEqual(
                new CornerRadius(DesignTokens.PinButtonCornerRadius),
                (CornerRadius)window.FindResource("PinButtonCornerRadius"));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void WindowAnimationDurations_AreSourcedFromDesignTokens()
    {
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(DesignTokens.PanelExpandContentMs),
            GetPrivateStaticField<TimeSpan>("ExpandContentAnimationDuration"));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(DesignTokens.PanelSwitchContentMs),
            GetPrivateStaticField<TimeSpan>("SwitchContentAnimationDuration"));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(DesignTokens.ReducedMotionFadeMs),
            GetPrivateStaticField<TimeSpan>("ReducedMotionContentAnimationDuration"));
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(DesignTokens.CategoryRevealMs),
            GetPrivateStaticField<TimeSpan>("CategoryRevealAnimationDuration"));
        Assert.AreEqual(
            DesignTokens.ContentEntranceOffsetPx,
            GetPrivateStaticField<double>("CategoryRevealOffset"));
    }

    private static void AssertTokenBrush(MainWindow window, string resourceKey, string tokenHex)
    {
        var brush = window.FindResource(resourceKey) as SolidColorBrush;
        Assert.IsNotNull(brush, resourceKey);
        Assert.AreEqual(ParseTokenColor(tokenHex), brush.Color, resourceKey);
    }

    private static Color ParseTokenColor(string tokenHex)
    {
        var value = Convert.ToInt32(tokenHex[1..], 16);
        if (tokenHex.Length == 9)
        {
            return Color.FromArgb(
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value);
        }

        return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
}
