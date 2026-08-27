namespace FloatingTransferStation.Services;

public sealed class AppLifecycleService : IDisposable
{
    private readonly StartupRegistrationService _startup;
    private SingleInstanceGuard? _singleInstance;

    public AppLifecycleService(StartupRegistrationService startup)
    {
        _startup = startup;
    }

    public static AppLifecycleService CreateDefault(string executablePath) =>
        new(StartupRegistrationService.CreateDefault(executablePath));

    public bool TryStart(string mutexName = SingleInstanceGuard.ApplicationMutexName)
    {
        if (_singleInstance is not null)
        {
            return true;
        }

        return SingleInstanceGuard.TryAcquire(mutexName, out _singleInstance);
    }

    public bool EnsureStartup(string executablePath) =>
        _startup.EnsureRegistered(executablePath);

    public void Dispose()
    {
        _singleInstance?.Dispose();
        _singleInstance = null;
    }
}
