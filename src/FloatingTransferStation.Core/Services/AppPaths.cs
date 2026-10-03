namespace FloatingTransferStation.Services;

public sealed record AppPaths(
    string DataDirectory,
    string BoardFile,
    string SettingsFile,
    string PreferencesFile,
    string ImagesDirectory,
    string ReviewsDirectory,
    string PluginsDirectory,
    string PluginStateFile)
{
    public static AppPaths CreateDefault(IDataDirectorySettings? settings = null)
    {
        var configured = DataDirectorySettings.NormalizeManagedDataDirectory(settings?.ReadDataDirectory());
        var dataDirectory = configured ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "FloatingTransferStation", "Data")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.DisplayName,
                "Data"));
        return FromDataDirectory(dataDirectory);
    }

    public static AppPaths ForTests(string dataDirectory) => FromDataDirectory(dataDirectory);

    public static AppPaths FromDataDirectory(string dataDirectory) => new(
        dataDirectory,
        Path.Combine(dataDirectory, "board.json"),
        Path.Combine(dataDirectory, "settings.json"),
        Path.Combine(dataDirectory, "preferences.json"),
        Path.Combine(dataDirectory, "images"),
        Path.Combine(dataDirectory, "reviews"),
        Path.Combine(dataDirectory, "plugins"),
        Path.Combine(dataDirectory, "plugins-state.json"));

    /// <summary>
    /// 应用插件目录偏好的单一解析规则：null/空白或等于默认 → 默认（数据目录下 plugins）；
    /// 非完整限定路径、设备路径或落在当前数据目录之内（含 Data 形状内部）→ 回退默认并
    /// 返回告警文案（插件目录不能随受管数据目录一起被搬迁/清理误伤）；其余覆盖为用户目录。
    /// 只改 PluginsDirectory；PluginStateFile（启用状态）按 record 语义仍留在数据目录。
    /// </summary>
    public static (AppPaths Paths, string? Warning) ResolvePluginsDirectoryOverride(
        AppPaths paths,
        string? pluginsDirectoryOverride)
    {
        if (string.IsNullOrWhiteSpace(pluginsDirectoryOverride))
        {
            return (paths, null);
        }

        string normalized;
        try
        {
            if (!Path.IsPathFullyQualified(pluginsDirectoryOverride))
            {
                return (paths, $"插件目录必须是完整路径，已回退默认：{pluginsDirectoryOverride}");
            }

            normalized = Path.GetFullPath(pluginsDirectoryOverride).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return (paths, $"插件目录无效，已回退默认：{pluginsDirectoryOverride}");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(normalized, paths.PluginsDirectory, comparison))
        {
            // 默认目录本身就在数据目录之内：与默认同一路径的覆盖没有额外语义，
            // 先于“数据目录之内”判定，按默认处理（含大小写差异）。
            return (paths, null);
        }

        if (DataDirectorySettings.IsPathWithin(normalized, paths.DataDirectory, comparison))
        {
            return (paths, "插件目录不能位于数据目录之内，已回退默认。");
        }

        return (paths with { PluginsDirectory = normalized }, null);
    }
}
