using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 统一删除路径的永久模式（2026-11-03 需求 1，契约 #19/#11 家族）：永久删除
/// 不进撤销栈（Ctrl+Z 不可恢复）、图片文件保存成功后立即清理（复用撤销批
/// 驱逐的同一段清理实现）、失败整批回滚与顺序保持。可撤销模式（默认）语义
/// 零变化，由 BoardMutationServiceUndoTests 既有用例锁定。
/// </summary>
[TestClass]
public sealed class BoardMutationServiceDeleteModeTests
{
    [TestMethod]
    public async Task PermanentDelete_DoesNotEnterUndoStack()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var item = board.AddText("永久删除不进栈");
        board.AddText("保留");
        var store = new DeleteModeStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsTrue(await service.DeleteManyAsync([item.Id], BoardDeleteMode.Permanent));
        Assert.AreEqual(0, service.PendingUndoCount, "永久删除不得进入撤销栈。");
        Assert.IsNull(board.FindItem(item.Id));

        var undone = await service.UndoLastAsync();
        Assert.IsFalse(undone, "撤销栈为空（永久删除不记批）。");
        Assert.IsNull(board.FindItem(item.Id), "Ctrl+Z 不得恢复永久删除的卡。");
        CollectionAssert.Contains(messages, "没有可撤销的操作。");
    }

    [TestMethod]
    public async Task PermanentDelete_CleansImageFileImmediately()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var path = Path.Combine(directory.Root, "images", "permanent.png");
        WritePng(path, 3, 3);
        var image = board.AddImage(Guid.NewGuid(), "images/permanent.png", path, BoardCategory.Inbox);
        var store = new DeleteModeStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([image.Id], BoardDeleteMode.Permanent));

        Assert.IsFalse(File.Exists(path), "永久删除的图片文件必须立即清理（不进撤销栈则无保留必要）。");
        Assert.AreEqual(1, store.DeleteCount);
    }

    [TestMethod]
    public async Task PermanentDelete_SaveFailureRestoresItemsAndKeepsImage()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var text = board.AddText("失败回滚的正文");
        var path = Path.Combine(directory.Root, "images", "rollback.png");
        WritePng(path, 3, 3);
        var image = board.AddImage(Guid.NewGuid(), "images/rollback.png", path, BoardCategory.Inbox);
        var store = new DeleteModeStore(directory.Root) { FailSave = true };
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsFalse(await service.DeleteManyAsync([text.Id, image.Id], BoardDeleteMode.Permanent));
        Assert.IsNotNull(board.FindItem(text.Id), "保存失败必须整批回滚正文条目。");
        Assert.IsNotNull(board.FindItem(image.Id), "保存失败必须整批回滚图片条目。");
        Assert.IsTrue(File.Exists(path), "保存失败不得清理图片文件。");
        Assert.AreEqual(0, store.DeleteCount);
        CollectionAssert.Contains(messages, "删除未保存，内容已恢复。");
    }

    [TestMethod]
    public async Task PermanentDelete_KeepsRemainingOrderAndPinnedPartition()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var pinned = board.AddText("置顶保留", BoardCategory.Inbox);
        var first = board.AddText("普通一", BoardCategory.Inbox);
        var middle = board.AddText("普通二", BoardCategory.Inbox);
        var last = board.AddText("普通三", BoardCategory.Inbox);
        board.SetPinnedMany([pinned.Id], true);
        var store = new DeleteModeStore(directory.Root);
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.DeleteManyAsync([middle.Id], BoardDeleteMode.Permanent));

        // 板序＝置顶区在前、普通区最新在前（AddText 置顶插入）：删除前为
        // [pinned, last, middle, first]，永久删除 middle 后剩余原样保持。
        CollectionAssert.AreEqual(
            new[] { pinned, last, first }.Select(item => item.Id).ToArray(),
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "永久删除后置顶分区与剩余顺序必须原样保持。");
    }

    [TestMethod]
    public async Task PermanentDelete_MixedWithUndoableDeleteKeepsLifoIntact()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var undoable = board.AddText("可撤销删除", BoardCategory.Inbox);
        var permanent = board.AddText("永久删除", BoardCategory.Inbox);
        board.AddText("保留");
        var store = new DeleteModeStore(directory.Root);
        var messages = new List<string>();
        var service = new BoardMutationService(board, store, messages.Add);

        Assert.IsTrue(await service.DeleteManyAsync([undoable.Id]));
        Assert.IsTrue(await service.DeleteManyAsync([permanent.Id], BoardDeleteMode.Permanent));
        Assert.AreEqual(1, service.PendingUndoCount, "栈内只有可撤销批。");

        Assert.IsTrue(await service.UndoLastAsync());

        Assert.IsNotNull(board.FindItem(undoable.Id), "撤销恢复的是可撤销批。");
        Assert.IsNull(board.FindItem(permanent.Id), "永久删除不得随邻批撤销恢复。");
        CollectionAssert.Contains(messages, "已恢复最近删除的内容。");
    }

    private static void WritePng(string path, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x5A);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private sealed class DeleteModeStore(string root) : IBoardStore
    {
        public bool FailSave { get; set; }

        public int DeleteCount { get; private set; }

        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (FailSave)
            {
                throw new IOException("Injected save failure.");
            }

            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath)
        {
            DeleteCount++;
            if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }

            return true;
        }
    }
}
