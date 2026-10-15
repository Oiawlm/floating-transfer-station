using System.Runtime.InteropServices;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Launcher;

/// <summary>
/// 打包应用激活的 COM 实现（#30）：IApplicationActivationManager.ActivateApplication
/// （AO_NONE）；RPC_E_CHANGED_MODE 等失败原样携带 HRESULT 交 Core 决策（限定回退
/// 类别见 AppLauncher）。绝不直接启动打包应用的 exe。
/// </summary>
internal sealed class ShellPackagedAppActivator
{
    public PackagedAppActivationResult Activate(string appUserModelId, string? arguments)
    {
        try
        {
            var managerType = Type.GetTypeFromCLSID(ShellInterop.ClsidApplicationActivationManager);
            if (managerType is null ||
                Activator.CreateInstance(managerType) is not ShellInterop.IApplicationActivationManager manager)
            {
                return PackagedAppActivationResult.Failure(unchecked((int)0x80004001));
            }

            var hresult = manager.ActivateApplication(
                appUserModelId,
                arguments ?? string.Empty,
                ShellInterop.ActivateOptions.None,
                out var processId);
            return hresult == 0
                ? PackagedAppActivationResult.Success(processId)
                : PackagedAppActivationResult.Failure(hresult);
        }
        catch (COMException exception)
        {
            return PackagedAppActivationResult.Failure(exception.HResult);
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            return PackagedAppActivationResult.Failure(unchecked((int)0x80004005));
        }
    }
}
