using System.Windows;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 设置窗口与主窗口之间的宿主契约：读取当前偏好、立即应用并异步持久化、
/// 提供自启管理与数据目录，以及触发主窗既有的保存退出序列（不得绕过冲刷语义）。
/// </summary>
public interface ISettingsHost
{
    Window HostWindow { get; }
    DesignTheme CurrentTheme { get; }
    AppPreferences CurrentPreferences { get; }
    IStartupManager StartupManager { get; }
    string DataDirectory { get; }
    void ApplyPreferences(AppPreferences preferences);
    void RequestApplicationExit();
}
