using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void ThemeSwitch_SwapsEveryTokenBrushBetweenLightAndDark()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            DesignThemeManager.Apply(window, DesignTheme.Light);
            AssertTokenBrush(window, "WindowShellBrush", DesignTokens.WindowShellHex);
            AssertTokenBrush(window, "AccentBrush", DesignTokens.AccentHex);
            AssertTokenBrush(window, "PrimaryTextBrush", DesignTokens.PrimaryTextHex);

            DesignThemeManager.Apply(window, DesignTheme.Dark);
            AssertTokenBrush(window, "WindowShellBrush", DesignTokens.WindowShellDarkHex);
            AssertTokenBrush(window, "TabRailBrush", DesignTokens.TabRailDarkHex);
            AssertTokenBrush(window, "CardBrush", DesignTokens.CardDarkHex);
            AssertTokenBrush(window, "BorderBrush", DesignTokens.BorderDarkHex);
            AssertTokenBrush(window, "PrimaryTextBrush", DesignTokens.PrimaryTextDarkHex);
            AssertTokenBrush(window, "SecondaryTextBrush", DesignTokens.SecondaryTextDarkHex);
            AssertTokenBrush(window, "AccentBrush", DesignTokens.AccentDarkHex);
            AssertTokenBrush(window, "DangerBrush", DesignTokens.DangerDarkHex);
            AssertTokenBrush(window, "HeaderBrush", DesignTokens.HeaderDarkHex);
            AssertTokenBrush(window, "HoverSurfaceBrush", DesignTokens.HoverSurfaceDarkHex);
            AssertTokenBrush(window, "HoverBorderBrush", DesignTokens.HoverBorderDarkHex);
            AssertTokenBrush(window, "SelectedCardBrush", DesignTokens.SelectedCardDarkHex);
            AssertTokenBrush(window, "SelectedCardBorderBrush", DesignTokens.SelectedCardBorderDarkHex);
            AssertTokenBrush(window, "ActiveCategoryBrush", DesignTokens.ActiveCategoryDarkHex);
            AssertTokenBrush(window, "DropTargetBrush", DesignTokens.DropTargetDarkHex);
            AssertTokenBrush(window, "ScrollThumbBrush", DesignTokens.ScrollThumbDarkHex);
            AssertTokenBrush(window, "ScrollThumbHoverBrush", DesignTokens.ScrollThumbHoverDarkHex);
            AssertTokenBrush(window, "StatusSurfaceBrush", DesignTokens.StatusSurfaceDarkHex);
            AssertTokenBrush(window, "OnAccentBrush", DesignTokens.OnAccentDarkHex);
            StringAssert.EndsWith(
                (window.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
                    .Replace('\\', '/'),
                "Resources/DesignTheme.Dark.xaml");

            DesignThemeManager.Apply(window, DesignTheme.Light);
            AssertTokenBrush(window, "WindowShellBrush", DesignTokens.WindowShellHex);
            StringAssert.EndsWith(
                (window.Resources.MergedDictionaries[1].Source?.OriginalString ?? string.Empty)
                    .Replace('\\', '/'),
                "Resources/DesignTheme.Light.xaml");
        }
        finally
        {
            DesignThemeManager.Apply(window, DesignTheme.Light);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ThemeSwitch_UpdatesRenderedShellAndRailSurfaces()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("深色主题卡片");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            DesignThemeManager.Apply(window, DesignTheme.Light);
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var shell = (Border)window.FindName("WindowShell");
            var rail = (Border)window.FindName("CategoryRail");

            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            Assert.AreEqual(
                ParseTokenColor(DesignTokens.WindowShellDarkHex),
                ((SolidColorBrush)shell.Background).Color);
            Assert.AreEqual(
                ParseTokenColor(DesignTokens.TabRailDarkHex),
                ((SolidColorBrush)rail.Background).Color);

            DesignThemeManager.Apply(window, DesignTheme.Light);
            CompleteLayout(window);
            Assert.AreEqual(
                ParseTokenColor(DesignTokens.WindowShellHex),
                ((SolidColorBrush)shell.Background).Color);
        }
        finally
        {
            DesignThemeManager.Apply(window, DesignTheme.Light);
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void ThemeEvidence_RendersBothThemes()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("深浅主题都保持可读的置顶卡片");
        board.SetPinnedMany([pinned.Id], true);
        board.AddText("普通卡片内容");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            DesignThemeManager.Apply(window, DesignTheme.Light);
            ExpandCategory(window, BoardCategory.Inbox);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(board.Items(BoardCategory.Inbox).Last());
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "theme-light.png",
                "FTS_THEME_EVIDENCE_DIR");

            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "theme-dark.png",
                "FTS_THEME_EVIDENCE_DIR");
        }
        finally
        {
            DesignThemeManager.Apply(window, DesignTheme.Light);
            CloseWindow(window);
        }
    }
}
