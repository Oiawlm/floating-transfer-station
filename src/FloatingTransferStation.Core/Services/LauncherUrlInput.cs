namespace FloatingTransferStation.Services;

/// <summary>网页地址归一化结果（#30 修订，Issue #179）：Success=true 时 Url 为
/// AbsoluteUri 规范形、DefaultName 为瓦片默认名（超长由 LauncherItem 构造截断）；
/// Success=false 时 Error 为对话框内联文案、不建条目。</summary>
public sealed record LauncherUrlNormalization(
    bool Success,
    string Url,
    string DefaultName,
    string? Error)
{
    public static LauncherUrlNormalization Accepted(string url, string defaultName) =>
        new(true, url, defaultName, null);

    public static LauncherUrlNormalization Rejected(string error) =>
        new(false, string.Empty, string.Empty, error);
}

/// <summary>
/// 网页地址输入归一化（#30 修订，Issue #179）：创建入口的唯一 ingress（与
/// LauncherFileImport 同层的纯静态类型，零网络）。算法＝trim → 含 "://" 原样解析、
/// 否则前缀 https:// → 仅接受 http/https → 输出 AbsoluteUri 规范形。规范化在创建
/// 入口完成 ⇒ TargetPath 恒为规范形 ⇒ 目录 DedupKey 零改动即得稳定去重；不得把
/// URL 规范化下沉到 LauncherIconKey（其同时是 DedupKey 组成部分）。
/// </summary>
public static class LauncherUrlInput
{
    private static readonly System.Globalization.IdnMapping Idn = new();

    public static LauncherUrlNormalization Normalize(string? input)
    {
        var trimmed = input?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return LauncherUrlNormalization.Rejected("请输入网页地址。");
        }

        var candidate = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"https://{trimmed}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return LauncherUrlNormalization.Rejected("网页地址格式不正确。");
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return LauncherUrlNormalization.Rejected("仅支持 http 或 https 网页地址。");
        }

        return LauncherUrlNormalization.Accepted(uri.AbsoluteUri, DefaultName(uri));
    }

    /// <summary>默认名＝主机 IDN 解码（中文域名友好）去 www. 前缀；非默认端口补
    /// :port（剥除后为空则回退原主机，不出空名）。.NET 10 实测 Uri.IdnHost 返回
    /// punycode 形（不回解码 Unicode），显式经 IdnMapping 还原；非 IDN 主机解码
    /// 异常按原样承载（fail-open，不因名称解码拒绝合法地址）。</summary>
    private static string DefaultName(Uri uri)
    {
        var host = uri.IdnHost;
        try
        {
            host = Idn.GetUnicode(host);
        }
        catch (ArgumentException)
        {
        }

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Length > "www.".Length)
        {
            host = host["www.".Length..];
        }

        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }
}
