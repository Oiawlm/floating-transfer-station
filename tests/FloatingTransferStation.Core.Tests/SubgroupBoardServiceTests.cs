using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Core.Tests;

/// <summary>
/// 分类内子分组（实验性，契约 #57）服务面：Reindex 派生边界/组头（仅 GroupedView）、
/// 稳定分区（置顶不动）、批量指派、持久化与撤销（BoardMutationService 管线）、
/// 装载守卫（缺字段/越界槽回落未指定）与快照克隆。
/// </summary>
[TestClass]
public sealed class SubgroupBoardServiceTests
{
    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [TestMethod]
    public void Reindex_GroupedModeDerivesSubgroupRegionMarkersFromAdjacency()
    {
        var board = new BoardService();
        var first = board.AddText("一", BoardCategory.Inbox);
        var second = board.AddText("二", BoardCategory.Inbox);
        var third = board.AddText("三", BoardCategory.Inbox);
        var fourth = board.AddText("四", BoardCategory.Inbox);
        // InsertAtTop 逆序入库：物理顺序 = 四, 三, 二, 一。
        var items = board.Items(BoardCategory.Inbox).ToArray();
        Assert.AreEqual(4, items.Length);
        items[0].Subgroup = null; // 四：未指定。
        items[1].Subgroup = 1;    // 三：重要组首。
        items[2].Subgroup = 1;    // 二：重要组内。
        items[3].Subgroup = 0;    // 一：待分类组首。

        board.SetSubgroupDisplay(SubgroupDisplayMode.GroupedView);

        Assert.IsFalse(items[0].StartsSubgroupRegion, "首卡无前邻，不起组。");
        Assert.IsTrue(items[1].StartsSubgroupRegion, "与前邻槽不同起组。");
        Assert.AreEqual("重要", items[1].SubgroupHeader, "组头按名清单解析（默认名）。");
        Assert.IsFalse(items[2].StartsSubgroupRegion, "同槽邻接不起组。");
        Assert.IsNull(items[2].SubgroupHeader);
        Assert.IsTrue(items[3].StartsSubgroupRegion);
        Assert.AreEqual("待分类", items[3].SubgroupHeader, "未指定组无组头、命名组有组头。");
        // 未指定组（items[0]）即使后邻命名组也不携带组头。
        Assert.IsNull(items[0].SubgroupHeader);

        // 名清单自定义即时反映到派生组头（settings 仍是单一事实源）。
        var settings = WindowSettings.Default.WithSubgroupName(BoardCategory.Inbox, 1, "急件");
        board.SetSubgroupDisplay(SubgroupDisplayMode.GroupedView, settings);
        Assert.AreEqual("急件", items[1].SubgroupHeader);
    }

    [TestMethod]
    public void Reindex_OffModeKeepsMarkersCleared()
    {
        var board = new BoardService();
        board.AddText("甲", BoardCategory.Inbox).Subgroup = 1;
        board.AddText("乙", BoardCategory.Inbox).Subgroup = 2;

        board.SetSubgroupDisplay(SubgroupDisplayMode.Off);
        foreach (var item in board.Items(BoardCategory.Inbox))
        {
            Assert.IsFalse(item.StartsSubgroupRegion, "Off 模式标记恒 false（触发器死路）。");
            Assert.IsNull(item.SubgroupHeader);
        }

        board.SetSubgroupDisplay(SubgroupDisplayMode.ChipRail);
        foreach (var item in board.Items(BoardCategory.Inbox))
        {
            Assert.IsFalse(item.StartsSubgroupRegion, "ChipRail 同样无组边界（形态互斥）。");
            Assert.IsNull(item.SubgroupHeader);
        }
    }

