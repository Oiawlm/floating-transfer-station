using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 启动瓦片占位字形状态派生（#30）：占位层只在图标未到达（Image.Tag 为 null＝
/// 提取/favicon 抓取在途或失败）时可见，图标回填（Tag 非 null）即收起为
/// Collapsed——透明底图标不再透出下层字形。两个模板（Application/Url）同锁；
/// 含 LauncherTileIcon_Loaded 真实装载链驱动用例（#13 精神）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void LauncherIcon_PlaceholderVisibleWhileIconPending()
    {
        using var host = new LauncherPlaceholderHost();
        host.ShowLauncherSurface();

        foreach (var item in host.Window.LaunchersForTests()!.Items)
        {
            var tile = FindLauncherTile(host.Window, item);
            Assert.IsNotNull(tile, $"瓦片未 realize：{item.DisplayName}。");
            var placeholder = FindLauncherPlaceholderGlyph(tile);
            Assert.IsNotNull(placeholder, "模板应含占位字形层。");
            Assert.IsNull(FindDescendant<Image>(tile)!.Source, "图标在途/失败时 Image.Source 为空。");
            Assert.AreEqual(
                Visibility.Visible,
                placeholder.Visibility,
                $"占位字形在图标到达前必须可见（{item.Kind} 提取在途/失败回退态）。");
        }
    }

    [STATestMethod]
    public void LauncherIcon_PlaceholderCollapsesWhenIconArrives()
    {
        using var host = new LauncherPlaceholderHost();
        host.ShowLauncherSurface();

        foreach (var item in host.Window.LaunchersForTests()!.Items)
        {
            var tile = FindLauncherTile(host.Window, item);
            Assert.IsNotNull(tile, $"瓦片未 realize：{item.DisplayName}。");
            var placeholder = FindLauncherPlaceholderGlyph(tile);
            Assert.IsNotNull(placeholder, "模板应含占位字形层。");
            Assert.AreEqual(Visibility.Visible, placeholder.Visibility, "前置：图标到达前占位可见。");

            FindDescendant<Image>(tile)!.Tag = DecodedTestIcon();
            Assert.AreEqual(
                Visibility.Collapsed,
                placeholder.Visibility,
                $"图标到达（Image.Tag 非 null）后占位层应收起（{item.Kind} 模板）——透明底图标不得透出字形。");
        }
    }

    [STATestMethod]
    public void LauncherIcon_TagClearedRestoresPlaceholderThenRefillCollapses()
    {
        using var host = new LauncherPlaceholderHost();
        host.ShowLauncherSurface();

        foreach (var item in host.Window.LaunchersForTests()!.Items)
        {
            var tile = FindLauncherTile(host.Window, item);
            Assert.IsNotNull(tile, $"瓦片未 realize：{item.DisplayName}。");
            var image = FindDescendant<Image>(tile)!;
            var placeholder = FindLauncherPlaceholderGlyph(tile);
            Assert.IsNotNull(placeholder, "模板应含占位字形层。");

            // 强制重取路径的两态拆步断言：清空与回填同一帧不可观察中间态，
            // 必须各自独立断言（清空→占位回可见；回填→占位再收起）。
            image.Tag = null;
            Assert.AreEqual(
                Visibility.Visible,
                placeholder.Visibility,
                $"Tag 清空后占位字形应恢复可见（{item.Kind} 模板）。");

            image.Tag = DecodedTestIcon();
            Assert.AreEqual(
                Visibility.Collapsed,
                placeholder.Visibility,
                $"Tag 回填后占位字形应再次收起（{item.Kind} 模板）。");
        }
    }

    [STATestMethod]
    public void LauncherIcon_LoadedChain_RealExtractionArrivalCollapsesPlaceholder()
    {
        var exePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (!File.Exists(exePath))
        {
            Assert.Inconclusive("本机无 notepad.exe（真实提取链无法验证）。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, exePath, "notepad", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);

            var live = window.LaunchersForTests()!.Items.Single();
            var tileIcon = WaitLauncherTileIcon(window, live, TimeSpan.FromSeconds(30));
            Assert.IsNotNull(tileIcon, "LauncherTileIcon_Loaded 真实装载链应回填图标。");

            CompleteLayout(window);
            var tile = FindLauncherTile(window, live);
            Assert.IsNotNull(tile, "瓦片未 realize。");
            var placeholder = FindLauncherPlaceholderGlyph(tile);
            Assert.IsNotNull(placeholder, "模板应含占位字形层。");
            Assert.AreEqual(
                Visibility.Collapsed,
                placeholder.Visibility,
                "真实提取链回填后占位字形应已收起（透明底图标不透出字形）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    /// <summary>
    /// 占位态测试宿主：Application（目标不存在＝提取必失败）＋ Url（fake handler
    /// 零路由＝favicon 404 负缓存）各一条——两模板的「图标未到达」态都确定可达，
    /// 回调 icon=null 恒不写 Tag，断言无竞态。
    /// </summary>
    private sealed class LauncherPlaceholderHost : IDisposable
    {
        private readonly TestDirectory _directory = new();

        public LauncherPlaceholderHost()
        {
            Handler = new FakeFaviconHandler();
            MainWindow.FaviconClientFactoryForTests = () => new FaviconClient(Handler);
            var store = new LocalStore(AppPaths.ForTests(_directory.Root), new AtomicTextWriter());
            Window = CreateLauncherWindowSync(
                _directory,
                new BoardService(),
                store,
                new RecordingLauncherStarter(),
                new LauncherItem(
                    Guid.NewGuid(),
                    LauncherKind.Application,
                    Path.Combine(_directory.Root, "missing-target.exe"),
                    "missing",
                    DateTimeOffset.UtcNow),
                new LauncherItem(
                    Guid.NewGuid(),
                    LauncherKind.Url,
                    "https://dead.example.com/",
                    "dead",
                    DateTimeOffset.UtcNow));
        }

        public MainWindow Window { get; }

        private FakeFaviconHandler Handler { get; }

        public void ShowLauncherSurface()
        {
            Window.Show();
            Window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(Window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(Window);
        }

        public void Dispose()
        {
            CloseWindowWithoutSaving(Window);
            MainWindow.FaviconClientFactoryForTests = null;
            Handler.Dispose();
            _directory.Dispose();
        }
    }

    /// <summary>图标槽占位字形（E8FD 应用/E774 网页）——按字形内容定位，与
    /// QuickLaunchUrl 的字形断言同款先例。</summary>
    private static TextBlock? FindLauncherPlaceholderGlyph(Border tile) =>
        FindDescendants<TextBlock>(tile).FirstOrDefault(text =>
            text.Text == "\uE8FD" || text.Text == "\uE774");

    private static BitmapSource DecodedTestIcon()
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(Png96());
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
