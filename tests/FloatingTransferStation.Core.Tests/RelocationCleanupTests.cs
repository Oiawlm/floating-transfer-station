using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>搬迁延迟清理契约：标记原子写入/读取/删除；清理处理器只删形状属实的旧目录。</summary>
[TestClass]
public sealed class RelocationCleanupTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "fts-cleanup-tests-" + Guid.NewGuid().ToString("N"));
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
        var current = Path.Combine(_root, "new", "悬浮中转站", "Data");
        var old = Path.Combine(_root, "old", "悬浮中转站", "Data");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "board.json"), "{}");
        return (current, old);
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
        Assert.IsFalse(Directory.Exists(Directory.GetParent(old)!.FullName), "空受管父一并移除。");
        Assert.IsFalse(RelocationCleanupMarker.TryRead(current, out _), "清理后标记必须删除。");
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
