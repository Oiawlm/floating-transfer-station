using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 受管数据目录搬迁原语的契约测试（数据目录模型统一）：形状规则（唯一受管形状
/// <c>&lt;所选&gt;/悬浮中转站</c>＋两个旧形状＝待迁移旧形）、拒绝矩阵、暂存复制/
/// 校验/发布/清理与失败模式（源始终完好）、就位迁移（旧嵌套形状→同父数据根）、
/// 删除安全矩阵。与安装器 PrepareDataDirectoryMigration 同协议。
/// </summary>
[TestClass]
public sealed class DataDirectoryRelocationTests
{
    private string _root = string.Empty;
    private DataDirectoryRelocator _relocator = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(TestTempRoot.Root, "fts-relocation-tests-" + Guid.NewGuid().ToString("N"));
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

    private string CreateLegacyNestedSource(params (string RelativePath, string Content)[] files) =>
        CreateSource(Path.Combine(_root, "old", "悬浮中转站", "Data"), files);

    private string CreateLegacyDirectSource(params (string RelativePath, string Content)[] files) =>
        CreateSource(Path.Combine(_root, "old", "Data"), files);

    private string CreateManagedSource(params (string RelativePath, string Content)[] files) =>
        CreateSource(Path.Combine(_root, "old", "悬浮中转站"), files);

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
    public void NormalizeManagedDataDirectory_AcceptsOnlyUnifiedRootShape()
    {
        var managed = Path.Combine(_root, "store", "悬浮中转站");
        Assert.AreEqual(managed, DataDirectorySettings.NormalizeManagedDataDirectory(managed));

        // 两个旧形状＝待迁移旧形：不在受管形状入口接受，经登记统一入口识别。
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory(
            Path.Combine(_root, "store", "Data")));
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory(
            Path.Combine(_root, "store", "悬浮中转站", "Data")));
    }

    [TestMethod]
    public void NormalizeRegisteredDataDirectory_AcceptsManagedAndBothLegacyShapes()
    {
        var managed = Path.Combine(_root, "store", "悬浮中转站");
        Assert.AreEqual(managed, DataDirectorySettings.NormalizeRegisteredDataDirectory(managed));
        var legacyDirect = Path.Combine(_root, "store", "Data");
        Assert.AreEqual(legacyDirect, DataDirectorySettings.NormalizeRegisteredDataDirectory(legacyDirect));
        var legacyNested = Path.Combine(_root, "store", "悬浮中转站", "Data");
        Assert.AreEqual(legacyNested, DataDirectorySettings.NormalizeRegisteredDataDirectory(legacyNested));
    }

    [TestMethod]
    public void NormalizeManagedDataDirectory_RejectsRootAdjacentAndUnmanagedShapes()
    {
        var root = Path.GetPathRoot(_root)!;
        Assert.IsNull(DataDirectorySettings.NormalizeManagedDataDirectory(
            Path.Combine(root, "悬浮中转站")), "受管形状：数据根直下盘根必须拒绝。");
        Assert.IsNull(DataDirectorySettings.NormalizeRegisteredDataDirectory(
            Path.Combine(root, "Data")), "旧直下形状：Data 直下盘根必须拒绝。");
        Assert.IsNull(DataDirectorySettings.NormalizeRegisteredDataDirectory(
            Path.Combine(root, "悬浮中转站", "Data")), "旧嵌套形状：中间层直下盘根必须拒绝。");
        Assert.IsNull(DataDirectorySettings.NormalizeRegisteredDataDirectory(
            Path.Combine(_root, "store", "Other")), "叶子名必须恰为受管/旧形叶子。");
        Assert.IsNull(DataDirectorySettings.NormalizeRegisteredDataDirectory("relative\\Data"));
        Assert.IsNull(DataDirectorySettings.NormalizeRegisteredDataDirectory(@"\\?\C:\Store\悬浮中转站"));
        Assert.IsNull(DataDirectorySettings.NormalizeRegisteredDataDirectory("  "));
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
    public void BuildDataDirectory_CombinesUnifiedRootShape()
    {
        var parent = Path.Combine(_root, "store");
        Assert.AreEqual(
            Path.Combine(parent, "悬浮中转站"),
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
        var managed = Path.Combine(_root, "store", "悬浮中转站");
        Assert.AreEqual(
            Path.Combine(_root, "store"),
            DataDirectorySettings.GetDataParentDirectory(managed));
        var legacyDirect = Path.Combine(_root, "store", "Data");
        Assert.AreEqual(
            Path.Combine(_root, "store"),
            DataDirectorySettings.GetDataParentDirectory(legacyDirect));
        var legacyNested = Path.Combine(_root, "store", "悬浮中转站", "Data");
        Assert.AreEqual(
            Path.Combine(_root, "store"),
            DataDirectorySettings.GetDataParentDirectory(legacyNested));
        Assert.ThrowsExactly<ArgumentException>(
            () => DataDirectorySettings.GetDataParentDirectory(Path.Combine(_root, "store", "Other")));
    }

    [TestMethod]
    public void IsLegacyManagedDataDirectory_DiscriminatesBothLegacyShapes()
    {
        Assert.IsTrue(DataDirectorySettings.IsLegacyManagedDataDirectory(
            Path.Combine(_root, "store", "Data")), "旧直下形状＝待迁移旧形。");
        Assert.IsTrue(DataDirectorySettings.IsLegacyManagedDataDirectory(
            Path.Combine(_root, "store", "悬浮中转站", "Data")), "旧嵌套形状＝待迁移旧形。");
        Assert.IsFalse(DataDirectorySettings.IsLegacyManagedDataDirectory(
            Path.Combine(_root, "store", "悬浮中转站")), "受管形状不是旧形。");
        Assert.IsFalse(DataDirectorySettings.IsLegacyManagedDataDirectory(
            Path.Combine(_root, "store", "Other")));
    }

    [TestMethod]
    public void Plan_RejectsUnchangedPath()
    {
        var source = CreateManagedSource(("board.json", "{}"));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.UnchangedPath,
            PlanRefusal(source, Path.GetDirectoryName(source)!));
    }

    [TestMethod]
    public void Plan_LegacyDirectSourceSameParentMigratesToManagedRoot()
    {
        // 同父形状归一：旧直下形状 <父>/Data → <父>/悬浮中转站。
        var source = CreateLegacyDirectSource(("board.json", "{}"));
        var userParent = DataDirectorySettings.GetDataParentDirectory(source);
        var plan = _relocator.Plan(source, userParent);
        Assert.AreEqual(Path.Combine(userParent, "悬浮中转站"), plan.TargetDataDirectory);
        Assert.IsNull(plan.SupersededSourceParentDirectory);
    }

    [TestMethod]
    public void Plan_LegacyNestedSourceSameParentPlansHoist()
    {
        // 同父形状归一：旧嵌套形状 <父>/悬浮中转站/Data → 目标＝中间层路径本身，
        // 发布采用旧根整体让位（就位迁移）。
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        var userParent = DataDirectorySettings.GetDataParentDirectory(source);
        var plan = _relocator.Plan(source, userParent);
        Assert.AreEqual(Path.Combine(userParent, "悬浮中转站"), plan.TargetDataDirectory);
        Assert.IsNotNull(plan.SupersededSourceParentDirectory);
        Assert.IsTrue(Path.GetFileName(plan.SupersededSourceParentDirectory!)
            .StartsWith(".fts-superseded-", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Plan_HoistRefusedWhenMiddleLayerHasOtherContent()
    {
        // 目标（中间层）已有源 Data 之外的内容＝目标已存在非空，拒绝就位迁移。
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        File.WriteAllText(
            Path.Combine(Directory.GetParent(source)!.FullName, "user-notes.txt"),
            "keep");
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetExists,
            PlanRefusal(source, DataDirectorySettings.GetDataParentDirectory(source)));
    }

    [TestMethod]
    public void Plan_RejectsExistingNonEmptyTarget()
    {
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.Combine(parent, "悬浮中转站"));
        File.WriteAllText(Path.Combine(parent, "悬浮中转站", "occupied.txt"), "keep");
        Assert.AreEqual(DataDirectoryRelocationRefusal.TargetExists, PlanRefusal(source, parent));
    }

    [TestMethod]
    public void Plan_AllowsPreExistingEmptyTarget()
    {
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.Combine(parent, "悬浮中转站"));
        var plan = _relocator.Plan(source, parent);
        Assert.AreEqual(Path.Combine(parent, "悬浮中转站"), plan.TargetDataDirectory);
    }

    [TestMethod]
    public void Plan_RejectsTargetInsideCurrentParentOutsideSameParentNormalization()
    {
        var legacy = CreateLegacyNestedSource(("board.json", "{}"));
        // 目标＝源所在目录（中间层）的更深层子目录：不是同父归一，拒绝。
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetInsideCurrentParent,
            PlanRefusal(legacy, Path.Combine(Directory.GetParent(legacy)!.FullName, "deeper")));

        var legacyDirect = CreateLegacyDirectSource(("board.json", "{}"));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.TargetInsideCurrentParent,
            PlanRefusal(legacyDirect, Path.Combine(DataDirectorySettings.GetDataParentDirectory(legacyDirect), "deeper")));

        var managed = CreateManagedSource(("board.json", "{}"));
        Assert.AreEqual(
            DataDirectoryRelocationRefusal.UnchangedPath,
            PlanRefusal(managed, Path.GetDirectoryName(managed)!));
    }

    [TestMethod]
    public void Plan_RejectsTargetParentInsideSourceData()
    {
        var source = CreateLegacyNestedSource(("board.json", "{}"));
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
        var missingManaged = Path.Combine(_root, "missing", "悬浮中转站");
        Assert.AreEqual(DataDirectoryRelocationRefusal.SourceMissing, PlanRefusal(missingManaged, NewTargetParent()));
    }

    [TestMethod]
    public void Plan_RejectsUnwritableTargetParentBlockedByFile()
    {
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.GetDirectoryName(parent)!);
        File.WriteAllText(parent, "occupied");
        Assert.AreEqual(DataDirectoryRelocationRefusal.TargetParentNotWritable, PlanRefusal(source, parent));
    }

    [TestMethod]
    public async Task StageVerifyPublish_LegacySourceMovesIntoManagedRootTarget()
    {
        var source = CreateLegacyNestedSource(
            ("board.json", """{"items":[]}"""),
            ("reviews/2026-10-01.md", "# 复盘"),
            ("plugins/sample/plugin.json", "{}"));
        Directory.CreateDirectory(Path.Combine(source, "images", "empty-nested"));
        var originalTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(source, "board.json"), originalTimestamp);

        var parent = NewTargetParent();
        var plan = _relocator.Plan(source, parent);
        Assert.AreEqual(Path.Combine(parent, "悬浮中转站"), plan.TargetDataDirectory);
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
    public async Task StageVerifyPublish_ManagedSourceMovesIntoManagedRootTarget()
    {
        // 受管形状登记作为迁移源同样全协议可用。
        var source = CreateManagedSource(("board.json", "{}"));
        var parent = NewTargetParent();
        var plan = _relocator.Plan(source, parent);
        await _relocator.StageAsync(plan);
        _relocator.VerifyStaged(plan);
        _relocator.Publish(plan);

        Assert.IsTrue(File.Exists(Path.Combine(parent, "悬浮中转站", "board.json")));
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")), "源目录必须完好。");
    }

    [TestMethod]
    public async Task StageVerifyPublish_HoistMovesDataIntoMiddleLayerPath()
    {
        // 就位迁移（旧嵌套形状归一）：中间层让位 → 暂存就位 → Data 落到
        // <父>/悬浮中转站 根下；让位副本交延迟清理（GetRelocationCleanupTarget）。
        var source = CreateLegacyNestedSource(
            ("board.json", "{}"),
            ("plugins/sample/plugin.json", "{}"));
        var userParent = DataDirectorySettings.GetDataParentDirectory(source);
        var middleLayer = Directory.GetParent(source)!.FullName;

        var plan = _relocator.Plan(source, userParent);
        await _relocator.StageAsync(plan);
        _relocator.VerifyStaged(plan);
        _relocator.Publish(plan);

        var target = plan.TargetDataDirectory;
        Assert.AreEqual(middleLayer, target, "就位迁移目标＝旧中间层路径。");
        Assert.IsTrue(File.Exists(Path.Combine(target, "board.json")), "数据文件直下新根。");
        Assert.IsTrue(File.Exists(Path.Combine(target, "plugins", "sample", "plugin.json")),
            "插件目录随迁移整体移动。");
        Assert.IsFalse(Directory.Exists(Path.Combine(target, "Data")), "新根内不得残留 Data 层。");
        Assert.IsFalse(Directory.Exists(plan.StagingDirectory), "发布后暂存目录应消失。");

        var hold = plan.SupersededSourceParentDirectory!;
        Assert.IsTrue(Directory.Exists(hold), "旧根整体让位到 .fts-superseded-*。");
        Assert.IsTrue(File.Exists(Path.Combine(hold, "Data", "board.json")), "让位副本保留旧数据。");
        Assert.AreEqual(
            Path.Combine(hold, "Data"),
            DataDirectoryRelocator.GetRelocationCleanupTarget(plan),
            "延迟清理目标＝让位副本内的 Data（守卫按旧直下形状放行）。");
    }

    [TestMethod]
    public async Task Publish_HoistRefusedWhenMiddleLayerChangedSincePlan()
    {
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        var userParent = DataDirectorySettings.GetDataParentDirectory(source);
        var plan = _relocator.Plan(source, userParent);
        await _relocator.StageAsync(plan);
        // Plan 之后发布之前，中间层被放入额外内容：就位前提失效。
        File.WriteAllText(
            Path.Combine(Directory.GetParent(source)!.FullName, "intruder.txt"),
            "keep");

        Assert.AreEqual(
            DataDirectoryRelocationRefusal.PublishFailed,
            Assert.ThrowsExactly<DataDirectoryRelocationException>(() => _relocator.Publish(plan)).Reason);
        Assert.IsTrue(Directory.Exists(plan.StagingDirectory), "未发布：暂存保留供 Discard。");
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")), "源目录必须完好。");

        _relocator.Discard(plan);
        Assert.IsFalse(Directory.Exists(plan.StagingDirectory));
    }

    [TestMethod]
    public async Task Publish_DeletesPreExistingEmptyTargetBeforeMoving()
    {
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        var parent = NewTargetParent();
        Directory.CreateDirectory(Path.Combine(parent, "悬浮中转站"));
        var plan = _relocator.Plan(source, parent);
        await _relocator.StageAsync(plan);
        _relocator.VerifyStaged(plan);
        _relocator.Publish(plan);

        Assert.IsTrue(File.Exists(Path.Combine(parent, "悬浮中转站", "board.json")));
    }

    [TestMethod]
    public async Task StagePublish_AllowsPreExistingNonEmptyTargetParentAndKeepsSiblings()
    {
        // 所选目录可预先存在且含其他内容，同级内容必须原样保留。
        var source = CreateLegacyNestedSource(("board.json", "{}"));
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

        Assert.IsTrue(File.Exists(Path.Combine(parent, "悬浮中转站", "board.json")));
        Assert.AreEqual("keep", File.ReadAllText(siblingFile));
        Assert.IsTrue(Directory.Exists(siblingDirectory));
    }

    [TestMethod]
    public async Task VerifyStaged_RejectsTruncatedCopyAndDiscardKeepsTargetParent()
    {
        var source = CreateLegacyNestedSource(("board.json", new string('x', 1024)));
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
        var source = CreateLegacyNestedSource(("board.json", "{}"));
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
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        var plan = _relocator.Plan(source, NewTargetParent());
        await _relocator.StageAsync(plan);
        Directory.CreateDirectory(plan.TargetDataDirectory);
        File.WriteAllText(Path.Combine(plan.TargetDataDirectory, "occupied.txt"), "keep");

        Assert.AreEqual(
            DataDirectoryRelocationRefusal.PublishFailed,
            Assert.ThrowsExactly<DataDirectoryRelocationException>(() => _relocator.Publish(plan)).Reason);
        Assert.IsTrue(Directory.Exists(plan.StagingDirectory));

        _relocator.Discard(plan);
        Assert.IsFalse(Directory.Exists(plan.StagingDirectory));
    }

    [TestMethod]
    public async Task TryRollbackPublish_HoistRestoresSourceDirectory()
    {
        // 就位迁移发布后登记失败：删除已发布目标＋让位副本移回＝源目录恢复原状。
        var source = CreateLegacyNestedSource(("board.json", "{}"));
        var userParent = DataDirectorySettings.GetDataParentDirectory(source);
        var plan = _relocator.Plan(source, userParent);
        await _relocator.StageAsync(plan);
        _relocator.VerifyStaged(plan);
        _relocator.Publish(plan);

        Assert.IsTrue(DataDirectoryRelocator.TryRollbackPublish(plan, out _));
        Assert.IsTrue(File.Exists(Path.Combine(source, "board.json")), "源目录原路恢复。");
        Assert.IsFalse(Directory.Exists(plan.SupersededSourceParentDirectory!), "让位副本已移回。");
        Assert.IsFalse(Directory.Exists(Path.Combine(userParent, "悬浮中转站", "board.json")),
            "发布副本已按形状守卫删除。");
    }

    [TestMethod]
    public void TryDeleteManagedDataDirectory_ManagedRootRemovesOnlyRootAndKeepsParent()
    {
        var parent = Path.Combine(_root, "keep-parent");
        var source = CreateSource(Path.Combine(parent, "悬浮中转站"), ("board.json", "{}"));
        var siblingFile = Path.Combine(parent, "user-notes.txt");
        File.WriteAllText(siblingFile, "keep");

        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _));
        Assert.IsFalse(Directory.Exists(source));
        Assert.IsTrue(Directory.Exists(parent), "受管形状：用户所选父目录即使已空也绝不移除。");
        Assert.IsTrue(File.Exists(siblingFile), "同级文件必须保留。");
    }

    [TestMethod]
    public void TryDeleteManagedDataDirectory_LegacyDirectRemovesOnlyDataAndKeepsParent()
    {
        var parent = Path.Combine(_root, "keep-parent");
        var source = CreateSource(Path.Combine(parent, "Data"), ("board.json", "{}"));

        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _));
        Assert.IsFalse(Directory.Exists(source));
        Assert.IsTrue(Directory.Exists(parent), "旧直下形状：用户所选父目录即使已空也绝不移除。");
    }

    [TestMethod]
    public void TryDeleteManagedDataDirectory_LegacyNestedRemovesEmptyIntermediateParent()
    {
        var source = CreateLegacyNestedSource(("board.json", "{}"));

        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _));
        Assert.IsFalse(Directory.Exists(source));
        Assert.IsFalse(Directory.Exists(Directory.GetParent(source)!.FullName), "空中间层应一并移除。");
        Assert.IsTrue(Directory.Exists(_root), "用户所选父目录必须保留。");
        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _), "目标不存在视为幂等成功。");
    }

    [TestMethod]
    public void TryDeleteManagedDataDirectory_RemovesEmptySupersededWrapperAfterData()
    {
        // 就位迁移让位副本的延迟清理：删 Data 后空的 .fts-superseded-* 包装一并移除。
        var wrapper = Path.Combine(_root, ".fts-superseded-20261105000000-1");
        var source = CreateSource(Path.Combine(wrapper, "Data"), ("board.json", "{}"));

        Assert.IsTrue(DataDirectoryRelocator.TryDeleteManagedDataDirectory(source, out _));
        Assert.IsFalse(Directory.Exists(source));
        Assert.IsFalse(Directory.Exists(wrapper), "空让位包装目录应一并移除。");
    }

    [TestMethod]
    public void TryDeleteManagedDataDirectory_RefusesUnmanagedAndRootAdjacentTargets()
    {
        var other = CreateSource(Path.Combine(_root, "plain", "Other"), ("keep.txt", "keep"));
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory(other, out _),
            "叶子名不受管/非旧形的目录必须拒绝删除。");
        Assert.IsTrue(File.Exists(Path.Combine(_root, "plain", "Other", "keep.txt")));

        var root = Path.GetPathRoot(_root)!;
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory(
                Path.Combine(root, "悬浮中转站"), out _),
            "受管形状数据根直下盘符根必须拒绝删除。");
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory(
                Path.Combine(root, "Data"), out _),
            "旧直下形状 Data 直下盘符根必须拒绝删除。");
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory(
                Path.Combine(root, "悬浮中转站", "Data"), out _),
            "旧嵌套形状中间层直下盘符根必须拒绝删除。");
        Assert.IsFalse(
            DataDirectoryRelocator.TryDeleteManagedDataDirectory("relative\\Data", out _),
            "相对路径必须拒绝删除。");
    }

    [TestMethod]
    public async Task StageAsync_ReportsProgressPerFile()
    {
        var source = CreateLegacyNestedSource(
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
        var source = CreateLegacyNestedSource(("a.txt", "12345"), ("sub/b.txt", "123"));
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
