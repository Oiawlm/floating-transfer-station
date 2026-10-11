namespace FloatingTransferStation.Services;

/// <summary>
/// 机密字符串保护契约（1.27.0 起，首个消费者是 DeepSeek API Key）：
/// 明文只允许在内存中经 Protect 转为密文、经 Unprotect 取回；
/// 持久化侧只保存密文。Unprotect 失败（密文损坏或来自别的机器/用户）
/// 返回 null 而不抛异常，由消费方按「未配置」降级处理。
/// </summary>
public interface ISecretProtector
{
    /// <summary>加密明文，返回可持久化的密文（base64）。</summary>
    string Protect(string plaintext);

    /// <summary>解密密文；失败返回 null（按未配置处理），不抛异常。</summary>
    string? Unprotect(string ciphertext);
}
