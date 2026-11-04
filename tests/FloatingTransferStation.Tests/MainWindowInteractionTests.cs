using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

[TestClass]
[TestCategory("Adversarial")]
public sealed partial class MainWindowInteractionTests
{
    private static MainWindow CreateWindow(
        TestDirectory directory,
        BoardService board,
        DefaultCaptureCategoryState? defaultCaptureCategory = null,
        IUpdateService? updateService = null,
        IPreferencesStore? preferencesStore = null,
        PluginCatalog? pluginCatalog = null,
        InputDeliveryService? inputDelivery = null)
    {
        defaultCaptureCategory ??= new DefaultCaptureCategoryState();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        return CreateWindow(
            board,
            store,
            WindowSettings.Default,
            defaultCaptureCategory,
            new ImageNormalizer(store.ImagesDirectory),
            updateService: updateService,
            preferencesStore: preferencesStore,
            pluginCatalog: pluginCatalog,
            inputDelivery: inputDelivery);
    }

    private static MainWindow CreateWindow(
        BoardService board,
        IBoardStore store,
        WindowSettings settings,
        DefaultCaptureCategoryState? defaultCaptureCategory = null,
        IUpdateService? updateService = null)
        => CreateWindow(
            board,
            store,
            settings,
            defaultCaptureCategory,
            new ImageNormalizer(store.ImagesDirectory),
            updateService: updateService);