    [TestMethod]
    public void PartitionNormalRegionBySubgroup_StableSlotOrderPinnedUntouched()
    {
        var board = new BoardService();
        var pinned = board.AddText("置顶", BoardCategory.Inbox);
        pinned.Subgroup = 2;
        board.SetPinnedMany([pinned.Id], true);
        var normal1 = board.AddText("普通一", BoardCategory.Inbox);
        var normal0 = board.AddText("普通零", BoardCategory.Inbox);
        var normal2 = board.AddText("普通二", BoardCategory.Inbox);
        var normal3 = board.AddText("普通三", BoardCategory.Inbox);
        // 物理顺序：置顶, 普通三, 普通二, 普通零, 普通一。
        var snapshotBefore = board.Items(BoardCategory.Inbox).ToArray();
        Assert.IsTrue(snapshotBefore[0].IsPinned);
        normal1.Subgroup = 1;
        normal0.Subgroup = 1;
        normal2.Subgroup = null;
        normal3.Subgroup = 0;

        var change = board.PartitionNormalRegionBySubgroup(BoardCategory.Inbox);

        Assert.IsTrue(change.Changed);
        var after = board.Items(BoardCategory.Inbox).ToArray();
        Assert.AreEqual(5, after.Length);
        Assert.AreSame(pinned, after[0], "置顶区不动（置顶优先不分组）。");
        Assert.AreSame(normal2, after[1], "未指定 null 居首。");
        Assert.AreSame(normal3, after[2], "槽 0 次之。");
        Assert.AreSame(normal0, after[3], "槽 1 组内保持既有相对序（普通零物理在普通一前）。");
        Assert.AreSame(normal1, after[4]);
        Assert.AreEqual(0, pinned.Order);
        Assert.AreEqual(1, normal2.Order, "分区经 Reindex 重写持久化 Order。");
        Assert.AreEqual(3, normal0.Order);
        Assert.AreEqual(4, normal1.Order);

        // 已聚簇时再分区＝no-op（Changed=false）。
        var noOp = board.PartitionNormalRegionBySubgroup(BoardCategory.Inbox);
        Assert.IsFalse(noOp.Changed);

        // 撤销（服务层）：恢复原顺序。
        board.Undo(change);
        var restored = board.Items(BoardCategory.Inbox).ToArray();
        CollectionAssert.AreEqual(
            snapshotBefore.Select(item => item.Id).ToArray(),
            restored.Select(item => item.Id).ToArray(),
            "撤销恢复分区前整类顺序。");
    }

    [TestMethod]
    public async Task PartitionNormalRegionBySubgroup_IsUndoableAndPersists()
    {
        var board = new BoardService();
        var first = board.AddText("一", BoardCategory.Inbox);
        var second = board.AddText("二", BoardCategory.Inbox);
        var third = board.AddText("三", BoardCategory.Inbox);
        first.Subgroup = 1;
        second.Subgroup = 0;
        third.Subgroup = 1;
        var originalIds = board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray();
        var store = new CapturingStore();
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.PartitionSubgroupsAsync(BoardCategory.Inbox));
        Assert.AreEqual(1, service.PendingUndoCount, "分区进撤销栈（可撤销批）。");
        Assert.AreEqual(1, store.Saved.Count, "分区重写持久化 Order 并落盘一次。");
        var persistedIds = store.Saved[0].Items
            .Where(item => item.Category == BoardCategory.Inbox)
            .OrderBy(item => item.Order)
            .Select(item => item.Id)
            .ToArray();
        CollectionAssert.AreNotEqual(
            originalIds,
            persistedIds,
            "落盘快照的 Order 已是分区后的聚簇序。");
        Assert.AreEqual(3, persistedIds.Length);

