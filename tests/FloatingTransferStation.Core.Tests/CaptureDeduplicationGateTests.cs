using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class CaptureDeduplicationGateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void IsRecentDuplicate_WhenNothingRecorded_ReturnsFalse()
    {
        var clock = new MutableClock { Now = T0 };
        var gate = new CaptureDeduplicationGate(Window, () => clock.Now);

        Assert.IsFalse(gate.IsRecentDuplicate(CapturedContentFingerprint.ForText("a")));
        Assert.IsFalse(gate.IsRecentDuplicate(CapturedContentFingerprint.ForText("b")));
    }

    [TestMethod]
    public void IsRecentDuplicate_WithinWindow_MatchesOnlyRecordedFingerprint()
    {
        var clock = new MutableClock { Now = T0 };
        var gate = new CaptureDeduplicationGate(Window, () => clock.Now);
        var accepted = CapturedContentFingerprint.ForText("accepted");
        var other = CapturedContentFingerprint.ForText("other");

        gate.RecordAccepted(accepted);
        clock.Now = clock.Now.AddSeconds(1);

        Assert.IsTrue(gate.IsRecentDuplicate(accepted));
        Assert.IsFalse(gate.IsRecentDuplicate(other));
    }

    [TestMethod]
    public void IsRecentDuplicate_AfterWindowElapsed_ReturnsFalse()
    {
        var clock = new MutableClock { Now = T0 };
        var gate = new CaptureDeduplicationGate(Window, () => clock.Now);
        var accepted = CapturedContentFingerprint.ForText("accepted");

        gate.RecordAccepted(accepted);
        clock.Now = clock.Now.Add(Window).Add(TimeSpan.FromTicks(1));

        Assert.IsFalse(gate.IsRecentDuplicate(accepted));
    }

    [TestMethod]
    public void IsRecentDuplicate_AtExactWindowBoundary_ReturnsTrue()
    {
        var clock = new MutableClock { Now = T0 };
        var gate = new CaptureDeduplicationGate(Window, () => clock.Now);
        var accepted = CapturedContentFingerprint.ForText("accepted");

        gate.RecordAccepted(accepted);
        clock.Now = clock.Now.Add(Window);

        Assert.IsTrue(gate.IsRecentDuplicate(accepted));
    }

    [TestMethod]
    public void RecordAccepted_OverwritesPreviousFingerprint()
    {
        var clock = new MutableClock { Now = T0 };
        var gate = new CaptureDeduplicationGate(Window, () => clock.Now);
        var first = CapturedContentFingerprint.ForText("first");
        var second = CapturedContentFingerprint.ForText("second");

        gate.RecordAccepted(first);
        clock.Now = clock.Now.AddSeconds(1);
        gate.RecordAccepted(second);
        clock.Now = clock.Now.AddSeconds(1);

        Assert.IsFalse(gate.IsRecentDuplicate(first));
        Assert.IsTrue(gate.IsRecentDuplicate(second));
    }

    [TestMethod]
    public void IsRecentDuplicate_WhenClockMovesBackwards_ReturnsFalse()
    {
        var clock = new MutableClock { Now = T0 };
        var gate = new CaptureDeduplicationGate(Window, () => clock.Now);
        var accepted = CapturedContentFingerprint.ForText("accepted");

        gate.RecordAccepted(accepted);
        clock.Now = clock.Now.AddSeconds(-1);

        Assert.IsFalse(gate.IsRecentDuplicate(accepted));
    }

    [TestMethod]
    public void ForText_SameText_ProducesEqualFingerprints()
    {
        var first = CapturedContentFingerprint.ForText("same text");
        var second = CapturedContentFingerprint.ForText("same text");

        Assert.AreEqual(first, second);
        Assert.AreEqual(CapturedContentKind.Text, first.Kind);
        Assert.AreEqual(64, first.Hash.Length);
        Assert.AreEqual(first.Hash.ToLowerInvariant(), first.Hash);
    }

    [TestMethod]
    public void ForText_DifferentText_ProducesDifferentFingerprints()
    {
        var first = CapturedContentFingerprint.ForText("alpha");
        var second = CapturedContentFingerprint.ForText("beta");

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(CapturedContentKind.Text, second.Kind);
    }

    [TestMethod]
    public async Task ForImageFile_SamePixelsAtDifferentPaths_ProducesEqualFingerprints()
    {
        using var directory = new TestDirectory();
        var firstPath = Path.Combine(directory.Root, "first.png");
        var secondPath = Path.Combine(directory.Root, "second.png");

        using (var image = new Image<Rgba32>(4, 3, new Rgba32(255, 0, 0)))
        {
            await image.SaveAsPngAsync(firstPath);
            await image.SaveAsPngAsync(secondPath);
        }

        var first = CapturedContentFingerprint.ForImageFile(firstPath);
        var second = CapturedContentFingerprint.ForImageFile(secondPath);

        Assert.IsTrue(first.HasValue);
        Assert.IsTrue(second.HasValue);
        Assert.AreEqual(first.Value, second.Value);
        Assert.AreEqual(CapturedContentKind.Image, first.Value.Kind);
    }

    [TestMethod]
    public async Task ForImageFile_DifferentPixels_ProducesDifferentFingerprints()
    {
        using var directory = new TestDirectory();
        var redPath = Path.Combine(directory.Root, "red.png");
        var bluePath = Path.Combine(directory.Root, "blue.png");

        using (var red = new Image<Rgba32>(4, 3, new Rgba32(255, 0, 0)))
        {
            await red.SaveAsPngAsync(redPath);
        }

        using (var blue = new Image<Rgba32>(4, 3, new Rgba32(0, 0, 255)))
        {
            await blue.SaveAsPngAsync(bluePath);
        }

        var first = CapturedContentFingerprint.ForImageFile(redPath);
        var second = CapturedContentFingerprint.ForImageFile(bluePath);

        Assert.IsTrue(first.HasValue);
        Assert.IsTrue(second.HasValue);
        Assert.AreNotEqual(first.Value, second.Value);
        Assert.AreEqual(CapturedContentKind.Image, second.Value.Kind);
    }

    [TestMethod]
    public async Task ForImageFile_DifferentDimensions_ProducesDifferentFingerprints()
    {
        using var directory = new TestDirectory();
        var smallPath = Path.Combine(directory.Root, "small.png");
        var largePath = Path.Combine(directory.Root, "large.png");

        using (var small = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30)))
        {
            await small.SaveAsPngAsync(smallPath);
        }

        using (var large = new Image<Rgba32>(3, 2, new Rgba32(10, 20, 30)))
        {
            await large.SaveAsPngAsync(largePath);
        }

        var first = CapturedContentFingerprint.ForImageFile(smallPath);
        var second = CapturedContentFingerprint.ForImageFile(largePath);

        Assert.IsTrue(first.HasValue);
        Assert.IsTrue(second.HasValue);
        Assert.AreNotEqual(first.Value, second.Value);
    }

    [TestMethod]
    public void ForImageFile_MissingFile_ReturnsNull()
    {
        using var directory = new TestDirectory();
        var missingPath = Path.Combine(directory.Root, "missing.png");

        var fingerprint = CapturedContentFingerprint.ForImageFile(missingPath);

        Assert.IsFalse(fingerprint.HasValue);
    }

    [TestMethod]
    public async Task ForImageFile_NonImageContent_ReturnsNull()
    {
        using var directory = new TestDirectory();
        var bogusPath = Path.Combine(directory.Root, "not-an-image.png");
        await File.WriteAllBytesAsync(bogusPath, [1, 2, 3]);

        var fingerprint = CapturedContentFingerprint.ForImageFile(bogusPath);

        Assert.IsFalse(fingerprint.HasValue);
    }

    [TestMethod]
    public void RecordAcceptedAndIsRecentDuplicate_UnderParallelAccess_DoesNotThrow()
    {
        var gate = new CaptureDeduplicationGate(Window, () => DateTimeOffset.UtcNow);
        var fingerprints = new[]
        {
            CapturedContentFingerprint.ForText("a"),
            CapturedContentFingerprint.ForText("b"),
            CapturedContentFingerprint.ForText("c"),
        };

        Parallel.For(0, 2000, i =>
        {
            var fingerprint = fingerprints[i % fingerprints.Length];
            gate.RecordAccepted(fingerprint);
            gate.IsRecentDuplicate(fingerprint);
        });
    }

    private sealed class MutableClock
    {
        public DateTimeOffset Now { get; set; }
    }
}
