using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Mac;

public sealed class App : Application
{
    private FileStream? _instanceLock;

    public override void Initialize()
    {
        Name = ProductIdentity.DisplayName;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var smokeIndex = Array.IndexOf(args, "--smoke-test");
            var smokeDirectory = smokeIndex >= 0 && smokeIndex + 1 < args.Length
                ? Path.GetFullPath(args[smokeIndex + 1]) : null;
            // Preview and smoke runs never touch the installed Windows application's data.
            var dataDirectory = smokeDirectory is not null
                ? Path.Combine(smokeDirectory, "Data")
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "Application Support", "FloatingTransferStation", "Data")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "FloatingTransferStation.MacPreview", "Data");
            try
            {
                Directory.CreateDirectory(dataDirectory);
                _instanceLock = new FileStream(Path.Combine(dataDirectory, ".instance.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                desktop.MainWindow = new MainWindow(AppPaths.FromDataDirectory(dataDirectory), smokeDirectory);
                desktop.Exit += (_, _) => _instanceLock?.Dispose();
            }
            catch (IOException)
            {
                desktop.Shutdown(1);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
