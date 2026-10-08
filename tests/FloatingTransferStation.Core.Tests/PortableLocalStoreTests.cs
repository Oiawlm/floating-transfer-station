using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class PortableLocalStoreTests
{
    [TestMethod]
    public async Task SaveAndLoad_PreservesContentOrderPinnedStateAndCategoryNames()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(directory.Root);
        Directory.CreateDirectory(paths.ImagesDirectory);
        var imagePath = Path.Combine(paths.ImagesDirectory, "sample.png");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        var board = new BoardService();
        var image = board.AddImage(Guid.NewGuid(), "images/sample.png", imagePath, BoardCategory.Reference);
        var text = board.AddText("跨平台内容", BoardCategory.Reference);
        board.SetPinnedMany([image.Id], true);
        var settings = WindowSettings.Default.WithCategoryName(BoardCategory.Reference, "参考资料");
        var store = new LocalStore(paths, new AtomicTextWriter());

        await store.SaveBoardAsync(board.CreateSnapshot());
        await store.SaveSettingsAsync(settings);
        var restored = new BoardService();
        restored.Restore(await new LocalStore(paths, new AtomicTextWriter()).LoadBoardAsync());

        CollectionAssert.AreEqual(new[] { image.Id, text.Id }, restored.Items(BoardCategory.Reference).Select(item => item.Id).ToArray());
        Assert.IsTrue(restored.Items(BoardCategory.Reference)[0].IsPinned);
        Assert.AreEqual(imagePath, restored.Items(BoardCategory.Reference)[0].ImageAbsolutePath);
        Assert.AreEqual("参考资料", (await store.LoadSettingsAsync()).CategoryName(BoardCategory.Reference));
    }

    [TestMethod]
    public async Task AtomicSave_RecoversPreviousVersionWhenCurrentJsonIsCorrupt()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        await store.SaveBoardAsync(TextSnapshot("previous"));
        await store.SaveBoardAsync(TextSnapshot("current"));
        await File.WriteAllTextAsync(paths.BoardFile, "{ corrupt json");

        var recovered = await store.LoadBoardAsync();

        Assert.AreEqual("previous", recovered.Items.Single().Text);
        Assert.HasCount(1, Directory.GetFiles(paths.DataDirectory, "board.json.corrupt-*.bak"));
        Assert.IsFalse(File.Exists(paths.BoardFile + ".tmp"));
    }

    [TestMethod]
    [DataRow("images/sample.png")]
    [DataRow("images\\sample.png")]
    public async Task LoadBoard_AcceptsManagedImagePathsFromBothPlatforms(string relativePath)
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(directory.Root);
        Directory.CreateDirectory(paths.ImagesDirectory);
        var imagePath = Path.Combine(paths.ImagesDirectory, "sample.png");
        await File.WriteAllBytesAsync(imagePath, [1]);
        var store = new LocalStore(paths, new AtomicTextWriter());
        await store.SaveBoardAsync(new BoardSnapshot
        {
            Items = [BoardItem.CreateImage(Guid.NewGuid(), relativePath, imagePath, DateTimeOffset.UtcNow)]
        });

        var loaded = await store.LoadBoardAsync();

        Assert.AreEqual(imagePath, loaded.Items.Single().ImageAbsolutePath);
    }

    [TestMethod]
    [DataRow("images/../../outside.png")]
    [DataRow("images\\..\\..\\outside.png")]
    [DataRow("../outside.png")]
    [DataRow("..\\outside.png")]
    public async Task LoadBoard_RejectsTraversalWithoutLosingValidContent(string relativePath)
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(Path.Combine(directory.Root, "data"));
        var externalImage = Path.Combine(directory.Root, "outside.png");
        await File.WriteAllBytesAsync(externalImage, [1]);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var snapshot = TextSnapshot("keep");
        snapshot.Items.Add(BoardItem.CreateImage(Guid.NewGuid(), relativePath, externalImage, DateTimeOffset.UtcNow));
        await store.SaveBoardAsync(snapshot);

        var loaded = await store.LoadBoardAsync();

        Assert.AreEqual("keep", loaded.Items.Single().Text);
        Assert.IsFalse(store.TryDeleteImage(externalImage));
        Assert.IsTrue(File.Exists(externalImage));
    }

    [TestMethod]
    public void ManagedImagePath_UsesCaseInsensitivePathsAndRequiresDirectoryBoundary()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Root, "images");
        Directory.CreateDirectory(root);
        var differentlyCasedPath = Path.Combine(directory.Root, "IMAGES", "sample.png");

        Assert.IsTrue(ManagedImagePath.IsAllowed(root, differentlyCasedPath), "Windows 路径比较不区分大小写。");
        Assert.IsFalse(ManagedImagePath.IsAllowed(root, Path.Combine(directory.Root, "images-other", "sample.png")));
        Assert.IsFalse(ManagedImagePath.IsAllowed(root, root));
    }

    [TestMethod]
    public void DefaultPaths_UsePlatformLocalDataDirectory()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.DisplayName, "Data");

        Assert.AreEqual(expected, AppPaths.CreateDefault().DataDirectory);
    }

    private static BoardSnapshot TextSnapshot(string text) => new()
    {
        Items = [BoardItem.CreateText(text, Guid.NewGuid(), DateTimeOffset.UtcNow)]
    };
}
