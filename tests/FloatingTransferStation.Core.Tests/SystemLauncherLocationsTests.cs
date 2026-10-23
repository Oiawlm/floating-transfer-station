using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 系统位置静态注册表（#30 扩展）：CLSID 解析名与 explorer 回退别名锁死（唯一引入
/// 点——产品代码与 XAML 只经本表取值）；CreateItem 构造 ShellLocation 条目；图标键
/// 对 ::{…} 目标小写规范化且不影响既有 AUMID 语义。
/// </summary>
[TestClass]
public sealed class SystemLauncherLocationsTests
{
    [TestMethod]
    public void Registry_ExposesExactlyRecycleBinAndThisComputer()
    {
        Assert.AreEqual(2, SystemLauncherLocations.All.Count, "注册表恰为两项（回收站/此电脑）。");

        var recycleBin = SystemLauncherLocations.All[0];
        Assert.AreEqual("回收站", recycleBin.DisplayName);
        Assert.AreEqual("::{645FF040-5081-101B-9F08-00AA002F954E}", recycleBin.TargetPath, "回收站 CLSID 字面量锁死。");
        Assert.AreEqual("shell:RecycleBinFolder", recycleBin.ExplorerFallbackArgument);

        var thisComputer = SystemLauncherLocations.All[1];
        Assert.AreEqual("此电脑", thisComputer.DisplayName);
        Assert.AreEqual("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", thisComputer.TargetPath, "此电脑 CLSID 字面量锁死。");
        Assert.AreEqual("shell:MyComputerFolder", thisComputer.ExplorerFallbackArgument);

        Assert.AreSame(SystemLauncherLocations.RecycleBin, recycleBin, "All 与具名成员同源。");
        Assert.AreSame(SystemLauncherLocations.ThisComputer, thisComputer, "All 与具名成员同源。");
    }

    [TestMethod]
    public void CreateItem_BuildsShellLocationEntryFromRegistry()
    {
        var item = SystemLauncherLocations.RecycleBin.CreateItem();

        Assert.AreNotEqual(Guid.Empty, item.Id);
        Assert.AreEqual(LauncherKind.ShellLocation, item.Kind);
        Assert.AreEqual(SystemLauncherLocations.RecycleBin.TargetPath, item.TargetPath);
        Assert.AreEqual("回收站", item.DisplayName);
        Assert.IsNull(item.Arguments, "系统位置条目无参数段。");
    }

    [TestMethod]
    public void FallbackLookup_IsCaseInsensitiveAndNullForUnknownTargets()
    {
        Assert.AreEqual(
            "shell:RecycleBinFolder",
            SystemLauncherLocations.TryGetExplorerFallbackArgument("::{645ff040-5081-101b-9f08-00aa002f954e}"),
            "目标小写变体仍命中（大小写不敏感）。");
        Assert.IsNull(
            SystemLauncherLocations.TryGetExplorerFallbackArgument("::{00000000-0000-0000-0000-000000000000}"),
            "非注册表目标无回退。");
    }

    [TestMethod]
    public void IconKey_ClsidTargetsNormalizeToLowercaseWithoutTouchingAumidSemantics()
    {
        var canonical = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        var lowered = "::{645ff040-5081-101b-9f08-00aa002f954e}";

        Assert.AreEqual(lowered, LauncherIconKey.NormalizeTarget(canonical), "CLSID 目标小写规范化。");
        Assert.AreEqual(
            LauncherIconKey.Compute(canonical),
            LauncherIconKey.Compute(lowered),
            "大小写变体同缓存键（同目标去重成立）。");
        Assert.AreEqual(
            lowered,
            LauncherIconKey.NormalizeTarget($"  {canonical}  "),
            "首尾空白先规范化再小写化。");
        Assert.AreNotEqual(
            LauncherIconKey.Compute(SystemLauncherLocations.RecycleBin.TargetPath),
            LauncherIconKey.Compute(SystemLauncherLocations.ThisComputer.TargetPath),
            "不同系统位置不同键。");
        Assert.AreEqual(
            "shell:AppsFolder\\App!Publisher",
            LauncherIconKey.NormalizeTarget("shell:AppsFolder\\App!Publisher"),
            "AUMID 解析名语义不变（原样保留）。");
    }
}
