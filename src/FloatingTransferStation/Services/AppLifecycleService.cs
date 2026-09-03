namespace FloatingTransferStation.Services;

public sealed class AppLifecycleService : IDisposable
{
    private SingleInstanceGuard? _singleInstance;

    public bool TryStart(string mutexName = SingleInstanceGuard.ApplicationMutexName)
    {
        if (_singleInstance is not null)
        {
            return true;
        }

        return SingleInstanceGuard.TryAcquire(mutexName, out _singleInstance);
    }

    public void Dispose()
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
    }
}
