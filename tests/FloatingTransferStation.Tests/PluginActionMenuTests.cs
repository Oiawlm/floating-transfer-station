using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 卡片右键「插件动作」菜单（1.51.0，契约 #31）：菜单可见性选集矩阵（single=恰
/// 一张文字卡、multi=文字卡 ≥2、图片卡忽略/纯图片禁用）、执行编排（板序快照、
/// 空结果跳过计数、批量替换、Ctrl+Z 逐卡恢复、卡已删作废消费）、运行中禁再入。
/// </summary>
[TestClass]
public sealed class PluginActionMenuTests
{
    private static string ActionPluginManifest() => """
        {
          "id": "typography-tidy",
          "name": "中文排版整理",
          "version": "1.0.0",
          "description": "测试动作插件",
          "kind": "text-actions",
          "actions": [
            {
              "id": "tidy-single",
              "title": "整理这张卡片",
              "accept": "text",
              "selection": "single",
              "output": "replace",
              "settings": [],
              "rules": [{ "pattern": "([A-Za-z])([0-9])", "replacement": "$2$1" }]
            },
            {
              "id": "tidy-multi",
              "title": "批量整理选中卡片",
              "accept": "text",
              "selection": "multi",
              "output": "replace",
              "settings": [],
              "rules": [{ "pattern": "([A-Za-z])([0-9])", "replacement": "$2$1" }]
            }
          ]
        }
        """;

    private sealed class Harness : IDisposable
    {
        public TestDirectory Directory { get; } = new();
        public MainWindow Window { get; }
        public BoardService Board { get; }
        public PluginCatalog Catalog { get; }
        public BoardMutationService Mutations { get; }

        public Harness(string manifest)
        {
            var pluginDirectory = Path.Combine(Directory.Root, "Data", "plugins", "typography-tidy");
            System.IO.Directory.CreateDirectory(pluginDirectory);
            File.WriteAllText(Path.Combine(pluginDirectory, PluginCatalog.ManifestFileName), manifest);
            Catalog = new PluginCatalog(
                AppPaths.ForTests(Path.Combine(Directory.Root, "Data")),
                null,
                new AtomicTextWriter());
            Catalog.LoadAsync().Wait();
            Catalog.SetEnabledAsync("typography-tidy", true).Wait();

            Board = new BoardService();
            Mutations = new BoardMutationService(
                Board,
                new RecordingSettingsBoardStore(Directory.Root),
                _ => { },
                new BoardOperationGate());
            Window = new MainWindow(
                Board,
                new RecordingSettingsBoardStore(Directory.Root),
                WindowSettings.Default,
                new ClipboardCaptureService(
                    new IdleClipboardReader(),
                    new ImageNormalizer(Path.Combine(Directory.Root, "images")),
                    Board,
                    new RecordingSettingsBoardStore(Directory.Root),
                    _ => { },
                    operationGate: new BoardOperationGate()),
                Mutations,
                new DragPayloadService(),
                new ExternalDropPayloadReader(new WindowsDataImageReader()),
                new ExternalDropImportService(
                    new ImageNormalizer(Path.Combine(Directory.Root, "images")),
                    Board,
                    new RecordingSettingsBoardStore(Directory.Root),
                    _ => { },
                    new BoardOperationGate()),
                preferences: null,
                preferencesStore: null,
                startupManager: new FakeStartupManager(),
                dataDirectory: Directory.Root,
                pluginCatalog: Catalog);
            // 真实应用由 Application 启动安装该上下文；测试环境补装，
            // 使 async 事件处理器的续延回到 UI 线程而不是线程池。
            System.Threading.SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(Window.Dispatcher));
            Window.Show();
            ExpandPanel();
        }

