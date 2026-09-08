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
    public void ManagedImagePath_UsesPlatformCaseSensitivityAndRequiresDirectoryBoundary()
    {
        using var directory = new TestDirectory();
        var root = Path.Combine(directory.Root, "images");
        Directory.CreateDirectory(root);
        var differentlyCasedPath = Path.Combine(directory.Root, "IMAGES", "sample.png");

        Assert.AreEqual(OperatingSystem.IsWindows(), ManagedImagePath.IsAllowed(root, differentlyCasedPath));
        Assert.IsFalse(ManagedImagePath.IsAllowed(root, Path.Combine(directory.Root, "images-other", "sample.png")));
        Assert.IsFalse(ManagedImagePath.IsAllowed(root, root));
    }

    [TestMethod]
    public async Task UnixCaseVariantDirectory_CannotLoadOrDeleteExternalFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix path behavior is verified by the macOS/Linux test jobs.");
        }

        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(directory.Root);
        var variantRoot = Path.Combine(directory.Root, "IMAGES");
        Directory.CreateDirectory(paths.ImagesDirectory);
        Directory.CreateDirectory(variantRoot);
        var externalImage = Path.Combine(variantRoot, "sentinel.png");
        await File.WriteAllBytesAsync(externalImage, [7]);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var snapshot = TextSnapshot("keep");
        snapshot.Items.Add(BoardItem.CreateImage(Guid.NewGuid(), "IMAGES/sentinel.png", externalImage, DateTimeOffset.UtcNow));
        await store.SaveBoardAsync(snapshot);

        Assert.AreEqual("keep", (await store.LoadBoardAsync()).Items.Single().Text);
        Assert.IsFalse(store.TryDeleteImage(externalImage));
        CollectionAssert.AreEqual(new byte[] { 7 }, await File.ReadAllBytesAsync(externalImage));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnixDirectorySymlink_CannotLoadOrDeleteTarget(bool replaceManagedRoot)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix symbolic links are verified by the macOS/Linux test jobs.");
        }

        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(Path.Combine(directory.Root, "data"));
        var externalRoot = Path.Combine(directory.Root, "external");
        Directory.CreateDirectory(externalRoot);
        var externalImage = Path.Combine(externalRoot, "sentinel.png");
        await File.WriteAllBytesAsync(externalImage, [3, 4]);
        var link = replaceManagedRoot ? paths.ImagesDirectory : Path.Combine(paths.ImagesDirectory, "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, externalRoot);
        try
        {
            var linkedImage = Path.Combine(link, "sentinel.png");
            var relativePath = replaceManagedRoot ? "images/sentinel.png" : "images/linked/sentinel.png";
            var store = new LocalStore(paths, new AtomicTextWriter());
            var snapshot = TextSnapshot("keep");
            snapshot.Items.Add(BoardItem.CreateImage(Guid.NewGuid(), relativePath, linkedImage, DateTimeOffset.UtcNow));
            await store.SaveBoardAsync(snapshot);

            Assert.AreEqual("keep", (await store.LoadBoardAsync()).Items.Single().Text);
            Assert.IsFalse(store.TryDeleteImage(linkedImage));
            CollectionAssert.AreEqual(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(externalImage));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task UnixFileSymlink_CannotLoadOrDeleteTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix symbolic links are verified by the macOS/Linux test jobs.");
        }

        using var directory = new TestDirectory();
        var paths = AppPaths.FromDataDirectory(directory.Root);
        Directory.CreateDirectory(paths.ImagesDirectory);
        var externalImage = Path.Combine(directory.Root, "sentinel.png");
        await File.WriteAllBytesAsync(externalImage, [5]);
        var link = Path.Combine(paths.ImagesDirectory, "linked.png");
        File.CreateSymbolicLink(link, externalImage);
        try
        {
            var store = new LocalStore(paths, new AtomicTextWriter());
            var snapshot = TextSnapshot("keep");
            snapshot.Items.Add(BoardItem.CreateImage(Guid.NewGuid(), "images/linked.png", link, DateTimeOffset.UtcNow));
            await store.SaveBoardAsync(snapshot);

            Assert.AreEqual("keep", (await store.LoadBoardAsync()).Items.Single().Text);
            Assert.IsFalse(store.TryDeleteImage(link));
            CollectionAssert.AreEqual(new byte[] { 5 }, await File.ReadAllBytesAsync(externalImage));
        }
        finally
        {
            File.Delete(link);
        }
    }

    [TestMethod]
    public async Task UnixLinkedDataParent_PreservesManagedImageAccess()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix symbolic links are verified by the macOS/Linux test jobs.");
        }

        using var directory = new TestDirectory();
        var physicalData = Path.Combine(directory.Root, "physical");
        var linkedData = Path.Combine(directory.Root, "configured");
        Directory.CreateDirectory(physicalData);
        Directory.CreateSymbolicLink(linkedData, physicalData);
        try
        {
            var paths = AppPaths.FromDataDirectory(linkedData);
            Directory.CreateDirectory(paths.ImagesDirectory);
            var image = Path.Combine(paths.ImagesDirectory, "managed.png");
            await File.WriteAllBytesAsync(image, [6]);
            var store = new LocalStore(paths, new AtomicTextWriter());
            await store.SaveBoardAsync(new BoardSnapshot
            {
                Items = [BoardItem.CreateImage(Guid.NewGuid(), "images/managed.png", image, DateTimeOffset.UtcNow)]
            });

            Assert.AreEqual(image, (await store.LoadBoardAsync()).Items.Single().ImageAbsolutePath);
            Assert.IsTrue(store.TryDeleteImage(image));
            Assert.IsFalse(File.Exists(Path.Combine(physicalData, "images", "managed.png")));
        }
        finally
        {
            Directory.Delete(linkedData);
        }
    }

    [TestMethod]
    public void DefaultPaths_UsePlatformLocalDataDirectory()
    {
        var expected = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "FloatingTransferStation", "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.DisplayName, "Data");

        Assert.AreEqual(expected, AppPaths.CreateDefault().DataDirectory);
    }

    private static BoardSnapshot TextSnapshot(string text) => new()
    {
        Items = [BoardItem.CreateText(text, Guid.NewGuid(), DateTimeOffset.UtcNow)]
    };
}
