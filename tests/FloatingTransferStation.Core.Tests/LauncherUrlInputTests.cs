using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 网页地址输入归一化矩阵（#30 修订，Issue #179）：创建入口唯一 ingress——
/// trim → 含 "://" 原样解析、否则前缀 https:// → 仅接受 http/https →
/// AbsoluteUri 规范形输出；默认名=IdnHost（去 www. 前缀、非默认端口补 :port）。
/// 归一化在创建入口完成 ⇒ TargetPath 恒为规范形 ⇒ 目录 DedupKey 零改动即得稳定去重。
/// </summary>
[TestClass]
public sealed class LauncherUrlInputTests
{
    [TestMethod]
    public void Normalize_WithoutScheme_PrependsHttps()
    {
        var result = LauncherUrlInput.Normalize("github.com");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("https://github.com/", result.Url, "无协议输入补 https://，规范形补尾斜杠。");
        Assert.AreEqual("github.com", result.DefaultName);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void Normalize_TrimsSurroundingWhitespace()
    {
        var result = LauncherUrlInput.Normalize("  https://example.com  ");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("https://example.com/", result.Url);
    }

    [TestMethod]
    public void Normalize_CaseDifferences_ConvergeToSameCanonicalForm()
    {
        var upper = LauncherUrlInput.Normalize("HTTP://Example.COM/Path");
        var lower = LauncherUrlInput.Normalize("http://example.com/Path");

        Assert.IsTrue(upper.Success);
        Assert.AreEqual("http://example.com/Path", upper.Url, "scheme 与域名大小写规范化（路径大小写保留）。");
        Assert.AreEqual(upper.Url, lower.Url, "大小写差异输入收敛同一规范形（去重稳定性）。");
        Assert.AreEqual(upper.DefaultName, lower.DefaultName);
    }

    [TestMethod]
    public void Normalize_ExplicitHttpScheme_IsKept()
    {
        var result = LauncherUrlInput.Normalize("http://example.com");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("http://example.com/", result.Url, "显式 http 不被改写为 https。");
    }

    [TestMethod]
    public void Normalize_NonHttpScheme_IsRejected()
    {
        var result = LauncherUrlInput.Normalize("ftp://example.com");

        Assert.IsFalse(result.Success, "含 :// 且非 http/https（ftp）拒绝。");
        StringAssert.Contains(result.Error, "http");
    }

    [TestMethod]
    public void Normalize_CustomSchemeFile_IsRejected()
    {
        var result = LauncherUrlInput.Normalize("file:///C:/Windows");

        Assert.IsFalse(result.Success, "自定义协议（file:）不接受。");
        StringAssert.Contains(result.Error, "http");
    }

    [TestMethod]
    public void Normalize_EmptyOrWhitespace_IsRejected()
    {
        Assert.IsFalse(LauncherUrlInput.Normalize(null).Success, "null 输入拒绝。");
        Assert.IsFalse(LauncherUrlInput.Normalize(string.Empty).Success, "空串拒绝。");
        Assert.IsFalse(LauncherUrlInput.Normalize("   ").Success, "纯空白拒绝。");
    }

    [TestMethod]
    public void Normalize_GarbageWithoutScheme_IsRejected()
    {
        var result = LauncherUrlInput.Normalize("abc def");

        Assert.IsFalse(result.Success, "无 :// 的垃圾输入补全后解析失败拒绝。");
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public void Normalize_EmptySchemeAuthority_IsRejected()
    {
        var result = LauncherUrlInput.Normalize("https://");

        Assert.IsFalse(result.Success, "仅 scheme 无主机拒绝。");
    }

    [TestMethod]
    public void Normalize_CanonicalForm_StripsDefaultPortAndAppendsSlash()
    {
        var result = LauncherUrlInput.Normalize("https://example.com:443");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("https://example.com/", result.Url, "默认端口剥离＋补尾斜杠（AbsoluteUri 规范形）。");
    }

    [TestMethod]
    public void Normalize_CanonicalForm_PreservesPathQueryAndFragment()
    {
        var result = LauncherUrlInput.Normalize("example.com/docs/guide?q=1#top");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("https://example.com/docs/guide?q=1#top", result.Url);
    }

    [TestMethod]
    public void Normalize_DefaultName_StripsWwwPrefix()
    {
        var result = LauncherUrlInput.Normalize("https://www.example.com");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("example.com", result.DefaultName, "默认名去 www. 前缀。");
    }

    [TestMethod]
    public void Normalize_DefaultName_KeepsWwwHostWhenNothingRemains()
    {
        var result = LauncherUrlInput.Normalize("https://www.");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("www.", result.DefaultName, "剥除后为空则回退原主机（不出空名）。");
    }

    [TestMethod]
    public void Normalize_DefaultName_AppendsNonDefaultPortOnly()
    {
        var ported = LauncherUrlInput.Normalize("http://example.com:8080");
        var defaultPort = LauncherUrlInput.Normalize("https://example.com:443");

        Assert.IsTrue(ported.Success);
        Assert.AreEqual("example.com:8080", ported.DefaultName, "非默认端口补 :port。");
        Assert.AreEqual("example.com", defaultPort.DefaultName, "默认端口不补。");
    }

    [TestMethod]
    public void Normalize_HostWithPort_ParsesAfterPrefixCompletion()
    {
        var result = LauncherUrlInput.Normalize("localhost:8080");

        Assert.IsTrue(result.Success, "localhost:8080 补全后有效（含冒号但非 ://）。");
        Assert.AreEqual("https://localhost:8080/", result.Url);
        Assert.AreEqual("localhost:8080", result.DefaultName);
    }

    [TestMethod]
    public void Normalize_IdnHost_UnicodeDefaultName()
    {
        var result = LauncherUrlInput.Normalize("https://例子.com");

        Assert.IsTrue(result.Success);
        Assert.AreEqual("https://例子.com/", result.Url, "Unicode 主机按 IRI 规范形保留（补尾斜杠；.NET 10 实测 AbsoluteUri 不转 punycode）。");
        Assert.AreEqual("例子.com", result.DefaultName, "默认名经 IdnMapping 解码（.NET 10 实测 IdnHost 返回 punycode，需显式还原）。");
        Assert.AreEqual("例子.com", LauncherUrlInput.Normalize("https://xn--fsqu00a.com/").DefaultName,
            "punycode 记法输入的默认名收敛同一解码形。");
    }
}
