using System.Diagnostics;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 数据目录搬迁编排的失败模式：登记失败→按形状守卫回滚已发布目录；标记失败→
/// 回滚目录并恢复登记；重启失败→不回滚、要求退出；成功→退出且登记指向新目录。
/// quiesce 用替身记录调用次序，验证“失败恢复、成功退出”的编排契约。
/// </summary>
[TestClass]
public sealed class DataDirectoryChangeServiceTests
{
    private string _root = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "fts-change-tests-" + Guid.NewGuid().ToString("N"));
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

    private string CreateManagedSource()
    {
        var source = Path.Combine(_root, "old", "悬浮中转站", "Data");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "board.json"), "{}");
        return source;
    }

    private sealed class RecordingQuiesce : IDataChangeQuiesceScope
    {
        public int QuiesceCount { get; private set; }
        public int ResumeCount { get; private set; }
        public bool PreparedForExit { get; private set; }

        public Task QuiesceAsync()
        {
            QuiesceCount++;
            return Task.CompletedTask;
        }

        public void Resume() => ResumeCount++;

        public void PrepareForExit() => PreparedForExit = true;
    }

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

    private DataDirectoryChangeService CreateService(
        IDataDirectoryRegistration registration,
        Func<ProcessStartInfo, bool> starter) => new(
        new DataDirectoryRelocator(),
        registration,
        new AtomicTextWriter(),
        static () => @"C:\fts-test-app.exe",
        starter);

    [TestMethod]
    public async Task ChangeAsync_CommitFailureRollsBackPublishedDirectoryAndResumes()
    {
        var source = CreateManagedSource();
        var quiesce = new RecordingQuiesce();
        var service = CreateService(new FakeRegistration(commitSucceeds: false), static _ => true);

        var result = await service.ChangeAsync(source, Path.Combine(_root, "next"), quiesce);

        Assert.IsFalse(result.ExitApplication);
        StringAssert.Contains(result.Error, "无法登记");
        Assert.AreEqual(1, quiesce.QuiesceCount);
        Assert.AreEqual(1, quiesce.ResumeCount, "失败必须恢复运行。");
        Assert.IsFalse(quiesce.PreparedForExit);
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")), "源目录必须完好。");
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "next", "悬浮中转站")), "已发布目录必须按形状守卫删除。");
    }

    [TestMethod]
    public async Task ChangeAsync_MarkerFailureRollsBackDirectoryAndRestoresRegistration()
    {
        var source = CreateManagedSource();
        // 标记写入失败：目标 Data 目录只读化难以跨平台模拟，改为占据目标目录内的
        // 标记文件路径，使原子写无法就位。
        var parent = Path.Combine(_root, "next");
        var registration = new FakeRegistration(commitSucceeds: true);
        var service = new DataDirectoryChangeService(
            new DataDirectoryRelocator(),
            registration,
            new BlockingMarkerWriter(Path.Combine(parent, "悬浮中转站", "Data")),
            static () => @"C:\fts-test-app.exe",
            static _ => true);

        var result = await service.ChangeAsync(source, parent, new RecordingQuiesce());

        Assert.IsFalse(result.ExitApplication);
        StringAssert.Contains(result.Error, "迁移标记");
        Assert.AreEqual(2, registration.Commits.Count, "标记失败必须把登记写回旧目录。");
        Assert.AreEqual(
            (Path.Combine(_root, "old", "悬浮中转站", "Data"), Path.Combine(_root, "old")),
            registration.Commits[^1]);
        Assert.IsFalse(Directory.Exists(Path.Combine(parent, "悬浮中转站")), "发布目录必须回滚删除。");
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")));
    }

    [TestMethod]
    public async Task ChangeAsync_SpawnFailureKeepsRegistrationAndExits()
    {
        var source = CreateManagedSource();
        var parent = Path.Combine(_root, "next");
        var registration = new FakeRegistration(commitSucceeds: true);
        var service = CreateService(registration, static _ => false);
        var quiesce = new RecordingQuiesce();

        var result = await service.ChangeAsync(source, parent, quiesce);

        Assert.IsTrue(result.ExitApplication, "登记已指向新目录：直接退出，提示手动启动。");
        StringAssert.Contains(result.Error, "自动重启失败");
        Assert.AreEqual(1, registration.Commits.Count, "重启失败不回滚登记。");
        Assert.AreEqual((Path.Combine(parent, "悬浮中转站", "Data"), parent), registration.Commits[0]);
        Assert.IsTrue(quiesce.PreparedForExit);
        Assert.IsTrue(File.Exists(Path.Combine(parent, "悬浮中转站", "Data", "board.json")), "新目录内容必须就位。");
        Assert.AreEqual(0, quiesce.ResumeCount);
    }

    [TestMethod]
    public async Task ChangeAsync_HappyPathCommitsRegistrationWritesMarkerAndSpawns()
    {
        var source = CreateManagedSource();
        var parent = Path.Combine(_root, "next");
        var registration = new FakeRegistration(commitSucceeds: true);
        ProcessStartInfo? spawned = null;
        var service = new DataDirectoryChangeService(
            new DataDirectoryRelocator(),
            registration,
            new AtomicTextWriter(),
            static () => @"C:\fts-test-app.exe",
            info =>
            {
                spawned = info;
                return true;
            });
        var quiesce = new RecordingQuiesce();

        var result = await service.ChangeAsync(source, parent, quiesce);

        Assert.IsTrue(result.ExitApplication);
        Assert.IsNull(result.Error);
        Assert.AreEqual((Path.Combine(parent, "悬浮中转站", "Data"), parent), registration.Commits[0]);
        Assert.IsTrue(File.Exists(Path.Combine(parent, "悬浮中转站", "Data", RelocationCleanupMarker.FileName)));
        Assert.IsNotNull(spawned);
        Assert.AreEqual(DataDirectoryChangeService.RelaunchArgument, spawned!.Arguments);
        Assert.AreEqual(0, quiesce.ResumeCount);
        Assert.IsTrue(quiesce.PreparedForExit);
        // 源目录保留给延迟清理（本次未启动新实例执行清理）。
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")));
    }

    [TestMethod]
    public async Task ChangeAsync_PlanRefusalFailsWithoutQuiesce()
    {
        var source = CreateManagedSource();
        var service = CreateService(new FakeRegistration(true), static _ => true);
        var quiesce = new RecordingQuiesce();

        var result = await service.ChangeAsync(source, Path.Combine(_root, "old"), quiesce);

        Assert.IsFalse(result.ExitApplication);
        Assert.AreEqual(0, quiesce.QuiesceCount, "预检拒绝不进入静默期。");
        StringAssert.Contains(result.Error, "当前内容目录");
    }

    private sealed class BlockingMarkerWriter(string blockedDirectory) : IAtomicTextWriter
    {
        public Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            if (path.StartsWith(blockedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("marker blocked");
            }

            return Task.CompletedTask;
        }
    }
}
