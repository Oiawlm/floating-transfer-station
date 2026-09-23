namespace FloatingTransferStation.Services;

/// <summary>
/// 开机自启管理。显示状态读取登记；写入只发生在用户显式切换时，
/// 开发启动不得注册（与安装器管理的 HKCU Run 值共用同一格式）。
/// </summary>
public interface IStartupManager
{
    bool IsEnabled();

    /// <summary>启用登录自启；失败抛 IOException/UnauthorizedAccessException，由调用方反馈。</summary>
    void Enable();

    /// <summary>禁用登录自启；失败抛 IOException/UnauthorizedAccessException，由调用方反馈。</summary>
    void Disable();
}
