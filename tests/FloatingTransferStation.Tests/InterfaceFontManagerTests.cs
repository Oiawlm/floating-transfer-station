using System.Windows;
using System.Windows.Media;
using FloatingTransferStation.Design;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 界面字体管理器（自定义字体全链路，2026-10-25）守护：行高推导公式矩阵
/// （默认字体→20 与现状一致；更高 em 字体→更大值且恒≥自然行高不裁切）、
/// 字体名解析（空白/未安装→默认回落）、窗口级资源槽位应用与 Owner 链
/// 存活窗口递归覆盖。
/// </summary>
[TestClass]
public sealed class InterfaceFontManagerTests
{
    /// <summary>默认字体 Segoe UI / Segoe UI Variable 的实测 LineSpacing（两者同值）。</summary>
    private const double DefaultLineSpacing = 1.330078125;

    /// <summary>推导行高越过默认 20 桶的 em 行距下界：(20−额外行距)/字号。</summary>
    private static double TallerThanDefaultThreshold =>
        (20d - DesignTokens.CardTextExtraLeadingPx) / DesignTokens.CardTextFontSize;

    [TestCleanup]
    public void Cleanup() => InterfaceFontManager.SetOverride(null);

    // 推导公式矩阵：ceil(CardTextFontSize × LineSpacing + ExtraLeadingPx)。
    // 默认行（1.330078125）＝20 与既有 CardTextLineHeight 字面量（契约 #25
    // 现状）精确一致；更高 em 行（1.36/1.5/1.699951(Gabriola 实测)/2.0）
    // 推导出 21/22/25/29 更大值；紧凑行（1.0）＝15 不低于自然行高上界。
    [TestMethod]
    [DataRow(1.0, 15d)]
    [DataRow(1.330078125, 20d)]
    [DataRow(1.36, 21d)]
    [DataRow(1.5, 22d)]
    [DataRow(1.699951171875, 25d)]
    [DataRow(2.0, 29d)]
    public void DeriveCardTextLineHeight_FormulaMatrix_DefaultFontKeepsTwenty(
        double lineSpacingEm,
        double expected)
    {
        var derived = InterfaceFontManager.DeriveCardTextLineHeight(lineSpacingEm);

        Assert.AreEqual(expected, derived, $"LineSpacing={lineSpacingEm} 的推导行高。");
        // 不裁切：结果恒 ≥ 自然行高（字号×行距）的整数上界（BlockLineHeight 语义下
        // 行高小于自然值会切字形/叠行）。
        Assert.IsTrue(
            derived >= Math.Ceiling(DesignTokens.CardTextFontSize * lineSpacingEm),
            $"推导行高不得低于自然行高上界（LineSpacing={lineSpacingEm}）。");
        if (lineSpacingEm > TallerThanDefaultThreshold)
        {
            Assert.IsTrue(
                derived > 20d,
                $"更高 em 字体（LineSpacing={lineSpacingEm}）的行高必须大于默认 20。");
        }
    }

    [TestMethod]
    public void DeriveCardTextLineHeight_IsMonotonicAndNeverClipsAcrossEmRange()
    {
        var previous = 0d;
        for (var step = 0; step <= 150; step++)
        {
            var spacing = Math.Round(1.0 + step * 0.01, 4);
            var derived = InterfaceFontManager.DeriveCardTextLineHeight(spacing);

            Assert.IsTrue(
                derived >= previous,
                $"行高必须随 em 行距单调不减（LineSpacing={spacing}）。");
            Assert.IsTrue(
                derived >= Math.Ceiling(DesignTokens.CardTextFontSize * spacing),
                $"推导行高恒≥自然行高（LineSpacing={spacing}）。");
            previous = derived;
        }
    }

    [TestMethod]
    public void DeriveCardTextLineHeight_MatchesFormulaForEveryInstalledFont()
    {
        // 真实字体的公式等价：本机全部已安装字体逐一过推导（机器无关——期望值
        // 由同一公式的字体度量实时计算，不依赖具体字体在不在）。
        foreach (var family in Fonts.SystemFontFamilies)
        {
            var derived = InterfaceFontManager.DeriveCardTextLineHeight(family);

            Assert.AreEqual(
                Math.Ceiling(
                    DesignTokens.CardTextFontSize * family.LineSpacing
                    + DesignTokens.CardTextExtraLeadingPx),
                derived,
                $"{family.Source} 必须与公式值一致。");
            Assert.IsTrue(
                derived >= Math.Ceiling(DesignTokens.CardTextFontSize * family.LineSpacing),
                $"{family.Source} 的推导行高不得低于自然行高（不裁切）。");
        }
    }