        /// <summary>反射调用内部执行入口并等待完成：UI 线程不得阻塞等待需要
        /// Dispatcher 的续延，改为循环泵帧直到任务完成（上限防死循环）。</summary>
        public void ExecuteAction(PluginActionHandle handle)
        {
            var task = (Task)typeof(MainWindow)
                .GetMethod("ExecutePluginActionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Window, [handle])!;
            for (var i = 0; !task.IsCompleted && i < 500; i++)
            {
                Pump(Window);
            }

            Assert.IsTrue(task.IsCompleted, "插件动作执行未在泵帧预算内完成。");
            Pump(Window);
        }

        /// <summary>主面板默认收起（EdgeHide 家族）：测试经悬停意图计时器展开收件箱。</summary>
        public void ExpandPanel()
        {
            var viewModel = (MainWindowViewModel)Window.DataContext;
            var panel = viewModel.Categories.Single(candidate => candidate.Category == BoardCategory.Inbox);
            InvokePrivate("CategoryTab_MouseEnter", new Border { DataContext = panel },
                new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0));
            InvokePrivate("ExpandIntentTimer_Tick", null, EventArgs.Empty);
            Pump(Window);
        }

        private void InvokePrivate(string name, object? parameter, EventArgs args) =>
            typeof(MainWindow)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Window, [parameter, args]);

        public BoardItem AddCard(string text) => Board.AddText(text, BoardCategory.Inbox);

        public ListBox BoardList => (ListBox)Window.FindName("BoardList")!;

        public IReadOnlyList<PluginActionHandle> BuildAvailable() =>
            (IReadOnlyList<PluginActionHandle>)typeof(MainWindow)
                .GetMethod("BuildAvailablePluginActions", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(Window, null)!;

        public void Dispose()
        {
            Window.Close();
            Directory.Dispose();
        }
    }

    private static void Pump(MainWindow window)
    {
        window.UpdateLayout();
        var frame = new System.Windows.Threading.DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    [STATestMethod]
    public void MenuVisibility_MatrixFollowsSelectionComposition()
    {
        using var harness = new Harness(ActionPluginManifest());
        var single = harness.AddCard("a1");
        var second = harness.AddCard("b2");
        var image = harness.Board.AddImage(Guid.NewGuid(), "images/none.png", Path.Combine(harness.Directory.Root, "images", "none.png"));
        Pump(harness.Window);

        // 单张文字卡：仅 single 可用。
        harness.BoardList.SelectedItems.Add(single);
        Pump(harness.Window);
        var available = harness.BuildAvailable();
        Assert.HasCount(1, available);
        Assert.AreEqual("tidy-single", available[0].Action.Id);

        // 两张文字卡：仅 multi 可用。
        harness.BoardList.SelectedItems.Add(second);
        Pump(harness.Window);
        available = harness.BuildAvailable();
        Assert.HasCount(1, available);
        Assert.AreEqual("tidy-multi", available[0].Action.Id);

        // 三选（两文一图）：multi 仍可用（图片卡忽略）。
        harness.BoardList.SelectedItems.Add(image);
        Pump(harness.Window);
        available = harness.BuildAvailable();
        Assert.HasCount(1, available);
        Assert.AreEqual("tidy-multi", available[0].Action.Id);
    }

    [STATestMethod]
    public void MenuVisibility_PureImageSelectionHasNoActions()
    {
        using var harness = new Harness(ActionPluginManifest());
        var image = harness.Board.AddImage(Guid.NewGuid(), "images/none.png", Path.Combine(harness.Directory.Root, "images", "none.png"));
        Pump(harness.Window);
        harness.BoardList.SelectedItems.Add(image);
        Pump(harness.Window);
        Assert.IsEmpty(harness.BuildAvailable());
    }

    [STATestMethod]
    public void Execute_ReplacesSelectedCardsInBoardOrderAndSupportsUndo()
    {
        using var harness = new Harness(ActionPluginManifest());
        var first = harness.AddCard("a1");
        var second = harness.AddCard("b2");
        Pump(harness.Window);
        harness.BoardList.SelectedItems.Add(first);
        harness.BoardList.SelectedItems.Add(second);
        Pump(harness.Window);

        var handle = harness.BuildAvailable().Single();
        harness.ExecuteAction(handle);

        Assert.AreEqual("1a", harness.Board.FindItem(first.Id)!.Text);
        Assert.AreEqual("2b", harness.Board.FindItem(second.Id)!.Text);

        // Ctrl+Z（撤销栈）逐卡恢复原文。
        harness.Mutations.UndoLastAsync().Wait();
        Assert.AreEqual("a1", harness.Board.FindItem(first.Id)!.Text);
        Assert.AreEqual("b2", harness.Board.FindItem(second.Id)!.Text);
    }

    [STATestMethod]
    public void Execute_SkipsEmptyResultsAndKeepsOriginal()
    {
        var manifest = """
        {
          "id": "typography-tidy",
          "name": "中文排版整理",
          "version": "1.0.0",
          "kind": "text-actions",
          "actions": [
            {
              "id": "tidy-multi",
              "title": "批量整理",
              "accept": "text",
              "selection": "multi",
              "output": "replace",
              "settings": [],
              "rules": [{ "pattern": "清空", "replacement": "" }]
            }
          ]
        }
        """;
        using var harness = new Harness(manifest);
        var clears = harness.AddCard("清空");
        var keeps = harness.AddCard("保留 b3");
        Pump(harness.Window);
        harness.BoardList.SelectedItems.Add(clears);
        harness.BoardList.SelectedItems.Add(keeps);
        Pump(harness.Window);

        harness.ExecuteAction(harness.BuildAvailable().Single());

        // trim 后为空的卡跳过保留原文；无匹配规则的卡原样保留。
        Assert.AreEqual("清空", harness.Board.FindItem(clears.Id)!.Text);
        Assert.AreEqual("保留 b3", harness.Board.FindItem(keeps.Id)!.Text);
    }

    [STATestMethod]
    public void Undo_ConsumesVoidedItemsWithoutRestoringDeleted()
    {
        using var harness = new Harness(ActionPluginManifest());
        var deleted = harness.AddCard("a1");
        var kept = harness.AddCard("b2");
        Pump(harness.Window);
        harness.BoardList.SelectedItems.Add(deleted);
        harness.BoardList.SelectedItems.Add(kept);
        Pump(harness.Window);

        harness.ExecuteAction(harness.BuildAvailable().Single());
        Assert.AreEqual("1a", harness.Board.FindItem(deleted.Id)!.Text);

        // 替换后删除其中一卡，再撤销：已删卡保持删除（原文随卡消失），其余恢复。
        harness.Board.RemoveMany([deleted.Id]);
        harness.Mutations.UndoLastAsync().Wait();
        Assert.IsNull(harness.Board.FindItem(deleted.Id));
        Assert.AreEqual("b2", harness.Board.FindItem(kept.Id)!.Text);
    }

    [STATestMethod]
    public void TryOpenPluginActionsMenu_SelectsClickedCardWhenNotSelected()
    {
        using var harness = new Harness(ActionPluginManifest());
        var clicked = harness.AddCard("a1");
        Pump(harness.Window);

        typeof(MainWindow)
            .GetMethod("TryOpenPluginActionsMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(harness.Window, [clicked]);
        Pump(harness.Window);

        // 命中卡不在选集内 → 先单选该卡（资源管理器惯例）。
        CollectionAssert.AreEquivalent(new[] { clicked.Id }, harness.BoardList.SelectedItems.OfType<BoardItem>().Select(item => item.Id).ToArray());
    }

    [STATestMethod]
    public void PluginActionsMenu_ReachableOnlyFromContentZoneRightClick()
    {
        // 手势交换（Issue #216）：插件动作菜单入口收窄为左半右键——右半右键
        // 是选择手势，不再弹菜单。
        using var harness = new Harness(ActionPluginManifest());
        var item = harness.AddCard("a1 菜单入口");
        Pump(harness.Window);
        var list = harness.BoardList;
        list.ScrollIntoView(item);
        Pump(harness.Window);
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item)!;
        Assert.IsNotNull(container);

        // 右半右键：toggle 选择，不弹菜单。
        list.RaiseEvent(NewZoneRightClickArgs(container, CardGestureZones.OperationsZone));
        Pump(harness.Window);
        CollectionAssert.AreEquivalent(new[] { item.Id }, list.SelectedItems.OfType<BoardItem>().Select(card => card.Id).ToArray());
        Assert.IsNull(harness.Window.LastPluginActionsMenuForEvidence, "右半右键不得打开插件动作菜单。");

        // 左半右键：打开插件动作菜单（命中卡先单选）。
        list.SelectedItems.Clear();
        list.RaiseEvent(NewZoneRightClickArgs(container, CardGestureZones.ContentZone));
        Pump(harness.Window);
        var menu = harness.Window.LastPluginActionsMenuForEvidence;
        Assert.IsNotNull(menu, "左半右键必须打开插件动作菜单。");
        Assert.IsTrue(menu.IsOpen);
        menu.IsOpen = false;
    }

    /// <summary>构造命中卡片某分区（模板 Tag 标记）的右键抬起事件（合成路径）。
    /// 右键专用隧道事件须在 BoardList 上 Raise（Source=分区命中元素）——在
    /// 后代元素上直接 Raise 不触达 BoardList 的处理器。</summary>
    private static System.Windows.Input.MouseButtonEventArgs NewZoneRightClickArgs(
        ListBoxItem container,
        string zoneTag)
    {
        var hitSource = FindZoneSource(container, zoneTag)
            ?? throw new InvalidOperationException($"卡片模板必须存在分区标记 {zoneTag} 的命中元素。");
        return new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice,
            0,
            System.Windows.Input.MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
            Source = hitSource
        };
    }

    /// <summary>深度优先搜索卡片容器可视化子树中带分区标记的命中元素
    ///（未命中返回 null——中途抛会截断兄弟子树的搜索）。</summary>
    private static FrameworkElement? FindZoneSource(FrameworkElement element, string zoneTag)
    {
        if (Equals(element.Tag, zoneTag))
        {
            return element;
        }

        var children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(element);
        for (var index = 0; index < children; index++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(element, index) is FrameworkElement child)
            {
                var found = FindZoneSource(child, zoneTag);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>插件动作右键菜单证据（环境变量门控，本地留档用）：打开菜单并截图。</summary>
    [STATestMethod]
    public void MenuEvidence_CapturePluginActionsContextMenu()
    {
        var directory = Environment.GetEnvironmentVariable("FTS_PLUGIN_ACTIONS_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            Assert.Inconclusive("未设置 FTS_PLUGIN_ACTIONS_EVIDENCE_DIR，跳过菜单证据产出。");
        }

        using var harness = new Harness(ActionPluginManifest());
        var first = harness.AddCard("a1 文本");
        var second = harness.AddCard("b2 文本");
        Pump(harness.Window);
        harness.BoardList.SelectedItems.Add(first);
        harness.BoardList.SelectedItems.Add(second);
        Pump(harness.Window);

        typeof(MainWindow)
            .GetMethod("TryOpenPluginActionsMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(harness.Window, [first]);
        Pump(harness.Window);
        var menu = harness.Window.LastPluginActionsMenuForEvidence;
        Assert.IsNotNull(menu);
        Assert.IsTrue(menu.IsOpen, "多选场景应打开插件动作菜单。");

        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(menu.ActualWidth == 0 ? menu.DesiredSize.Width : menu.ActualWidth),
            (int)Math.Ceiling(menu.ActualHeight == 0 ? menu.DesiredSize.Height : menu.ActualHeight),
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(menu);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        System.IO.Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, "plugin-actions-context-menu.png"));
        encoder.Save(stream);
        menu.IsOpen = false;
    }

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }

    private sealed class RecordingSettingsBoardStore(string root) : IBoardStore
    {
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public bool Enabled { get; set; }

        public bool IsEnabled() => Enabled;

        public void Enable() => Enabled = true;

        public void Disable() => Enabled = false;
    }
}
