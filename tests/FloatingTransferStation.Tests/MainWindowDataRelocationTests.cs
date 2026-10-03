using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 主窗数据目录搬迁的静默期契约：quiesce 停止全部写入者并封闭操作门、成功后直接
/// 放行退出；失败恢复运行（重开操作门、恢复剪贴板监听）；插件目录偏好的守卫规则。
/// </summary>
[TestClass]
public sealed class MainWindowDataRelocationTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "fts-window-relocation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void PumpDispatcherUntil(Dispatcher dispatcher, Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static T GetPrivateField<T>(MainWindow window, string fieldName) =>
        (T)typeof(MainWindow).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;

    private sealed class FakeRegistration(bool commitSucceeds) : IDataDirectoryRegistration
    {
        public List<(string DataDirectory, string DataParent)> Commits { get; } = [];

        public string? ReadDataDirectory() => null;

        public bool TryCommit(string dataDirectory, string dataParent)
        {
            if (!commitSucceeds)
            {
                return false;
            }

            Commits.Add((dataDirectory, dataParent));
            return true;
        }
    }

    private sealed class ThrowingClipboardReader : IClipboardReader
    {
        public int ReadCount { get; private set; }

        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            throw new InvalidOperationException("静默期不得读取剪贴板。");
        }
    }

    private MainWindow CreateWindow(
        string sourceDataDirectory,
        ThrowingClipboardReader reader,
        IDataDirectoryRegistration registration,
        bool spawnSucceeds,
        out BoardOperationGate gate,
        IPreferencesStore? preferencesStore = null)
    {
        var paths = AppPaths.ForTests(sourceDataDirectory);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var board = new BoardService();
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var changeService = new DataDirectoryChangeService(
            new DataDirectoryRelocator(),
            registration,
            new AtomicTextWriter(),
            static () => @"C:\fts-test-app.exe",
            _ => spawnSucceeds);
        var clipboard = new ClipboardCaptureService(
            reader,
            new ImageNormalizer(store.ImagesDirectory),
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
                new ImageNormalizer(store.ImagesDirectory), board, store, ShowStatus, operationGate),
            preferencesStore: preferencesStore is null ? null : preferencesStore,
            dataDirectory: sourceDataDirectory,
            operationGate: operationGate,
            dataDirectoryChangeService: changeService);
        gate = operationGate;
        return window;
    }

    private string CreateManagedSource()
    {
        var source = Path.Combine(_root, "old", "悬浮中转站", "Data");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "board.json"), "{}");
        return source;
    }

    [STATestMethod]
    public void ChangeDataDirectory_SuccessPublishesCommitsMarkerAndClosesWindow()
    {
        var source = CreateManagedSource();
        var reader = new ThrowingClipboardReader();
        var registration = new FakeRegistration(commitSucceeds: true);
        var window = CreateWindow(source, reader, registration, spawnSucceeds: true, out var gate);
        window.Show();

        var targetParent = Path.Combine(_root, "next");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var change = window.ChangeDataDirectoryAsync(targetParent);
        PumpDispatcherUntil(window.Dispatcher, change);
        var result = change.Result;

        Assert.IsTrue(result.ExitApplication);
        Assert.IsNull(result.Error);
        Assert.AreEqual(1, registration.Commits.Count);
        Assert.AreEqual(
            (Path.Combine(targetParent, "悬浮中转站", "Data"), targetParent),
            registration.Commits[0]);
        var target = Path.Combine(targetParent, "悬浮中转站", "Data");
        Assert.AreEqual(
            File.ReadAllText(Path.Combine(source, "board.json")),
            File.ReadAllText(Path.Combine(target, "board.json")),
            "新目录内容必须与冲刷后的源一致。");
        Assert.IsTrue(File.Exists(Path.Combine(target, RelocationCleanupMarker.FileName)));
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")), "旧目录保留给延迟清理。");
        Assert.IsTrue(gate.IsSealed, "静默期封门后保持封闭直到退出。");
        Assert.AreEqual(0, reader.ReadCount, "静默期没有写入者触碰旧目录。");
        Assert.IsTrue(GetPrivateField<bool>(window, "_allowClose"), "退出走免冲刷关闭路径。");
        Assert.IsTrue(closed.Task.IsCompleted, "迁移成功后主窗在同一泵程内退出。");
    }

    [STATestMethod]
    public void ChangeDataDirectory_CommitFailureResumesWindowAndReopensGate()
    {
        var source = CreateManagedSource();
        var reader = new ThrowingClipboardReader();
        var window = CreateWindow(source, reader, new FakeRegistration(commitSucceeds: false), spawnSucceeds: true, out var gate);
        window.Show();

        var change = window.ChangeDataDirectoryAsync(Path.Combine(_root, "next"));
        PumpDispatcherUntil(window.Dispatcher, change);
        var result = change.Result;

        Assert.IsFalse(result.ExitApplication);
        StringAssert.Contains(result.Error, "无法登记");
        Assert.IsTrue(window.IsEnabled, "失败后主窗恢复可用。");
        Assert.IsFalse(gate.IsSealed, "失败必须重开操作门。");
        var cancellation = GetPrivateField<CancellationTokenSource>(window, "_windowOperationCancellation");
        Assert.IsFalse(cancellation.IsCancellationRequested, "失败后恢复新的取消令牌。");
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "next", "悬浮中转站")), "发布目录回滚删除。");
        Assert.IsNotNull(GetPrivateField<System.Windows.Interop.HwndSource>(window, "_windowSource"), "剪贴板钩子恢复。");

        // 恢复后的窗口仍可正常走既有退出序列。
        typeof(MainWindow).GetField("_allowClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(window, true);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        PumpDispatcherUntil(window.Dispatcher, closed.Task);
    }

    [STATestMethod]
    public void ChangeDataDirectory_SpawnFailureExitsWithoutRollback()
    {
        var source = CreateManagedSource();
        var reader = new ThrowingClipboardReader();
        var registration = new FakeRegistration(commitSucceeds: true);
        var window = CreateWindow(source, reader, registration, spawnSucceeds: false, out _);
        window.Show();

        var targetParent = Path.Combine(_root, "next");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var change = window.ChangeDataDirectoryAsync(targetParent);
        PumpDispatcherUntil(window.Dispatcher, change);
        var result = change.Result;

        Assert.IsTrue(result.ExitApplication);
        StringAssert.Contains(result.Error, "自动重启失败");
        Assert.AreEqual(1, registration.Commits.Count, "登记不回滚。");
        Assert.IsTrue(File.Exists(Path.Combine(targetParent, "悬浮中转站", "Data", "board.json")));
        Assert.IsTrue(GetPrivateField<bool>(window, "_allowClose"));
        Assert.IsTrue(closed.Task.IsCompleted, "重启失败同样直接退出本实例。");
    }

    [STATestMethod]
    public void ApplyPluginsDirectory_RejectsInsideDataDirectoryAndSavesValidOverride()
    {
        var source = CreateManagedSource();
        var reader = new ThrowingClipboardReader();
        var saved = new List<AppPreferences>();
        var preferencesStore = new CapturingPreferencesStore(saved);
        var window = CreateWindow(
            source, reader, new FakeRegistration(true), spawnSucceeds: true, out _, preferencesStore);

        var insideTask = window.ApplyPluginsDirectoryAsync(Path.Combine(source, "custom-plugins"));
        PumpDispatcherUntil(window.Dispatcher, insideTask);
        var inside = insideTask.Result;
        Assert.IsNotNull(inside, "数据目录之内的插件目录必须拒绝。");
        Assert.AreEqual(0, saved.Count);

        var external = Path.Combine(_root, "my-plugins");
        var errorTask = window.ApplyPluginsDirectoryAsync(external);
        PumpDispatcherUntil(window.Dispatcher, errorTask);
        var error = errorTask.Result;
        Assert.IsNull(error);
        Assert.AreEqual(1, saved.Count);
        Assert.AreEqual(
            Path.GetFullPath(external).TrimEnd(Path.DirectorySeparatorChar),
            saved[0].PluginsDirectoryOverride);

        var resetTask = window.ApplyPluginsDirectoryAsync(null);
        PumpDispatcherUntil(window.Dispatcher, resetTask);
        var resetError = resetTask.Result;
        Assert.IsNull(resetError);
        Assert.AreEqual(2, saved.Count);
        Assert.IsNull(saved[1].PluginsDirectoryOverride);

        typeof(MainWindow).GetField("_allowClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(window, true);
        window.Close();
    }

    private sealed class CapturingPreferencesStore(List<AppPreferences> saved) : IPreferencesStore
    {
        public Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AppPreferences.Default);

        public Task SavePreferencesAsync(AppPreferences preferences, CancellationToken cancellationToken = default)
        {
            saved.Add(preferences);
            return Task.CompletedTask;
        }
    }
}
