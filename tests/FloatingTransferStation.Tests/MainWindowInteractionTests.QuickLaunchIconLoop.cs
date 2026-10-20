using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动图标真实闭环（#30 修订，Issue #206 实机根因修复）：Application 类
/// 「添加→提取→瓦片显示→重启取缓存」窗口级闭环（镜像 QuickLaunchPicker 的 AUMID
/// 闭环先例，补「合成可达、实机不可达」家族缺口）；提取异常不再毒化 pending
/// （等待者照常完成＋后续请求可恢复）；服务级 PNG 发布与 IconStamp 推进。
/// 本机（图标缓存未热/SHGetImageList 坏死环境）与 CI（主路径健康）都必须绿。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void IconLoop_Application_Notepad_ClosedLoopAcrossRestart()
    {
        var exePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(exePath))
        {
            Assert.Inconclusive("本机无 notepad.exe（Application 取图闭环无法验证）。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, exePath, "notepad", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            var tileIcon = WaitLauncherTileIcon(window, item, TimeSpan.FromSeconds(30));
            Assert.IsNotNull(tileIcon, "添加后应凭真实提取显示应用图标（瓦片图标非占位）。");

            // stamp 经 fire-and-forget 串行协议推进（线程池保存），轮询等待落定；
            // 断言对象=窗口目录内的条目实例（装载反序列化体，与构造入参非同实例）。
            var windowItem = window.LaunchersForTests().Items.Single(loaded => loaded.Id == item.Id);
            var stampDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (windowItem.IconStamp is null && DateTime.UtcNow < stampDeadline)
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            }

            Assert.IsNotNull(windowItem.IconStamp, "提取成功后 IconStamp 应推进（经目录串行协议持久化）。");
            var pngPath = Path.Combine(paths.LauncherIconsDirectory, item.IconKey + ".png");
            Assert.IsTrue(File.Exists(pngPath), "PNG 应已发布进磁盘缓存（tmp→Move 协议）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }

        // 重启：新窗口/新图标服务实例，仅凭持久化条目走「缓存读」分支取图（非重新提取）。
        var window2 = CreateLauncherWindowSync(directory, board, store, starter);
        try
        {
            window2.Show();
            window2.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window2, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window2);

            var restored = window2.LaunchersForTests().Items.Single(loaded => loaded.Id == item.Id);
            var tileIcon2 = WaitLauncherTileIcon(window2, restored, TimeSpan.FromSeconds(30));
            Assert.IsNotNull(tileIcon2, "重启后仅凭持久化条目应从磁盘缓存取回图标（瓦片图标非占位）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window2);
        }
    }

    [STATestMethod]
    public void IconLoop_RequestIcon_ExceptionCompletesWaitersAndRecovers()
    {
        var exePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Assert.IsTrue(File.Exists(exePath), "notepad.exe 应存在（异常毒化回归的前提）。");

        using var directory = new TestDirectory();
        var iconsDir = Path.Combine(directory.Root, "launcher-icons");
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = LauncherCatalog.LoadAsync(store, _ => { }).GetAwaiter().GetResult();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, exePath, "notepad", DateTimeOffset.UtcNow);
        catalog.AddRangeAsync([item]).GetAwaiter().GetResult();

        var uiDispatcher = Dispatcher.CurrentDispatcher;
        using var service = LauncherIconService.Start(catalog, iconsDir, uiDispatcher);

        // 第一次请求：注入过滤器外异常（此前缺陷=等待者永久悬挂、瓦片永远占位）。
        service.LoadOverrideForTests = _ =>
            throw new InvalidOperationException("毒化注入：异常必须完成等待者而非悬挂");
        var (firstResult, firstDone) = RequestIconAndWait(service, item, uiDispatcher, TimeSpan.FromSeconds(10));
        Assert.IsTrue(firstDone, "异常路径必须完成等待者（毒化缺陷=永不回调）。");
        Assert.IsNull(firstResult, "异常路径回调应为 null（保持占位语义）。");

        // 第二次请求：恢复真实提取——证明状态未被第一次失败毒化。
        service.LoadOverrideForTests = null;
        var (secondResult, secondDone) = RequestIconAndWait(service, item, uiDispatcher, TimeSpan.FromSeconds(30));
        Assert.IsTrue(secondDone, "恢复后的请求必须照常完成。");
        Assert.IsNotNull(secondResult, "恢复后的请求应真实提取成功（notepad.exe）。");
    }

    [STATestMethod]
    public void IconLoop_ServiceLevel_RealExe_PublishesPngAndAdvancesStamp()
    {
        var exePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        Assert.IsTrue(File.Exists(exePath), "notepad.exe 应存在（服务级闭环的前提）。");

        using var directory = new TestDirectory();
        var iconsDir = Path.Combine(directory.Root, "launcher-icons");
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = LauncherCatalog.LoadAsync(store, _ => { }).GetAwaiter().GetResult();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, exePath, "notepad", DateTimeOffset.UtcNow);
        catalog.AddRangeAsync([item]).GetAwaiter().GetResult();

        var uiDispatcher = Dispatcher.CurrentDispatcher;
        using var service = LauncherIconService.Start(catalog, iconsDir, uiDispatcher);
        var (icon, done) = RequestIconAndWait(service, item, uiDispatcher, TimeSpan.FromSeconds(30));
        Assert.IsTrue(done, "RequestIcon 回调应在时限内到达。");
        Assert.IsNotNull(icon, "真实 exe 目标应提取成功（主路径或直提取阶梯）。");
        Assert.IsTrue(File.Exists(Path.Combine(iconsDir, item.IconKey + ".png")), "PNG 应已发布。");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (item.IconStamp is null && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(uiDispatcher, TimeSpan.FromMilliseconds(20));
        }

        Assert.IsNotNull(item.IconStamp, "IconStamp 应在发布后推进（fire-and-forget 串行协议）。");
    }

    private static (BitmapSource? Icon, bool Done) RequestIconAndWait(
        LauncherIconService service,
        LauncherItem item,
        Dispatcher uiDispatcher,
        TimeSpan timeout)
    {
        BitmapSource? result = null;
        var done = false;
        service.RequestIcon(item, icon =>
        {
            result = icon;
            done = true;
        });

        var deadline = DateTime.UtcNow + timeout;
        while (!done && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(uiDispatcher, TimeSpan.FromMilliseconds(20));
        }

        return (result, done);
    }

    private static ImageSource? WaitLauncherTileIcon(MainWindow window, LauncherItem item, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            CompleteLayout(window);
            if (FindLauncherTile(window, item) is { } tile &&
                FindDescendant<Image>(tile)?.Source is { } icon)
            {
                return icon;
            }

            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));
        }

        return null;
    }
}
