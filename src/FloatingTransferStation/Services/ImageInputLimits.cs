using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;

namespace FloatingTransferStation.Services;

public sealed class ImageInputLimits
{
    // Keep full resolution within a bounded input budget. Larger inputs are rejected, never resized.
    public ImageInputLimits(
        int MaxEncodedBytes = 64 * 1024 * 1024,
        long MaxPixels = 64_000_000,
        int MaxDecodedBufferMegabytes = 512)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEncodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPixels);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPixels, int.MaxValue / 4L);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDecodedBufferMegabytes);
        this.MaxEncodedBytes = MaxEncodedBytes;
        this.MaxPixels = MaxPixels;
        this.MaxDecodedBufferMegabytes = MaxDecodedBufferMegabytes;
        var configuration = Configuration.Default.Clone();
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            AllocationLimitMegabytes = MaxDecodedBufferMegabytes,
            MaximumPoolSizeMegabytes = Math.Min(64, MaxDecodedBufferMegabytes)
        });
        IdentifyFirstFrame = new DecoderOptions { Configuration = configuration, MaxFrames = 1, SkipMetadata = true };
        IdentifyStaticImage = new DecoderOptions { Configuration = configuration, MaxFrames = 2, SkipMetadata = true };
        DecodeFirstFrame = new DecoderOptions { Configuration = configuration, MaxFrames = 1 };
    }

    public static ImageInputLimits Default { get; } = new();

    public int MaxEncodedBytes { get; }
    public long MaxPixels { get; }
    public int MaxDecodedBufferMegabytes { get; }

    internal DecoderOptions IdentifyFirstFrame { get; }
    internal DecoderOptions IdentifyStaticImage { get; }
    internal DecoderOptions DecodeFirstFrame { get; }

    internal static bool IsAllocationLimitFailure(Exception exception) =>
        exception is InvalidMemoryOperationException ||
        (exception.InnerException is not null && IsAllocationLimitFailure(exception.InnerException));

    internal ImageInputLimitException AllocationFailure(Exception exception) => new(
        $"Image decoding exceeds the {MaxDecodedBufferMegabytes}-MiB buffer limit.", exception);

    public void ValidateEncodedLength(long length)
    {
        if (length > MaxEncodedBytes)
        {
            throw new ImageInputLimitException($"Image input exceeds the {MaxEncodedBytes}-byte limit.");
        }
    }

    public void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
        {
            throw new ImageInputLimitException($"Image dimensions exceed the {MaxPixels}-pixel limit or are invalid.");
        }
    }
}

public sealed class ImageInputLimitException(string message, Exception? innerException = null)
    : IOException(message, innerException);
