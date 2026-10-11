using System.Security.Cryptography;
using System.Text;

namespace FloatingTransferStation.Services;

/// <summary>
/// ISecretProtector 的 Windows DPAPI（CurrentUser）实现：密文与本机当前用户绑定，
/// 随数据目录迁到别的机器/用户后解密必然失败（Unprotect 返回 null，按未配置降级）。
/// 不使用自定义熵——熵只能是编译期常数，不构成额外防线。
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser));

    public string? Unprotect(string ciphertext)
    {
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
