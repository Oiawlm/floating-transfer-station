using System.Diagnostics;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class LocalStoreTests
{
    [TestMethod]
    public void AppPaths_UnregisteredDirectoryFallsBackToLegacyDefault()
    {
        var paths = AppPaths.CreateDefault(new FakeDataDirectorySettings(null));
        var expectedDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "悬浮中转站",
            "Data");

        Assert.AreEqual(expectedDataDirectory, paths.DataDirectory);
        Assert.AreEqual(Path.Combine(expectedDataDirectory, "board.json"), paths.BoardFile);
        Assert.AreEqual(Path.Combine(expectedDataDirectory, "settings.json"), paths.SettingsFile);
        Assert.AreEqual(Path.Combine(expectedDataDirectory, "images"), paths.ImagesDirectory);
    }

    [TestMethod]
    public void AppPaths_RegisteredManagedDirectoryOverridesLegacyDefault()
    {
        var registered = Path.Combine(@"D:\资料", ProductIdentity.DisplayName, "Data");
        var paths = AppPaths.CreateDefault(new FakeDataDirectorySettings(registered));

        Assert.AreEqual(registered, paths.DataDirectory);
        Assert.AreEqual(Path.Combine(registered, "board.json"), paths.BoardFile);
        Assert.AreEqual(Path.Combine(registered, "settings.json"), paths.SettingsFile);
        Assert.AreEqual(Path.Combine(registered, "images"), paths.ImagesDirectory);
    }

    [TestMethod]
    public void AppPaths_EmptyRegisteredDirectoryFallsBackToLegacyDefault()
    {
        AssertFallsBackToLegacyDefault("   ");
    }

    [TestMethod]
    public void AppPaths_InvalidRegisteredDirectoryFallsBackToLegacyDefault()
    {
        AssertFallsBackToLegacyDefault("\0");
    }

    [TestMethod]
    public void AppPaths_UnmanagedRegisteredDirectoryFallsBackToLegacyDefault()
    {
        AssertFallsBackToLegacyDefault(@"D:\资料\其他软件");
    }

    [TestMethod]
    public void AppPaths_RelativeRegisteredDirectoryFallsBackToLegacyDefault()
    {
        AssertFallsBackToLegacyDefault(Path.Combine(ProductIdentity.DisplayName, "Data"));
    }

    [TestMethod]
    public void AppPaths_DriveRelativeRegisteredDirectoryFallsBackToLegacyDefault()
    {
        AssertFallsBackToLegacyDefault($"D:{ProductIdentity.DisplayName}\\Data");
    }

    private static void AssertFallsBackToLegacyDefault(string? candidate)
    {
        var paths = AppPaths.CreateDefault(new FakeDataDirectorySettings(candidate));
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.DisplayName,
            "Data");

        Assert.AreEqual(expected, paths.DataDirectory);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsReadableJsonAndExistingImage()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.ImagesDirectory);
        var imagePath = Path.Combine(paths.ImagesDirectory, "image.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47]);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var snapshot = new BoardSnapshot
        {
            Items =
            [
                BoardItem.CreateText("中文提示词", Guid.Parse("00000000-0000-0000-0000-000000000001"), DateTimeOffset.Parse("2026-08-09T00:00:00Z")),
                BoardItem.CreateImage(
                    Guid.Parse("00000000-0000-0000-0000-000000000002"),
                    "images/image.png",
                    imagePath,
                    DateTimeOffset.Parse("2026-08-09T00:00:01Z"))
            ]
        };

        await store.SaveBoardAsync(snapshot);
        var loaded = await store.LoadBoardAsync();
        var json = await File.ReadAllTextAsync(paths.BoardFile);

        Assert.AreEqual(2, loaded.Items.Count);
        StringAssert.Contains(json, "中文提示词");
        Assert.AreEqual(imagePath, loaded.Items.Single(item => item.Kind == BoardItemKind.Image).ImageAbsolutePath);
    }

    [TestMethod]
    public async Task LoadBoard_LegacyItemWithoutPinnedStateDefaultsToUnpinned()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.BoardFile, """
            {
              "schemaVersion": 1,
              "items": [
                {
                  "id": "00000000-0000-0000-0000-000000000051",
                  "kind": "Text",
                  "category": "Inbox",
                  "order": 0,
                  "createdAt": "2026-08-22T00:00:00+00:00",
                  "text": "旧内容"
                }
              ]
            }
            """);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var loaded = await store.LoadBoardAsync();

        Assert.IsFalse(loaded.Items.Single().IsPinned);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTripsPinnedState()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var item = BoardItem.CreateText("置顶内容", Guid.NewGuid(), DateTimeOffset.UtcNow);
        item.IsPinned = true;

        await store.SaveBoardAsync(new BoardSnapshot { Items = [item] });
        var loaded = await store.LoadBoardAsync();
        var json = await File.ReadAllTextAsync(paths.BoardFile);

        Assert.IsTrue(loaded.Items.Single().IsPinned);
        StringAssert.Contains(json, "\"isPinned\": true");
    }

    [TestMethod]
    public async Task Settings_RoundTripCustomCategoryNames()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default
            .WithCategoryName(BoardCategory.CustomerOriginal, string.Empty)
            .WithCategoryName(BoardCategory.Reference, "参考");

        await store.SaveSettingsAsync(settings);
        var loaded = await store.LoadSettingsAsync();

        Assert.AreEqual(string.Empty, loaded.CategoryName(BoardCategory.CustomerOriginal));
        Assert.AreEqual("参考", loaded.CategoryName(BoardCategory.Reference));
        Assert.AreEqual("提示词", loaded.CategoryName(BoardCategory.Prompt));
        Assert.AreEqual("待分类", loaded.CategoryName(BoardCategory.Inbox));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task LoadBoard_CorruptPrimaryFallsBackToLastBackup()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var first = SnapshotWithText("可恢复");
        var second = SnapshotWithText("当前");
        await store.SaveBoardAsync(first);
        await store.SaveBoardAsync(second);
        await File.WriteAllTextAsync(paths.BoardFile, "{ truncated");

        var loaded = await store.LoadBoardAsync();

        Assert.AreEqual("可恢复", loaded.Items.Single().Text);
        Assert.AreEqual(1, Directory.GetFiles(paths.DataDirectory, "board.json.corrupt-*.bak").Length);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task LoadBoard_MissingPrimaryFallsBackToLastBackup()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        await store.SaveBoardAsync(SnapshotWithText("可恢复"));
        await store.SaveBoardAsync(SnapshotWithText("当前"));
        File.Delete(paths.BoardFile);

        var loaded = await store.LoadBoardAsync();

        Assert.AreEqual("可恢复", loaded.Items.Single().Text);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task LoadBoard_MissingOrEscapingImagesAreFiltered()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        var json = """
        {
          "schemaVersion": 1,
          "items": [
            {
              "id": "00000000-0000-0000-0000-000000000001",
              "kind": "Image",
              "category": "Inbox",
              "order": 0,
              "createdAt": "2026-08-09T00:00:00+00:00",
              "imageRelativePath": "images/missing.png"
            },
            {
              "id": "00000000-0000-0000-0000-000000000002",
              "kind": "Image",
              "category": "Inbox",
              "order": 1,
              "createdAt": "2026-08-09T00:00:01+00:00",
              "imageRelativePath": "../outside.png"
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(paths.BoardFile, json);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var loaded = await store.LoadBoardAsync();

        Assert.AreEqual(0, loaded.Items.Count);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    [DataRow("images/invalid\0.png")]
    [DataRow("\0")]
    public async Task LoadBoard_MalformedImagePathDoesNotDiscardOtherItems(string relativePath)
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.ImagesDirectory);
        var validImagePath = Path.Combine(paths.ImagesDirectory, "valid.png");
        await File.WriteAllBytesAsync(validImagePath, [1, 2, 3]);
        var before = BoardItem.CreateText("before", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var after = BoardItem.CreateText("after", Guid.NewGuid(), DateTimeOffset.UtcNow);
        var validImage = BoardItem.CreateImage(Guid.NewGuid(), "images/valid.png", validImagePath, DateTimeOffset.UtcNow);
        var store = new LocalStore(paths, new AtomicTextWriter());
        await store.SaveBoardAsync(new BoardSnapshot
        {
            Items =
            [
                before,
                BoardItem.CreateImage(Guid.NewGuid(), relativePath, string.Empty, DateTimeOffset.UtcNow),
                after,
                validImage
            ]
        });
        var originalJson = await File.ReadAllTextAsync(paths.BoardFile);

        var loaded = await store.LoadBoardAsync();

        CollectionAssert.AreEqual(new[] { before.Id, after.Id, validImage.Id }, loaded.Items.Select(item => item.Id).ToArray());
        Assert.AreEqual(originalJson, await File.ReadAllTextAsync(paths.BoardFile));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ImagesReachedThroughDirectoryLinkAreExcludedAndNotDeleted(bool linkImagesRoot)
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(Path.Combine(directory.Root, "data"));
        var externalDirectory = Path.Combine(directory.Root, "external");
        Directory.CreateDirectory(externalDirectory);
        Directory.CreateDirectory(paths.DataDirectory);
        var externalImage = Path.Combine(externalDirectory, "sentinel.png");
        await File.WriteAllBytesAsync(externalImage, [1, 2, 3]);
        var link = linkImagesRoot ? paths.ImagesDirectory : Path.Combine(paths.ImagesDirectory, "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateDirectoryJunction(link, externalDirectory);
        try
        {
            var relativePath = linkImagesRoot ? "images/sentinel.png" : "images/linked/sentinel.png";
            var linkedImage = Path.Combine(link, "sentinel.png");
            var store = new LocalStore(paths, new AtomicTextWriter());
            await store.SaveBoardAsync(new BoardSnapshot
            {
                Items =
                [
                    BoardItem.CreateImage(Guid.NewGuid(), relativePath, linkedImage, DateTimeOffset.UtcNow),
                    BoardItem.CreateText("keep", Guid.NewGuid(), DateTimeOffset.UtcNow)
                ]
            });

            var loaded = await store.LoadBoardAsync();
            var deleted = store.TryDeleteImage(linkedImage);

            Assert.IsFalse(deleted, "A managed-looking path through a directory link must not delete its target.");
            Assert.AreEqual("keep", loaded.Items.Single().Text);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(externalImage));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ManagedImagesBelowLinkedDataParentRemainUsable()
    {
        using var directory = new TestDirectory();
        var physicalData = Path.Combine(directory.Root, "physical-data");
        var linkedData = Path.Combine(directory.Root, "configured-data");
        Directory.CreateDirectory(Path.Combine(physicalData, "images"));
        CreateDirectoryJunction(linkedData, physicalData);
        try
        {
            var paths = AppPaths.ForTests(linkedData);
            var imagePath = Path.Combine(paths.ImagesDirectory, "managed.png");
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            var store = new LocalStore(paths, new AtomicTextWriter());
            await store.SaveBoardAsync(new BoardSnapshot
            {
                Items = [BoardItem.CreateImage(Guid.NewGuid(), "images/managed.png", imagePath, DateTimeOffset.UtcNow)]
            });

            var loaded = await store.LoadBoardAsync();

            Assert.AreEqual(imagePath, loaded.Items.Single().ImageAbsolutePath);
            Assert.IsTrue(store.TryDeleteImage(imagePath));
            Assert.IsFalse(File.Exists(Path.Combine(physicalData, "images", "managed.png")));
        }
        finally
        {
            Directory.Delete(linkedData);
        }
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task LoadBoard_UnknownSchemaStartsWithEmptyBoard()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(
            paths.BoardFile,
            """
            {
              "schemaVersion": 999,
              "items": [
                {
                  "id": "00000000-0000-0000-0000-000000000001",
                  "kind": "Text",
                  "category": "Inbox",
                  "order": 0,
                  "createdAt": "2026-08-09T00:00:00+00:00",
                  "text": "不应载入"
                }
              ]
            }
            """);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var loaded = await store.LoadBoardAsync();

        Assert.AreEqual(0, loaded.Items.Count);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task LoadBoard_NullItemIsOmittedWhileValidItemIsRecovered()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(
            paths.BoardFile,
            """
            {
              "schemaVersion": 1,
              "items": [
                null,
                {
                  "id": "00000000-0000-0000-0000-000000000001",
                  "kind": "Text",
                  "category": "Inbox",
                  "order": 0,
                  "createdAt": "2026-08-09T00:00:00+00:00",
                  "text": "仍可恢复"
                }
              ]
            }
            """);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var loaded = await store.LoadBoardAsync();

        Assert.AreEqual("仍可恢复", loaded.Items.Single().Text);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task SaveBoard_WhenWriterFailsLeavesPreviousFileUntouched()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var working = new LocalStore(paths, new AtomicTextWriter());
        await working.SaveBoardAsync(SnapshotWithText("旧内容"));
        var before = await File.ReadAllTextAsync(paths.BoardFile);
        var failing = new LocalStore(paths, new ThrowingAtomicTextWriter());

        await Assert.ThrowsExactlyAsync<IOException>(
            () => failing.SaveBoardAsync(SnapshotWithText("新内容")));

        Assert.AreEqual(before, await File.ReadAllTextAsync(paths.BoardFile));
    }

    [TestMethod]
    public async Task Settings_RoundTripWithoutAddingCustomPathSetting()
    {
        using var directory = new TestDirectory();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var settings = new WindowSettings(420, 720, 90);

        await store.SaveSettingsAsync(settings);

        Assert.AreEqual(settings, await store.LoadSettingsAsync());
    }

    [TestMethod]
    public async Task TryDeleteImage_ManagedImageIsDeleted()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        Directory.CreateDirectory(paths.ImagesDirectory);
        var imagePath = Path.Combine(paths.ImagesDirectory, "managed.png");
        await File.WriteAllBytesAsync(imagePath, [0x00]);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var deleted = store.TryDeleteImage(imagePath);

        Assert.IsTrue(deleted);
        Assert.IsFalse(File.Exists(imagePath));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task TryDeleteImage_OutsideManagedDirectoryIsRejectedAndPreserved()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var outsidePath = Path.Combine(directory.Root, "outside.png");
        await File.WriteAllBytesAsync(outsidePath, [0x00]);
        var store = new LocalStore(paths, new AtomicTextWriter());

        var deleted = store.TryDeleteImage(outsidePath);

        Assert.IsFalse(deleted);
        Assert.IsTrue(File.Exists(outsidePath));
    }

    private static BoardSnapshot SnapshotWithText(string text) => new()
    {
        Items =
        [
            BoardItem.CreateText(
                text,
                Guid.Parse("00000000-0000-0000-0000-000000000010"),
                DateTimeOffset.Parse("2026-08-09T00:00:00Z"))
        ]
    };

    private static void CreateDirectoryJunction(string path, string target)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:FTS_LINK_PATH -Target $env:FTS_LINK_TARGET | Out-Null");
        startInfo.Environment["FTS_LINK_PATH"] = path;
        startInfo.Environment["FTS_LINK_TARGET"] = target;
        using var process = Process.Start(startInfo)!;
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Creating the synthetic directory junction timed out.");
        }
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
        Assert.IsTrue((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
    }

    private sealed class ThrowingAtomicTextWriter : IAtomicTextWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken = default) =>
            throw new IOException("Injected write failure.");
    }

    private sealed class FakeDataDirectorySettings(string? dataDirectory) : IDataDirectorySettings
    {
        public string? ReadDataDirectory() => dataDirectory;
    }
}
