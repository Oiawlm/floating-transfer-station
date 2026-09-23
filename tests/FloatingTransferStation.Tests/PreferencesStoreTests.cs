using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class PreferencesStoreTests
{
    [TestMethod]
    public async Task LoadPreferences_ReturnsDefaultsWhenFileIsMissing()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());

        var preferences = await store.LoadPreferencesAsync();

        Assert.AreEqual(AppPreferences.Default, preferences);
        Assert.AreEqual(ThemePreference.FollowSystem, preferences.ThemeMode);
        Assert.IsTrue(preferences.AnimationsEnabled);
    }

    [TestMethod]
    public async Task SaveAndLoadPreferences_RoundTripsCamelCaseJson()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var preferences = new AppPreferences(ThemePreference.Dark, AnimationsEnabled: false);

        await store.SavePreferencesAsync(preferences);
        var loaded = await store.LoadPreferencesAsync();

        Assert.AreEqual(preferences, loaded);
        Assert.IsTrue(File.Exists(paths.PreferencesFile));
        var json = await File.ReadAllTextAsync(paths.PreferencesFile);
        StringAssert.Contains(json, "\"themeMode\": \"Dark\"");
        StringAssert.Contains(json, "\"animationsEnabled\": false");
    }

    [TestMethod]
    public async Task LoadPreferences_FallsBackToBackupWhenPrimaryIsCorrupt()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var firstSave = new AppPreferences(ThemePreference.Dark, AnimationsEnabled: false);
        await store.SavePreferencesAsync(firstSave);
        // 第二次保存产生 .bak（内容为第一次保存），再破坏主文件验证回退。
        await store.SavePreferencesAsync(new AppPreferences(ThemePreference.Light, AnimationsEnabled: true));
        await File.WriteAllTextAsync(paths.PreferencesFile, "{ not json");

        var loaded = await store.LoadPreferencesAsync();

        Assert.AreEqual(firstSave, loaded);
    }

    [TestMethod]
    public async Task LoadPreferences_PreservesCorruptFileAndReturnsDefaults()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        await File.WriteAllTextAsync(paths.PreferencesFile, "{ not json");

        var loaded = await store.LoadPreferencesAsync();

        Assert.AreEqual(AppPreferences.Default, loaded);
        Assert.IsTrue(
            Directory.EnumerateFiles(directory.Root, "preferences.json.corrupt-*.bak").Any(),
            "The corrupt primary file must be preserved for recovery.");
    }

    [TestMethod]
    public void AppPaths_IncludesThePreferencesFileInTheDataDirectory()
    {
        using var directory = new TestDirectory();

        var paths = AppPaths.ForTests(directory.Root);

        Assert.AreEqual(
            Path.Combine(directory.Root, "preferences.json"),
            paths.PreferencesFile);
    }
}

[TestClass]
public sealed class WindowsStartupManagerTests
{
    [TestMethod]
    public void BuildRunCommand_WrapsThePathInQuotesLikeTheInstaller()
    {
        // 与 installer/悬浮中转站.iss 的 ValueData: """{app}\{#MyAppExeName}""" 保持一致。
        Assert.AreEqual(
            "\"C:\\Programs\\悬浮中转站\\悬浮中转站.exe\"",
            WindowsStartupManager.BuildRunCommand("C:\\Programs\\悬浮中转站\\悬浮中转站.exe"));
    }

    [TestMethod]
    public void BuildRunCommand_RejectsMissingPath()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => WindowsStartupManager.BuildRunCommand(null!));
    }
}
