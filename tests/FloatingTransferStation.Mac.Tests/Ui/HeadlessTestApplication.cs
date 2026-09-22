using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

namespace FloatingTransferStation.Mac.Tests.Ui;

public sealed class HeadlessTestApplication : Application
{
    public override void Initialize()
    {
        // headless 无连续渲染时钟，动画永不推进；关闭动效以保持收展状态确定性。
        FloatingTransferStation.Mac.MainWindow.MotionEnabled = false;
        Styles.Add(new FluentTheme());
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<HeadlessTestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
