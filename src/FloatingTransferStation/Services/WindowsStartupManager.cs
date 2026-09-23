using System.Security;
using FloatingTransferStation.Services;
using Microsoft.Win32;

namespace FloatingTransferStation.Services;

/// <summary>
/// Windows 开机自启：读写 HKCU …\CurrentVersion\Run。
/// 值名与带引号路径格式和 installer/悬浮中转站.iss 完全一致；
/// 仅在用户显式切换时写入（开发启动不注册），显示状态始终只读登记。
/// </summary>
public sealed class WindowsStartupManager : IStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string BuildRunCommand(string executablePath) =>
        $"\"{executablePath ?? throw new ArgumentNullException(nameof(executablePath))}\"";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var registered = key?.GetValue(ProductIdentity.DisplayName) as string;
            return string.Equals(
                registered,
                BuildRunCommand(Environment.ProcessPath ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new IOException("Unable to open the startup registry key.");
        key.SetValue(
            ProductIdentity.DisplayName,
            BuildRunCommand(Environment.ProcessPath ?? string.Empty),
            RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ProductIdentity.DisplayName, throwOnMissingValue: false);
    }
}
