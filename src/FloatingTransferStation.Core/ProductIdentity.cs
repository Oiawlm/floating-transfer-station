namespace FloatingTransferStation;

public static class ProductIdentity
{
    public const string DisplayName = "悬浮中转站";
    public static string Version { get; } = typeof(ProductIdentity).Assembly.GetName().Version!.ToString(3);
    public const string SettingsRegistryKey = @"Software\FloatingTransferStation";
    public const string DataDirectoryRegistryValue = "DataDirectory";
    public const string DataParentDirectoryRegistryValue = "DataParentDirectory";

    // repo 地址单一来源：更新服务（releases/latest 检查、发布页回退）与设置窗页脚
    // GitHub 链接都从这里取值，禁止在 XAML/服务里再写字面量。
    public const string GitHubRepoHtmlUrl = "https://github.com/Oiawlm/floating-transfer-station";
    public const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/Oiawlm/floating-transfer-station/releases/latest";
}
