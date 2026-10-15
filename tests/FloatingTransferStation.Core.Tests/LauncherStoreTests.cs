using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// launchers.json 存储契约（#30，Issue #162）：原子往返（信封 + 数组顺序即唯一真相）、
/// 损坏回落 .bak + .corrupt 快照、未知版本原样上抛（目录层只读停用）、未知 Kind 丢条、
/// 重复 Id 取首条、空目标丢条、会话态字段不序列化。
/// </summary>
[TestClass]
public sealed class LauncherStoreTests
{
    private static LocalStore CreateStore(TestDirectory directory, IAtomicTextWriter? writer = null) =>
        new(AppPaths.ForTests(directory.Root), writer ?? new AtomicTextWriter());

    private static LauncherItem Item(
        string target = @"C:\Apps\Tool.exe",
        string name = "Tool",
        LauncherKind kind = LauncherKind.Application,
        string? arguments = null) =>
        new(Guid.NewGuid(), kind, target, name, new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero), arguments);

    [TestMethod]
    public async Task RoundTrip_PreservesEnvelopeOrderAndFields()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var first = Item(@"C:\Apps\A.exe", "甲");
        var second = Item(@"C:\Apps\B.lnk", "乙", arguments: "--flag");
        var third = new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.PackagedApp,
            "App!Publisher",
            "打包应用",
            new DateTimeOffset(2026, 10, 14, 13, 0, 0, TimeSpan.Zero));

        await store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [first, second, third]));
        var loaded = await CreateStore(directory).LoadAsync();

        Assert.AreEqual(LauncherStoreData.CurrentVersion, loaded.Version);
        var ids = loaded.Items.Select(item => item.Id).ToArray();
        CollectionAssert.AreEqual(new[] { first.Id, second.Id, third.Id }, ids, "数组顺序即唯一真相。");
        Assert.AreEqual(@"C:\Apps\B.lnk", loaded.Items[1].TargetPath);
        Assert.AreEqual("--flag", loaded.Items[1].Arguments);
        Assert.AreEqual(LauncherKind.PackagedApp, loaded.Items[2].Kind);
        Assert.AreEqual("打包应用", loaded.Items[2].DisplayName);
    }

    [TestMethod]
    public async Task CorruptFile_FallsBackToBakAndPreservesCorruptCopy()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = CreateStore(directory);
        var saved = Item(@"C:\Apps\Kept.exe", "保留");
        // 两次保存：原子写只在替换既有文件时产生 .bak（与 board.json 同管线）。
        await store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [saved]));
        await store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [saved]));
        await File.WriteAllTextAsync(paths.LaunchersFile, "{ not valid json");

        var loaded = await CreateStore(directory).LoadAsync();

        Assert.AreEqual(saved.Id, loaded.Items.Single().Id, "主文件损坏回落 .bak。");
        Assert.IsTrue(
            Directory.EnumerateFiles(paths.DataDirectory, "launchers.json.corrupt-*.bak").Any(),
            "损坏副本按 .corrupt-* 快照保留。");
    }

    [TestMethod]
    public async Task MissingFile_ReturnsEmptyEnvelope()
    {
        using var directory = new TestDirectory();
        var loaded = await CreateStore(directory).LoadAsync();

        Assert.AreEqual(LauncherStoreData.CurrentVersion, loaded.Version);
        Assert.AreEqual(0, loaded.Items.Count);
    }

    [TestMethod]
    public async Task NewerVersion_ReturnsDataAsIsForReadOnlyGate()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.LaunchersFile, """
            {
              "version": 2,
              "items": [
                {
                  "id": "0f2b7d1e111122223333444455556666",
                  "kind": "Application",
                  "targetPath": "C:\\Apps\\Future.exe",
                  "displayName": "未来版本条目",
                  "createdAt": "2026-10-14T08:00:00+00:00"
                }
              ]
            }
            """);

        var loaded = await CreateStore(directory).LoadAsync();

        Assert.AreEqual(2, loaded.Version, "未知更高版本原样上抛（目录层只读停用，不在此丢数据）。");
        Assert.AreEqual(1, loaded.Items.Count);
    }

    [TestMethod]
    public async Task UnknownKind_DropsItemKeepsOthers()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.LaunchersFile, """
            {
              "version": 1,
              "items": [
                {
                  "id": "0f2b7d1e111122223333444455556666",
                  "kind": "SomethingElse",
                  "targetPath": "C:\\Apps\\Weird.exe",
                  "displayName": "未知类型",
                  "createdAt": "2026-10-14T08:00:00+00:00"
                },
                {
                  "id": "0f2b7d1e111122223333444455556667",
                  "kind": "Application",
                  "targetPath": "C:\\Apps\\Kept.exe",
                  "displayName": "保留",
                  "createdAt": "2026-10-14T08:00:00+00:00"
                }
              ]
            }
            """);

        var loaded = await CreateStore(directory).LoadAsync();

        Assert.AreEqual(1, loaded.Items.Count, "未知 Kind 数值/字符串丢条。");
        Assert.AreEqual("保留", loaded.Items.Single().DisplayName);
    }

    [TestMethod]
    public async Task DuplicateId_KeepsFirstItem()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.LaunchersFile, """
            {
              "version": 1,
              "items": [
                {
                  "id": "0f2b7d1e111122223333444455556666",
                  "kind": "Application",
                  "targetPath": "C:\\Apps\\First.exe",
                  "displayName": "第一条",
                  "createdAt": "2026-10-14T08:00:00+00:00"
                },
                {
                  "id": "0f2b7d1e111122223333444455556666",
                  "kind": "Application",
                  "targetPath": "C:\\Apps\\Second.exe",
                  "displayName": "第二条",
                  "createdAt": "2026-10-14T08:00:00+00:00"
                }
              ]
            }
            """);

        var loaded = await CreateStore(directory).LoadAsync();

        Assert.AreEqual(1, loaded.Items.Count, "重复 Id 取首条。");
        Assert.AreEqual("第一条", loaded.Items.Single().DisplayName);
    }

    [TestMethod]
    public async Task BlankTarget_DropsItem()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.LaunchersFile, """
            {
              "version": 1,
              "items": [
                {
                  "id": "0f2b7d1e111122223333444455556666",
                  "kind": "Application",
                  "targetPath": "   ",
                  "displayName": "空目标",
                  "createdAt": "2026-10-14T08:00:00+00:00"
                }
              ]
            }
            """);

        var loaded = await CreateStore(directory).LoadAsync();

        Assert.AreEqual(0, loaded.Items.Count, "目标空/无效丢条。");
    }

    [TestMethod]
    public async Task SessionFields_AreNeverSerialized()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = CreateStore(directory);
        var item = Item(@"C:\Apps\Session.exe", "会话");
        item.DraftName = "草稿";
        item.IsEditingName = true;
        item.LaunchFailed = true;

        await store.SaveAsync(new LauncherStoreData(LauncherStoreData.CurrentVersion, [item]));
        var json = await File.ReadAllTextAsync(paths.LaunchersFile);
        var loaded = await CreateStore(directory).LoadAsync();

        Assert.IsFalse(json.Contains("draftName") || json.Contains("isEditingName") || json.Contains("launchFailed"),
            "会话态字段不序列化。");
        Assert.AreEqual("会话", loaded.Items.Single().DisplayName);
        Assert.IsFalse(loaded.Items.Single().LaunchFailed);
    }

    [TestMethod]
    public async Task IdSerializesAsGuidNFormat()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);

        await store.SaveAsync(new LauncherStoreData(
            LauncherStoreData.CurrentVersion,
            [Item(@"C:\Apps\N.exe", "N")]));

        var json = await File.ReadAllTextAsync(AppPaths.ForTests(directory.Root).LaunchersFile);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var id = document.RootElement.GetProperty("items")[0].GetProperty("id").GetString();
        Assert.IsNotNull(id);
        Assert.IsFalse(id.Contains('-'), "Guid 序列化为 N 格式（32 位十六进制无连字符）。");
        Assert.AreEqual(32, id.Length);
    }
}
