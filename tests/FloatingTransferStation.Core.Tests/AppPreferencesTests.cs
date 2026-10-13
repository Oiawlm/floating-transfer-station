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

    [TestMethod]
    public void Default_HasNoDeepSeekKeyAndOmitsModelField()
    {
        Assert.IsNull(AppPreferences.Default.DeepSeekApiKeyProtected, "默认不得携带任何 API Key（密文或明文）。");
        // 模型选择机制已删除（契约 #28）：偏好不再承载模型，序列化不得出现 deepSeekModel。
        Assert.IsFalse(
            JsonSerializer.Serialize(AppPreferences.Default)
                .Contains("deepSeekModel", StringComparison.Ordinal));
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesDeepSeekKey()
    {
        var preferences = AppPreferences.Default with
        {
            DeepSeekApiKeyProtected = "AQAAANCMnd8BFdERjHoAwGiClCAABFAKE-CIPHER"
        };

        var restored = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));

        Assert.AreEqual(preferences, restored);
        Assert.AreEqual("AQAAANCMnd8BFdERjHoAwGiClCAABFAKE-CIPHER", restored!.DeepSeekApiKeyProtected);
    }

    [TestMethod]
    public void OlderJson_WithoutDeepSeekFields_FallsBackToUnconfigured()
    {
        var storeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"themeMode":"Dark","animationsEnabled":true}""",
            storeOptions);

        Assert.IsNotNull(restored);
        Assert.IsNull(restored.DeepSeekApiKeyProtected, "1.27.0 之前的 preferences.json 必须回落到未配置。");
    }

    [TestMethod]
    public async Task LegacyPreferencesFile_WithRetiredDeepSeekModelField_LoadsViaProductionStore()
    {
        // 模型选择机制删除（契约 #28）：老 preferences.json 里的 deepSeekModel 残留键必须被
        // 生产读取路径（LocalStore 的 JsonOptions，未映射成员跳过）忽略，不得让加载失败
        // 或回落到损坏兜底（与 autoCleanupLastRunAtUtc 退役同口径）。
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PreferencesFile)!);
        await File.WriteAllTextAsync(
            paths.PreferencesFile,
            """{"themeMode":"Dark","deepSeekModel":"deepseek-v4-pro"}""");
        using var store = new LocalStore(paths, new AtomicTextWriter());

        var loaded = await store.LoadPreferencesAsync();

        Assert.AreEqual(ThemePreference.Dark, loaded.ThemeMode, "其余偏好必须照常读取。");
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesSettingsWindowGeometry()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -700.5,
            SettingsWindowTop = 80,
            SettingsWindowWidth = 860,
            SettingsWindowHeight = 520
        };

        var restored = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));

        Assert.AreEqual(preferences, restored);
        Assert.AreEqual(-700.5, restored!.SettingsWindowLeft);
        Assert.AreEqual(80, restored.SettingsWindowTop);
        Assert.AreEqual(860, restored.SettingsWindowWidth);
        Assert.AreEqual(520, restored.SettingsWindowHeight);
    }

    [TestMethod]
    public void DefaultAndOlderJson_SettingWindowGeometryIsNull()
    {
        Assert.IsNull(AppPreferences.Default.SettingsWindowLeft);
        Assert.IsNull(AppPreferences.Default.SettingsWindowHeight);

        var storeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"themeMode":"Light"}""",
            storeOptions);

        Assert.IsNotNull(restored);
        Assert.IsNull(restored.SettingsWindowLeft, "旧 preferences.json 的几何字段必须回落 null（默认定位）。");
        Assert.IsNull(restored.SettingsWindowHeight);
    }

    [TestMethod]
    public void Default_EnablesAutoUpdateCheck()
    {
        Assert.IsTrue(AppPreferences.Default.AutoUpdateCheckEnabled);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesAutoUpdateCheckPreference()
    {
        var preferences = AppPreferences.Default with { AutoUpdateCheckEnabled = false };

        var restored = JsonSerializer.Deserialize<AppPreferences>(
            JsonSerializer.Serialize(preferences));

        Assert.AreEqual(preferences, restored);
        Assert.IsFalse(restored!.AutoUpdateCheckEnabled);
    }

    [TestMethod]
    public void OlderJson_WithoutAutoUpdateCheckField_FallsBackToEnabled()
    {
        var storeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"themeMode":"Light","autoCleanupEnabled":false}""",
            storeOptions);

        Assert.IsNotNull(restored);
        Assert.IsTrue(restored.AutoUpdateCheckEnabled, "旧 preferences.json 缺字段时必须回落到默认开启。");
        Assert.IsFalse(restored.AutoCleanupEnabled, "测试数据里的旧字段语义不受新字段影响。");
    }

    [TestMethod]
    public void Default_UsesSolidPinnedDivider()
    {
        Assert.AreEqual(PinnedDividerStyle.Solid, AppPreferences.Default.PinnedDivider);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesPinnedDividerPreference()
    {
        // LocalStore 以 camelCase + 字符串枚举持久化，四值都走同一份选项验证往返。
        var storeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        foreach (var style in new[]
                 {
                     PinnedDividerStyle.None,
                     PinnedDividerStyle.Solid,
                     PinnedDividerStyle.Dashed,
                     PinnedDividerStyle.AccentBar
                 })
        {
            var preferences = AppPreferences.Default with { PinnedDivider = style };

            var restored = JsonSerializer.Deserialize<AppPreferences>(
                JsonSerializer.Serialize(preferences, storeOptions), storeOptions);

            Assert.AreEqual(preferences, restored);
            Assert.AreEqual(style, restored!.PinnedDivider);
        }
    }

    [TestMethod]
    public void OlderJson_WithoutPinnedDividerField_FallsBackToSolid()
    {
        var storeOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        var restored = JsonSerializer.Deserialize<AppPreferences>(
            """{"themeMode":"Light","autoUpdateCheckEnabled":false}""",
            storeOptions);

        Assert.IsNotNull(restored);
        Assert.AreEqual(
            PinnedDividerStyle.Solid,
            restored.PinnedDivider,
            "旧 preferences.json 缺字段时必须回落到默认实线（升级后边界可见）。");
    }
}
