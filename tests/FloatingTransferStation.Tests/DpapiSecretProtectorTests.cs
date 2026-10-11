using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class DpapiSecretProtectorTests
{
    private const string Plaintext = "sk-real-key-never-appears-here";

    [TestMethod]
    public void ProtectThenUnprotect_RestoresPlaintext()
    {
        var protector = new DpapiSecretProtector();

        var cipher = protector.Protect(Plaintext);

        Assert.AreNotEqual(Plaintext, cipher, "密文不得等于明文。");
        Assert.IsFalse(cipher.Contains(Plaintext, StringComparison.Ordinal), "密文不得包含明文。");
        Assert.AreEqual(Plaintext, protector.Unprotect(cipher));
    }

    [TestMethod]
    public void Unprotect_WithGarbageBase64_ReturnsNullWithoutThrowing()
    {
        var protector = new DpapiSecretProtector();

        Assert.IsNull(protector.Unprotect("这不是 base64！！！"));
    }

    [TestMethod]
    public void Unprotect_WithTamperedCipher_ReturnsNullWithoutThrowing()
    {
        var protector = new DpapiSecretProtector();
        var cipher = protector.Protect(Plaintext);
        var bytes = Convert.FromBase64String(cipher);
        bytes[^1] ^= 0xFF;
        var tampered = Convert.ToBase64String(bytes);

        Assert.IsNull(protector.Unprotect(tampered), "被篡改的密文必须判为解密失败（按未配置降级）。");
    }
}
