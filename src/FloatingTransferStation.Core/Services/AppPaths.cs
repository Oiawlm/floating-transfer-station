namespace FloatingTransferStation.Services;

public sealed record AppPaths(
    string DataDirectory,
    string BoardFile,
    string SettingsFile,
    string ImagesDirectory)
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
        Path.Combine(dataDirectory, "images"));
}
