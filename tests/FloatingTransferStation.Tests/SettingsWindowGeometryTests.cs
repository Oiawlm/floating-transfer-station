using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗口几何契约（#23，Issue #106）：冻结 min/max 约束、持久化往返、
/// 越界钳制、负坐标合法、非法值/全屏外回落默认定位、「结束才落盘」时机与
/// 最大化不落盘。多屏/显示器断开经 WorkAreaResolverOverride 假工作区注入。
/// </summary>
[TestClass]
public sealed class SettingsWindowGeometryTests
{
    [STATestMethod]
    public void Geometry_FrozenConstraints_AreDeclaredAndEnforced()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            Assert.AreEqual(SizeToContent.Manual, settings.SizeToContent, "SizeToContent 冻结为 Manual（内容驱动测量会把高度撑回内容高度）。");
            Assert.AreEqual(ResizeMode.CanResize, settings.ResizeMode, "冻结为 CanResize（系统级 8 向缩放）。");
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(settings);
            Assert.IsNotNull(chrome);
            Assert.AreEqual(6d, chrome.ResizeBorderThickness.Left, "无边框缩放命中区冻结为 6。");
            Assert.AreEqual(560d, settings.MinWidth, 0.01, "最小宽冻结值 560。");
            Assert.AreEqual(460d, settings.MinHeight, 0.01, "最小高冻结值 460。");
            Assert.AreEqual(900d, settings.MaxWidth, 0.01, "最大宽冻结值 900。");
            Assert.AreEqual(
                Math.Max(460d, work.Height - 48d),
                settings.MaxHeight,
                0.01,
                "最大高冻结式：max(最小高 460, 工作区高 − 48)。");
            Assert.AreEqual(680d, settings.ActualWidth, 0.01, "默认宽冻结值 680。");
            Assert.AreEqual(600d, settings.ActualHeight, 0.01, "默认高冻结值 600。");

            settings.Width = 400;
            CompleteLayout(settings);
            Assert.IsTrue(settings.ActualWidth >= 560 - 0.01, "宽度不得低于最小值。");
            settings.Width = 1200;
            CompleteLayout(settings);
            Assert.IsTrue(settings.ActualWidth <= 900 + 0.01, "宽度不得超出最大值。");
            settings.Height = 300;
            CompleteLayout(settings);
            Assert.IsTrue(settings.ActualHeight >= 460 - 0.01, "高度不得低于最小值。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_PersistedRectWithinWorkArea_IsRestoredVerbatim()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = 60,
            SettingsWindowTop = 80,
            SettingsWindowWidth = 860,
            SettingsWindowHeight = 520
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.AreEqual(860d, settings.ActualWidth, 0.01);
            Assert.AreEqual(520d, settings.ActualHeight, 0.01);
            Assert.AreEqual(60d, settings.Left, 0.01, "工作区内的持久化位置应原样恢复。");
            Assert.AreEqual(80d, settings.Top, 0.01);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_NegativeCoordinatesLegalWithinTargetMonitorWorkArea()
    {
        // 左侧副屏布局（虚拟桌面 [-1920..0)+主屏，副屏工作区 DIP [-1920, 0)）：
        // 完全落在副屏内的持久化负坐标须原样恢复，不钳非负、不因越主屏缘被收紧。
        SettingsWindowPlacement.WorkAreaResolverOverride = (_, _) =>
            new SettingsWindowPlacement.WorkArea(-1920, 0, 0, 1040);
        SettingsWindowPlacement.VirtualScreenBoundsOverride = () => (-1920, 0, 1920, 1040);
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -700,
            SettingsWindowTop = 100,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.AreEqual(-700d, settings.Left, 0.01, "左侧副屏内负坐标合法，不得被钳到非负。");
            Assert.AreEqual(100d, settings.Top, 0.01);
            Assert.AreEqual(
                Math.Max(460d, 1040d - 48d),
                settings.MaxHeight,
                0.01,
                "MaxHeight 应按目标显示器工作区重算。");
        }
        finally
        {
            SettingsWindowPlacement.WorkAreaResolverOverride = null;
            SettingsWindowPlacement.VirtualScreenBoundsOverride = null;
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_PartiallyOffscreen_ClampsIntoWorkArea()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -300,
            SettingsWindowTop = 50,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            Assert.AreEqual(work.Left, settings.Left, 0.01, "左越界部分应钳到工作区左缘。");
            Assert.AreEqual(50d, settings.Top, 0.01);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_InvalidValues_FallBackToDefaultOwnerRelativePlacement()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = double.NaN,
            SettingsWindowTop = 50,
            SettingsWindowWidth = 860,
            SettingsWindowHeight = 520
        };
        AssertDefaultPlacement(preferences, "NaN 属非法值，整组回落默认定位。");
    }

