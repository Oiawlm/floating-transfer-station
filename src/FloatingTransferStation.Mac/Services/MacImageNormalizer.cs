using FloatingTransferStation.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace FloatingTransferStation.Mac.Services;

public interface IMacImageNormalizer
{
    Task<StoredImage> NormalizeStaticFileAsync(string path, CancellationToken cancellationToken = default);
    Task<StoredImage> NormalizeCandidatesAsync(IReadOnlyList<ReadOnlyMemory<byte>> candidates,
        CancellationToken cancellationToken = default);
}

public sealed class MacImageNormalizer(string imagesDirectory, ImageInputLimits? limits = null) : IMacImageNormalizer
{
    private readonly ImageInputLimits _limits = limits ?? ImageInputLimits.Default;

    public Task<StoredImage> NormalizeStaticFileAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => NormalizeStaticFileCoreAsync(path, cancellationToken), cancellationToken);

    private async Task<StoredImage> NormalizeStaticFileCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (!ImageFileSupport.IsSupported(path))
        {
            throw new InvalidDataException("Only supported static image files can be imported.");
        }

        try
        {
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _limits.ValidateEncodedLength(source.Length);
            var info = await Image.IdentifyAsync(_limits.IdentifyStaticImage, source, cancellationToken);
            _limits.ValidateDimensions(info.Width, info.Height);
            if (!ImageFileSupport.HasSingleFrame(info, source))
            {
                throw new InvalidDataException("Animated image files are not supported.");
            }

            source.Position = 0;
            using var image = await Image.LoadAsync(_limits.DecodeFirstFrame, source, cancellationToken);
            _limits.ValidateDimensions(image.Width, image.Height);
            return await SaveAsync(image, cancellationToken);
        }
        catch (Exception exception) when (ImageInputLimits.IsAllocationLimitFailure(exception))
        {
            throw _limits.AllocationFailure(exception);
        }
    }

    public Task<StoredImage> NormalizeCandidatesAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> candidates, CancellationToken cancellationToken = default) =>
        Task.Run(() => NormalizeCandidatesCoreAsync(candidates, cancellationToken), cancellationToken);

    private async Task<StoredImage> NormalizeCandidatesCoreAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> candidates, CancellationToken cancellationToken)
    {
        var ordered = new List<(ReadOnlyMemory<byte> Bytes, long Area)>();
        ImageInputLimitException? limitFailure = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _limits.ValidateEncodedLength(candidate.Length);
                var info = Image.Identify(_limits.IdentifyFirstFrame, candidate.Span);
                _limits.ValidateDimensions(info.Width, info.Height);
                ordered.Add((candidate, (long)info.Width * info.Height));
            }
            catch (ImageInputLimitException exception)
            {
                limitFailure ??= exception;
            }
            catch (Exception exception) when (ImageInputLimits.IsAllocationLimitFailure(exception))
            {
                limitFailure ??= _limits.AllocationFailure(exception);
            }
            catch (Exception exception) when (IsDecodeFailure(exception))
            {
                // A broken representation must not hide another valid representation.
            }
        }

        foreach (var candidate in ordered.OrderByDescending(candidate => candidate.Area))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Image image;
            try
            {
                image = Image.Load(_limits.DecodeFirstFrame, candidate.Bytes.Span);
            }
            catch (Exception exception) when (ImageInputLimits.IsAllocationLimitFailure(exception))
            {
                limitFailure ??= _limits.AllocationFailure(exception);
                continue;
            }
            catch (Exception exception) when (IsDecodeFailure(exception))
            {
                continue;
            }

            using (image)
            {
                _limits.ValidateDimensions(image.Width, image.Height);
                return await SaveAsync(image, cancellationToken);
            }
        }

        if (limitFailure is not null)
        {
            throw limitFailure;
        }

        throw new InvalidDataException("No usable image representation was found.");
    }

    private async Task<StoredImage> SaveAsync(Image image, CancellationToken cancellationToken)
    {
        image.Mutate(operation => operation.AutoOrient());
        var id = Guid.NewGuid();
        var fileName = $"{id:N}.png";
        var destination = Path.Combine(imagesDirectory, fileName);
        var temporary = destination + ".tmp";
        EnsureManaged(destination);
        EnsureManaged(temporary);
        Directory.CreateDirectory(imagesDirectory);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await image.SaveAsPngAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureManaged(temporary);
            EnsureManaged(destination);
            File.Move(temporary, destination, overwrite: false);
            return new StoredImage(id, $"images/{fileName}", destination);
        }
        finally
        {
            if (ManagedImagePath.IsAllowed(imagesDirectory, temporary) && File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void EnsureManaged(string path)
    {
        if (!ManagedImagePath.IsAllowed(imagesDirectory, path))
        {
            throw new InvalidDataException("Image destination is outside the managed image directory.");
        }
    }

    private static bool IsDecodeFailure(Exception exception) =>
        exception is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or EndOfStreamException;
}
