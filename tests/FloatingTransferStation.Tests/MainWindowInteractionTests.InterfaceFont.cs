using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 界面字体证据（自定义字体全链路，2026-10-25）：默认字体与两款自定义字体
/// （含高字形 Gabriola——本机未安装时自动跳过该字体组合）× 浅/深主题渲染
/// 取证；同时断言显示态正文自然高度＝行数×推导行高（默认 4×20 与现状一致，
/// 高字形字体＝更大值且不裁切）。取证目录环境变量 FTS_FONT_EVIDENCE_DIR
/// 未设置时不出 PNG（CI 空转），与 ThemeEvidence 同法。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    // 中英混排：拉丁字形直接呈现所选字体（汉字缺字时按 WPF 回退——与生产
    // 中文界面一致），四行显式换行定行数与高度断言。
    private const string FontEvidenceCardText =
        "第1行 Evidence AaBb 字体证据\n第2行 Sample GgQq 行高推导\n第3行 Text 0123 不裁切验证\n第4行 Proof ZzYy 四行同高";

    [STATestMethod]
    public void FontEvidence_RendersDefaultAndCustomFontsInBothThemes()
    {
        // 默认（null）＋两款自定义：微软雅黑 UI（CJK 常用）与 Gabriola（高字形，
        // LineSpacing≈1.70）。渲染取证在本机出证：字体未安装的组合跳过。
        var fonts = new (string? Name, string Label)[]
        {
            (null, "default"),
            ("Microsoft YaHei UI", "yahei"),
            ("Gabriola", "gabriola")
        };
        foreach (var (font, label) in fonts)
        {
            var family = font is null
                ? InterfaceFontManager.DefaultFamily()
                : InterfaceFontManager.ResolveFamily(font);
            if (family is null)
            {
                continue;
            }

            var expectedLineHeight = InterfaceFontManager.DeriveCardTextLineHeight(family);
            if (font is null)
            {
                Assert.AreEqual(20d, expectedLineHeight, "前置：默认字体推导行高必须=现状 20。");
            }

            foreach (var theme in new[] { DesignTheme.Light, DesignTheme.Dark })
            {
                using var directory = new TestDirectory();
                var board = new BoardService();
                board.AddText(FontEvidenceCardText, BoardCategory.Inbox);
                var window = CreateWindow(directory, board);
                try
                {
                    window.Show();
                    window.ApplyPreferences(window.CurrentPreferences with { InterfaceFont = font });
                    DesignThemeManager.Apply(window, theme);
                    ExpandCategory(window, BoardCategory.Inbox);
                    CompleteLayout(window);

                    var item = board.Items(BoardCategory.Inbox).Single();
                    var display = CardDisplayTextOf(ContainerOf(window, item), item);
                    Assert.AreEqual(
                        4 * expectedLineHeight,
                        display.ActualHeight,
                        0.5,
                        $"{label}（{theme}）：4 行正文自然高度必须=4×推导行高 {expectedLineHeight}。");
                    if (font is null)
                    {
                        Assert.AreEqual(
                            20d,
                            (double)window.FindResource("CardTextLineHeight"),
                            "默认态 FindResource 必须解析到样式字典的 20。");
                    }
                    else
                    {
                        Assert.AreEqual(
                            expectedLineHeight,
                            (double)window.Resources[InterfaceFontManager.CardTextLineHeightKey],
                            "窗口级行高槽位必须与推导值一致。");
                    }

                    SaveVisualEvidence(
                        (Border)window.FindName("WindowShell"),
                        $"font-{label}-{(theme == DesignTheme.Dark ? "dark" : "light")}.png",
                        "FTS_FONT_EVIDENCE_DIR");
                }
                finally
                {
                    InterfaceFontManager.SetOverride(null);
                    DesignThemeManager.Apply(window, DesignTheme.Light);
                    CloseWindow(window);
                }
            }
        }
    }
}
