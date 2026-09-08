using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FloatingTransferStation.Mac.Services;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Mac;

public sealed partial class MainWindow
{
    private async Task RunNativeClipboardSmokeAsync(string directory)
    {
        if (!OperatingSystem.IsMacOS() || _smokeDirectory is null ||
            !string.Equals(Path.GetFullPath(directory), Path.GetFullPath(_smokeDirectory), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Native clipboard smoke requires the isolated macOS smoke directory.");
        }

        var clipboard = Clipboard ?? throw new InvalidOperationException("The native clipboard is unavailable.");
        var pasteboard = new MacPasteboard();
        var runId = Guid.NewGuid().ToString("N");
        // A unique platform UTI identifies only our synthetic generation without reading another owner's content.
        var marker = DataFormat.CreateBytesPlatformFormat($"io.github.oiawlm.fts.smoke.{runId}");
        var originalCategory = _captureCategory;
        var originalSnapshot = JsonSerializer.Serialize(_board.CreateSnapshot());
        var originalIds = _board.CreateSnapshot().Items.Select(item => item.Id).ToHashSet();
        var checks = new List<string>();
        long? ownedChangeCount = null;
        var clearedSyntheticClipboard = false;
        IStorageFile? sourceFile = null;
        _captureCategory = BoardCategory.Reference;

        try
        {
            var knownText = $"fts-native-text-{runId}";
            var textState = await PublishAsync(DataTransferItem.CreateText(knownText));
            Require(textState.Types.Contains("public.utf8-plain-text") && !textState.IsPrivate,
                "Native text format was not exposed by NSPasteboard.");
            var beforeText = CurrentIds();
            RequireOwnedGeneration();
            Require(await _monitor.CaptureNowAsync(), "Native text capture did not complete.");
            var capturedText = RequireSingleAdded(beforeText, BoardItemKind.Text);
            Require(capturedText.Text == knownText, "Native text capture returned a different value.");
            Require((await _store.LoadBoardAsync()).Items.Any(item => item.Id == capturedText.Id && item.Text == knownText),
                "Native text capture was not persisted.");
            checks.Add("text");

            var privateReads = 0;
            var privateItem = new DataTransferItem();
            privateItem.Set(DataFormat.Text, () =>
            {
                privateReads++;
                return $"fts-native-private-{runId}";
            });
            privateItem.Set(DataFormat.CreateBytesPlatformFormat("org.nspasteboard.ConcealedType"), new byte[] { 1 });
            var beforePrivateMemory = JsonSerializer.Serialize(_board.CreateSnapshot());
            var beforePrivateDisk = JsonSerializer.Serialize(await _store.LoadBoardAsync());
            var privateState = await PublishAsync(privateItem);
            Require(privateState.IsPrivate, "NSPasteboard did not expose the concealed marker.");
            // AppKit may eagerly request plain text while publishing. Only subsequent requests belong to capture.
            var readsAfterPublishing = privateReads;
            RequireOwnedGeneration();
            Require(!await _monitor.CaptureNowAsync(), "A concealed native generation was imported.");
            Require(privateReads == readsAfterPublishing, "The concealed native text payload was requested during capture.");
            var contentReadRequests = 0;
            var privacyProbe = new ClipboardMonitorService(() => clipboard, () => _captureCategory,
                _reader, _imports, _ => { }, pasteboard, () =>
                {
                    contentReadRequests++;
                    return clipboard.TryGetDataAsync();
                });
            Require(!await privacyProbe.CaptureNowAsync() && contentReadRequests == 0,
                "The native concealed marker did not prevent entry into clipboard content reading.");
            await privacyProbe.StopAsync();
            Require(JsonSerializer.Serialize(_board.CreateSnapshot()) == beforePrivateMemory &&
                JsonSerializer.Serialize(await _store.LoadBoardAsync()) == beforePrivateDisk,
                "The concealed generation changed the board or persisted content.");
            checks.Add("private-marker");

            var samplePath = Path.GetFullPath(Path.Combine(directory, "sample.png"));
            var sampleBytes = await File.ReadAllBytesAsync(samplePath);
            var sampleInfo = SixLabors.ImageSharp.Image.Identify(sampleBytes);
            var imageItem = DataTransferItem.Create(DataFormat.CreateBytesPlatformFormat("public.png"), sampleBytes);
            var imageState = await PublishAsync(imageItem);
            Require(imageState.Types.Contains("public.png") && !imageState.HasFiles && !imageState.IsPrivate,
                "The native PNG generation exposed unexpected types.");
            var nativeImage = pasteboard.ReadImages(imageState);
            Require(nativeImage is TransferPayload.ImageCandidates candidates &&
                candidates.Candidates.Any(bytes => bytes.Span.SequenceEqual(sampleBytes)),
                "The native NSData image read did not preserve the encoded PNG.");
            var beforeImage = CurrentIds();
            RequireOwnedGeneration();
            Require(await _monitor.CaptureNowAsync(), "Native encoded-image capture did not complete.");
            var capturedImage = RequireSingleAdded(beforeImage, BoardItemKind.Image);
            await RequireImageCopyAsync(capturedImage, samplePath, sampleInfo.Width, sampleInfo.Height);
            checks.Add("encoded-image");

            sourceFile = await StorageProvider.TryGetFileFromPathAsync(new Uri(samplePath))
                ?? throw new InvalidOperationException("The synthetic source file could not be opened through the native storage provider.");
            var fileState = await PublishAsync(DataTransferItem.CreateFile(sourceFile));
            Require(fileState.HasFiles && !fileState.IsPrivate, "NSPasteboard did not expose the native file URL.");
            RequireOwnedGeneration();
            TransferPayload? filePayload;
            using (var data = await clipboard.TryGetDataAsync())
            {
                Require(data is not null, "The native file data transfer was unavailable.");
                filePayload = await _reader.ReadAsync(data!);
            }
            RequireOwnedGeneration();
            Require(filePayload is TransferPayload.ImageFiles files && files.Paths.Count == 1 &&
                string.Equals(Path.GetFullPath(files.Paths[0]), samplePath, StringComparison.Ordinal),
                "The native file transfer did not preserve its source path.");
            var beforeFile = CurrentIds();
            Require(await _imports.ImportAsync(filePayload!, _captureCategory), "Native file-transfer import did not complete.");
            var capturedFile = RequireSingleAdded(beforeFile, BoardItemKind.Image);
            await RequireImageCopyAsync(capturedFile, samplePath, sampleInfo.Width, sampleInfo.Height);
            Require((await File.ReadAllBytesAsync(samplePath)).AsSpan().SequenceEqual(sampleBytes),
                "Native clipboard imports modified the synthetic source image.");
            checks.Add("file-transfer");
        }
        finally
        {
            _captureCategory = originalCategory;
            try
            {
                var addedItems = _board.CreateSnapshot().Items.Where(item => !originalIds.Contains(item.Id)).ToArray();
                if (addedItems.Length > 0)
                {
                    Require(await _mutations.DeleteManyAsync(addedItems.Select(item => item.Id).ToArray()),
                        "Native clipboard smoke could not remove its synthetic board items.");
                    Require(addedItems.Where(item => item.Kind == BoardItemKind.Image)
                        .All(item => !File.Exists(item.ImageAbsolutePath)),
                        "Native clipboard smoke left a generated image copy behind.");
                }

                Require(JsonSerializer.Serialize(_board.CreateSnapshot()) == originalSnapshot,
                    "Native clipboard smoke did not restore the original board state.");
                Require(JsonSerializer.Serialize(await _store.LoadBoardAsync()) == originalSnapshot,
                    "Native clipboard smoke did not restore the persisted board state.");
            }
            finally
            {
                try
                {
                    // Do not restore prior contents or clear a newer generation owned by another application.
                    var current = pasteboard.ReadState();
                    if (ownedChangeCount is not null && current.ChangeCount == ownedChangeCount && current.Types.Contains(marker.Identifier))
                    {
                        await clipboard.ClearAsync();
                        clearedSyntheticClipboard = true;
                    }
                }
                finally
                {
                    sourceFile?.Dispose();
                }
            }
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "native-clipboard.json"), JsonSerializer.Serialize(new
        {
            passed = true,
            platform = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            checks,
            syntheticItemsRemoved = true,
            syntheticClipboardCleared = clearedSyntheticClipboard
        }));

        async Task<PasteboardState> PublishAsync(DataTransferItem item)
        {
            item.Set(marker, new byte[] { 1 });
            var previous = pasteboard.ReadState().ChangeCount;
            var data = new DataTransfer();
            data.Add(item);
            try
            {
                // Successful SetDataAsync transfers ownership to Avalonia until clipboard replacement.
                await clipboard.SetDataAsync(data);
            }
            catch
            {
                ((IDisposable)data).Dispose();
                throw;
            }

            var state = pasteboard.ReadState();
            Require(state.ChangeCount != previous && state.Types.Contains(marker.Identifier),
                "Publishing a synthetic generation did not update native changeCount and its unique marker.");
            ownedChangeCount = state.ChangeCount;
            return state;
        }

        void RequireOwnedGeneration()
        {
            var current = pasteboard.ReadState();
            Require(ownedChangeCount is not null && current.ChangeCount == ownedChangeCount && current.Types.Contains(marker.Identifier),
                "Another clipboard owner interrupted the native smoke test.");
        }

        HashSet<Guid> CurrentIds() => _board.CreateSnapshot().Items.Select(item => item.Id).ToHashSet();

        BoardItem RequireSingleAdded(HashSet<Guid> previousIds, BoardItemKind kind)
        {
            var added = _board.CreateSnapshot().Items.Where(item => !previousIds.Contains(item.Id)).ToArray();
            Require(added.Length == 1 && added[0].Kind == kind && added[0].Category == _captureCategory,
                "Native clipboard capture did not add exactly one item in the requested category.");
            return added[0];
        }

        async Task RequireImageCopyAsync(BoardItem image, string sourcePath, int width, int height)
        {
            Require(image.ImageAbsolutePath is { } path && File.Exists(path) &&
                ManagedImagePath.IsAllowed(_store.ImagesDirectory, path) &&
                !string.Equals(Path.GetFullPath(path), sourcePath, StringComparison.Ordinal),
                "Native image capture did not create an independent managed image copy.");
            var info = await SixLabors.ImageSharp.Image.IdentifyAsync(image.ImageAbsolutePath!);
            Require(info.Width == width && info.Height == height, "Native image capture changed the image dimensions.");
            Require((await _store.LoadBoardAsync()).Items.Any(item => item.Id == image.Id && item.Kind == BoardItemKind.Image),
                "Native image capture was not persisted.");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
