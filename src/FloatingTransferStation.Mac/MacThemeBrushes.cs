using Avalonia.Media;
using FloatingTransferStation.Design;

namespace FloatingTransferStation.Mac;

/// <summary>
/// Mac 端主题画刷集：浅/深两套均取自共享 Core 的 DesignTokens，
/// 跟随系统亮暗由窗口在 ActualThemeVariant 变化时整体重应用。
/// </summary>
internal sealed class MacThemeBrushes
{
    private MacThemeBrushes(
        IBrush windowShell,
        IBrush tabRail,
        IBrush card,
        IBrush border,
        IBrush ink,
        IBrush secondaryText,
        IBrush accent,
        IBrush onAccent)
    {
        WindowShell = windowShell;
        TabRail = tabRail;
        Card = card;
        BorderLine = border;
        Ink = ink;
        SecondaryText = secondaryText;
        Accent = accent;
        OnAccent = onAccent;
    }

    public IBrush WindowShell { get; }

    public IBrush TabRail { get; }

    public IBrush Card { get; }

    public IBrush BorderLine { get; }

    public IBrush Ink { get; }

    public IBrush SecondaryText { get; }

    public IBrush Accent { get; }

    public IBrush OnAccent { get; }

    public static MacThemeBrushes Light { get; } = new(
        Brush.Parse(DesignTokens.WindowShellHex),
        Brush.Parse(DesignTokens.TabRailHex),
        Brush.Parse(DesignTokens.CardHex),
        Brush.Parse(DesignTokens.BorderHex),
        Brush.Parse(DesignTokens.PrimaryTextHex),
        Brush.Parse(DesignTokens.SecondaryTextHex),
        Brush.Parse(DesignTokens.AccentHex),
        Brush.Parse(DesignTokens.OnAccentHex));

    public static MacThemeBrushes Dark { get; } = new(
        Brush.Parse(DesignTokens.WindowShellDarkHex),
        Brush.Parse(DesignTokens.TabRailDarkHex),
        Brush.Parse(DesignTokens.CardDarkHex),
        Brush.Parse(DesignTokens.BorderDarkHex),
        Brush.Parse(DesignTokens.PrimaryTextDarkHex),
        Brush.Parse(DesignTokens.SecondaryTextDarkHex),
        Brush.Parse(DesignTokens.AccentDarkHex),
        Brush.Parse(DesignTokens.OnAccentDarkHex));
}
