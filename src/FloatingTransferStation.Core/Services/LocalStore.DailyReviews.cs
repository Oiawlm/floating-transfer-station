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
        if (!HasRecordedContent(content))
        {
            return new DailyReviewDocument(date, string.Empty, false, Fingerprint(string.Empty));
        }

        return new DailyReviewDocument(date, content, true, Fingerprint(content));
    }

    /// <summary>
    /// 枚举「有文字记录」的日期（升序）：与 <see cref="LoadAsync"/> 的 Exists
    /// 判定共用 <see cref="HasRecordedContent"/>——空白文件视为无记录（唯一判定源）。
    /// 非法文件名与非 .md 文件跳过；枚举与读取之间文件被删/占用时按无记录跳过
    /// （可用性查询对瞬时 IO 竞态取降级语义，与 watcher 防抖的 IO 容忍同族）。
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> EnumerateRecordedDatesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(ReviewsDirectory))
        {
            return Array.Empty<DateOnly>();
        }

        var dates = new List<DateOnly>();
        foreach (var path in Directory.EnumerateFiles(ReviewsDirectory, "*.md"))
        {
            if (!TryParseDate(Path.GetFileNameWithoutExtension(path), out var date))
            {
                continue;
            }

            try
            {
                var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (HasRecordedContent(content))
                {
                    dates.Add(date);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 文件在枚举后、读取前消失或被占用：视同无记录，不阻断整表。
            }
        }

        dates.Sort();
        return dates;
    }

    public async Task SaveAsync(
        DateOnly date,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!HasRecordedContent(content))
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

    /// <summary>「有无文字记录」的唯一判定源：LoadAsync 的 Exists 与日期枚举共用。</summary>
    private static bool HasRecordedContent(string content) => !string.IsNullOrWhiteSpace(content);

    private static string Fingerprint(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
