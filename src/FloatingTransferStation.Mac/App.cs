using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Mac;

public sealed class App : Application
{
    private FileStream? _instanceLock;

    public override void Initialize()
    {
        Name = ProductIdentity.DisplayName;
        // 不固定亮色：默认跟随系统亮暗；--preview-theme=dark|light 仅供预览强制指定。
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                // Keep the last-resort UI-thread failure from taking the app down;
                // known failure paths report through the status line instead.
                args.Handled = true;
            };
            var args = desktop.Args ?? [];
            var smokeIndex = Array.IndexOf(args, "--smoke-test");
            var smokeDirectory = smokeIndex >= 0 && smokeIndex + 1 < args.Length
                ? Path.GetFullPath(args[smokeIndex + 1]) : null;
            var themeIndex = Array.IndexOf(args, "--preview-theme");
            if (themeIndex >= 0 && themeIndex + 1 < args.Length &&
                string.Equals(args[themeIndex + 1], "dark", StringComparison.OrdinalIgnoreCase))
            {
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            }
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                desktop.Shutdown(1);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
