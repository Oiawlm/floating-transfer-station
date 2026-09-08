namespace FloatingTransferStation;

public static class ProductIdentity
{
    public const string DisplayName = "悬浮中转站";
    public static string Version { get; } = typeof(ProductIdentity).Assembly.GetName().Version!.ToString(3);
    public const string SettingsRegistryKey = @"Software\FloatingTransferStation";
    public const string DataDirectoryRegistryValue = "DataDirectory";
}
