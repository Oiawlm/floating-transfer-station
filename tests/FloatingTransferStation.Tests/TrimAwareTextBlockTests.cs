using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FloatingTransferStation.Controls;

namespace FloatingTransferStation.Tests;

/// <summary>
/// TrimAwareTextBlock（1.26.0，契约 #22）的截断信号守护：判据是渲染布局事实
/// （MeasureOverride 双次测量：先 ∞ 高取自然高度，再 min(约束高, MaxHeight)
/// 限高收尾），不是字符数。恰好整行数（自然高度 == 限高）不得误判截断
/// （0.5 DIP 容差语义）。限高 MaxHeight 是唯一来源，随宽度/限高变化的翻转
/// 由 measure 驱动（虚拟化回收后重测自校正的同一机制）。
/// </summary>
[TestClass]
public sealed class TrimAwareTextBlockTests
{
    private const double LineHeight = 20d;

    /// <summary>构造承载控件：每行一个「字」（换行符强制定行），行数完全可控。</summary>
    private static (TrimAwareTextBlock Host, TextBlock Body) CreateHost(int lineCount, double maxHeight = 100d)
    {
        var body = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Text = string.Join("\n", Enumerable.Repeat("字", lineCount)),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        var host = new TrimAwareTextBlock { Child = body, MaxHeight = maxHeight };
        return (host, body);
    }

    /// <summary>无换行符的连续长文（折行数完全由宽度驱动的形态）。</summary>
    private static (TrimAwareTextBlock Host, TextBlock Body) CreateTruncatableHost(int totalCharacters)
    {
        var body = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Text = new string('字', totalCharacters),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        var host = new TrimAwareTextBlock { Child = body, MaxHeight = 100d };
        return (host, body);
    }

    [STATestMethod]
    public void TrimAwareTextBlock_LongTextIsTruncated()
    {
        var (host, _) = CreateHost(lineCount: 8);

        host.Measure(new Size(60, double.PositiveInfinity));

        Assert.IsTrue(host.IsTruncated, "8 行自然高度（160）超出限高 100 必须判截断。");
        Assert.IsTrue(
            host.DesiredSize.Height <= 100d,
            $"受限测量收尾：DesiredSize（{host.DesiredSize.Height}）不得超过限高。");
    }

    [STATestMethod]
    public void TrimAwareTextBlock_ShortOrExactlyFiveLinesIsNotTruncated()
    {
        var (shortHost, _) = CreateHost(lineCount: 3);
        shortHost.Measure(new Size(60, double.PositiveInfinity));
        Assert.IsFalse(shortHost.IsTruncated, "3 行（60）在限高 100 内不得判截断。");

        // 恰好 5 行：自然高度 == 限高（100 == 100）——0.5 DIP 容差下不算截断，
        // 没有省略号出现的布局事实。
        var (exactHost, _) = CreateHost(lineCount: 5);
        exactHost.Measure(new Size(60, double.PositiveInfinity));
        Assert.IsFalse(exactHost.IsTruncated, "恰好 5 行（100 == 限高 100）不得误判截断。");
        Assert.IsTrue(
            exactHost.DesiredSize.Height <= 100d,
            $"恰好 5 行的 DesiredSize（{exactHost.DesiredSize.Height}）不得超过限高。");
    }

    [STATestMethod]
    public void TrimAwareTextBlock_WidthChangeFlipsTruncation()
    {
        // 无换行符的连续长文（换行会定行、与宽度无关）：宽度驱动折行数变化。
        var (host, body) = CreateTruncatableHost(totalCharacters: 40);

        // 窄宽：约 4 字/行 → 10 行 → 截断。
        host.Measure(new Size(60, double.PositiveInfinity));
        Assert.IsTrue(host.IsTruncated, "窄宽下长文必须判截断。");

        // 拖宽：全部文字一行放下（40×14=560 < 800）→ 1 行 → 截断翻转消失。
        host.Measure(new Size(800, double.PositiveInfinity));
        Assert.IsFalse(host.IsTruncated, "拖宽后单行放下必须翻转回未截断。");

        // 收窄回到窄宽：翻转回截断（measure 驱动，虚拟化回收重测的同一机制）。
        host.Measure(new Size(60, double.PositiveInfinity));
        Assert.IsTrue(host.IsTruncated, "收窄后必须翻转回截断。");
        _ = body;
    }

    [STATestMethod]
    public void TrimAwareTextBlock_MaxHeightChangeFlipsTruncation()
    {
        var (host, _) = CreateHost(lineCount: 6, maxHeight: 100d);

        host.Measure(new Size(60, double.PositiveInfinity));
        Assert.IsTrue(host.IsTruncated, "6 行（120）超出限高 100 必须判截断。");

        // 限高放宽到 120（同一来源 MaxHeight）：恰好放下 → 未截断。
        host.MaxHeight = 120d;
        host.Measure(new Size(60, double.PositiveInfinity));
        Assert.IsFalse(host.IsTruncated, "限高放宽后恰好放下必须翻转回未截断。");
    }
}
