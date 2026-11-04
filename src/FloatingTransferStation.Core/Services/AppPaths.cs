namespace FloatingTransferStation.Services;

/// <summary>
/// 数据根内全部受管路径的单一布局来源：数据文件直接放在数据根
/// <c>&lt;所选&gt;/悬浮中转站</c> 里，插件目录结构性内嵌为 <c>数据根/plugins</c>
/// （无独立调整能力）。默认位置（无登记）＝<c>%LocalAppData%\悬浮中转站\</c>，
/// 即以 LocalAppData 为「所选目录」的同一形状。
/// </summary>
public sealed record AppPaths(
    string DataDirectory,
    string BoardFile,
    string SettingsFile,
    string PreferencesFile,
    string ImagesDirectory,
    string ReviewsDirectory,
    string PluginsDirectory,
    string PluginStateFile,
    string LaunchersFile,
    string LauncherIconsDirectory,
    string LauncherDiscoveriesFile)
{
    public static AppPaths CreateDefault(IDataDirectorySettings? settings = null)
    {
        // 登记读取按「受管形状优先、两个旧形状待迁移接受」统一解析：旧形登记在
        // 启动迁移失败时保旧运行，因此这里不能只认受管形状。
        var configured = DataDirectorySettings.NormalizeRegisteredDataDirectory(settings?.ReadDataDirectory());
        return FromDataDirectory(configured ?? DefaultDataDirectory());
    }

    /// <summary>默认数据根：以 LocalAppData 为「所选目录」的同一形状。</summary>
    public static string DefaultDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductIdentity.DisplayName);

    public static AppPaths ForTests(string dataDirectory) => FromDataDirectory(dataDirectory);

    public static AppPaths FromDataDirectory(string dataDirectory) => new(
        dataDirectory,
        Path.Combine(dataDirectory, "board.json"),
        Path.Combine(dataDirectory, "settings.json"),
        Path.Combine(dataDirectory, "preferences.json"),
        Path.Combine(dataDirectory, "images"),
        Path.Combine(dataDirectory, "reviews"),
        Path.Combine(dataDirectory, "plugins"),
        Path.Combine(dataDirectory, "plugins-state.json"),
        Path.Combine(dataDirectory, "launchers.json"),
        Path.Combine(dataDirectory, "launcher-icons"),
        Path.Combine(dataDirectory, "launcher-discoveries.json"));
}
