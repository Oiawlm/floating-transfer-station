using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace FloatingTransferStation.Mac.Tests.Ui;

public sealed class HeadlessTestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<HeadlessTestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
