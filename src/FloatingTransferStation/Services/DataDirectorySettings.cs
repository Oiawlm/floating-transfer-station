using System.Security;
using Microsoft.Win32;

namespace FloatingTransferStation.Services;

public interface IDataDirectorySettings
{
    string? ReadDataDirectory();
}

public sealed class WindowsDataDirectorySettings : IDataDirectorySettings
{
    public string? ReadDataDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProductIdentity.SettingsRegistryKey);
            return DataDirectorySettings.NormalizeManagedDataDirectory(
                key?.GetValue(ProductIdentity.DataDirectoryRegistryValue) as string);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}

public static class DataDirectorySettings
{
    public static string? NormalizeManagedDataDirectory(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
            {
                return null;
            }

            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var owner = Directory.GetParent(full);
            return owner is not null &&
                   string.Equals(Path.GetFileName(full), "Data", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(owner.Name, ProductIdentity.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
