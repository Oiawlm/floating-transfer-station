using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

public sealed partial class LocalStore
{
    private static readonly string ReviewDatePattern = "yyyy-MM-dd";
    private readonly SemaphoreSlim _reviewWriteGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string?> _reviewWriteFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _reviewChangeDebounces = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reviewWatcherLock = new();
    private FileSystemWatcher? _reviewWatcher;

    public string ReviewsDirectory => _paths.ReviewsDirectory;

    public event EventHandler<DailyReviewFileChangedEventArgs>? Changed;

    public async Task<DailyReviewDocument> LoadAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var path = ReviewPath(date);
        if (!File.Exists(path))
        {
            return new DailyReviewDocument(date, string.Empty, false, Fingerprint(string.Empty));
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new DailyReviewDocument(date, string.Empty, false, Fingerprint(string.Empty));
        }

        return new DailyReviewDocument(date, content, true, Fingerprint(content));
    }

    public async Task<IReadOnlyList<DateOnly>> ListDatesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ReviewsDirectory))
        {
            return [];
        }

        var dates = new List<DateOnly>();
        foreach (var path in Directory.EnumerateFiles(ReviewsDirectory, "*.md", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseDate(Path.GetFileNameWithoutExtension(path), out var date))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(content))
            {
                dates.Add(date);
            }
        }

        dates.Sort((left, right) => right.CompareTo(left));
        return dates;
    }

    public async Task SaveAsync(
        DateOnly date,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            await DeleteAsync(date, cancellationToken).ConfigureAwait(false);
            return;
        }

        var path = ReviewPath(date);
        var fingerprint = Fingerprint(content);
        await _reviewWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
            _reviewWriteFingerprints[path] = fingerprint;
        }
        finally
        {
            _reviewWriteGate.Release();
        }
    }

    public async Task DeleteAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var path = ReviewPath(date);
        await _reviewWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            _reviewWriteFingerprints[path] = null;
        }
        finally
        {
            _reviewWriteGate.Release();
        }
    }

    public void StartWatching()
    {
        lock (_reviewWatcherLock)
        {
            if (_reviewWatcher is not null)
            {
                return;
            }

            Directory.CreateDirectory(ReviewsDirectory);
            var watcher = new FileSystemWatcher(ReviewsDirectory, "*.md")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            watcher.Created += ReviewWatcher_FileChanged;
            watcher.Changed += ReviewWatcher_FileChanged;
            watcher.Deleted += ReviewWatcher_FileChanged;
            watcher.Renamed += ReviewWatcher_FileRenamed;
            watcher.Error += ReviewWatcher_Error;
            _reviewWatcher = watcher;
        }
    }

    public void StopWatching()
    {
        FileSystemWatcher? watcher;
        lock (_reviewWatcherLock)
        {
            watcher = _reviewWatcher;
            _reviewWatcher = null;
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= ReviewWatcher_FileChanged;
            watcher.Changed -= ReviewWatcher_FileChanged;
            watcher.Deleted -= ReviewWatcher_FileChanged;
            watcher.Renamed -= ReviewWatcher_FileRenamed;
            watcher.Error -= ReviewWatcher_Error;
            watcher.Dispose();
        }

        foreach (var entry in _reviewChangeDebounces.ToArray())
        {
            if (_reviewChangeDebounces.TryRemove(entry.Key, out var cancellation))
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
        }
    }

    public void Dispose()
    {
        StopWatching();
        _reviewWriteGate.Dispose();
    }

    private void ReviewWatcher_FileChanged(object sender, FileSystemEventArgs e) =>
        QueueReviewChange(e.FullPath, e.ChangeType switch
        {
            WatcherChangeTypes.Created => DailyReviewFileChangeKind.Created,
            WatcherChangeTypes.Deleted => DailyReviewFileChangeKind.Deleted,
            _ => DailyReviewFileChangeKind.Changed
        });

    private void ReviewWatcher_FileRenamed(object sender, RenamedEventArgs e)
    {
        QueueReviewChange(e.OldFullPath, DailyReviewFileChangeKind.Renamed);
        QueueReviewChange(e.FullPath, DailyReviewFileChangeKind.Renamed);
    }

    private void ReviewWatcher_Error(object sender, ErrorEventArgs e) =>
        Changed?.Invoke(this, new DailyReviewFileChangedEventArgs(null, DailyReviewFileChangeKind.RescanRequired));

    private void QueueReviewChange(string path, DailyReviewFileChangeKind kind)
    {
        if (!TryParseDate(Path.GetFileNameWithoutExtension(path), out var date))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = _reviewChangeDebounces.AddOrUpdate(path, cancellation, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return cancellation;
        });
        if (!ReferenceEquals(previous, cancellation))
        {
            return;
        }

        _ = DispatchReviewChangeAsync(path, date, kind, cancellation);
    }

    private async Task DispatchReviewChangeAsync(
        string path,
        DateOnly date,
        DailyReviewFileChangeKind kind,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token).ConfigureAwait(false);
            if (_reviewWriteFingerprints.TryGetValue(path, out var expected))
            {
                var actual = File.Exists(path) ? Fingerprint(await File.ReadAllTextAsync(path).ConfigureAwait(false)) : null;
                if (actual == expected)
                {
                    _reviewWriteFingerprints.TryRemove(path, out _);
                    return;
                }
            }

            Changed?.Invoke(this, new DailyReviewFileChangedEventArgs(date, kind));
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            Changed?.Invoke(this, new DailyReviewFileChangedEventArgs(date, DailyReviewFileChangeKind.Changed));
        }
        finally
        {
            if (_reviewChangeDebounces.TryRemove(path, out var current) && ReferenceEquals(current, cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private string ReviewPath(DateOnly date) =>
        Path.Combine(ReviewsDirectory, date.ToString(ReviewDatePattern, CultureInfo.InvariantCulture) + ".md");

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, ReviewDatePattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
