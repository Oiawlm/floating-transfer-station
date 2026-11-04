using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 数据根布局与默认位置的契约测试（数据目录模型统一）：默认＝
/// <c>%LocalAppData%\悬浮中转站\</c>（以 LocalAppData 为「所选目录」的同一形状）；
/// 登记读取走「受管形状优先＋两个旧形状待迁移接受」统一入口；插件目录结构性
/// 内嵌为数据根 plugins。
/// </summary>
[TestClass]
public sealed class AppPathsTests
{
    private sealed class FixedSettings(string? registered) : IDataDirectorySettings
    {
        public string? ReadDataDirectory() => registered;
    }

    [TestMethod]
    public void CreateDefault_UsesUnifiedRootShapeDefaultPosition()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.DisplayName);
        Assert.AreEqual(expected, AppPaths.CreateDefault().DataDirectory);
        Assert.AreEqual(expected, AppPaths.DefaultDataDirectory());
    }

    [TestMethod]
    public void CreateDefault_AcceptsManagedAndLegacyRegistrations()
    {
        var root = Path.Combine(TestTempRoot.Root, "fts-app-paths-" + Guid.NewGuid().ToString("N"));
        var managed = Path.Combine(root, "store", "悬浮中转站");
        Assert.AreEqual(managed, AppPaths.CreateDefault(new FixedSettings(managed)).DataDirectory);

        var legacyDirect = Path.Combine(root, "store", "Data");
        Assert.AreEqual(legacyDirect, AppPaths.CreateDefault(new FixedSettings(legacyDirect)).DataDirectory,
            "旧直下形状＝待迁移旧形：启动归一失败时保旧运行。");

        var legacyNested = Path.Combine(root, "store", "悬浮中转站", "Data");
        Assert.AreEqual(legacyNested, AppPaths.CreateDefault(new FixedSettings(legacyNested)).DataDirectory,
            "旧嵌套形状＝待迁移旧形：启动归一失败时保旧运行。");
    }

    [TestMethod]
    public void CreateDefault_FallsBackToDefaultForUnmanagedRegistration()
    {
        var paths = AppPaths.CreateDefault(new FixedSettings(@"C:\Store\Other"));
        Assert.AreEqual(AppPaths.DefaultDataDirectory(), paths.DataDirectory);
    }

    [TestMethod]
    public void FromDataDirectory_PluginsDirectoryIsStructurallyNested()
    {
        // 插件目录恒等于 数据根\plugins（无独立调整能力），启用状态同留数据根。
        var root = Path.Combine(TestTempRoot.Root, "fts-app-paths-root");
        var paths = AppPaths.FromDataDirectory(root);
        Assert.AreEqual(Path.Combine(root, "plugins"), paths.PluginsDirectory);
        Assert.AreEqual(Path.Combine(root, "plugins-state.json"), paths.PluginStateFile);
        Assert.AreEqual(Path.Combine(root, "board.json"), paths.BoardFile);
        Assert.AreEqual(Path.Combine(root, "preferences.json"), paths.PreferencesFile);
        Assert.AreEqual(Path.Combine(root, "images"), paths.ImagesDirectory);
        Assert.AreEqual(Path.Combine(root, "reviews"), paths.ReviewsDirectory);
    }

    [TestMethod]
    public void PluginCatalog_UserPluginsDirectoryFollowsDataRoot()
    {
        var root = Path.Combine(TestTempRoot.Root, "fts-app-paths-catalog");
        var catalog = new PluginCatalog(AppPaths.FromDataDirectory(root), null, new AtomicTextWriter());
        Assert.AreEqual(Path.Combine(root, "plugins"), catalog.UserPluginsDirectory);
    }
}
