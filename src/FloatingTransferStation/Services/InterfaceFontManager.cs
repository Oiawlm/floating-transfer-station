using System.Windows;
using System.Windows.Media;
using FloatingTransferStation.Design;

namespace FloatingTransferStation.Services;

/// <summary>
/// 界面字体（自定义字体全链路，2026-10-25）：进程级当前字体（null = 默认
/// DesignTokens.InterfaceFontFamily）＋按字体度量推导的卡片正文行高族资源。
/// 应用方式与 DesignThemeManager 的主题整本替换不同——字体与行高是四个
/// 窗口级槽位资源（覆盖 MainWindowStyles.xaml 中的同名默认值），消费者一律
/// DynamicResource 引用，槽位增删即时生效；默认态删除槽位、回落样式字典的
/// 单一定义（数值唯一来源不被窗口级副本稀释）。
/// 行高推导：ceil(CardTextFontSize × LineSpacing + CardTextExtraLeadingPx)，
/// 默认字体 Segoe UI / Segoe UI Variable（LineSpacing 同为 1.330078）→ 20，
/// 与既有 CardTextLineHeight=20 精确一致（契约 #25 行距唯一来源的数值语义
/// 保持，锁定测试互锁）；更高 em 字体得到更大行高且 ≥ 自然行高（不裁切），
/// 行数上限资源随行高等比放大（默认 100/40 不变）。
/// </summary>
public static class InterfaceFontManager
{
    public const string InterfaceFontFamilyKey = "InterfaceFontFamily";
    public const string CardTextLineHeightKey = "CardTextLineHeight";
    public const string CardTextPreviewMaxHeightKey = "CardTextPreviewMaxHeight";
    public const string CardNoteMaxHeightKey = "CardNoteMaxHeight";

    private static FontFamily? _overrideFamily;

    /// <summary>进程级当前覆盖字体（null = 默认）。镜像偏好 InterfaceFont，由
    /// MainWindow.ApplyPreferences 唯一管线喂值；后建的窗口在构造期读本值自应用。</summary>
    public static FontFamily? OverrideFamily => _overrideFamily;

    public static FontFamily CurrentFamily => _overrideFamily ?? DefaultFamily();

    public static FontFamily DefaultFamily() => new(DesignTokens.InterfaceFontFamily);

    /// <summary>
    /// 解析字体族名：空白、或不在系统字体清单（Fonts.SystemFontFamilies 的
    /// Source 串；含被卸载的旧偏好）一律回落 null=默认。WPF 对不存在的名字
    /// 不抛异常而给回退度量，靠清单校验保住「所见即所选」。
    /// </summary>
    public static FontFamily? ResolveFamily(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
        {
            return null;
        }

        var trimmed = fontName.Trim();
        return Fonts.SystemFontFamilies.Any(family =>
            string.Equals(family.Source, trimmed, StringComparison.OrdinalIgnoreCase))
            ? new FontFamily(trimmed)
            : null;
    }

    public static void SetOverride(string? fontName)
    {
        _overrideFamily = ResolveFamily(fontName);
    }

    /// <summary>把当前字体（默认或覆盖）与推导行高族资源应用到窗口。
    /// 默认态删除窗口级槽位，回落 MainWindowStyles.xaml 的单一定义。</summary>
    public static void Apply(Window window)
    {
        if (_overrideFamily is null)
        {
            window.Resources.Remove(InterfaceFontFamilyKey);
            window.Resources.Remove(CardTextLineHeightKey);
            window.Resources.Remove(CardTextPreviewMaxHeightKey);
            window.Resources.Remove(CardNoteMaxHeightKey);
            return;
        }

        var lineHeight = DeriveCardTextLineHeight(_overrideFamily);
        window.Resources[InterfaceFontFamilyKey] = _overrideFamily;
        window.Resources[CardTextLineHeightKey] = lineHeight;
        window.Resources[CardTextPreviewMaxHeightKey] =
            lineHeight * DesignTokens.CardTextMaxVisibleLines;
        window.Resources[CardNoteMaxHeightKey] =
            lineHeight * DesignTokens.CardNoteMaxVisibleLines;
    }

    /// <summary>应用到根窗口及其全部后代存活窗口（Owner 链递归；测试宿主无
    /// Application.Current，与 DesignThemeManager 同法不依赖应用级枚举）。</summary>
    public static void ApplyToWindowTree(Window root)
    {
        Apply(root);
        ApplyToOwnedWindows(root);
    }

    private static void ApplyToOwnedWindows(Window owner)
    {
        // WindowCollection 是非泛型集合（快照防枚举中变更）。
        foreach (Window child in owner.OwnedWindows.Cast<Window>().ToArray())
        {
            Apply(child);
            ApplyToOwnedWindows(child);
        }
    }

    /// <summary>行高推导纯函数：字号 × 行距（em）＋额外行距，向上取整——
    /// 结果恒 ≥ 自然行高的整数上界（BlockLineHeight 不裁切字形）。</summary>
    public static double DeriveCardTextLineHeight(double lineSpacingEm) =>
        Math.Ceiling(DesignTokens.CardTextFontSize * lineSpacingEm + DesignTokens.CardTextExtraLeadingPx);

    public static double DeriveCardTextLineHeight(FontFamily family) =>
        DeriveCardTextLineHeight(family.LineSpacing);
}
