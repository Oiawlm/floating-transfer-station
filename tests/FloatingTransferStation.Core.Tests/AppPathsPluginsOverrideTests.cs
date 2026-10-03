using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>插件目录偏好的解析规则：默认回退、数据目录之内拒绝、同默认路径归一化。</summary>
[TestClass]
public sealed class AppPathsPluginsOverrideTests
{
    private static string Root { get; } = Path.Combine(Path.GetTempPath(), "fts-app-paths-" + Guid.NewGuid().ToString("N"));

    [TestInitialize]
    public void Initialize() => Directory.CreateDirectory(Root);

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void NullOverride_KeepsDefault()
    {
        var paths = AppPaths.ForTests(Path.Combine(Root, "Data"));
        var (resolved, warning) = AppPaths.ResolvePluginsDirectoryOverride(paths, null);

        Assert.AreSame(paths, resolved);
        Assert.IsNull(warning);
    }

    [TestMethod]
    public void OverrideInsideDataDirectory_FallsBackWithWarning()
    {
        var paths = AppPaths.ForTests(Path.Combine(Root, "Data"));
        var (resolved, warning) = AppPaths.ResolvePluginsDirectoryOverride(
            paths,
            Path.Combine(paths.DataDirectory, "custom"));

        Assert.AreSame(paths, resolved);
        StringAssert.Contains(warning, "数据目录之内");
    }

    [TestMethod]
    public void OverrideEqualToDefault_NormalizesWithoutWarning()
    {
        var paths = AppPaths.ForTests(Path.Combine(Root, "Data"));
        var (resolved, warning) = AppPaths.ResolvePluginsDirectoryOverride(paths, paths.PluginsDirectory);

        Assert.AreSame(paths, resolved);
        Assert.IsNull(warning);
    }

    [TestMethod]
    public void RelativeOverride_FallsBackWithWarning()
    {
        var paths = AppPaths.ForTests(Path.Combine(Root, "Data"));
        var (resolved, warning) = AppPaths.ResolvePluginsDirectoryOverride(paths, "relative/plugins");

        Assert.AreSame(paths, resolved);
        StringAssert.Contains(warning, "完整路径");
    }

    [TestMethod]
    public void ValidExternalOverride_ReplacesPluginsDirectoryOnly()
    {
        var paths = AppPaths.ForTests(Path.Combine(Root, "Data"));
        var external = Path.Combine(Root, "MyPlugins");
        var (resolved, warning) = AppPaths.ResolvePluginsDirectoryOverride(
            paths,
            external + Path.DirectorySeparatorChar);

        Assert.IsNull(warning);
        Assert.AreEqual(
            Path.GetFullPath(external).TrimEnd(Path.DirectorySeparatorChar),
            resolved.PluginsDirectory);
        Assert.AreEqual(paths.PluginStateFile, resolved.PluginStateFile, "启用状态文件必须留在数据目录。");
        Assert.AreEqual(paths.DataDirectory, resolved.DataDirectory);
        Assert.AreEqual(paths.BoardFile, resolved.BoardFile);
    }
}
