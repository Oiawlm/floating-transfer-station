using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 系统位置条目（#30 扩展）：CLSID 解析名是唯一 TargetPath 形态；explorer 回退别名
/// 与 CLSID 同源登记（启动直接派发失败时的限定回退参数）。
/// </summary>
public sealed record SystemLauncherLocation(
    string DisplayName,
    string TargetPath,
    string ExplorerFallbackArgument)
{
    /// <summary>按注册表构造启动项（显示名与目标单一来源；条目与既有瓦片同型可改名/删除/去重）。</summary>
    public LauncherItem CreateItem() => new(
        Guid.NewGuid(),
        LauncherKind.ShellLocation,
        TargetPath,
        DisplayName,
        DateTimeOffset.Now);
}

/// <summary>
/// 系统位置静态注册表（#30 扩展）：回收站与「此电脑」。CLSID 解析路径只经本表引入
/// （禁止散落字面量）；＋菜单「系统位置」子菜单、图标提取与启动回退共用同一来源。
/// </summary>
public static class SystemLauncherLocations
{
    public static readonly SystemLauncherLocation RecycleBin = new(
        "回收站",
        "::{645FF040-5081-101B-9F08-00AA002F954E}",
        "shell:RecycleBinFolder");

    public static readonly SystemLauncherLocation ThisComputer = new(
        "此电脑",
        "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
        "shell:MyComputerFolder");

    public static IReadOnlyList<SystemLauncherLocation> All { get; } = [RecycleBin, ThisComputer];

    /// <summary>按目标路径查 explorer 回退别名（大小写不敏感；非注册表目标=null=不回退）。</summary>
    public static string? TryGetExplorerFallbackArgument(string targetPath) =>
        All.FirstOrDefault(location =>
            string.Equals(location.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase))
            ?.ExplorerFallbackArgument;
}
