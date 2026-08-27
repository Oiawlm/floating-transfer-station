namespace FloatingTransferStation.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    public const string ApplicationMutexName = @"Local\FloatingTransferStation.App";
    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
