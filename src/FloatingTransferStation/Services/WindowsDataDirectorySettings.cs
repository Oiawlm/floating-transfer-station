using System.Security;
using Microsoft.Win32;

namespace FloatingTransferStation.Services;

/// <summary>
/// 数据目录登记（HKCU Software\FloatingTransferStation）的读写：应用启动与安装器/
/// 卸载器共用同一组值（DataDirectory + DataParentDirectory），形状契约见
/// <see cref="DataDirectorySettings"/>。写入顺序与回滚语义和安装器
/// WriteDataDirectoryRegistration 一致：先父目录后数据目录，任一失败恢复两值原状。
/// </summary>
public sealed class WindowsDataDirectorySettings : IDataDirectorySettings, IDataDirectoryRegistration
{
    public string? ReadDataDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProductIdentity.SettingsRegistryKey);
            // 统一入口：受管形状优先、两个旧形状作为「待迁移旧形」接受——旧形登记
            // 在启动归一失败时保旧运行，读取不能只认受管形状。
            return DataDirectorySettings.NormalizeRegisteredDataDirectory(
                key?.GetValue(ProductIdentity.DataDirectoryRegistryValue) as string);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public bool TryCommit(string dataDirectory, string dataParentDirectory)
    {
        string? previousDataDirectory = null;
        string? previousDataParent = null;
        var hadDataDirectory = false;
        var hadDataParent = false;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(ProductIdentity.SettingsRegistryKey))
            {
                if (key is null)
                {
                    return false;
                }

                previousDataDirectory = key.GetValue(ProductIdentity.DataDirectoryRegistryValue) as string;
                hadDataDirectory = previousDataDirectory is not null;
                previousDataParent = key.GetValue(ProductIdentity.DataParentDirectoryRegistryValue) as string;
                hadDataParent = previousDataParent is not null;
                // 与安装器同序：先写 DataParentDirectory 再写 DataDirectory。
                key.SetValue(ProductIdentity.DataParentDirectoryRegistryValue, dataParentDirectory, RegistryValueKind.String);
                key.SetValue(ProductIdentity.DataDirectoryRegistryValue, dataDirectory, RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
        {
            Restore(ProductIdentity.DataDirectoryRegistryValue, previousDataDirectory, hadDataDirectory);
            Restore(ProductIdentity.DataParentDirectoryRegistryValue, previousDataParent, hadDataParent);
            return false;
        }
    }

    private static void Restore(string valueName, string? previousValue, bool existed)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProductIdentity.SettingsRegistryKey);
            if (key is null)
            {
                return;
            }

            if (existed && previousValue is not null)
            {
                key.SetValue(valueName, previousValue, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
        {
            // 回滚尽力而为：登记不完整时下一次读取按形状校验回退，不会指错目录。
        }
    }
}
