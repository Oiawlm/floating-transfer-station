using Microsoft.Win32;

namespace FloatingTransferStation.Services;

public interface IStartupValueStore
{
    void Set(string name, string value);
}

public sealed class WindowsStartupValueStore : IStartupValueStore
{
    public void Set(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true)
            ?? throw new IOException("The current-user startup registry key is unavailable.");
        key.SetValue(name, value, RegistryValueKind.String);
    }
}

public sealed class StartupRegistrationService
{
    private readonly string _installedDirectory;
    private readonly IStartupValueStore _values;

    public StartupRegistrationService(string installedDirectory, IStartupValueStore values)
    {
        _installedDirectory = Path.GetFullPath(installedDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        _values = values;
    }

    public static StartupRegistrationService CreateDefault(
        string executablePath,
        IStartupValueStore? values = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException(
                "An absolute executable path with a directory is required.",
                nameof(executablePath));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
            ?? throw new ArgumentException(
                "An executable path with a directory is required.",
                nameof(executablePath));
        return new StartupRegistrationService(directory, values ?? new WindowsStartupValueStore());
    }

    public bool EnsureRegistered(string executablePath)
    {
        var executable = Path.GetFullPath(executablePath);
        var executableDirectory = Path.GetDirectoryName(executable)?.TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(executableDirectory, _installedDirectory, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(executable), "悬浮中转站.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _values.Set(ProductIdentity.DisplayName, $"\"{executable}\"");
        return true;
    }
}