        Assert.IsTrue(await service.UndoLastAsync());
        CollectionAssert.AreEqual(
            originalIds,
            board.Items(BoardCategory.Inbox).Select(item => item.Id).ToArray(),
            "撤销恢复分区前顺序。");
        Assert.AreEqual(2, store.Saved.Count, "撤销同样落盘。");
    }

    [TestMethod]
    public async Task AssignSubgroup_BatchUpdatesFieldAndPersists()
    {
        var board = new BoardService();
        var first = board.AddText("一", BoardCategory.Inbox);
        var second = board.AddText("二", BoardCategory.Inbox);
        var keeper = board.AddText("三", BoardCategory.Inbox);
        keeper.Subgroup = 2;
        var store = new CapturingStore();
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.AssignSubgroupAsync([first.Id, second.Id], 3));
        Assert.AreEqual(3, first.Subgroup);
        Assert.AreEqual(3, second.Subgroup);
        Assert.AreEqual(2, keeper.Subgroup, "批外条目不动。");
        Assert.AreEqual(1, store.Saved.Count);
        Assert.AreEqual(1, service.PendingUndoCount, "有实际变更的指派进撤销栈。");
        var persistedFirst = store.Saved[0].Items.Single(item => item.Id == first.Id);
        Assert.AreEqual(3, persistedFirst.Subgroup, "指派随快照持久化。");

        // 同槽幂等：不写盘、不入栈。
        Assert.IsTrue(await service.AssignSubgroupAsync([first.Id], 3));
        Assert.AreEqual(1, store.Saved.Count);
        Assert.AreEqual(1, service.PendingUndoCount, "幂等调用不追加撤销批。");
    }

    [TestMethod]
    public async Task AssignSubgroup_IsUndoable()
    {
        var board = new BoardService();
        var first = board.AddText("一", BoardCategory.Inbox);
        var second = board.AddText("二", BoardCategory.Inbox);
        second.Subgroup = 1;
        var store = new CapturingStore();
        var service = new BoardMutationService(board, store, _ => { });

        Assert.IsTrue(await service.AssignSubgroupAsync([first.Id, second.Id], 0));
        Assert.AreEqual(1, service.PendingUndoCount);
        Assert.IsTrue(await service.UndoLastAsync());
        Assert.IsNull(first.Subgroup, "撤销恢复各卡原始槽（null 保持 null）。");
        Assert.AreEqual(1, second.Subgroup, "撤销恢复各卡原始槽（1 恢复 1）。");
        Assert.AreEqual(2, store.Saved.Count, "撤销落盘一次。");
    }

    [TestMethod]
    public void Restore_LegacyJsonMissingSubgroupLoadsAsUnspecified()
    {
        // 旧 board.json 无 subgroup 字段：免迁移读为 null（SchemaVersion 保持 1）。
        var snapshot = JsonSerializer.Deserialize<BoardSnapshot>(
            """
            {
              "schemaVersion": 1,
              "items": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "kind": "Text",
                  "category": "Inbox",
                  "order": 0,
                  "createdAt": "2026-11-05T00:00:00Z",
                  "text": "旧数据"
                }
              ]
            }
            """,
            StoreOptions);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(1, snapshot.SchemaVersion);

        var board = new BoardService();
        board.Restore(snapshot!);

        var item = board.Items(BoardCategory.Inbox).Single();
        Assert.IsNull(item.Subgroup, "缺字段回落未指定。");
    }

    [TestMethod]
    public void Restore_SubgroupSlotOutOfRangeLoadsAsUnspecified()
    {
        var snapshot = JsonSerializer.Deserialize<BoardSnapshot>(
            """
            {
              "schemaVersion": 1,
              "items": [
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "kind": "Text",
                  "category": "Inbox",
                  "order": 0,
                  "createdAt": "2026-11-05T00:00:00Z",
                  "text": "越界七",
                  "subgroup": 7
                },
                {
                  "id": "33333333-3333-3333-3333-333333333333",
                  "kind": "Text",
                  "category": "Inbox",
                  "order": 1,
                  "createdAt": "2026-11-05T00:00:00Z",
                  "text": "越界负一",
                  "subgroup": -1
                }
              ]
            }
            """,
            StoreOptions);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(2, snapshot.Items.Count);

        var board = new BoardService();
        board.Restore(snapshot!);

        foreach (var item in board.Items(BoardCategory.Inbox))
        {
            Assert.IsNull(item.Subgroup, "越界槽号一律回落未指定（读取端一次做齐）。");
        }
    }

    [TestMethod]
    public void CreateSnapshot_ClonesSubgroupField()
    {
        var board = new BoardService();
        board.AddText("未指定", BoardCategory.Inbox);
        var assigned = board.AddText("已指派", BoardCategory.Inbox);
        assigned.Subgroup = 2;

        var snapshot = board.CreateSnapshot();

        Assert.IsNull(snapshot.Items.Single(item => item.Text == "未指定").Subgroup);
        Assert.AreEqual(2, snapshot.Items.Single(item => item.Id == assigned.Id).Subgroup);
        // 克隆是深拷贝槽值：改源不回写快照。
        assigned.Subgroup = 3;
        Assert.AreEqual(2, snapshot.Items.Single(item => item.Id == assigned.Id).Subgroup);
    }

    private sealed class CapturingStore : IBoardStore
    {
        public List<BoardSnapshot> Saved { get; } = [];

        public string ImagesDirectory { get; } = string.Empty;

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Saved.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}
