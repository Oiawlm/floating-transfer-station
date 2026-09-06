using System.Diagnostics;
using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class ImageNormalizerPathTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepairStoredImagesOnce_RejectsReparseRootAndChild(bool linkIsRoot)
    {
        using var directory = new TestDirectory();
        var images = Path.Combine(directory.Root, "images");
        var outside = Path.Combine(directory.Root, "outside");
        Directory.CreateDirectory(outside);
        var originalPath = Path.Combine(outside, "transparent.png");
        using (var image = new Image<Rgba32>(1, 1, new Rgba32(10, 20, 30, 0)))
        {
            await image.SaveAsPngAsync(originalPath);
        }
        var before = await File.ReadAllBytesAsync(originalPath);
        var link = linkIsRoot ? images : Path.Combine(images, "linked");
        if (!linkIsRoot)
        {
            Directory.CreateDirectory(images);
        }
        CreateJunction(link, outside);
        try
        {
            await new ImageNormalizer(images).RepairStoredImagesOnceAsync([Path.Combine(link, "transparent.png")]);

            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(originalPath));
            Assert.IsFalse(File.Exists(Path.Combine(outside, ".zero-alpha-repair-v1")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [TestMethod]
    public async Task NormalizeFile_RejectsReparseImageRootBeforeCreatingCopies()
    {
        using var directory = new TestDirectory();
        var images = Path.Combine(directory.Root, "images");
        var outside = Path.Combine(directory.Root, "outside");
        Directory.CreateDirectory(outside);
        var source = Path.Combine(directory.Root, "source.png");
        using (var image = new Image<Rgba32>(1, 1, SixLabors.ImageSharp.Color.Red))
        {
            await image.SaveAsPngAsync(source);
        }
        CreateJunction(images, outside);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new ImageNormalizer(images).NormalizeFileAsync(source));

            Assert.IsEmpty(Directory.EnumerateFiles(outside));
        }
        finally
        {
            Directory.Delete(images);
        }
    }

    private static void CreateJunction(string path, string target)
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
        startInfo.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:FTS_IMAGE_TEST_LINK -Target $env:FTS_IMAGE_TEST_TARGET | Out-Null");
        startInfo.Environment["FTS_IMAGE_TEST_LINK"] = path;
        startInfo.Environment["FTS_IMAGE_TEST_TARGET"] = target;
        using var process = Process.Start(startInfo)!;
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Creating the synthetic image directory junction timed out.");
        }
        Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd());
    }
}
