using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 受管数据目录搬迁原语的契约测试（契约 #46 双形状矩阵）：形状规则（新
/// <c>&lt;所选&gt;/Data</c>＋旧嵌套形状只读兼容）、拒绝矩阵、暂存复制/校验/发布/
/// 清理与失败模式（源始终完好）、删除安全矩阵。与安装器
/// PrepareDataDirectoryMigration 同协议。
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

    private string CreateLegacySource(params (string RelativePath, string Content)[] files) =>
        CreateSource(Path.Combine(_root, "old", "悬浮中转站", "Data"), files);

    private string CreateDirectSource(params (string RelativePath, string Content)[] files) =>
        CreateSource(Path.Combine(_root, "old", "Data"), files);

    private static string CreateSource(string source, params (string RelativePath, string Content)[] files)
    {
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
    public void NormalizeManagedDataDirectory_AcceptsBothShapes()
    {
        var legacy = Path.Combine(_root, "store", "悬浮中转站", "Data");
        Assert.AreEqual(legacy, DataDirectorySettings.NormalizeManagedDataDirectory(legacy));
        var direct = Path.Combine(_root, "store", "Data");
        Assert.AreEqual(direct, DataDirectorySettings.NormalizeManagedDataDirectory(direct));
    }

    [TestMethod]
    public void NormalizeManagedDataDirectory_RejectsRootAdjacentAndUnmanagedShapes()
    {
        var root = Path.GetPathRoot(_root)!;
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory(
            Path.Combine(root, "悬浮中转站", "Data")), "旧形状：中间层直下盘根必须拒绝。");
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory(
            Path.Combine(root, "Data")), "新形状：Data 直下盘根必须拒绝。");
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory(
            Path.Combine(_root, "store", "Other")), "叶子名必须恰为 Data。");
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory("relative\\Data"));
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory(@"\\?\C:\Store\Data"));
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory("  "));
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
    public void BuildDataDirectory_CombinesDirectShape()
    {
        var parent = Path.Combine(_root, "store");
        Assert.AreEqual(
            Path.Combine(parent, "Data"),
            DataDirectorySettings.BuildDataDirectory(parent + Path.DirectorySeparatorChar));
    }

    [TestMethod]
    public void BuildDataDirectory_RejectsInvalidParent()
    {
        Assert.ThrowsExactly<ArgumentException>(
            () => DataDirectorySettings.BuildDataDirectory(Path.GetPathRoot(_root)!));
    }

    [TestMethod]
    public void GetDataParentDirectory_StripsPerShape()
    {
        var legacy = Path.Combine(_root, "store", "悬浮中转站", "Data");
        Assert.AreEqual(
            Path.Combine(_root, "store"),
            DataDirectorySettings.GetDataParentDirectory(legacy));
        var direct = Path.Combine(_root, "store", "Data");
        Assert.AreEqual(
            Path.Combine(_root, "store"),
            DataDirectorySettings.GetDataParentDirectory(direct));
        Assert.ThrowsExactly<ArgumentException>(
            () => DataDirectorySettings.GetDataParentDirectory(Path.Combine(_root, "store", "Other")));
    }

    [TestMethod]
    public void IsLegacyManagedDataDirectory_DiscriminatesByParentLeaf()
    {
        Assert.IsTrue(DataDirectorySettings.IsLegacyManagedDataDirectory(
            Path.Combine(_root, "store", "悬浮中转站", "Data")));
        Assert.IsFalse(DataDirectorySettings.IsLegacyManagedDataDirectory(
            Path.Combine(_root, "store", "Data")));
        Assert.IsFalse(DataDirectorySettings.IsLegacyManagedDataDirectory(
            Path.Combine(_root, "store", "Other")));
    }

    [TestMethod]
    public void Plan_RejectsUnchangedPath()
    {
        var source = CreateDirectSource(("board.json", "{}"));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.UnchangedPath,
            PlanRefusal(source, Path.GetDirectoryName(source)!));
    }

    [TestMethod]
    public void Plan_LegacySourceSameParentMigratesToDirectShape()
    {
        // 契约 #46 存量迁移矩阵：设置内重选同父目录＝显式迁移到新形状 <同父>/Data。
        var source = CreateLegacySource(("board.json", "{}"));
        var userParent = DataDirectorySettings.GetDataParentDirectory(source);
        var plan = _relocator.Plan(source, userParent);
        Assert.AreEqual(Path.Combine(userParent, "Data"), plan.TargetDataDirectory);
    }

    [TestMethod]
    public void Plan_RejectsExistingTargetDataDirectory()
    {
        var source = CreateLegacySource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.Combine(parent, "Data"));
        Assert.AreEqual(DataDirectoryRelocationRefusal.TargetExists, PlanRefusal(source, parent));
    }

    [TestMethod]
    public void Plan_RejectsTargetInsideCurrentParent()
    {
        var legacy = CreateLegacySource(("board.json", "{}"));
        var legacyContainer = Directory.GetParent(legacy)!.FullName;
        // 旧形状容器本身作为新选目录：目标 <容器>/Data 与源同路径，按不变路径拒绝。
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.UnchangedPath,
            PlanRefusal(legacy, legacyContainer));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetInsideCurrentParent,
            PlanRefusal(legacy, Path.Combine(legacyContainer, "deeper")));

        var direct = CreateDirectSource(("board.json", "{}"));
        var directParent = Path.GetDirectoryName(direct)!;
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.UnchangedPath,
            PlanRefusal(direct, directParent));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetInsideCurrentParent,
            PlanRefusal(direct, Path.Combine(directParent, "deeper")));
    }

    [TestMethod]
    public void Plan_RejectsTargetParentInsideSourceData()
    {
        var source = CreateLegacySource(("board.json", "{}"));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetInsideSourceData,
            PlanRefusal(source, Path.Combine(source, "nested")));
    }

    [TestMethod]
    public void Plan_RejectsMissingSource()
    {
        var missingLegacy = Path.Combine(_root, "missing", "悬浮中转站", "Data");
        Assert.AreEqual(DataDirectoryRelocationRefusal.SourceMissing, PlanRefusal(missingLegacy, NewTargetParent()));
        var missingDirect = Path.Combine(_root, "missing", "Data");
        Assert.AreEqual(DataDirectoryRelocationRefusal.SourceMissing, PlanRefusal(missingDirect, NewTargetParent()));
    }

    [TestMethod]
    public void Plan_RejectsUnwritableTargetParentBlockedByFile()
    {
        var source = CreateLegacySource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.GetDirectoryName(parent)!);
        File.WriteAllText(parent, "occupied");
        Assert.AreEqual(DataDirectoryRelocationRefusal.TargetParentNotWritable, PlanRefusal(source, parent));
    }

    [TestMethod]
    public async Task StageVerifyPublish_LegacySourceMovesIntoDirectShapeTarget()
    {
        var source = CreateLegacySource(
            ("board.json", """{"items":[]}"""),
            ("reviews/2026-10-01.md", "# 复盘"),
            ("plugins/sample/plugin.json", "{}"));
        Directory.CreateDirectory(Path.Combine(source, "images", "empty-nested"));
        var originalTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(source, "board.json"), originalTimestamp);

        var parent = NewTargetParent();
        var plan = _relocator.Plan(source, parent);
        Assert.AreEqual(Path.Combine(parent, "Data"), plan.TargetDataDirectory);
        Assert.AreEqual(
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar),
            plan.TargetParentDirectory);
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
    public async Task StageVerifyPublish_DirectSourceMovesIntoDirectShapeTarget()
    {
        // 契约 #46：新形状登记（本版本起产生）作为迁移源同样全协议可用。
        var source = CreateDirectSource(("board.json", "{}"));
        var parent = NewTargetParent();
        var plan = _relocator.Plan(source, parent);
        await _relocator.StageAsync(plan);
        _relocator.VerifyStaged(plan);
        _relocator.Publish(plan);

        Assert.IsTrue(File.Exists(Path.Combine(parent, "Data", "board.json")));
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")), "源目录必须完好。");
    }

    [TestMethod]
    public async Task StagePublish_AllowsPreExistingNonEmptyTargetParentAndKeepsSiblings()
    {
        // 契约 #46：新选目录可预先存在且含其他内容，同级内容必须原样保留。
        var source = CreateLegacySource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(parent);
        var siblingFile = Path.Combine(parent, "user-notes.txt");
        File.WriteAllText(siblingFile, "keep");
        var siblingDirectory = Path.Combine(parent, "UnrelatedFiles");
        Directory.CreateDirectory(siblingDirectory);

        var plan = _relocator.Plan(source, parent);
        await _relocator.StageAsync(plan);
        _relocator.VerifyStaged(plan);
        _relocator.Publish(plan);

        Assert.IsTrue(File.Exists(Path.Combine(parent, "Data", "board.json")));
        Assert.AreEqual("keep", File.ReadAllText(siblingFile));
        Assert.IsTrue(Directory.Exists(siblingDirectory));
    }

    [TestMethod]
    public async Task VerifyStaged_RejectsTruncatedCopyAndDiscardKeepsTargetParent()
    {
        var source = CreateLegacySource(("board.json", new string('x', 1024)));
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
        Assert.IsTrue(Directory.Exists(plan.TargetParentDirectory), "所选目录是用户目录，即使已空也绝不移除。");
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")));
    }

    [TestMethod]
    public async Task StageAsync_PreCanceledTokenCleansStagingAndThrows()
    {
        var source = CreateLegacySource(("board.json", "{}"));
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
        var source = CreateLegacySource(("board.json", "{}"));
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
    public void TryDeleteManagedDataDirectory_LegacyShapeRemovesEmptyIntermediateParent()
    {
        var source = CreateLegacySource(("board.json", "{}"));

        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _));
        Assert.IsFalse(Directory.Exists(source));
        Assert.IsFalse(Directory.Exists(Directory.GetParent(source)!.FullName), "空中间层应一并移除。");
        Assert.IsTrue(Directory.Exists(_root), "用户所选父目录必须保留。");
        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _), "目标不存在视为幂等成功。");
    }

    [TestMethod]
    public void TryDeleteManagedDataDirectory_DirectShapeRemovesOnlyDataAndKeepsParent()
    {
        var parent = Path.Combine(_root, "keep-parent");
        var source = CreateSource(Path.Combine(parent, "Data"), ("board.json", "{}"));
        var siblingFile = Path.Combine(parent, "user-notes.txt");
        File.WriteAllText(siblingFile, "keep");

        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _));
        Assert.IsFalse(Directory.Exists(source));
        Assert.IsTrue(Directory.Exists(parent), "新形状：用户所选父目录即使已空也绝不移除（契约 #46）。");
        Assert.IsTrue(File.Exists(siblingFile), "同级文件必须保留。");
    }

    [TestMethod]
    public void TryDeleteManagedDataDirectory_RefusesUnmanagedAndRootAdjacentTargets()
    {
        var other = CreateSource(Path.Combine(_root, "plain", "Other"), ("keep.txt", "keep"));
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory(other, out _),
            "叶子名不是 Data 的目录必须拒绝删除。");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "plain", "Other", "keep.txt")));

        var root = Path.GetPathRoot(_root)!;
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory(
                Path.Combine(root, "Data"), out _),
            "Data 直下盘符根必须拒绝删除。");
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory(
                Path.Combine(root, "悬浮中转站", "Data"), out _),
            "旧形状中间层直下盘符根必须拒绝删除。");
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory("relative\\Data", out _),
            "相对路径必须拒绝删除。");
    }

    [TestMethod]
    public async Task StageAsync_ReportsProgressPerFile()
    {
        var source = CreateLegacySource(
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
        var source = CreateLegacySource(("a.txt", "12345"), ("sub/b.txt", "123"));
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
