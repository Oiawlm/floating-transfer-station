namespace FloatingTransferStation.Tests;

/// <summary>
/// 缩放感知几何断言基建的锁定测试（DpiGeometryAssert 唯一事实源的数学契约）：
/// 预算推导在 100%/125%/150% 下的一致性、边界含等（半像素恰好取满必须判过）、
/// 越界必红（注入超出预算的扰动必须失败）。纯数学，不拉起窗口。
/// </summary>
[TestClass]
public sealed class DpiGeometryAssertTests
{
    [TestMethod]
    public void DipTolerance_DerivesFromScaleAndQuantizationCount()
    {
        // DIP 域容差＝量化次数 × 半物理像素 ÷ 缩放（＋浮点尘埃 1e-6 物理 px）：
        // 125% 下 Single 恰为 0.4 DIP（半物理像素 0.5 ÷ 1.25），Pair 为 0.8 DIP——
        // 正是本机 125% 实测残差族（58 vs 58.4、50 vs 50.4、1536 vs 1536.8）的理论上界。
        Assert.AreEqual((0.5 + 1e-6) / 1.0, DpiGeometryAssert.DipTolerance(1.0, PixelQuantization.Single), 1e-9);
        Assert.AreEqual((0.5 + 1e-6) / 1.25, DpiGeometryAssert.DipTolerance(1.25, PixelQuantization.Single), 1e-9);
        Assert.AreEqual((0.5 + 1e-6) / 1.5, DpiGeometryAssert.DipTolerance(1.5, PixelQuantization.Single), 1e-9);
        Assert.AreEqual((1.0 + 1e-6) / 1.25, DpiGeometryAssert.DipTolerance(1.25, PixelQuantization.Pair), 1e-9);
        Assert.AreEqual((1.0 + 1e-6) / 1.0, DpiGeometryAssert.DipTolerance(1.0, PixelQuantization.Pair), 1e-9);
    }

    [TestMethod]
    public void PixelTolerance_CountsHalfPhysicalPixelsWithoutScale()
    {
        // 物理像素域比较（PointToScreen/GetWindowRect）不经缩放换算：
        // ScreenPair＝4 次量化＝2 物理像素（本机 125% 实测 1855 vs 1854 的残差族上界）。
        Assert.AreEqual(0.5 + 1e-6, DpiGeometryAssert.PixelTolerance(PixelQuantization.Single), 1e-9);
        Assert.AreEqual(1.0 + 1e-6, DpiGeometryAssert.PixelTolerance(PixelQuantization.Pair), 1e-9);
        Assert.AreEqual(2.0 + 1e-6, DpiGeometryAssert.PixelTolerance(PixelQuantization.ScreenPair), 1e-9);
    }

    [TestMethod]
    public void AreEqual_AcceptsWorstCaseRoundingResidual()
    {
        // 残差恰好取满半像素预算（含浮点表示尘埃）必须判过：这是量化残差，不是几何错误。
        // 125%：58 DIP → 72.5 物理 → 取整 73 → 回读 58.400000000000006；
        // 和差侧 1536 vs 1536.8000000000002（两次量化各半像素）同源。
        var single125 = DpiGeometryAssert.DipTolerance(1.25, PixelQuantization.Single);
        var pair125 = DpiGeometryAssert.DipTolerance(1.25, PixelQuantization.Pair);
        var collapsedWidth = Math.Round(72.5d) / 1.25d;
        var rightEdgeSum = (Math.Round(1478d * 1.25d) + Math.Round(58d * 1.25d)) / 1.25d;
        Assert.AreEqual(58d, collapsedWidth, single125);
        Assert.AreEqual(1536d, rightEdgeSum, pair125);
    }

    [TestMethod]
    public void AreEqual_RejectsPerturbationBeyondQuantizationBudget()
    {
        // 越过预算的真实几何偏移必须红：偏移按「预算的整数倍」运行时构造
        // （4×预算远超半像素残差，且非常量表达式，静态不可判定为恒假）。
        var single125 = DpiGeometryAssert.DipTolerance(1.25, PixelQuantization.Single);
        var single100 = DpiGeometryAssert.DipTolerance(1.0, PixelQuantization.Single);
        var screenPair = DpiGeometryAssert.PixelTolerance(PixelQuantization.ScreenPair);
        AssertFails(() => Assert.AreEqual(58d, 58d + 4 * single125, single125));
        AssertFails(() => Assert.AreEqual(58d, 58d + 4 * single100, single100));
        AssertFails(() => Assert.AreEqual(1855d, 1855d - 2 * screenPair, screenPair));
    }

    private static void AssertFails(Action action)
    {
        try
        {
            action();
        }
        catch (AssertFailedException)
        {
            return;
        }

        Assert.Fail("超出量化预算的偏移必须使断言失败。");
    }
}
