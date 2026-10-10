using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class AppPreferencesTests
{
    [TestMethod]
    public void Default_DisablesGlobalHotkey()
    {
        Assert.IsFalse(AppPreferences.Default.GlobalHotkeyEnabled);
        Assert.IsTrue(AppPreferences.Default.AnimationsEnabled);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesGlobalHotkeyPreference()
    {
        var preferences = new AppPreferences(
            ThemePreference.Dark,
            AnimationsEnabled: false,
            GlobalHotkeyEnabled: true);

        var restored = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));

        Assert.AreEqual(preferences, restored);
        Assert.IsTrue(restored!.GlobalHotkeyEnabled);
    }

    [TestMethod]
    public void OlderJson_WithoutGlobalHotkeyField_FallsBackToDisabled()
    {
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"ThemeMode":0,"AnimationsEnabled":false}""");

        Assert.IsNotNull(restored);
        Assert.IsFalse(restored.GlobalHotkeyEnabled, "旧版 preferences.json 缺字段时必须回落到默认关闭。");
        Assert.IsFalse(restored.AnimationsEnabled);
    }

    [TestMethod]
    public void Default_EnablesCopyGesturesAndUsesDualTrashBehavior()
    {
        var preferences = AppPreferences.Default;

        Assert.IsTrue(preferences.RightClickCardCopyEnabled);
        Assert.IsTrue(preferences.CopySelectionWithCtrlCEnabled);
        Assert.AreEqual(TrashNoSelectionLeftClickAction.ClearNonPinned, preferences.TrashNoSelectionLeftClick);
        Assert.AreEqual(TrashNoSelectionRightClickAction.ClearAll, preferences.TrashNoSelectionRightClick);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesCopyGesturesAndTrashBehavior()
    {
        var preferences = new AppPreferences(
            ThemeMode: ThemePreference.Light,
            AnimationsEnabled: true,
            GlobalHotkeyEnabled: false,
            RightClickCardCopyEnabled: false,
            CopySelectionWithCtrlCEnabled: false,
            TrashNoSelectionLeftClick: TrashNoSelectionLeftClickAction.ClearAll,
            TrashNoSelectionRightClick: TrashNoSelectionRightClickAction.NoAction);

        var restored = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));

        Assert.AreEqual(preferences, restored);
        Assert.IsFalse(restored!.RightClickCardCopyEnabled);
        Assert.IsFalse(restored.CopySelectionWithCtrlCEnabled);
        Assert.AreEqual(TrashNoSelectionLeftClickAction.ClearAll, restored.TrashNoSelectionLeftClick);
        Assert.AreEqual(TrashNoSelectionRightClickAction.NoAction, restored.TrashNoSelectionRightClick);
    }

    [TestMethod]
    public void OlderJson_WithoutCopyAndTrashFields_FallsBackToDefaults()
    {
        // LocalStore 以 camelCase + 字符串枚举写 preferences.json；
        // 这里用同一份选项模拟 1.14.x 写出的旧文件升级到 1.15.0 的读取路径。
        var storeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"themeMode":"Dark","animationsEnabled":true,"globalHotkeyEnabled":true}""",
            storeOptions);

        Assert.IsNotNull(restored);
        Assert.AreEqual(ThemePreference.Dark, restored.ThemeMode);
        Assert.IsTrue(restored.GlobalHotkeyEnabled);
        Assert.IsTrue(restored.RightClickCardCopyEnabled, "1.15.0 之前的 preferences.json 必须回落到复制手势默认开启。");
        Assert.IsTrue(restored.CopySelectionWithCtrlCEnabled);
        Assert.AreEqual(TrashNoSelectionLeftClickAction.ClearNonPinned, restored.TrashNoSelectionLeftClick);
        Assert.AreEqual(TrashNoSelectionRightClickAction.ClearAll, restored.TrashNoSelectionRightClick);
    }

    [TestMethod]
    public void Default_UsesDefaultPluginsDirectory()
    {
        Assert.IsNull(AppPreferences.Default.PluginsDirectoryOverride);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesPluginsDirectoryOverride()
    {
        var preferences = new AppPreferences(PluginsDirectoryOverride: @"D:\MyPlugins");

        var restored = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));

        Assert.AreEqual(preferences, restored);
        Assert.AreEqual(@"D:\MyPlugins", restored!.PluginsDirectoryOverride);
    }

    [TestMethod]
    public void OlderJson_WithoutPluginsDirectoryField_FallsBackToDefault()
    {
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"ThemeMode":0,"AnimationsEnabled":true}""");

        Assert.IsNotNull(restored);
        Assert.IsNull(restored.PluginsDirectoryOverride, "旧版 preferences.json 缺字段时必须回落到默认插件目录。");
    }

    [TestMethod]
    public void Default_EnablesAutoCleanup()
    {
        Assert.IsTrue(AppPreferences.Default.AutoCleanupEnabled, "自动清理必须默认开启（老用户升级即生效）。");
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesAutoCleanupPreference()
    {
        var preferences = AppPreferences.Default with { AutoCleanupEnabled = false };

        var restored = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));

        Assert.AreEqual(preferences, restored);
        Assert.IsFalse(restored!.AutoCleanupEnabled);
    }

    [TestMethod]
    public void OlderJson_WithoutAutoCleanupFields_FallsBackToEnabled()
    {
        var storeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"themeMode":"Dark","animationsEnabled":true,"globalHotkeyEnabled":true}""",
            storeOptions);

        Assert.IsNotNull(restored);
        Assert.IsTrue(restored.AutoCleanupEnabled, "1.23.0 之前的 preferences.json 必须回落到自动清理默认开启。");
    }

    [TestMethod]
    public async Task LegacyPreferencesFile_WithRetiredAutoCleanupTimestamp_LoadsViaProductionStore()
    {
        // 1.25.0 删除了间隔清扫记账字段 autoCleanupLastRunAtUtc；老 preferences.json
        // 里的残留字段必须被生产读取路径（LocalStore 的 JsonOptions，未映射成员跳过）
        // 忽略，不得让加载失败或回落到损坏兜底。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PreferencesFile)!);
        await File.WriteAllTextAsync(
            paths.PreferencesFile,
            """{"themeMode":"Dark","autoCleanupEnabled":false,"autoCleanupLastRunAtUtc":"2026-10-09T08:30:00+00:00"}""");
        using var store = new LocalStore(paths, new AtomicTextWriter());

        var loaded = await store.LoadPreferencesAsync();

        Assert.IsFalse(loaded.AutoCleanupEnabled, "开关字段必须照常读取。");
    }
}