    /// <summary>测试窗口的唯一组合接缝（internal 供全部测试类委托）：组装共享服务元组并
    /// 施加 harness 密封缺省（契约 #53：真实剪贴板写响亮失败 stub＋无网络更新服务假件）。
    /// 各测试类的本地 CreateWindow 只负责自己的差异件（读者/启动器/热键源/密钥保护器等），
    /// 组合核收敛于此——不逐测试打补丁。</summary>
    internal static MainWindow CreateWindow(
        BoardService board,
        IBoardStore store,
        WindowSettings settings,
        DefaultCaptureCategoryState? defaultCaptureCategory,
        IImageNormalizer normalizer,
        IClipboardReader? clipboardReader = null,
        IUpdateService? updateService = null,
        IPreferencesStore? preferencesStore = null,
        ILauncherStore? launcherStore = null,
        AppLauncher? appLauncher = null,
        string? dataDirectory = null,
        AppPreferences? preferences = null,
        PluginCatalog? pluginCatalog = null,
        IStartupManager? startupManager = null,
        IGlobalHotkeySource? globalHotkeySource = null,
        BoardOperationGate? operationGate = null,
        DataDirectoryChangeService? dataDirectoryChangeService = null,
        ISecretProtector? secretProtector = null,
        DeepSeekClient? deepSeekClient = null,
        AiTextOrganizer? aiTextOrganizer = null,
        InputDeliveryService? inputDelivery = null)
    {
        defaultCaptureCategory ??= new DefaultCaptureCategoryState();
        // harness 密封缺省②（契约 #53）：未显式注入更新服务时缺省用无网络假件（恒无更新），
        // 消灭「窗口存活 ≥30 秒即触发真实 GitHub 外联」的敞口（MainWindow.xaml.cs 的
        // UpdateService 兜底只属生产组合根）。显式传假的 Updates 族测试零改动。
        updateService ??= SealedUpdateService.Instance;
        operationGate ??= new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            clipboardReader ?? new NeverReadClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate,
            defaultCaptureCategory: defaultCaptureCategory);
        window = new MainWindow(
            board,
            store,
            settings,
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
            defaultCaptureCategory,
            updateService: updateService,
            preferences: preferences,
            preferencesStore: preferencesStore,
            launcherStore: launcherStore,
            appLauncher: appLauncher,
            dataDirectory: dataDirectory ?? ((store as LocalStore)?.ImagesDirectory is { } imagesDirectory
                ? Path.GetDirectoryName(imagesDirectory)
                : null),
            pluginCatalog: pluginCatalog,
            startupManager: startupManager,
            globalHotkeySource: globalHotkeySource,
            operationGate: operationGate,
            dataDirectoryChangeService: dataDirectoryChangeService,
            secretProtector: secretProtector,
            deepSeekClient: deepSeekClient,
            aiTextOrganizer: aiTextOrganizer,
            // harness 密封缺省③（需求 6）：输入送达的 Win32 接缝缺省＝静默假件——
            // 未显式注入送达服务的测试一旦触发左半裸单击送达，不触碰真实剪贴板/
            // 前台/SendInput（送达序列全假件、恒成功）。断言送达语义的测试注入
            // 自己的假件断言序列。
            inputDelivery: inputDelivery ?? new InputDeliveryService(new SilentInputDeliveryNative()));
        // harness 密封缺省①（契约 #53）：真实剪贴板写缺省＝响亮失败 stub——未显式声明写
        // 意图的测试一旦走到 WriteClipboardData 即失败并给可读原因（产品侧捕获子句只接
        // COM/External/InvalidOperation，AssertFailedException 穿透直达测试）。既有显式
        // 设置 ClipboardWriterOverride 的测试在测试体内覆写本缺省，零改动。
        window.ClipboardWriterOverride ??= RejectUndeclaredRealClipboardWrite;
        return window;
    }

    /// <summary>harness 密封缺省③（需求 6）：输入送达 Win32 接缝的静默假件。
    /// 前台窗口恒为「一个外部假窗口」（IsWindow=true、非本进程），送达序列的
    /// 剪贴板/焦点/按键注入全部内存假件、恒成功——测试触发送达不产生任何
    /// 系统级副作用。送达语义断言用各自的假件，本假件只兜既有测试的顺路触发。</summary>
    private sealed class SilentInputDeliveryNative : IInputDeliveryNative
    {
        public static readonly nint ForeignTargetWindow = new(0x12345678);

        public nint GetForegroundWindow() => ForeignTargetWindow;

        public bool IsWindow(nint hwnd) => hwnd == ForeignTargetWindow;

        public bool IsOwnProcessWindow(nint hwnd) => false;

        public bool SetForegroundWindow(nint hwnd) => true;

        public bool SendControlV() => true;

        public IDataObject? GetClipboard() => null;

        public bool WriteClipboard(IDataObject data) => true;

        public bool SetClipboard(IDataObject data) => true;

        public Task DelayAsync(TimeSpan delay) => Task.CompletedTask;
    }

    /// <summary>送达语义断言用记录假件：恒定外部目标窗口、全操作成功、逐调用
    /// 记录序列与负载/备份引用（焦点/注入/剪贴板写可按需注入失败）。</summary>
    private sealed class RecordingInputDeliveryNative : IInputDeliveryNative
    {
        public static readonly nint ForeignWindow = new(0x13572468);

        public List<string> Calls { get; } = [];

        public IDataObject? WrittenPayload { get; private set; }

        public IDataObject? RestoredBackup { get; private set; }

        public IDataObject? BackupToReturn { get; set; } = new DataObject("原剪贴板内容");

        public bool SetForegroundResult { get; set; } = true;

        public bool SendResult { get; set; } = true;

        public bool WriteResult { get; set; } = true;

        public nint GetForegroundWindow()
        {
            Calls.Add("get-foreground");
            return ForeignWindow;
        }

        public bool IsWindow(nint hwnd) => hwnd == ForeignWindow;

        public bool IsOwnProcessWindow(nint hwnd) => false;

        public bool SetForegroundWindow(nint hwnd)
        {
            Calls.Add("set-foreground");
            return SetForegroundResult;
        }

        public bool SendControlV()
        {
            Calls.Add("send-ctrl-v");
            return SendResult;
        }

        public IDataObject? GetClipboard()
        {
            Calls.Add("get-clipboard");
            return BackupToReturn;
        }

        public bool WriteClipboard(IDataObject data)
        {
            Calls.Add("write-clipboard");
            WrittenPayload = data;
            return WriteResult;
        }

        public bool SetClipboard(IDataObject data)
        {
            Calls.Add("set-clipboard");
            RestoredBackup = data;
            return true;
        }

        public Task DelayAsync(TimeSpan delay)
        {
            Calls.Add("delay");
            return Task.CompletedTask;
        }
    }

    /// <summary>密封缺省①的响亮失败 stub：真实剪贴板写必须显式声明（捕获 override）。</summary>
    private static bool RejectUndeclaredRealClipboardWrite(DataObject data)
        => throw new AssertFailedException(
            "harness 密封缺省（契约 #53）：本测试触发了真实剪贴板写（WriteClipboardData）但未声明写意图。"
            + "真实剪贴板是全机共享资源，测试写它会污染宿主现场；请在测试内显式设置 "
            + "window.ClipboardWriterOverride 捕获负载后断言。");

    /// <summary>密封缺省②的无网络更新服务假件：恒无更新、不支持下载与安装启动。</summary>
    private sealed class SealedUpdateService : IUpdateService
    {
        public static readonly SealedUpdateService Instance = new();

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(UpdateCheckResult.UpToDate());

        public Task<UpdateDownloadResult> DownloadUpdateAsync(
            UpdateCheckResult check,
            string downloadDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(UpdateDownloadResult.Fail(
                UpdateErrorKind.DownloadFailed,
                "harness 密封缺省：无网络假件不支持下载。"));

        public bool TryStartSilentInstaller(string installerPath) => false;
    }

    private static (Border Layer, Border Marker) FindCategoryFeedback(
        MainWindow window,
        BoardCategory category)
    {
        var viewModel = (MainWindowViewModel)window.DataContext;
        var categoryViewModel = viewModel.Categories.Single(candidate => candidate.Category == category);
        var tab = FindCategoryTab(window, categoryViewModel);
        var layerStyle = (Style)window.FindResource("CategoryActiveLayerStyle");
        var markerStyle = (Style)window.FindResource("CategoryActiveMarkerStyle");
        var layer = FindDescendants<Border>(tab)
            .Single(candidate => ReferenceEquals(candidate.Style, layerStyle));
        var marker = FindDescendants<Border>(tab)
            .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
        return (layer, marker);
    }

    private static (Grid Host, TranslateTransform Transform) FindPanelContent(
        MainWindow window) =>
        ((Grid)window.FindName("PanelContentHost"),
         (TranslateTransform)window.FindName("PanelContentTransform"));

    private static void StartPanelContentAnimation(
        Grid host,
        TranslateTransform transform)
    {
        host.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0d, 1d, TimeSpan.FromSeconds(1)));
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(6d, 0d, TimeSpan.FromSeconds(1)));
        Assert.IsTrue(host.HasAnimatedProperties);
        Assert.IsTrue(transform.HasAnimatedProperties);
    }

    private static void AssertPanelContentAnimationStopped(
        Grid host,
        TranslateTransform transform)
    {
        Assert.IsFalse(host.HasAnimatedProperties);
        Assert.IsFalse(transform.HasAnimatedProperties);
        Assert.AreEqual(1d, host.Opacity);
        Assert.AreEqual(0d, transform.X);
    }

    private static void AddScrollableItems(BoardService board, BoardCategory category)
    {
        for (var index = 0; index < 24; index++)
        {
            var item = board.AddText($"{category} {index}: {new string('x', 180)}");
            if (category != BoardCategory.Inbox)
            {
                board.MoveMany([item.Id], category, board.Items(category).Count);
            }
        }
    }

    private static void EnterCategory(MainWindow window, BoardCategory category)
    {
        var viewModel = (MainWindowViewModel)window.DataContext;
        var panel = viewModel.Categories.Single(candidate => candidate.Category == category);
        InvokePrivate(
            window,
            "CategoryTab_MouseEnter",
            new Border { DataContext = panel },
            NewMouseEventArgs());
    }

    private static void ExpandCategory(MainWindow window, BoardCategory category)
    {
        EnterCategory(window, category);
        InvokePrivate(window, "ExpandIntentTimer_Tick", null, EventArgs.Empty);
    }

    private static MouseEventArgs NewMouseEventArgs() => new(Mouse.PrimaryDevice, 0);

    private static MouseEventArgs NewMouseEventArgs(
        RoutedEvent routedEvent,
        DependencyObject source) =>
        new(Mouse.PrimaryDevice, 0)
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    private static MouseButtonEventArgs NewMouseButtonEventArgs(
        RoutedEvent routedEvent,
        DependencyObject source) =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    /// <summary>带 timestamp 的鼠标事件构造：手势层双击判定依赖真实时间戳。</summary>
    private static MouseButtonEventArgs NewMouseButtonEventArgs(
        RoutedEvent routedEvent,
        DependencyObject source,
        int timestamp) =>
        new(Mouse.PrimaryDevice, timestamp, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    /// <summary>带 timestamp 与修饰键的鼠标事件构造（合成路径）：瓦片单击处理读
    /// 全局 Keyboard.Modifiers——修饰键语义测试经 NewModifiedKeyEventArgs 同族的
    /// ModifierKeyboardDevice 注入键盘设备（Keyboard.Modifiers 跟随焦点设备的
    /// 修饰态），或测试直接用真实 SendInput 组合键注入。</summary>
    private static MouseButtonEventArgs NewMouseButtonEventArgs(
        RoutedEvent routedEvent,
        DependencyObject source,
        int timestamp,
        ModifierKeys modifiers)
    {
        var device = new ModifierKeyboardDevice(modifiers);
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, timestamp, MouseButton.Left)
        {
            RoutedEvent = routedEvent,
            Source = source
        };
        return args;
    }

    private static TextCompositionEventArgs NewTextCompositionEventArgs(
        RoutedEvent routedEvent,
        IInputElement source,
        string text) =>
        new(
            Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, source, text))
        {
            RoutedEvent = routedEvent,
            Source = source
        };

    private static KeyEventArgs NewKeyEventArgs(Window window, Key key) =>
        new(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = window
        };

    private static KeyEventArgs NewModifiedKeyEventArgs(
        Window window,
        Key key,
        ModifierKeys modifiers) =>
        new(
            new ModifierKeyboardDevice(modifiers),
            PresentationSource.FromVisual(window)!,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
            Source = window
        };

    private sealed class ModifierKeyboardDevice : KeyboardDevice
    {
        private readonly ModifierKeys _modifiers;

        public ModifierKeyboardDevice(ModifierKeys modifiers)
            : base(InputManager.Current)
        {
            _modifiers = modifiers;
        }

        protected override KeyStates GetKeyStatesFromSystem(Key key)
        {
            var isDown = key switch
            {
                Key.LeftCtrl or Key.RightCtrl => _modifiers.HasFlag(ModifierKeys.Control),
                Key.LeftShift or Key.RightShift => _modifiers.HasFlag(ModifierKeys.Shift),
                Key.LeftAlt or Key.RightAlt => _modifiers.HasFlag(ModifierKeys.Alt),
                Key.LWin or Key.RWin => _modifiers.HasFlag(ModifierKeys.Windows),
                _ => false
            };
            return isDown ? KeyStates.Down : KeyStates.None;
        }
    }

    private static void ScrollTo(Window window, ScrollViewer viewer, double offset)
    {
        viewer.ScrollToVerticalOffset(offset);
        CompleteLayout(window);
        Assert.AreEqual(offset, viewer.VerticalOffset, 0.5, "Scroll setup did not reach the requested offset.");
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

    private static void WithClientAreaAnimationsDisabled(MainWindow window, Action action)
    {
        var animations = window.Resources.Contains(SystemParameters.ClientAreaAnimationKey)
            ? window.Resources[SystemParameters.ClientAreaAnimationKey]
            : null;
        window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            action();
        }
        finally
        {
            if (animations is null)
            {
                window.Resources.Remove(SystemParameters.ClientAreaAnimationKey);
            }
            else
            {
                window.Resources[SystemParameters.ClientAreaAnimationKey] = animations;
            }
        }
    }

    private static void CollapseForSetup(MainWindow window)
    {
        // 作为前置条件的收起走减弱动效路径：同步提交，不等待出场动画。合成 Root_
        // MouseLeave 不移动物理指针——先把真实指针移离窗口（普通收起不变量：提交时
        // 指针在展开窗外），防止收起交接完成点的静止指针重评估（Issue #173）把机器
        // 光标恰停在把手上的偶然形态误判为回场。
        SetCursorPos(40, 40);
        WithClientAreaAnimationsDisabled(
            window,
            () =>
            {
                InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
                InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
                CompleteLayout(window);
            });
    }

    private static void PumpDispatcherFor(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void WaitForDeleteButtonReenabled(MainWindow window, Button delete)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!delete.IsEnabled && DateTime.UtcNow < deadline)
        {
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        }

        Assert.IsTrue(delete.IsEnabled, "第一次删除的收尾未在超时内完成。");
    }

    private static void CloseWindow(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler closedHandler = (_, _) => closed.TrySetResult();
        window.Closed += closedHandler;
        try
        {
            window.Close();
            PumpDispatcherUntil(window.Dispatcher, closed.Task);
        }
        finally
        {
            window.Closed -= closedHandler;
        }
    }

    private static void CloseWindowWithoutSaving(Window window)
    {
        var allowClose = typeof(MainWindow).GetField(
            "_allowClose",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(allowClose);
        allowClose.SetValue(window, true);
        CloseWindow(window);
    }

    private static void PumpDispatcherUntil(Dispatcher dispatcher, Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var timedOut = false;
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            timeout.Tick += (_, _) =>
            {
                timedOut = true;
                timeout.Stop();
                frame.Continue = false;
            };
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            timeout.Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (timedOut && !task.IsCompleted)
            {
                throw new TimeoutException("The dispatcher operation did not complete within five seconds.");
            }
        }

        task.GetAwaiter().GetResult();
    }

    private static void InvokePrivate(MainWindow window, string methodName, params object?[] arguments)
    {
        var method = GetPrivateMethod(methodName);
        Assert.IsNotNull(method);
        method.Invoke(window, arguments);
    }

    private static void InvokePrivateTask(
        MainWindow window,
        string methodName,
        params object?[] arguments)
    {
        var method = GetPrivateMethod(methodName);
        Assert.IsNotNull(method);
        var task = method.Invoke(window, arguments) as Task;
        Assert.IsNotNull(task);
        PumpDispatcherUntil(window.Dispatcher, task);
    }

    private static MethodInfo? GetPrivateMethod(string methodName) =>
        typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);

    private static T GetPrivateField<T>(MainWindow window, string fieldName)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(window)!;
    }

    private static T GetPrivateStaticField<T>(string fieldName)
    {
        var field = typeof(MainWindow).GetField(
            fieldName,
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (T)field.GetValue(null)!;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static Border FindCategoryTab(MainWindow window, CategoryViewModel category)
    {
        var rail = window.FindName("CategoryRail") as Border;
        Assert.IsNotNull(rail);
        return FindDescendants<Border>(rail).Single(candidate =>
            ReferenceEquals(candidate.DataContext, category) &&
            Equals(candidate.Tag, category.Category));
    }

    private static Border FindCollapsedCategoryTab(MainWindow window)
    {
        var viewModel = (MainWindowViewModel)window.DataContext;
        var handle = window.FindName("CollapsedCategoryHandle") as ContentControl;
        Assert.IsNotNull(handle);
        return FindDescendants<Border>(handle).Single(candidate =>
            ReferenceEquals(candidate.DataContext, viewModel.DefaultCapturePanel) &&
            Equals(candidate.Tag, viewModel.DefaultCapturePanel.Category));
    }

    private static Rect ScreenBounds(FrameworkElement element) =>
        new(element.PointToScreen(new Point()), new Size(element.ActualWidth, element.ActualHeight));

    private static DataObject CreateInternalDragData()
    {
        var data = new DataObject();
        data.SetData(DragPayloadService.InternalItemIdFormat, Guid.NewGuid().ToString("D"));
        return data;
    }

    private static DragEventArgs NewDragEventArgs(
        IDataObject data,
        RoutedEvent routedEvent,
        DependencyObject target) =>
        NewDragEventArgs(data, routedEvent, target, new Point());

    private static DragEventArgs NewDragEventArgs(
        IDataObject data,
        RoutedEvent routedEvent,
        DependencyObject target,
        Point position)
    {
        var arguments = new object[]
        {
            data,
            (DragDropKeyStates)0,
            DragDropEffects.Copy | DragDropEffects.Move,
            target,
            position
        };
        var eventArgs = (DragEventArgs?)Activator.CreateInstance(
            typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null);
        Assert.IsNotNull(eventArgs);
        eventArgs.RoutedEvent = routedEvent;
        return eventArgs;
    }

    private sealed class SingleReadFileDropDataObject(string[] paths) : IDataObject
    {
        private readonly DataObject _inner = CreateDataObject(paths);

        public int FileDropReadCount { get; private set; }

        public object? GetData(string format, bool autoConvert)
        {
            if (format == DataFormats.FileDrop && ++FileDropReadCount > 1)
            {
                throw new InvalidOperationException("FileDrop was read more than once.");
            }

            return _inner.GetData(format, autoConvert);
        }

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => _inner.GetData(format);
        public bool GetDataPresent(string format, bool autoConvert) =>
            _inner.GetDataPresent(format, autoConvert);
        public bool GetDataPresent(string format) => _inner.GetDataPresent(format);
        public bool GetDataPresent(Type format) => _inner.GetDataPresent(format);
        public string[] GetFormats(bool autoConvert) => _inner.GetFormats(autoConvert);
        public string[] GetFormats() => _inner.GetFormats();
        public void SetData(string format, object data, bool autoConvert) =>
            throw new NotSupportedException();
        public void SetData(string format, object data) => throw new NotSupportedException();
        public void SetData(Type format, object data) => throw new NotSupportedException();
        public void SetData(object data) => throw new NotSupportedException();

        private static DataObject CreateDataObject(string[] paths)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            return data;
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        const int bytesPerPixel = 4;
        var stride = width * bytesPerPixel;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)0x7F);
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveVisualEvidence(
        FrameworkElement visual,
        string fileName,
        string environmentVariable = "FTS_CLEAR_SELECTION_EVIDENCE_DIR")
    {
        var directory = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight),
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private static void WriteAnimatedGif(string path)
    {
        using var image = new SixLabors.ImageSharp.Image<
            SixLabors.ImageSharp.PixelFormats.Rgba32>(
            2,
            2,
            SixLabors.ImageSharp.Color.Red);
        image.Frames.AddFrame(image.Frames.RootFrame);
        using var stream = File.Create(path);
        image.Save(stream, new SixLabors.ImageSharp.Formats.Gif.GifEncoder());
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x0008;
    private static readonly nint HwndNotopmost = -2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private static bool IsWindowTopmost(nint hwnd) =>
        (GetWindowLong(hwnd, GwlExStyle) & WsExTopmost) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public bool Contains(int x, int y) =>
            x >= Left && x < Right && y >= Top && y < Bottom;

        public override string ToString() => $"{Left},{Top},{Width},{Height}";
    }

    private static IOException? TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (IOException exception)
        {
            return exception;
        }
    }

    private sealed class NeverReadClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Clipboard reader should not be used while inspecting the window.");
    }

    private sealed class CountingClipboardReader : IClipboardReader
    {
        public int ReadCount { get; private set; }

        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(new ClipboardSnapshot(1, null, [], "must not capture"));
        }
    }

    private sealed class SingleClipboardReader(ClipboardSnapshot snapshot) : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class CancelThenTextClipboardReader : IClipboardReader
    {
        private readonly TaskCompletionSource _releaseFirstRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReadCanceled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstReadFinished { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount => _readCount;
        public bool FirstTokenCanBeCanceled { get; private set; }
        public bool SecondTokenCanBeCanceled { get; private set; }
        public bool SecondTokenWasCanceled { get; private set; }

        public async Task<ClipboardSnapshot> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstTokenCanBeCanceled = cancellationToken.CanBeCanceled;
                FirstReadStarted.TrySetResult();
                try
                {
                    var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    var completed = await Task.WhenAny(_releaseFirstRead.Task, cancellation);
                    if (ReferenceEquals(completed, cancellation))
                    {
                        await cancellation;
                    }

                    return new ClipboardSnapshot(92, null, [], null);
                }
                catch (OperationCanceledException)
                {
                    FirstReadCanceled.TrySetResult();
                    throw;
                }
                finally
                {
                    FirstReadFinished.TrySetResult();
                }
            }

            SecondTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            SecondTokenWasCanceled = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
            return new ClipboardSnapshot(93, null, [], "captured after failed close");
        }

        public void ReleaseFirstRead() => _releaseFirstRead.TrySetResult();
    }

    private sealed class BlockingImageNormalizer(string imagesDirectory) : IImageNormalizer
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string? StoredPath { get; private set; }

        public Task<StoredImage> NormalizeFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("File normalization is not expected.");

        public Task<StoredImage> NormalizeStaticFileAsync(
            string sourcePath,
            Guid? id = null,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Static file normalization is not expected.");

        public async Task<StoredImage> NormalizeClipboardAsync(
            IReadOnlyList<ClipboardImageCandidate> candidates,
            Guid? id = null,
            CancellationToken cancellationToken = default)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return await dispatcher.InvokeAsync(
                () =>
                {
                    var storedId = id ?? Guid.NewGuid();
                    Directory.CreateDirectory(imagesDirectory);
                    StoredPath = Path.Combine(imagesDirectory, $"{storedId:N}.png");
                    File.WriteAllBytes(StoredPath, [0x89, 0x50, 0x4E, 0x47]);
                    Returned.TrySetResult();
                    return new StoredImage(storedId, $"images/{storedId:N}.png", StoredPath);
                },
                DispatcherPriority.Background);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class RecordingBoardStore(string root) : IBoardStore
    {
        public Exception? SaveFailure { get; set; }
        public Exception? SettingsSaveFailure { get; set; }
        public TimeSpan? SaveDelay { get; set; }
        public TaskCompletionSource ImageDeleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SaveCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public BoardSnapshot? LastPersistedSnapshot { get; private set; }
        public WindowSettings? LastSavedSettings { get; private set; }
        public int SaveCount { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (SaveDelay is { } delay)
            {
                return SaveAfterDelayAsync(snapshot, delay, cancellationToken);
            }

            SaveCore(snapshot);
            return Task.CompletedTask;
        }

        // 仅在显式要求慢保存时走异步路径；失败抛出保持历史同步语义。
        private async Task SaveAfterDelayAsync(
            BoardSnapshot snapshot,
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            SaveCore(snapshot);
        }

        private void SaveCore(BoardSnapshot snapshot)
        {
            if (SaveFailure is not null)
            {
                throw SaveFailure;
            }

            LastPersistedSnapshot = snapshot;
            SaveCount++;
            SaveCompleted.TrySetResult();
        }

        public Task<WindowSettings> LoadSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (SettingsSaveFailure is not null)
            {
                throw SettingsSaveFailure;
            }

            LastSavedSettings = settings;
            return Task.CompletedTask;
        }

        public bool TryDeleteImage(string? absolutePath)
        {
            if (!string.IsNullOrWhiteSpace(absolutePath) && File.Exists(absolutePath))
            {
                File.Delete(absolutePath);
            }

            ImageDeleted.TrySetResult();
            return true;
        }
    }

    private sealed class BlockingSettingsSaveBoardStore(string root) : IBoardStore
    {
        private readonly TaskCompletionSource _releaseSettingsSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SettingsSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int BoardSaveCount { get; private set; }
        public int SettingsSaveCount { get; private set; }
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            BoardSaveCount++;
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public async Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default)
        {
            SettingsSaveCount++;
            SettingsSaveStarted.TrySetResult();
            await _releaseSettingsSave.Task.WaitAsync(cancellationToken);
        }

        public bool TryDeleteImage(string? absolutePath) => true;

        public void ReleaseSettingsSave() => _releaseSettingsSave.TrySetResult();
    }

    private sealed class BlockingFirstSuccessfulSaveBoardStore(string root) : IBoardStore
    {
        private readonly TaskCompletionSource _releaseFirstSave = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _saveCount;

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstSaveCompleted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public async Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) != 1)
            {
                return;
            }

            FirstSaveStarted.TrySetResult();
            await _releaseFirstSave.Task.WaitAsync(cancellationToken);
            FirstSaveCompleted.TrySetResult();
        }

        public Task<WindowSettings> LoadSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;

        public void ReleaseFirstSave() => _releaseFirstSave.TrySetResult();
    }

    private sealed class UiThreadFailingFirstSaveBoardStore(string root) : IBoardStore
    {
        private readonly TaskCompletionSource _failFirstSave = new();
        private int _saveCount;

        public TaskCompletionSource FirstSaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public async Task SaveBoardAsync(
            BoardSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _saveCount) != 1)
            {
                return;
            }

            FirstSaveStarted.TrySetResult();
            await _failFirstSave.Task.WaitAsync(cancellationToken);
            throw new IOException("Injected first-save failure.");
        }

        public Task<WindowSettings> LoadSettingsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(
            WindowSettings settings,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;

        public void FailFirstSave() => _failFirstSave.TrySetResult();
    }
}
