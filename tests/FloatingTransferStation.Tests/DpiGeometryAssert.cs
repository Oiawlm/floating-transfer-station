using System.Windows;
using System.Windows.Media;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 缩放感知几何比较的量化边界预算：比较两侧一共经过多少次 DIP→物理像素「取最近整数」量化。
/// 每次量化最多引入半个物理像素的舍入残差（非整倍缩放下必然存在：58 DIP ×1.25＝72.5 物理
/// 像素被取整后回除缩放，得 57.6/58.4 而非 58）；预算按次数把残差换算到比较域，在
/// 100%/125%/150% 数学上同样成立——硬编码 DIP 容差（0.01/0.5）只能在个别缩放下靠种子
/// 对齐侥幸通过或直接误报。
/// </summary>
internal enum PixelQuantization
{
    /// <summary>单个 HWND 几何值（Show/贴齐/恢复等 Win32 放置后的 Left/Top/Width/Height/
    /// ActualWidth/ActualHeight）与纯 DIP 期望（常量、SystemParameters、纯函数输出）比较。</summary>
    Single = 1,

    /// <summary>两个 HWND 几何值之和（如 Left＋Width 对 work.Right）或两次独立 HWND 测量互比。</summary>
    Pair = 2,

    /// <summary>PointToScreen 屏幕坐标两侧互比：每侧＝窗口放置量化＋屏幕坐标取整。</summary>
    ScreenPair = 4,
}

/// <summary>
/// 几何断言的缩放感知容差唯一来源：任何经过 HWND 物理像素域的窗口几何值都带量化舍入残差，
/// 断言域必须按比较域与当前显示缩放吸收它（第一性原理：一次量化 ≤ 半个物理像素）。
/// 锁定：DpiGeometryAssertTests（预算推导与边界行为）。
/// </summary>
internal static class DpiGeometryAssert
{
    /// <summary>一次「取最近整数」量化的最大舍入残差＝半个物理像素（第一性常数，非拍定的容差）。</summary>
    private const double HalfPhysicalPixel = 0.5;

    /// <summary>浮点往返尘埃裕度（物理像素域）：残差恰好取满半像素预算时不得因浮点表示误判。</summary>
    private const double FloatDustPixels = 1e-6;

    /// <summary>DIP 域缩放感知相等断言：期望与实测均为 DIP，容差＝量化次数 × 半物理像素 ÷ 当前缩放。</summary>
    public static void AreEqual(
        double expectedDip,
        double actualDip,
        Window window,
        PixelQuantization quantization,
        string message)
    {
        var scale = DpiScale(window);
        Assert.AreEqual(
            expectedDip,
            actualDip,
            DipTolerance(scale, quantization),
            $"{message}（DPI ×{scale:0.###}，{Describe(quantization)}）");
    }

    /// <summary>物理像素域相等断言：期望与实测均已是物理像素（PointToScreen/GetWindowRect 结果），
    /// 容差直接按量化次数计，不经缩放换算。</summary>
    public static void AreEqualScreen(
        double expectedPx,
        double actualPx,
        PixelQuantization quantization,
        string message)
    {
        Assert.AreEqual(
            expectedPx,
            actualPx,
            PixelTolerance(quantization),
            $"{message}（{Describe(quantization)}）");
    }

    /// <summary>下界断言：actualDip ≥ lowerBoundDip − 量化残差预算（如「窗口左缘不得推出工作区」）。</summary>
    public static void IsAtLeast(
        double actualDip,
        double lowerBoundDip,
        Window window,
        PixelQuantization quantization,
        string message)
    {
        var scale = DpiScale(window);
        Assert.IsTrue(
            actualDip >= lowerBoundDip - DipTolerance(scale, quantization),
            $"{message}：实际 {actualDip} DIP 应 ≥ {lowerBoundDip} DIP（已吸收量化残差，DPI ×{scale:0.###}，{Describe(quantization)}）。");
    }

    /// <summary>上界断言：actualDip ≤ upperBoundDip ＋ 量化残差预算（如「窗口下缘不得探出工作区」）。</summary>
    public static void IsAtMost(
        double actualDip,
        double upperBoundDip,
        Window window,
        PixelQuantization quantization,
        string message)
    {
        var scale = DpiScale(window);
        Assert.IsTrue(
            actualDip <= upperBoundDip + DipTolerance(scale, quantization),
            $"{message}：实际 {actualDip} DIP 应 ≤ {upperBoundDip} DIP（已吸收量化残差，DPI ×{scale:0.###}，{Describe(quantization)}）。");
    }

    /// <summary>窗口当前显示缩放（DIP→物理像素因子）；窗口未显示（无 HWND）时恒为 1——
    /// 未显示的窗口不存在物理像素量化，纯 DIP 域无需预算。</summary>
    internal static double DpiScale(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        var matrix = source?.CompositionTarget?.TransformToDevice;
        return matrix is { M11: > 0 } m && double.IsFinite(m.M11) ? m.M11 : 1d;
    }

    /// <summary>DIP 域容差＝量化次数 × 半物理像素 ÷ 缩放 ＋ 浮点尘埃。</summary>
    internal static double DipTolerance(double scale, PixelQuantization quantization) =>
        PixelTolerance(quantization) / (scale > 0 ? scale : 1d);

    /// <summary>物理像素域容差＝量化次数 × 半物理像素 ＋ 浮点尘埃。</summary>
    internal static double PixelTolerance(PixelQuantization quantization) =>
        (int)quantization * HalfPhysicalPixel + FloatDustPixels;

    private static string Describe(PixelQuantization quantization) =>
        $"缩放感知容差＝{(int)quantization} × 半物理像素量化残差";
}
