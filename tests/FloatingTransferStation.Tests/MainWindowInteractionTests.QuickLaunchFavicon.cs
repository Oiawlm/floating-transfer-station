using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
/// Url 瓦片 favicon 抓取闭环（#30 修订，Issue #206）：fake handler 注入的离线验证——
/// 抓取成功后瓦片显示 favicon＋PNG 落盘＋stamp="ok"；编辑目标绕过负缓存重抓；启动
/// 重试轮每条目每进程一次；失败负缓存防抖（不再发请求）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void Favicon_UrlTile_FetchSuccess_ShowsFaviconAndPersists()
    {
        using var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", FakeResponse.Image(Png96()));
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var seeded = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.com/", "example.com", DateTimeOffset.UtcNow);
        MainWindow.FaviconClientFactoryForTests = () => new FaviconClient(handler);

        try
        {
            var window = CreateLauncherWindowSync(directory, board, store, starter, seeded);
            try
            {
                window.Show();
                window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
                ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
                CompleteLayout(window);

                var live = window.LaunchersForTests().Items.Single();
                var tileIcon = WaitLauncherTileIcon(window, live, TimeSpan.FromSeconds(20));
                Assert.IsNotNull(tileIcon, "favicon 抓取成功后瓦片应显示图标（非地球字形回退态）。");

                var stampDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (live.IconStamp != LauncherIconService.UrlIconStampOk && DateTime.UtcNow < stampDeadline)
                {
                    PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
                }

                Assert.AreEqual(LauncherIconService.UrlIconStampOk, live.IconStamp, "Url 条目 stamp 应推进为 \"ok\"。");
                Assert.IsTrue(
                    File.Exists(Path.Combine(paths.LauncherIconsDirectory, live.IconKey + ".png")),
                    "favicon PNG 应发布进既有 launcher-icons 磁盘缓存。");
            }
            finally
            {
                CloseWindowWithoutSaving(window);
            }
        }
        finally
        {
            MainWindow.FaviconClientFactoryForTests = null;
        }
    }

    [STATestMethod]
    public void Favicon_EditTarget_RefetchesBypassingNegativeCache()
    {
        using var handler = new FakeFaviconHandler();
        handler.Route("https://old.example.com/favicon.ico", FakeResponse.Image(Png32()));
        handler.Route("https://new.example.com/favicon.ico", FakeResponse.Image(Png96()));
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var seeded = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://old.example.com/", "old", DateTimeOffset.UtcNow);
        MainWindow.FaviconClientFactoryForTests = () => new FaviconClient(handler);

        try
        {
            var window = CreateLauncherWindowSync(directory, board, store, starter, seeded);
            try
            {
                window.Show();
                window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
                ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
                Keyboard.ClearFocus();
                CompleteLayout(window);
                var live = window.LaunchersForTests().Items.Single();

                // 编辑目标到新地址（同 _showEditTargetDialog 注入先例；Url 对话框字段=TargetInputBox）。
                window._showEditTargetDialog = dialog =>
                {
                    Assert.AreEqual("https://old.example.com/", ((System.Windows.Controls.TextBox)dialog.FindName("TargetInputBox")!).Text);
                    ((System.Windows.Controls.TextBox)dialog.FindName("TargetInputBox")!).Text = "https://new.example.com/";
                    dialog.SaveButton_ClickForTests();
                    return true;
                };
                window.RaiseEvent(NewKeyEventArgs(window, Key.Left));
                window.RaiseEvent(NewModifiedKeyEventArgs(window, Key.Enter, ModifierKeys.Alt));
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));

                Assert.AreEqual("https://new.example.com/", live.TargetPath, "目标已更新。");
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    CompleteLayout(window);
                    if (FindLauncherTile(window, live) is { } tile &&
                        FindDescendant<Image>(tile)?.Source is BitmapSource { } source &&
                        source.PixelWidth >= 96)
                    {
                        break;
                    }

                    PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));
                }

                var tileIcon = WaitLauncherTileIcon(window, live, TimeSpan.FromSeconds(10));
                Assert.IsNotNull(tileIcon, "编辑目标后新地址 favicon 应重抓成功（96px 帧上屏）。");
                Assert.IsTrue(
                    handler.RequestedUrls.Any(url => url.StartsWith("https://new.example.com/", StringComparison.Ordinal)),
                    "新地址确实发起了抓取（绕过负缓存）。");
            }
            finally
            {
                CloseWindowWithoutSaving(window);
            }
        }
        finally
        {
            MainWindow.FaviconClientFactoryForTests = null;
        }
    }

    [STATestMethod]
    public void Favicon_StartupRetryRound_OncePerEntryPerProcess()
    {
        using var handler = new FakeFaviconHandler();
        handler.Route("https://example.com/favicon.ico", FakeResponse.Image(Png96()));
        using var directory = new TestDirectory();
        var iconsDir = Path.Combine(directory.Root, "launcher-icons");
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = LauncherCatalog.LoadAsync(store, _ => { }).GetAwaiter().GetResult();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.com/", "example.com", DateTimeOffset.UtcNow);
        catalog.AddRangeAsync([item]).GetAwaiter().GetResult();

        using var client = new FaviconClient(handler);
        using var scheduler = new FaviconFetchScheduler();
        client.OriginPermitFactory = scheduler.AcquireAsync;
        var uiDispatcher = Dispatcher.CurrentDispatcher;
        using var service = LauncherIconService.Start(catalog, iconsDir, uiDispatcher, client, scheduler);

        service.RunStartupRetryRoundOnce();
        var pngPath = Path.Combine(iconsDir, item.IconKey + ".png");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!File.Exists(pngPath) && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(uiDispatcher, TimeSpan.FromMilliseconds(50));
        }

        Assert.IsTrue(File.Exists(pngPath), "启动重试轮应完成无图标条目的抓取发布。");
        var requestsAfterFirstRound = handler.Requests.Count;
        Assert.IsTrue(requestsAfterFirstRound > 0, "首轮确实发起请求。");

        service.RunStartupRetryRoundOnce();
        PumpDispatcherFor(uiDispatcher, TimeSpan.FromMilliseconds(300));
        Assert.AreEqual(requestsAfterFirstRound, handler.Requests.Count, "每条目每进程至多一次（已缓存的条目不再请求）。");
    }

    [STATestMethod]
    public void Favicon_Failure_NegativeCacheDebouncesWithinTtl()
    {
        using var handler = new FakeFaviconHandler();
        using var directory = new TestDirectory();
        var iconsDir = Path.Combine(directory.Root, "launcher-icons");
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var catalog = LauncherCatalog.LoadAsync(store, _ => { }).GetAwaiter().GetResult();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://dead.example.com/", "dead", DateTimeOffset.UtcNow);
        catalog.AddRangeAsync([item]).GetAwaiter().GetResult();

        using var client = new FaviconClient(handler);
        using var scheduler = new FaviconFetchScheduler();
        client.OriginPermitFactory = scheduler.AcquireAsync;
        var uiDispatcher = Dispatcher.CurrentDispatcher;
        using var service = LauncherIconService.Start(catalog, iconsDir, uiDispatcher, client, scheduler);

        var (firstIcon, firstDone) = RequestIconAndWait(service, item, uiDispatcher, TimeSpan.FromSeconds(20));
        Assert.IsTrue(firstDone, "失败也必须完成等待者（占位回退）。");
        Assert.IsNull(firstIcon, "阶梯全灭＝无图标（字形回退态）。");
        var requestsAfterFailure = handler.Requests.Count;
        Assert.IsTrue(requestsAfterFailure > 0, "首次确有请求。");

        var (secondIcon, secondDone) = RequestIconAndWait(service, item, uiDispatcher, TimeSpan.FromSeconds(10));
        Assert.IsTrue(secondDone, "负缓存命中也必须完成等待者。");
        Assert.IsNull(secondIcon);
        Assert.AreEqual(requestsAfterFailure, handler.Requests.Count, "TTL 内负缓存命中＝零新请求（防抖）。");
    }

    private static byte[] Png96() => TestPng(96, 96);

    private static byte[] Png32() => TestPng(32, 32);

    /// <summary>
    /// 发布证据（FTS_QUICKLAUNCH_EVIDENCE_DIR 设置时实拍）：Url 瓦片 favicon 成功态
    /// 与失败回退字形态 × 浅/深主题（#30 修订，Issue #206；沿用 QuickLaunchEvidence
    /// 管线）。fake handler 注入，零真实网络。
    /// </summary>
    [STATestMethod]
    public void Favicon_Evidence_CaptureTwoStatesBothThemes()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_QUICKLAUNCH_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_QUICKLAUNCH_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var handler = new FakeFaviconHandler();
        handler.Route("https://ok.example.com/favicon.ico", FakeResponse.Image(Png96()));
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = new[]
        {
            new LauncherItem(Guid.NewGuid(), LauncherKind.Url, "https://ok.example.com/", "抓取成功站点", DateTimeOffset.UtcNow),
            new LauncherItem(Guid.NewGuid(), LauncherKind.Url, "https://dead.example.com/", "回退站点", DateTimeOffset.UtcNow),
        };
        MainWindow.FaviconClientFactoryForTests = () => new FaviconClient(handler);

        try
        {
            var window = CreateLauncherWindowSync(directory, board, store, starter, items);
            try
            {
                window.Show();
                window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
                ExpandCategory(window, QuickLaunchMigration.LauncherCategory);

                // 等待成功条目 favicon 上屏（回退条目 404 保持字形）。
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    CompleteLayout(window);
                    var okItem = window.LaunchersForTests().Items[0];
                    if (FindLauncherTile(window, okItem) is { } tile &&
                        FindDescendant<Image>(tile)?.Source is not null)
                    {
                        break;
                    }

                    PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(50));
                }

                CompleteLayout(window);
                foreach (var (theme, suffix) in new[]
                         {
                             (DesignTheme.Light, "light"),
                             (DesignTheme.Dark, "dark"),
                         })
                {
                    DesignThemeManager.Apply(window, theme);
                    CompleteLayout(window);
                    SaveVisualEvidence(
                        (Border)window.FindName("WindowShell")!,
                        $"quicklaunch-favicon-{suffix}.png",
                        "FTS_QUICKLAUNCH_EVIDENCE_DIR");
                }
            }
            finally
            {
                CloseWindowWithoutSaving(window);
            }
        }
        finally
        {
            MainWindow.FaviconClientFactoryForTests = null;
        }
    }

    /// <summary>真实可解码 PNG（WPF 编码器合成不透明色块；声明尺寸=真实像素尺寸）。</summary>
    private static byte[] TestPng(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            // Pbgra32 实心橙块（证据截图肉眼可辨）。
            pixels[offset] = 0x1E;
            pixels[offset + 1] = 0x8A;
            pixels[offset + 2] = 0xF0;
            pixels[offset + 3] = 0xFF;
        }

        var source = BitmapSource.Create(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private sealed class FakeFaviconHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpResponseMessage> _routes = [];

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestedUrls => Requests.Select(request => request.RequestUri!.ToString()).ToList();

        public void Route(string url, HttpResponseMessage response) => _routes[url] = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            if (_routes.TryGetValue(url, out var routed))
            {
                var bytes = routed.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                var copy = new HttpResponseMessage(routed.StatusCode) { Content = new ByteArrayContent(bytes) };
                copy.Content.Headers.ContentType = routed.Content.Headers.ContentType;
                return Task.FromResult(copy);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) });
        }
    }

    private static class FakeResponse
    {
        public static HttpResponseMessage Image(byte[] bytes)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new("image/png");
            return response;
        }
    }
}
