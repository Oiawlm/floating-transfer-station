using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingTransferStation.Models;

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
    public void Default_EnablesAutoCleanupWithoutBaseline()
    {
        Assert.IsTrue(AppPreferences.Default.AutoCleanupEnabled, "自动清理必须默认开启（老用户升级即生效）。");
        Assert.IsNull(AppPreferences.Default.AutoCleanupLastRunAtUtc, "默认无调度基线：首次清扫在首个周期之后。");
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesAutoCleanupPreferenceAndTimestamp()
    {
        var preferences = AppPreferences.Default with
        {
            AutoCleanupEnabled = false,
            AutoCleanupLastRunAtUtc = new DateTimeOffset(2026, 10, 9, 8, 30, 0, TimeSpan.Zero)
        };

        var restored = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));

        Assert.AreEqual(preferences, restored);
        Assert.IsFalse(restored!.AutoCleanupEnabled);
        Assert.AreEqual(preferences.AutoCleanupLastRunAtUtc, restored.AutoCleanupLastRunAtUtc);
    }

    [TestMethod]
    public void OlderJson_WithoutAutoCleanupFields_FallsBackToEnabledWithoutBaseline()
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
        Assert.IsNull(restored.AutoCleanupLastRunAtUtc, "缺记账字段时必须回落到未建基线。");
    }
}
