using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class PluginCatalogTests
{
    private static string ManifestJson(
        string id = "demo-tidy",
        string name = "演示整理",
        string version = "1.0.0",
        string kind = "text-cleaner",
        string? rules = null) =>
        $$"""
        {
          "id": "{{id}}",
          "name": "{{name}}",
          "version": "{{version}}",
          "kind": "{{kind}}"{{(rules is null ? string.Empty : $",\n  \"textRules\": {rules}")}}
        }
        """;

    private static string SimpleRuleJson(string pattern, string replacement = "") =>
        $$"""{ "pattern": "{{pattern}}", "replacement": "{{replacement}}" }""";

    private static void WriteManifest(string directory, string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginCatalog.ManifestFileName), json);
    }

    private static PluginCatalog CreateCatalog(TestDirectory directory, string? builtInRoot = null) =>
        new(
            AppPaths.FromDataDirectory(directory.Root),
            builtInRoot is null ? null : Path.Combine(builtInRoot, "plugins"),
            new AtomicTextWriter());

    [TestMethod]
    public void TryParse_AcceptsValidManifestWithRules()
    {
        var json = ManifestJson(rules: $"[{SimpleRuleJson("blank", "空白")}]");

        var parsed = PluginManifest.TryParse(json, out var manifest, out var error);

        Assert.IsTrue(parsed, error);
        Assert.IsNotNull(manifest);
        Assert.AreEqual("demo-tidy", manifest!.Id);
        Assert.AreEqual("演示整理", manifest.Name);
        Assert.AreEqual(PluginKind.TextCleaner, manifest.Kind);
        Assert.HasCount(1, manifest.TextRules);
        Assert.AreEqual("blank", manifest.TextRules[0].Pattern);
        Assert.AreEqual("空白", manifest.TextRules[0].Replacement);
    }

    [TestMethod]
    public void TryParse_RejectsUppercaseId()
    {
        var parsed = PluginManifest.TryParse(ManifestJson(id: "Bad-Id"), out _, out var error);

        Assert.IsFalse(parsed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void TryParse_RejectsMissingName()
    {
        var parsed = PluginManifest.TryParse(
            """{"id":"demo-tidy","version":"1.0.0","kind":"text-cleaner"}""",
            out _,
            out var error);

        Assert.IsFalse(parsed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void TryParse_RejectsUnsupportedKind()
    {
        var parsed = PluginManifest.TryParse(
            """{"id":"demo-tidy","name":"x","version":"1.0.0","kind":"theme"}""",
            out _,
            out var error);

        Assert.IsFalse(parsed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public void TryParse_RejectsInvalidRegexPattern()
    {
        var parsed = PluginManifest.TryParse(
            """{"id":"demo-tidy","name":"x","version":"1.0.0","kind":"text-cleaner","textRules":[{"pattern":"([unclosed"}]}""",
            out _,
            out var error);

        Assert.IsFalse(parsed);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    public async Task LoadAsync_ListsDiscoveredPluginsButKeepsThemDisabled()
    {
        using var directory = new TestDirectory();
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "demo-tidy"),
            ManifestJson());
        var catalog = CreateCatalog(directory);

        await catalog.LoadAsync();

        var entry = catalog.Entries.Single(entry => entry.Id == "demo-tidy");
        Assert.AreEqual(PluginOrigin.User, entry.Origin);
        Assert.IsFalse(entry.Enabled);
        Assert.AreEqual(0, catalog.TextCleaner.RuleCount);
    }

    [TestMethod]
    public async Task SetEnabledAsync_RebuildsPipelineAndPersistsAcrossReload()
    {
        using var directory = new TestDirectory();
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "demo-tidy"),
            ManifestJson(rules: $"[{SimpleRuleJson("world", "世界")}]"));
        var catalog = CreateCatalog(directory);
        await catalog.LoadAsync();

        await catalog.SetEnabledAsync("demo-tidy", true);

        Assert.AreEqual(1, catalog.TextCleaner.RuleCount);
        Assert.AreEqual("hello 世界", catalog.TextCleaner.Apply("hello world"));

        var reloaded = CreateCatalog(directory);
        await reloaded.LoadAsync();
        Assert.IsTrue(reloaded.Entries.Single(entry => entry.Id == "demo-tidy").Enabled);
        Assert.AreEqual(1, reloaded.TextCleaner.RuleCount);

        await catalog.SetEnabledAsync("demo-tidy", false);
        Assert.AreEqual(0, catalog.TextCleaner.RuleCount);
    }

    [TestMethod]
    public async Task UserPlugin_OverridesBuiltInPluginWithSameId()
    {
        using var directory = new TestDirectory();
        using var builtIn = new TestDirectory();
        WriteManifest(
            Path.Combine(builtIn.Root, "plugins", "demo-tidy"),
            ManifestJson(name: "内建版本"));
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "demo-tidy"),
            ManifestJson(name: "用户版本"));
        var catalog = CreateCatalog(directory, builtInRoot: builtIn.Root);

        await catalog.LoadAsync();

        var entry = catalog.Entries.Single(entry => entry.Id == "demo-tidy");
        Assert.AreEqual(PluginOrigin.User, entry.Origin);
        Assert.AreEqual("用户版本", entry.Manifest!.Name);
    }

    [TestMethod]
    public async Task InvalidManifest_IsListedWithErrorAndCannotBeEnabled()
    {
        using var directory = new TestDirectory();
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "broken-one"),
            """{"id":"broken-one"}""");
        var catalog = CreateCatalog(directory);
        await catalog.LoadAsync();

        var entry = catalog.Entries.Single(entry => entry.Id == "broken-one");
        Assert.IsNull(entry.Manifest);
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.LoadError));
        Assert.IsFalse(entry.Enabled);
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            () => catalog.SetEnabledAsync("broken-one", true));
        Assert.AreEqual(0, catalog.TextCleaner.RuleCount);
    }

    [TestMethod]
    public async Task MissingManifestFile_IsReportedAsLoadError()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Root, "plugins", "empty-shell"));
        var catalog = CreateCatalog(directory);

        await catalog.LoadAsync();

        var entry = catalog.Entries.Single();
        Assert.IsNull(entry.Manifest);
        Assert.IsTrue(entry.LoadError!.Contains(PluginCatalog.ManifestFileName, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LoadAsync_DropsOrphanedStateEntriesOnNextSave()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(directory.Root);
        Directory.CreateDirectory(directory.Root);
        await File.WriteAllTextAsync(
            paths.PluginStateFile,
            """{"enabled":{"ghost-plugin":true}}""");
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "demo-tidy"),
            ManifestJson());
        var catalog = CreateCatalog(directory);
        await catalog.LoadAsync();

        await catalog.SetEnabledAsync("demo-tidy", true);
        var saved = await File.ReadAllTextAsync(paths.PluginStateFile);

        Assert.IsFalse(saved.Contains("ghost-plugin", StringComparison.Ordinal));
        Assert.IsTrue(saved.Contains("demo-tidy", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Pipeline_FallsBackToOriginalTextWhenRulesClearContent()
    {
        using var directory = new TestDirectory();
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "eat-all"),
            ManifestJson(id: "eat-all", rules: $"[{SimpleRuleJson(".", "")}]"));
        var catalog = CreateCatalog(directory);
        await catalog.LoadAsync();

        await catalog.SetEnabledAsync("eat-all", true);

        Assert.AreEqual("keep me", catalog.TextCleaner.Apply("keep me"));
        Assert.AreEqual("  ", catalog.TextCleaner.Apply("  "));
    }

    [TestMethod]
    public async Task Pipeline_AppliesRulesAcrossPluginsInListOrder()
    {
        using var directory = new TestDirectory();
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "aaa-first"),
            ManifestJson(id: "aaa-first", name: "先", rules: $"[{SimpleRuleJson("one", "1")}]"));
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "zzz-second"),
            ManifestJson(id: "zzz-second", name: "后", rules: $"[{SimpleRuleJson("1", "一")}]"));
        var catalog = CreateCatalog(directory);
        await catalog.LoadAsync();

        await catalog.SetEnabledAsync("aaa-first", true);
        await catalog.SetEnabledAsync("zzz-second", true);

        Assert.AreEqual("一", catalog.TextCleaner.Apply("one"));
    }

    [TestMethod]
    public async Task Pipeline_MatchesAnchorsPerLineBecauseRulesCompileWithMultiline()
    {
        using var directory = new TestDirectory();
        // CRLF 文本里 .NET 的 $ 位于 \n 之前（\r 与 \n 之间），
        // 插件规则需用 lookahead 让空白紧贴行界；平台提供 Multiline 行锚定。
        WriteManifest(
            Path.Combine(directory.Root, "plugins", "trim-edges"),
            ManifestJson(id: "trim-edges", rules: $$"""[{ "pattern": "[ \t]+(?=\r?\n|$)", "replacement": "" }]"""));
        var catalog = CreateCatalog(directory);
        await catalog.LoadAsync();

        await catalog.SetEnabledAsync("trim-edges", true);

        Assert.AreEqual(
            "keep\r\nno trailing\r\ntail",
            catalog.TextCleaner.Apply("keep   \r\nno trailing\t\r\ntail"));
    }

    [TestMethod]
    public async Task CorruptStateFile_FallsBackToAllDisabled()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(directory.Root);
        Directory.CreateDirectory(directory.Root);
        await File.WriteAllTextAsync(paths.PluginStateFile, "{ not json");
        WriteManifest(Path.Combine(directory.Root, "plugins", "demo-tidy"), ManifestJson());
        var catalog = CreateCatalog(directory);

        await catalog.LoadAsync();

        Assert.IsFalse(catalog.Entries.Single().Enabled);
    }
}
