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
            return DataDirectorySettings.NormalizeManagedDataDirectory(
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

/// <summary>数据目录登记写入的抽象，供应用内搬迁编排注入替身测试注册表失败路径。</summary>
public interface IDataDirectoryRegistration
{
    string? ReadDataDirectory();

    bool TryCommit(string dataDirectory, string dataParentDirectory);
}
