using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 受管数据目录搬迁原语的契约测试：形状规则、拒绝矩阵、暂存复制/校验/发布/清理
/// 与失败模式（源始终完好）。与安装器 PrepareDataDirectoryMigration 同协议。
/// </summary>
[TestClass]
public sealed class DataDirectoryRelocationTests
{
    private string _root = string.Empty;
    private DataDirectoryRelocator _relocator = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "fts-relocation-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _relocator = new DataDirectoryRelocator();
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

    private string CreateManagedSource(params (string RelativePath, string Content)[] files)
    {
        var source = Path.Combine(_root, "old", "悬浮中转站", "Data");
        Directory.CreateDirectory(source);
        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(source, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return source;
    }

    private string NewTargetParent() => Path.Combine(_root, "new-" + Guid.NewGuid().ToString("N")[..8]);

    [TestMethod]
    public void NormalizeManagedDataDirectory_RejectsGrandparentRoot()
    {
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory(
            Path.Combine(Path.GetPathRoot(_root)!, "悬浮中转站", "Data")));
        Assert.AreEqual(
            Path.Combine(_root, "悬浮中转站", "Data"),
            DataDirectorySettings.NormalizeManagedDataDirectory(
                Path.Combine(_root, "悬浮中转站", "Data")));
    }

    [TestMethod]
    public void IsValidDataParentShape_RejectsRelativeRootAndDevicePaths()
    {
        Assert.IsFalse(DataDirectorySettings.IsValidDataParentShape("relative\\path"));
        Assert.IsFalse(DataDirectorySettings.IsValidDataParentShape("  "));
        Assert.IsFalse(DataDirectorySettings.IsValidDataParentShape(Path.GetPathRoot(_root)!));
        Assert.IsFalse(DataDirectorySettings.IsValidDataParentShape(@"\\?\C:\Store"));
        Assert.IsFalse(DataDirectorySettings.IsValidDataParentShape(@"\\.\C:\Store"));
        Assert.IsTrue(DataDirectorySettings.IsValidDataParentShape(Path.Combine(_root, "somewhere")));
    }

