using FloatingTransferStation.Models;

namespace FloatingTransferStation.Tests;

/// <summary>标签轨道「结构常量」不变式锁定（2026-11-04 回归修复，设计契约 #54）：
/// 轨道宽＝内容无关结构常量 58，禁止为内容加宽；内容侧按两字/行自适应消费
/// 结构宽（纯布局约束，锁定测试见 App 套件 MainWindowInteractionTests）。</summary>
[TestClass]
public sealed class CategoryRailLabelTests
{
    [TestMethod]
    public void TabWidth_IsTheContentIndependentStructuralConstant58()
    {
        // 2026-11-03 W7 曾为四字默认名单行显示加宽到 66，2026-11-04 判定为回归：
        // 结构尺寸不得为内容让步，常量恢复 58 并由此锁定防再漂移。
        // 经局部变量中转：绕开 MSTEST0032 对「常量 vs 常量」的恒真折叠告警——
        // 这把锁防的是未来改动把常量漂离 58。
        double actualTabWidth = WindowSettings.TabWidth;
        Assert.AreEqual(58d, actualTabWidth);
    }
}