    [STATestMethod]
    public void Geometry_InfinityNonPositiveAndPartialNull_FallBackAsWholeGroup()
    {
        // 整组语义：任一字段非法（±∞/非正尺寸/部分 null）→ 整组回落，不逐字段抢救。
        var cases = new[]
        {
            (
                "Left=+∞",
                AppPreferences.Default with
                {
                    SettingsWindowLeft = double.PositiveInfinity,
                    SettingsWindowTop = 50,
                    SettingsWindowWidth = 860,
                    SettingsWindowHeight = 520
                }
            ),
            (
                "Width=0",
                AppPreferences.Default with
                {
                    SettingsWindowLeft = 60,
                    SettingsWindowTop = 50,
                    SettingsWindowWidth = 0,
                    SettingsWindowHeight = 520
                }
            ),
            (
                "Height<0",
                AppPreferences.Default with
                {
                    SettingsWindowLeft = 60,
                    SettingsWindowTop = 50,
                    SettingsWindowWidth = 860,
                    SettingsWindowHeight = -520
                }
            ),
            (
                "Height=null（部分字段缺失）",
                AppPreferences.Default with
                {
                    SettingsWindowLeft = 60,
                    SettingsWindowTop = 50,
                    SettingsWindowWidth = 860,
                    SettingsWindowHeight = null
                }
            )
        };
        foreach (var (name, preferences) in cases)
        {
            AssertDefaultPlacement(preferences, $"{name}：非法/不完整几何必须整组回落默认定位。");
        }
    }