    [TestMethod]
    public void BuildDataDirectory_CombinesManagedShape()
    {
        var parent = Path.Combine(_root, "store");
        Assert.AreEqual(
            Path.Combine(parent, "悬浮中转站", "Data"),
            DataDirectorySettings.BuildDataDirectory(parent + Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void BuildDataDirectory_RejectsInvalidParent()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => DataDirectorySettings.BuildDataDirectory(Path.GetPathRoot(_root)!));
    }

    [TestMethod]
    public void Plan_RejectsUnchangedPath()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.UnchangedPath,
            PlanRefusal(source, Directory.GetParent(Directory.GetParent(source)!.FullName)!.FullName));
    }

    [TestMethod]
    public void Plan_RejectsExistingTargetDataDirectory()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.Combine(parent, "悬浮中转站", "Data"));
        Assert.AreEqual(DataDirectoryRelocationRefusal.TargetExists, PlanRefusal(source, parent));
    }

    [TestMethod]
    public void Plan_RejectsExistingTargetManagedParent()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.Combine(parent, "悬浮中转站"));
        Assert.AreEqual(DataDirectoryRelocationRefusal.TargetManagedParentExists, PlanRefusal(source, parent));
    }

    [TestMethod]
    public void Plan_RejectsTargetInsideCurrentManagedParent()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        var managedParent = Directory.GetParent(source)!.FullName;
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetInsideCurrentManagedParent,
            PlanRefusal(source, managedParent));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetInsideCurrentManagedParent,
            PlanRefusal(source, Path.Combine(managedParent, "deeper")));
    }

    [TestMethod]
    public void Plan_RejectsTargetParentInsideSourceData()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetInsideSourceData,
            PlanRefusal(source, Path.Combine(source, "nested")));
    }

    [TestMethod]
    public void Plan_RejectsMissingSource()
    {
        var missing = Path.Combine(_root, "missing", "悬浮中转站", "Data");
        Assert.AreEqual(DataDirectoryRelocationRefusal.SourceMissing, PlanRefusal(missing, NewTargetParent()));
    }

    [TestMethod]
    public void Plan_RejectsUnwritableTargetParentBlockedByFile()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.GetDirectoryName(parent)!);
        File.WriteAllText(parent, "occupied");
        Assert.AreEqual(DataDirectoryRelocationRefusal.TargetParentNotWritable, PlanRefusal(source, parent));
    }

    [TestMethod]
    public async Task StageVerifyPublish_HappyPathCopiesEverythingAndKeepsSource()
    {
        var source = CreateManagedSource(
            ("board.json", """{"items":[]}"""),
            ("reviews/2026-10-01.md", "# 复盘"),
            ("plugins/sample/plugin.json", "{}"));
        Directory.CreateDirectory(Path.Combine(source, "images", "empty-nested"));
        var originalTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(source, "board.json"), originalTimestamp);

        var parent = NewTargetParent();
        var plan = _relocator.Plan(source, parent);
        await _relocator.StageAsync(plan);
        _relocator.VerifyStaged(plan);
        _relocator.Publish(plan);

        var target = plan.TargetDataDirectory;
        Assert.AreEqual("""{"items":[]}""", File.ReadAllText(Path.Combine(target, "board.json")));
        Assert.AreEqual("# 复盘", File.ReadAllText(Path.Combine(target, "reviews", "2026-10-01.md")));
        Assert.IsTrue(Directory.Exists(Path.Combine(target, "images", "empty-nested")), "空目录必须一并复制。");
        Assert.AreEqual(
            originalTimestamp,
            File.GetLastWriteTimeUtc(Path.Combine(target, "board.json")),
            "文件最后写入时间必须保留。");
        Assert.IsFalse(Directory.Exists(plan.StagingDirectory), "发布后暂存目录应消失。");
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")), "复制语义：源目录必须完好。");
        Assert.IsTrue(Directory.Exists(Path.Combine(source, "images", "empty-nested")));
    }

    [TestMethod]
    public async Task VerifyStaged_RejectsTruncatedCopyAndDiscardCleansUp()
    {
        var source = CreateManagedSource(("board.json", new string('x', 1024)));
        var parent = NewTargetParent();
        var plan = _relocator.Plan(source, parent);
        await _relocator.StageAsync(plan);
        File.WriteAllText(
            Path.Combine(plan.StagingDirectory, "board.json"),
            new string('x', 10));

        Assert.AreEqual(
            DataDirectoryRelocationRefusal.VerificationFailed,
            Assert.ThrowsExactly<DataDirectoryRelocationException>(() => _relocator.VerifyStaged(plan)).Reason);

        _relocator.Discard(plan);
        Assert.IsFalse(Directory.Exists(plan.StagingDirectory));
        Assert.IsFalse(Directory.Exists(plan.TargetManagedParent), "受管父为空时应一并移除。");
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")));
    }

    [TestMethod]
    public async Task StageAsync_PreCanceledTokenCleansStagingAndThrows()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        var plan = _relocator.Plan(source, NewTargetParent());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => _relocator.StageAsync(plan, null, canceled.Token));

        Assert.IsFalse(Directory.Exists(plan.StagingDirectory));
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")));
    }

    [TestMethod]
    public async Task Publish_ConflictingTargetThrowsAndKeepsStagingForDiscard()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        var plan = _relocator.Plan(source, NewTargetParent());
        await _relocator.StageAsync(plan);
        Directory.CreateDirectory(plan.TargetDataDirectory);

        Assert.AreEqual(
            DataDirectoryRelocationRefusal.PublishFailed,
            Assert.ThrowsExactly<DataDirectoryRelocationException>(() => _relocator.Publish(plan)).Reason);
        Assert.IsTrue(Directory.Exists(plan.StagingDirectory));

        _relocator.Discard(plan);
        Assert.IsFalse(Directory.Exists(plan.StagingDirectory));
    }

    [TestMethod]
    public void TryDeleteManagedDataDirectory_RemovesOnlyShapeValidTrees()
    {
        var source = CreateManagedSource(("board.json", "{}"));

        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _));
        Assert.IsFalse(Directory.Exists(source));
        Assert.IsFalse(Directory.Exists(Directory.GetParent(source)!.FullName), "空受管父应一并移除。");

        Assert.IsFalse(DataDirectoryRelocator.TryDeleteManagedDataDirectory(
            Path.Combine(_root, "plain", "Data"),
            out _),
            "形状不符的目录必须拒绝删除。");
        Assert.IsTrue(Directory.Exists(_root));

        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _), "目标不存在视为幂等成功。");
    }

    [TestMethod]
    public async Task StageAsync_ReportsProgressPerFile()
    {
        var source = CreateManagedSource(
            ("a.txt", "aaa"),
            ("sub/b.txt", "bbbb"));
        var plan = _relocator.Plan(source, NewTargetParent());
        var updates = new List<DataDirectoryCopyProgress>();

        // 用同步收集器而非 Progress<T>：Progress 经 SynchronizationContext 异步
        // 投递，单次 Task.Yield 在 CI 负载下排不干队列（曾致偶发假失败）；本测
        // 试点是「逐文件上报」的契约，上报时序无关。
        await _relocator.StageAsync(plan, new CollectingProgress(updates));

        Assert.AreEqual(2, updates.Count);
        Assert.AreEqual(7, updates[^1].TotalBytes);
        Assert.AreEqual(2, updates[^1].CopiedFiles);
    }

    private sealed class CollectingProgress(List<DataDirectoryCopyProgress> updates)
        : IProgress<DataDirectoryCopyProgress>
    {
        public void Report(DataDirectoryCopyProgress update) => updates.Add(update);
    }

    [TestMethod]
    public void Plan_MeasuresSourceSize()
    {
        var source = CreateManagedSource(("a.txt", "12345"), ("sub/b.txt", "123"));
        var plan = _relocator.Plan(source, NewTargetParent());
        Assert.AreEqual(8, plan.SourceSizeBytes);
    }

    private DataDirectoryRelocationRefusal PlanRefusal(string source, string targetParent)
    {
        var exception = Assert.ThrowsExactly<DataDirectoryRelocationException>(
            () => _relocator.Plan(source, targetParent));
        return exception.Reason;
    }
}
