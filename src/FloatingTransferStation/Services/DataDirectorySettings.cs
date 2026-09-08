using System.Security;
using Microsoft.Win32;

namespace FloatingTransferStation.Services;

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
