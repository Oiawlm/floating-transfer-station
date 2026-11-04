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

    // 联系方式单一来源（2026-11-05）：微信号与二维码位图路径只在这里声明——
    // 设置窗页脚关于区从代码后置取值（XAML/视图层零字面量副本，锁定测试
    // LifecycleTests.ReleaseMetadata.WeChatContact_IsSingleSourcedFromProductIdentity）；
    // README「联系与反馈」段是对外发布面，展示值与此处一致由同一测试互锁。
    public const string WeChatId = "niubizhe_gengzhudong";

    // 应用内二维码位图：程序集内资源路径（不含 pack 前缀与程序集名——前者由视图
    // 按 pack 规范组合，后者运行时从承载程序集推导，均不在此硬编码第二份）；
    // 页脚缩略图与放大层共用同一冻结位图实例。
    public const string WeChatQrResourcePath = "/Resources/WeChatQr.png";
}
