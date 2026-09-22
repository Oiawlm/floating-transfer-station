using Avalonia.Media;
using Avalonia.Media.Immutable;
using FloatingTransferStation.Design;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Mac.Tests;

[TestClass]
public sealed class MacThemeBrushesTests
{
    [TestMethod]
    public void LightBrushes_MatchCoreDesignTokens()
    {
        AssertBrush(MacThemeBrushes.Light.WindowShell, DesignTokens.WindowShellHex);
        AssertBrush(MacThemeBrushes.Light.TabRail, DesignTokens.TabRailHex);
        AssertBrush(MacThemeBrushes.Light.Card, DesignTokens.CardHex);
        AssertBrush(MacThemeBrushes.Light.BorderLine, DesignTokens.BorderHex);
        AssertBrush(MacThemeBrushes.Light.Ink, DesignTokens.PrimaryTextHex);
        AssertBrush(MacThemeBrushes.Light.SecondaryText, DesignTokens.SecondaryTextHex);
        AssertBrush(MacThemeBrushes.Light.Accent, DesignTokens.AccentHex);
        AssertBrush(MacThemeBrushes.Light.OnAccent, DesignTokens.OnAccentHex);
    }

    [TestMethod]
    public void DarkBrushes_MatchCoreDesignTokens()
    {
        AssertBrush(MacThemeBrushes.Dark.WindowShell, DesignTokens.WindowShellDarkHex);
        AssertBrush(MacThemeBrushes.Dark.TabRail, DesignTokens.TabRailDarkHex);
        AssertBrush(MacThemeBrushes.Dark.Card, DesignTokens.CardDarkHex);
        AssertBrush(MacThemeBrushes.Dark.BorderLine, DesignTokens.BorderDarkHex);
        AssertBrush(MacThemeBrushes.Dark.Ink, DesignTokens.PrimaryTextDarkHex);
        AssertBrush(MacThemeBrushes.Dark.SecondaryText, DesignTokens.SecondaryTextDarkHex);
        AssertBrush(MacThemeBrushes.Dark.Accent, DesignTokens.AccentDarkHex);
        AssertBrush(MacThemeBrushes.Dark.OnAccent, DesignTokens.OnAccentDarkHex);
    }

    private static void AssertBrush(IBrush? brush, string tokenHex)
    {
        Assert.IsInstanceOfType(brush, typeof(ImmutableSolidColorBrush));
        var color = ((ImmutableSolidColorBrush)brush!).Color;
        Assert.AreEqual(
            uint.Parse(tokenHex[1..], System.Globalization.NumberStyles.HexNumber),
            ((uint)color.R << 16) | ((uint)color.G << 8) | color.B,
            tokenHex);
    }
}
