using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 搬迁延迟清理契约：标记原子写入/读取/删除；清理处理器只删形状属实的旧目录
///（受管形状只删数据根、旧直下形状只删 Data、旧嵌套形状连带空中间层、就位迁移
/// 让位包装空即移除）。
/// </summary>
[TestClass]
public sealed class RelocationCleanupTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(TestTempRoot.Root, "fts-cleanup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

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

    private (string Current, string Old) CreateDirectories()
    {
        var current = Path.Combine(_root, "new", "悬浮中转站");
        var old = Path.Combine(_root, "old", "悬浮中转站", "Data");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "board.json"), "{}");
        return (current, old);
    }

    [TestMethod]
    public void CleanupProcessor_ManagedShapeOldDirectoryKeepsUserParent()
    {
        // 受管形状旧数据根（<父>/悬浮中转站）延迟清理：只删数据根本身，
        // 用户所选父目录（myparent）绝不移除。
        var parent = Path.Combine(_root, "old-parent");
        var old = Path.Combine(parent, "悬浮中转站");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "board.json"), "{}");
        var current = Path.Combine(_root, "new", "悬浮中转站");
        Directory.CreateDirectory(current);
        Assert.IsTrue(RelocationCleanupMarker.TryWriteAsync(current, old, new AtomicTextWriter()).Result);

        var notice = DataDirectoryCleanupProcessor.Run(current);

        Assert.IsNull(notice);
        Assert.IsFalse(Directory.Exists(old), "旧数据根必须删除。");
        Assert.IsTrue(Directory.Exists(parent), "用户所选父目录必须保留。");
        Assert.IsFalse(RelocationCleanupMarker.TryRead(current, out _), "清理后标记必须删除。");
    }

    [TestMethod]
    public void CleanupProcessor_LegacyDirectOldDirectoryKeepsUserParent()
    {
        // 旧直下形状旧目录（<父>/Data）延迟清理：只删 Data，用户所选父目录绝不移除。
        var parent = Path.Combine(_root, "old-parent");
        var old = Path.Combine(parent, "Data");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "board.json"), "{}");
        var current = Path.Combine(_root, "new", "悬浮中转站");
        Directory.CreateDirectory(current);
        Assert.IsTrue(RelocationCleanupMarker.TryWriteAsync(current, old, new AtomicTextWriter()).Result);

        var notice = DataDirectoryCleanupProcessor.Run(current);

        Assert.IsNull(notice);
        Assert.IsFalse(Directory.Exists(old), "旧 Data 目录必须删除。");
        Assert.IsTrue(Directory.Exists(parent), "旧直下形状的用户父目录必须保留。");
        Assert.IsFalse(RelocationCleanupMarker.TryRead(current, out _), "清理后标记必须删除。");
    }

    [TestMethod]
    public async Task Marker_RoundTripsAndDeletes()
    {
        var (current, old) = CreateDirectories();

        Assert.IsFalse(RelocationCleanupMarker.TryRead(current, out _));
        Assert.IsTrue(await RelocationCleanupMarker.TryWriteAsync(current, old, new AtomicTextWriter()));
        Assert.IsTrue(RelocationCleanupMarker.TryRead(current, out var recorded));
        Assert.AreEqual(old, recorded);

        RelocationCleanupMarker.TryDelete(current);
        Assert.IsFalse(RelocationCleanupMarker.TryRead(current, out _));
    }

    [TestMethod]
    public void CleanupProcessor_DeletesShapeValidOldDirectoryAndClearsMarker()
    {
        var (current, old) = CreateDirectories();
        Assert.IsTrue(RelocationCleanupMarker.TryWriteAsync(current, old, new AtomicTextWriter()).Result);

        var notice = DataDirectoryCleanupProcessor.Run(current);

        Assert.IsNull(notice);
        Assert.IsFalse(Directory.Exists(old), "旧受管目录必须删除。");
        Assert.IsFalse(Directory.Exists(Directory.GetParent(old)!.FullName), "旧嵌套形状：空「悬浮中转站」中间层一并移除。");
        Assert.IsFalse(RelocationCleanupMarker.TryRead(current, out _), "清理后标记必须删除。");
    }

    [TestMethod]
    public void CleanupProcessor_RemovesEmptySupersededWrapperAfterHoist()
    {
        // 就位迁移（旧嵌套形状归一）：标记记录让位副本内的 Data；清理删除 Data 后
        // 空的 .fts-superseded-* 包装一并移除，用户所选父目录保留。
        var userParent = Path.Combine(_root, "store");
        var current = Path.Combine(userParent, "悬浮中转站");
        Directory.CreateDirectory(current);
        var wrapper = Path.Combine(userParent, ".fts-superseded-20261105000000-1");
        var old = Path.Combine(wrapper, "Data");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "board.json"), "{}");
        Assert.IsTrue(RelocationCleanupMarker.TryWriteAsync(current, old, new AtomicTextWriter()).Result);

        var notice = DataDirectoryCleanupProcessor.Run(current);

        Assert.IsNull(notice);
        Assert.IsFalse(Directory.Exists(old), "让位副本内的旧 Data 必须删除。");
        Assert.IsFalse(Directory.Exists(wrapper), "空的让位包装目录必须移除。");
        Assert.IsTrue(Directory.Exists(userParent), "用户所选父目录必须保留。");
    }

    [TestMethod]
    public void CleanupProcessor_KeepsOldDirectoryWhenMarkerInvalid()
    {
        var (current, old) = CreateDirectories();
        File.WriteAllText(
            Path.Combine(current, RelocationCleanupMarker.FileName),
            """{"oldDataDirectory":"not a managed path"}""");

        var notice = DataDirectoryCleanupProcessor.Run(current);

        StringAssert.Contains(notice, "未自动删除");
        Assert.IsTrue(File.Exists(Path.Combine(old, "board.json")), "形状不符的目录必须保留。");
        Assert.IsTrue(RelocationCleanupMarker.TryRead(current, out _), "校验不过时保留标记。");
    }

    [TestMethod]
    public void CleanupProcessor_NoMarkerDoesNothing()
    {
        var (current, _) = CreateDirectories();
        Assert.IsNull(DataDirectoryCleanupProcessor.Run(current));
    }
}