    [TestMethod]
    public void DeriveCardTextLineHeight_DefaultFamilyKeepsTwenty()
    {
        Assert.AreEqual(
            20d,
            InterfaceFontManager.DeriveCardTextLineHeight(InterfaceFontManager.DefaultFamily()),
            "默认字体（Segoe UI Variable, Segoe UI——两族实测 LineSpacing 同为 1.330078）"
                + "的推导行高必须与既有 CardTextLineHeight=20 一致（现状零变化）。");
    }

    [TestMethod]
    public void ResolveFamily_FallsBackToDefaultForNullOrUninstalledNames()
    {
        Assert.IsNull(InterfaceFontManager.ResolveFamily(null));
        Assert.IsNull(InterfaceFontManager.ResolveFamily("   "));
        Assert.IsNull(
            InterfaceFontManager.ResolveFamily("NoSuchFontXYZ-不存在的字体"),
            "未安装字体名必须回落默认：WPF 对未知名字不抛错而给回退度量，"
                + "靠系统字体清单校验保住「所见即所选」。");
    }

    [TestMethod]
    public void ResolveFamily_AcceptsInstalledFamilyNameCaseInsensitively()
    {
        var installed = Fonts.SystemFontFamilies.First(family => family.Source != "Segoe UI");
        var resolved = InterfaceFontManager.ResolveFamily(installed.Source.ToUpperInvariant());

        Assert.IsNotNull(resolved);
        Assert.AreEqual(
            installed.Source,
            resolved.Source,
            true,
            "解析结果必须指向同一字体族（大小写不敏感清单匹配）。");
    }

    [STATestMethod]
    public void Apply_SetsDerivedSlotResourcesAndDefaultRemovesThem()
    {
        try
        {
            var family = Fonts.SystemFontFamilies.Single(f => f.Source == "Segoe UI");
            InterfaceFontManager.SetOverride("Segoe UI");
            var window = new Window();
            InterfaceFontManager.Apply(window);

            Assert.AreEqual(
                "Segoe UI",
                ((FontFamily)window.Resources[InterfaceFontManager.InterfaceFontFamilyKey]).Source);
            var lineHeight =
                (double)window.Resources[InterfaceFontManager.CardTextLineHeightKey];
            Assert.AreEqual(InterfaceFontManager.DeriveCardTextLineHeight(family), lineHeight);
            Assert.AreEqual(
                lineHeight * DesignTokens.CardTextMaxVisibleLines,
                (double)window.Resources[InterfaceFontManager.CardTextPreviewMaxHeightKey],
                "正文可见上限＝行高×5（默认字体=100 与既有几何一致）。");
            Assert.AreEqual(
                lineHeight * DesignTokens.CardNoteMaxVisibleLines,
                (double)window.Resources[InterfaceFontManager.CardNoteMaxHeightKey],
                "备注可见上限＝行高×2（默认字体=40 与既有几何一致）。");

            InterfaceFontManager.SetOverride(null);
            InterfaceFontManager.Apply(window);
            Assert.IsFalse(
                window.Resources.Contains(InterfaceFontManager.InterfaceFontFamilyKey),
                "默认态必须删除窗口级槽位，回落 MainWindowStyles.xaml 的单一定义。");
            Assert.IsFalse(window.Resources.Contains(InterfaceFontManager.CardTextLineHeightKey));
            Assert.IsFalse(window.Resources.Contains(InterfaceFontManager.CardTextPreviewMaxHeightKey));
            Assert.IsFalse(window.Resources.Contains(InterfaceFontManager.CardNoteMaxHeightKey));
        }
        finally
        {
            InterfaceFontManager.SetOverride(null);
        }
    }

    [STATestMethod]
    public void ApplyToWindowTree_CoversOwnedWindowsRecursively()
    {
        // WPF 要求 Owner 必须是已显示的窗口：先显示再挂 Owner 链。
        var root = new Window();
        root.Show();
        var child = new Window { Owner = root };
        child.Show();
        var grandChild = new Window { Owner = child };
        try
        {
            InterfaceFontManager.SetOverride("Segoe UI");
            InterfaceFontManager.ApplyToWindowTree(root);

            var chain = new[] { root, child, grandChild };
            for (var index = 0; index < chain.Length; index++)
            {
                Assert.AreEqual(
                    "Segoe UI",
                    ((FontFamily)chain[index].Resources[InterfaceFontManager.InterfaceFontFamilyKey]).Source,
                    $"Owner 链第 {index} 层窗口必须被覆盖。");
            }
        }
        finally
        {
            InterfaceFontManager.SetOverride(null);
            grandChild.Close();
            child.Close();
            root.Close();
        }
    }
}
