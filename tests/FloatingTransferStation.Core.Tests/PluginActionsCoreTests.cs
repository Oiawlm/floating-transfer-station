using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 插件 L1 文本动作协议（1.51.0，契约 #31）：清单校验正反例、双 kind 共存、
/// 两种替换语义（动作 .NET 捕获 vs cleaner 字面）、when 门控随设置重建、
/// 空结果跳过、预算/超时/输出超限整批失败无部分提交、设置持久化往返与
/// 旧格式兼容、孤儿剔除、非法动作可见不可启用、重新扫描。
/// </summary>
[TestClass]
public sealed class PluginActionsCoreTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "fts-actions-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DataDirectory => Path.Combine(_root, "Data");

    private string UserPluginsDirectory => Path.Combine(DataDirectory, "plugins");

    private string BuiltInPluginsDirectory => Path.Combine(_root, "BuiltIn", "plugins");

    private PluginCatalog CreateCatalog()
    {
        var catalog = new PluginCatalog(
            AppPaths.ForTests(DataDirectory),
            BuiltInPluginsDirectory,
            new AtomicTextWriter());
        return catalog;
    }

    private static void WritePlugin(string pluginRoot, string json)
    {
        Directory.CreateDirectory(pluginRoot);
        File.WriteAllText(Path.Combine(pluginRoot, PluginCatalog.ManifestFileName), json);
    }

    private static string ActionJson(
        string rules,
        string id = "tidy-card",
        string title = "整理这张卡片",
        string selection = "single",
        string settings = "null") =>
        $$"""
        {
          "id": "{{id}}",
          "title": "{{title}}",
          "accept": "text",
          "selection": "{{selection}}",
          "output": "replace",
          "settings": {{settings}},
          "rules": {{rules}}
        }
        """;

    private static string RuleJson(string pattern, string replacement, string whenClause = "") =>
        $$"""{ "pattern": "{{pattern}}", "replacement": "{{replacement}}"{{whenClause}} }""";

    private static string TextActionsManifestJson(string actionsJson, string id = "typography-tidy") =>
        $$"""
        {
          "id": "{{id}}",
          "name": "中文排版整理",
          "version": "1.0.0",
          "description": "测试动作插件",
          "kind": "text-actions",
          "actions": {{actionsJson}}
        }
        """;

    // ---- 清单校验 ----

    [TestMethod]
    public void TryParse_TextActions_AcceptsValidManifestWithSettingsAndWhen()
    {
        var manifestJson = TextActionsManifestJson($$"""
        [{
          "id": "tidy-card",
          "title": "整理这张卡片",
          "accept": "text",
          "selection": "multi",
          "output": "replace",
          "settings": [
            { "key": "punct-to-fullwidth", "label": "半角标点转全角", "type": "bool", "default": false },
            { "key": "style", "label": "风格", "type": "choice", "options": ["紧凑", "宽松"], "default": "紧凑" },
            { "key": "note", "label": "备注", "type": "text", "default": "" }
          ],
          "rules": [
            { "pattern": ",", "replacement": "，", "when": "punct-to-fullwidth" },
            { "pattern": " {2,}", "replacement": " " }
          ]
        }]
        """);

        var parsed = PluginManifest.TryParse(manifestJson, out var manifest, out var error);

        Assert.IsTrue(parsed, error);
        Assert.AreEqual(PluginKind.TextActions, manifest!.Kind);
        var action = manifest.Actions!.Single();
        Assert.AreEqual("tidy-card", action.Id);
        Assert.AreEqual(PluginActionSelection.Multi, action.Selection);
        Assert.HasCount(3, action.Settings);
        Assert.HasCount(2, action.Rules);
        Assert.AreEqual("punct-to-fullwidth", action.Rules[0].When);
        Assert.IsNull(action.Rules[1].When);
    }

    [TestMethod]
    public void TryParse_TextActions_RejectsReservedNewCardsOutput()
    {
        var manifestJson = TextActionsManifestJson($"[{ActionJson(rules: $"[{RuleJson("a", "b")}]", settings: "null").Replace("\"output\": \"replace\"", "\"output\": \"new-cards\"")}]");

        var parsed = PluginManifest.TryParse(manifestJson, out _, out var error);

        Assert.IsFalse(parsed);
        Assert.IsTrue(error.Contains("保留字"), error);
    }

    [TestMethod]
    public void TryParse_TextActions_RejectsDuplicateActionId()
    {
        var rule = RuleJson("a", "b");
        var manifestJson = TextActionsManifestJson($"[{ActionJson(rules: $"[{rule}]")}, {ActionJson(id: "tidy-card", rules: $"[{rule}]")}]");

        var parsed = PluginManifest.TryParse(manifestJson, out _, out var error);

        Assert.IsFalse(parsed);
        Assert.IsTrue(error.Contains("重复"), error);
    }

    [TestMethod]
    public void TryParse_TextActions_RejectsWhenReferencingUndeclaredSetting()
    {
        var manifestJson = TextActionsManifestJson($"[{ActionJson(rules: $"[{RuleJson("a", "b", whenClause: ", \"when\": \"missing\"")}]")}]");

        var parsed = PluginManifest.TryParse(manifestJson, out _, out var error);

        Assert.IsFalse(parsed);
        Assert.IsTrue(error.Contains("未声明"), error);
    }

    [TestMethod]
    public void TryParse_TextActions_RejectsChoiceDefaultOutsideOptions()
    {
        var manifestJson = TextActionsManifestJson($"[{ActionJson(rules: $"[{RuleJson("a", "b")}]", settings: """[{ "key": "style", "label": "风格", "type": "choice", "options": ["紧凑", "宽松"], "default": "居中" }]""")}]");

        var parsed = PluginManifest.TryParse(manifestJson, out _, out var error);

        Assert.IsFalse(parsed);
        Assert.IsTrue(error.Contains("default"), error);
    }

    [TestMethod]
    public void TryParse_TextActions_RejectsTooFewChoiceOptions()
    {
        var manifestJson = TextActionsManifestJson($"[{ActionJson(rules: $"[{RuleJson("a", "b")}]", settings: """[{ "key": "style", "label": "风格", "type": "choice", "options": ["紧凑"], "default": "紧凑" }]""")}]");

        var parsed = PluginManifest.TryParse(manifestJson, out _, out var error);

        Assert.IsFalse(parsed);
        Assert.IsTrue(error.Contains("2–8"), error);
    }

    [TestMethod]
    public void TryParse_TextActions_RejectsInvalidSettingKey()
    {
        var manifestJson = TextActionsManifestJson($"[{ActionJson(rules: $"[{RuleJson("a", "b")}]", settings: """[{ "key": "Bad_Key", "label": "风格", "type": "text" }]""")}]");

        var parsed = PluginManifest.TryParse(manifestJson, out _, out var error);

        Assert.IsFalse(parsed);
        Assert.IsTrue(error.Contains("key"), error);
    }

    [TestMethod]
    public void TryParse_TextCleaner_RejectsWhenOnCleanerRule()
    {
        var manifestJson = """
        {
          "id": "demo-tidy",
          "name": "演示",
          "version": "1.0.0",
          "kind": "text-cleaner",
          "textRules": [{ "pattern": "a", "replacement": "b", "when": "flag" }]
        }
        """;

        var parsed = PluginManifest.TryParse(manifestJson, out _, out var error);

        Assert.IsFalse(parsed);
        Assert.IsTrue(error.Contains("when"), error);
    }

    // ---- 双 kind 共存与管线 ----

    private const string CleanerManifest = """
        {
          "id": "demo-tidy",
          "name": "演示整理",
          "version": "1.0.0",
          "kind": "text-cleaner",
          "textRules": [{ "pattern": "坏字", "replacement": "$1好字" }]
        }
        """;

    private static string ActionPluginJson(string selection = "single", string whenClause = "") => TextActionsManifestJson(
        $$"""
        [{
          "id": "tidy-card",
          "title": "整理这张卡片",
          "accept": "text",
          "selection": "{{selection}}",
          "output": "replace",
          "settings": [{ "key": "flag", "label": "开关", "type": "bool", "default": true }],
          "rules": [
            { "pattern": "([A-Za-z])([0-9])", "replacement": "$2$1"{{whenClause}} }
          ]
        }]
        """,
        id: "typography-tidy");

    [TestMethod]
    public async Task Catalog_DualKindsCoexistAndEnabledActionsOnlyExposeTextActions()
    {
        WritePlugin(Path.Combine(BuiltInPluginsDirectory, "demo-tidy"), CleanerManifest);
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), ActionPluginJson());
        var catalog = CreateCatalog();
        await catalog.LoadAsync();

        Assert.HasCount(2, catalog.Entries);
        catalog.SetEnabledAsync("demo-tidy", true).Wait();
        catalog.SetEnabledAsync("typography-tidy", true).Wait();

        // text-cleaner 走自动管线、text-actions 走动作快照，互不串扰。
        Assert.AreEqual("$1好字", catalog.TextCleaner.Apply("坏字"));
        var actions = catalog.EnabledActions;
        Assert.HasCount(1, actions);
        Assert.AreEqual("typography-tidy", actions[0].PluginId);
        Assert.AreEqual(PluginKind.TextActions, actions[0].Entry.Manifest!.Kind);
    }

    [TestMethod]
    public void Pipeline_ActionReplacementUsesNetCaptures()
    {
        var catalog = CreateCatalog();
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), ActionPluginJson());
        catalog.LoadAsync().Wait();
        catalog.SetEnabledAsync("typography-tidy", true).Wait();

        var handle = catalog.EnabledActions.Single();
        var result = handle.Pipeline.Apply([new PluginActionCardInput(Guid.NewGuid(), "a1 b2")]);

        Assert.IsTrue(result.IsSuccess, result.FailureReason);
        Assert.AreEqual("1a 2b", result.Cards![0].NewText);
        Assert.AreEqual(2, result.Cards[0].RuleHits);
    }

    [TestMethod]
    public async Task Catalog_CleanerReplacementIsLiteral()
    {
        WritePlugin(Path.Combine(UserPluginsDirectory, "demo-tidy"), CleanerManifest);
        var catalog = CreateCatalog();
        await catalog.LoadAsync();
        await catalog.SetEnabledAsync("demo-tidy", true);

        // $1 按字面写回（.NET 捕获语义只在动作管线生效）。
        Assert.AreEqual("$1好字", catalog.TextCleaner.Apply("坏字"));
    }

    [TestMethod]
    public async Task Pipeline_WhenGatingFollowsSettingValues()
    {
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), ActionPluginJson(whenClause: ", \"when\": \"flag\""));
        var catalog = CreateCatalog();
        await catalog.LoadAsync();
        await catalog.SetEnabledAsync("typography-tidy", true);

        // 默认 true：规则生效。
        Assert.AreEqual("1a", catalog.EnabledActions.Single().Pipeline.Apply([new PluginActionCardInput(Guid.NewGuid(), "a1")]).Cards![0].NewText);

        // 关闭：规则被门控排除，动作不再可用（全部规则关闭 → 管线为空）。
        await catalog.SetActionSettingValueAsync("typography-tidy", "tidy-card", "flag", "false");
        Assert.IsEmpty(catalog.EnabledActions);
        var rebuilt = catalog.TryBuildActionPipeline("typography-tidy", "tidy-card");
        Assert.IsNull(rebuilt);

        // 重新打开：恢复可用。
        await catalog.SetActionSettingValueAsync("typography-tidy", "tidy-card", "flag", "true");
        Assert.HasCount(1, catalog.EnabledActions);
    }

    [TestMethod]
    public void Pipeline_SkipsEmptyResultsAndCountsThem()
    {
        var catalog = CreateCatalog();
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), TextActionsManifestJson(
            $"[{ActionJson(rules: $"[{RuleJson("x", string.Empty)}]")}]"));
        catalog.LoadAsync().Wait();
        catalog.SetEnabledAsync("typography-tidy", true).Wait();

        var keep = Guid.NewGuid();
        var result = catalog.EnabledActions.Single().Pipeline.Apply([
            new PluginActionCardInput(keep, "内容"),
            new PluginActionCardInput(Guid.NewGuid(), "xxx")
        ]);

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(1, result.Cards!);
        Assert.AreEqual(keep, result.Cards[0].ItemId);
        Assert.AreEqual(1, result.SkippedEmptyCount);
    }

    [TestMethod]
    public void Pipeline_InputBudgetFailureAbortsWholeBatch()
    {
        var catalog = CreateCatalog();
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), ActionPluginJson());
        catalog.LoadAsync().Wait();
        catalog.SetEnabledAsync("typography-tidy", true).Wait();

        var result = catalog.EnabledActions.Single().Pipeline.Apply([
            new PluginActionCardInput(Guid.NewGuid(), new string('a', PluginActionLimits.TotalInputCharacters + 1))
        ]);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsNull(result.Cards);
        Assert.IsTrue(result.FailureReason!.Contains("上限"), result.FailureReason);
    }

    [TestMethod]
    public void Pipeline_OutputBudgetFailureAbortsWholeBatch()
    {
        var catalog = CreateCatalog();
        // 复制扩张规则：a → aa（每轮翻倍），把 2000 字符复制到 64k（> 单卡输出上限 50k）。
        var rules = string.Join(",", Enumerable.Repeat(RuleJson("a", "aa"), 5));
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"),
            TextActionsManifestJson($"[{ActionJson(rules: $"[{rules}]")}]"));
        catalog.LoadAsync().Wait();
        catalog.SetEnabledAsync("typography-tidy", true).Wait();

        var result = catalog.EnabledActions.Single().Pipeline.Apply(
            [new PluginActionCardInput(Guid.NewGuid(), new string('a', 2000))]);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue(result.FailureReason!.Contains("输出上限"), result.FailureReason);
    }

    [TestMethod]
    public void Pipeline_RuleTimeoutFailsWholeBatchWithReadableReason()
    {
        var catalog = CreateCatalog();
        // 灾难性回溯：(a+)+$ 对尾部 b 的匹配远超单规则 100ms 超时。
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"),
            TextActionsManifestJson($"[{ActionJson(rules: $"[{RuleJson("(a+)+$", "x")}]")}]"));
        catalog.LoadAsync().Wait();
        catalog.SetEnabledAsync("typography-tidy", true).Wait();

        var result = catalog.EnabledActions.Single().Pipeline.Apply(
            [new PluginActionCardInput(Guid.NewGuid(), new string('a', 64) + "b")]);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue(result.FailureReason!.Contains("超时"), result.FailureReason);
    }

    // ---- 状态持久化 ----

    [TestMethod]
    public async Task Catalog_ActionSettingsPersistAcrossReload()
    {
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), ActionPluginJson(whenClause: ", \"when\": \"flag\""));
        var catalog = CreateCatalog();
        await catalog.LoadAsync();
        await catalog.SetEnabledAsync("typography-tidy", true);
        await catalog.SetActionSettingValueAsync("typography-tidy", "tidy-card", "flag", "false");

        var reloaded = CreateCatalog();
        await reloaded.LoadAsync();
        Assert.IsEmpty(reloaded.EnabledActions);
        Assert.AreEqual("false", reloaded.GetActionSettingOverrides("typography-tidy", "tidy-card")["flag"]);
    }

    [TestMethod]
    public async Task Catalog_LegacyStateFileWithoutActionSettings_LoadsWithDefaults()
    {
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), ActionPluginJson());
        Directory.CreateDirectory(DataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(DataDirectory, "plugins-state.json"),
            """{ "enabled": ["typography-tidy"] }""");
        var catalog = CreateCatalog();
        await catalog.LoadAsync();

        // 旧文件兼容：启用状态生效，设置回落清单默认（true → 规则生效可用）。
        Assert.HasCount(1, catalog.EnabledActions);
        Assert.AreEqual("true", catalog.EnabledActions[0].Action.ResolveSettingValue(
            catalog.GetActionSettingOverrides("typography-tidy", "tidy-card"), "flag"));
    }

    [TestMethod]
    public async Task Catalog_OrphanSettingValuesPrunedOnSave()
    {
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), ActionPluginJson());
        Directory.CreateDirectory(DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(DataDirectory, "plugins-state.json"), """
        {
          "enabled": ["typography-tidy"],
          "actionSettings": { "typography-tidy": { "tidy-card": { "ghost": "1", "flag": "false" } } }
        }
        """);
        var catalog = CreateCatalog();
        await catalog.LoadAsync();

        // 保存任意状态后孤儿 key（ghost）与未声明值被剔除，声明值保留。
        await catalog.SetEnabledAsync("typography-tidy", true);
        var persisted = await File.ReadAllTextAsync(Path.Combine(DataDirectory, "plugins-state.json"));
        Assert.IsFalse(persisted.Contains("ghost"), persisted);
        Assert.IsTrue(persisted.Contains("\"flag\""), persisted);
    }

    [TestMethod]
    public async Task Catalog_InvalidActionManifest_VisibleButNotEnableable()
    {
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"),
            TextActionsManifestJson($"[{ActionJson(rules: "[]")}]"));
        var catalog = CreateCatalog();
        await catalog.LoadAsync();

        var entry = catalog.Entries.Single();
        Assert.IsNull(entry.Manifest);
        Assert.IsNotNull(entry.LoadError);
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => catalog.SetEnabledAsync("typography-tidy", true));
    }

    [TestMethod]
    public async Task Catalog_Rescan_ReloadsEntriesAndRebuildsPipelines()
    {
        WritePlugin(Path.Combine(UserPluginsDirectory, "typography-tidy"), ActionPluginJson());
        var catalog = CreateCatalog();
        await catalog.LoadAsync();
        await catalog.SetEnabledAsync("typography-tidy", true);
        Assert.HasCount(1, catalog.EnabledActions);

        // 移除插件文件夹后重扫：可用动作清空，条目消失。
        Directory.Delete(Path.Combine(UserPluginsDirectory, "typography-tidy"), recursive: true);
        await catalog.RescanAsync();
        Assert.IsEmpty(catalog.Entries);
        Assert.IsEmpty(catalog.EnabledActions);
    }
}