    [STATestMethod]
    public void Geometry_FullyOffscreen_FallsBackToDefaultOwnerRelativePlacement()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -99999,
            SettingsWindowTop = -99999,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        AssertDefaultPlacement(preferences, "完全落在所有显示器之外，整组回落默认定位。");
    }

    [STATestMethod]
    public void Geometry_PersistOnlyAtOperationEnd_MaximizedStateNotPersisted()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var store = GetPreferencesStore(window);
            Assert.IsNull(store.LastSaved, "打开设置窗口本身不得触发几何落盘。");

            // 头部拖拽中间态不落盘，DragCompleted（结束）才落盘。
            var thumb = (Thumb)settings.FindName("HeaderDragRegion");
            thumb.RaiseEvent(new DragDeltaEventArgs(30, 20));
            CompleteLayout(settings);
            Assert.IsNull(store.LastSaved, "拖拽进行中不得写盘（结束才落盘）。");
            thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            CompleteLayout(settings);
            Assert.IsNotNull(store.LastSaved, "拖拽结束必须把几何写回偏好。");
            Assert.AreEqual(settings.Left, store.LastSaved!.SettingsWindowLeft!.Value, 0.01);
            Assert.AreEqual(settings.ActualWidth, store.LastSaved.SettingsWindowWidth!.Value, 0.01);

            // 系统缩放结束（WM_EXITSIZEMOVE 路径）同样落盘。
            store.Reset();
            settings.Width = 760;
            CompleteLayout(settings);
            InvokeCommitPlacement(settings);
            CompleteLayout(settings);
            Assert.IsNotNull(store.LastSaved, "系统缩放结束必须把几何写回偏好。");
            Assert.AreEqual(760d, store.LastSaved!.SettingsWindowWidth!.Value, 0.01);

            // 最大化状态不落盘：恢复的是 Normal 几何。
            settings.WindowState = WindowState.Maximized;
            store.Reset();
            InvokeCommitPlacement(settings);
            CompleteLayout(settings);
            Assert.IsNull(store.LastSaved, "最大化状态不得落盘。");
            settings.WindowState = WindowState.Normal;

            // 最小化同属非 Normal 态：不落盘。
            settings.WindowState = WindowState.Minimized;
            InvokeCommitPlacement(settings);
            CompleteLayout(settings);
            Assert.IsNull(store.LastSaved, "最小化状态不得落盘。");
            settings.WindowState = WindowState.Normal;
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_SystemSizeMoveCommitsAtEndAndSuspendsClampDuringSizing()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var store = GetPreferencesStore(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(settings).Handle;
            Assert.IsTrue(handle != IntPtr.Zero);
            const int WmEnterSizeMove = 0x0231;
            const int WmExitSizeMove = 0x0232;

            // 系统缩放开始：安全网钳制挂起——把窗口推到工作区外，位置不得被立刻拉回。
            PostMessage(handle, WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero);
            CompleteLayout(settings);
            settings.Top = SystemParameters.WorkArea.Bottom + 500;
            CompleteLayout(settings);
            Assert.IsTrue(
                settings.Top > SystemParameters.WorkArea.Bottom,
                "WM_SIZEMOVE 期间安全网必须挂起（不与交互缩放打架）。");

            // 系统缩放结束：收口一次——钳回工作区 + 落盘。
            PostMessage(handle, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero);
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            Assert.IsTrue(
                settings.Top <= work.Bottom + 0.01,
                "WM_EXITSIZEMOVE 后必须钳回工作区。");
            Assert.IsNotNull(store.LastSaved, "系统缩放结束必须落盘（EXITSIZEMOVE 路径）。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_MaximizeStaysWithinWorkArea()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            settings.WindowState = WindowState.Maximized;
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            // WM_GETMINMAXINFO 按所在显示器工作区修正无边框最大化（留 resize 边框容差）。
            Assert.IsTrue(settings.Top >= work.Top - 8, $"最大化上缘不得探出工作区，Top={settings.Top}。");
            Assert.IsTrue(settings.Left >= work.Left - 8, $"最大化左缘不得探出工作区，Left={settings.Left}。");
            Assert.IsTrue(
                settings.Top + settings.ActualHeight <= work.Bottom + 8,
                $"最大化下缘不得探出工作区（含任务栏），Bottom={settings.Top + settings.ActualHeight}。");
            settings.WindowState = WindowState.Normal;
            CompleteLayout(settings);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_TargetMonitorIsResolvedByRectCenter()
    {
        // 中心选屏接线：解析入参必须是持久化矩形的中心（而非左上角/最大交集）。
        double? resolvedX = null;
        double? resolvedY = null;
        SettingsWindowPlacement.WorkAreaResolverOverride = (x, y) =>
        {
            resolvedX = x;
            resolvedY = y;
            return new SettingsWindowPlacement.WorkArea(-1920, 0, 0, 1040);
        };
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -300,
            SettingsWindowTop = 100,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.IsNotNull(resolvedX);
            Assert.AreEqual(-300d + 680d / 2, resolvedX!.Value, 0.01, "选屏必须以矩形中心 X 为准。");
            Assert.AreEqual(100d + 600d / 2, resolvedY!.Value, 0.01, "选屏必须以矩形中心 Y 为准。");
        }
        finally
        {
            SettingsWindowPlacement.WorkAreaResolverOverride = null;
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_MaxHeightNeverBelowMinHeightOnSmallDisplay()
    {
        SettingsWindowPlacement.WorkAreaResolverOverride = (_, _) =>
            new SettingsWindowPlacement.WorkArea(0, 0, 1000, 400);
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = 10,
            SettingsWindowTop = 10,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.AreEqual(
                460d,
                settings.MaxHeight,
                0.01,
                "矮工作区（400 − 48 < 460）下 MaxHeight 必须抬到最小高，禁止 MaxHeight < MinHeight。");
        }
        finally
        {
            SettingsWindowPlacement.WorkAreaResolverOverride = null;
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_DisconnectedMonitor_FallsBackToPrimaryWorkAreaClamp()
    {
        // 显示器断开：查询不可用（null）→ 按契约退主屏工作区；持久化矩形（居主屏内的
        // 尺寸与越界位置）被钳进主屏工作区，而不是丢回默认定位。
        SettingsWindowPlacement.WorkAreaResolverOverride = (_, _) => null;
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = 1600,
            SettingsWindowTop = 60,
            SettingsWindowWidth = 860,
            SettingsWindowHeight = 520
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            Assert.AreEqual(
                Math.Max(work.Right - 860, work.Left),
                settings.Left,
                0.01,
                "显示器断开后位置应钳进主屏工作区（右缘对齐）而非回落默认定位。");
            Assert.AreEqual(860d, settings.ActualWidth, 0.01, "尺寸合法时应原样保留。");
        }
        finally
        {
            SettingsWindowPlacement.WorkAreaResolverOverride = null;
            CloseAll(window, settings);
        }
    }

    private static void AssertDefaultPlacement(AppPreferences preferences, string message)
    {
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            var expectedLeft = Math.Max(work.Left, Math.Min(window.Left - settings.Width - 12, work.Right - settings.Width));
            var expectedTop = Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - settings.ActualHeight));
            Assert.AreEqual(expectedLeft, settings.Left, 0.01, message);
            Assert.AreEqual(expectedTop, settings.Top, 0.01, message);
            Assert.AreEqual(680d, settings.ActualWidth, 0.01, "回落默认定位时尺寸应为冻结默认值。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    private static void InvokeCommitPlacement(SettingsWindow settings)
    {
        var method = typeof(SettingsWindow).GetMethod(
            "CommitPlacement",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "CommitPlacement 是落盘唯一入口。");
        method.Invoke(settings, null);
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static RecordingPreferencesStore GetPreferencesStore(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_preferencesStore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "MainWindow._preferencesStore 应存在。");
        return (RecordingPreferencesStore)field.GetValue(window)!;
    }

    // ---- 基建（按本测试套既有各文件自持 helper 的模式复制）----

    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingPreferencesStore preferencesStore,
        FakeStartupManager startupManager,
        AppPreferences? preferences = null)
    {
        var store = new RecordingSettingsBoardStore(directory.Root);
        var board = new BoardService();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new IdleClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate);
        window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                operationGate),
            preferences: preferences,
            preferencesStore: preferencesStore,
            startupManager: startupManager,
            dataDirectory: directory.Root);
        return window;
    }

    private static (MainWindow Window, SettingsWindow Settings) OpenSettings(AppPreferences? preferences = null)
    {
        var directory = new TestDirectory();
        var store = new RecordingPreferencesStore();
        var startup = new FakeStartupManager();
        var window = CreateWindow(directory, store, startup, preferences);
        window.Show();
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear);
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var field = typeof(MainWindow).GetField(
            "_settingsWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var settings = (SettingsWindow?)field.GetValue(window);
        Assert.IsNotNull(settings);
        return (window, settings);
    }

    private static void CompleteLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void CloseAll(MainWindow window, SettingsWindow settings)
    {
        settings.Close();
        window.Close();
    }

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }

    private sealed class RecordingPreferencesStore : IPreferencesStore
    {
        public AppPreferences? LastSaved { get; private set; }
        public AppPreferences Seeded { get; set; } = AppPreferences.Default;

        public Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Seeded);

        public Task SavePreferencesAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            LastSaved = preferences;
            return Task.CompletedTask;
        }

        public void Reset() => LastSaved = null;
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public bool Enabled { get; set; }

        public bool IsEnabled() => Enabled;

        public void Enable() => Enabled = true;

        public void Disable() => Enabled = false;
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
}
