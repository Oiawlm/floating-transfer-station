using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class CategoryDisplayOrderTests
{
    [TestMethod]
    public void DisplayOrder_WithoutCustomization_FallsBackToCatalogOrder()
    {
        Assert.AreSame(
            BoardCategoryCatalog.Ordered,
            WindowSettings.Default.DisplayOrder);
    }

    [TestMethod]
    public void DisplayOrder_InvalidCustomOrder_FallsBackToCatalogOrder()
    {
        // 非法（数量不符/重复/未定义成员）一律回落默认序，校验只在读取端一次做齐。
        var invalidOrders = new[]
        {
            new[] { BoardCategory.Prompt, BoardCategory.Reference },
            new[] { BoardCategory.Prompt, BoardCategory.Prompt, BoardCategory.Reference, BoardCategory.Inbox },
            new[] { (BoardCategory)99, BoardCategory.Prompt, BoardCategory.Reference, BoardCategory.Inbox }
        };

        foreach (var order in invalidOrders)
        {
            var settings = new WindowSettings(360, 640, 80) { CategoryOrder = order };

            Assert.AreSame(
                BoardCategoryCatalog.Ordered,
                settings.DisplayOrder,
                $"非法顺序 {string.Join(",", order)} 必须回落默认序。");
        }
    }

    [TestMethod]
    public void DisplayOrder_ValidCustomOrder_IsReturned()
    {
        var order = new[]
        {
            BoardCategory.Inbox,
            BoardCategory.Prompt,
            BoardCategory.Reference,
            BoardCategory.CustomerOriginal
        };
        var settings = WindowSettings.Default.WithCategoryOrder(order);

        CollectionAssert.AreEqual(order, settings.DisplayOrder.ToArray());
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesCustomCategoryOrder()
    {
        var storeOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var settings = WindowSettings.Default.WithCategoryOrder(
        [
            BoardCategory.Inbox,
            BoardCategory.CustomerOriginal,
            BoardCategory.Prompt,
            BoardCategory.Reference
        ]);

        var restored = System.Text.Json.JsonSerializer.Deserialize<WindowSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings, storeOptions),
            storeOptions);

        CollectionAssert.AreEqual(
            settings.DisplayOrder.ToArray(),
            restored!.DisplayOrder.ToArray());
        // 名字与顺序零耦合：顺序定制不产生名字快照。
        Assert.IsNull(restored.CategoryNames);
    }

    [TestMethod]
    public void ResetToDefault_PreservesCustomCategoryOrder()
    {
        var settings = WindowSettings.Default.WithCategoryOrder(
        [
            BoardCategory.Inbox,
            BoardCategory.Prompt,
            BoardCategory.Reference,
            BoardCategory.CustomerOriginal
        ]);

        var reset = settings.ResetToDefault(1600, 900);

        CollectionAssert.AreEqual(
            settings.DisplayOrder.ToArray(),
            reset.DisplayOrder.ToArray(),
            "标签顺序是用户定制，与窗口几何一样不随恢复默认重置。");
    }

    [TestMethod]
    public void ViewModel_AppliesCustomOrderAtConstruction()
    {
        var viewModel = new MainWindowViewModel(
            new BoardService(),
            WindowSettings.Default.WithCategoryOrder(
            [
                BoardCategory.Inbox,
                BoardCategory.Prompt,
                BoardCategory.Reference,
                BoardCategory.CustomerOriginal
            ]));

        CollectionAssert.AreEqual(
            new[]
            {
                BoardCategory.Inbox,
                BoardCategory.Prompt,
                BoardCategory.Reference,
                BoardCategory.CustomerOriginal
            },
            viewModel.Categories.Select(panel => panel.Category).ToArray());
    }

    [TestMethod]
    public void ViewModel_ApplyCategoryOrder_RearrangesSameInstancesAndNotifies()
    {
        var board = new BoardService();
        board.AddText("内容", BoardCategory.Inbox);
        var viewModel = new MainWindowViewModel(board, WindowSettings.Default);
        var originalPanels = viewModel.Categories.ToArray();
        var notified = new List<string>();
        viewModel.PropertyChanged += (_, args) => notified.Add(args.PropertyName ?? string.Empty);

        viewModel.ApplyCategoryOrder(
        [
            BoardCategory.Prompt,
            BoardCategory.Inbox,
            BoardCategory.CustomerOriginal,
            BoardCategory.Reference
        ]);

        CollectionAssert.AreEqual(
            new[]
            {
                BoardCategory.Prompt,
                BoardCategory.Inbox,
                BoardCategory.CustomerOriginal,
                BoardCategory.Reference
            },
            viewModel.Categories.Select(panel => panel.Category).ToArray());
        // 实例身份被 ActivePanel/默认接收/复盘表面切换依赖：只许重排、绝不新建。
        CollectionAssert.AreEquivalent(
            originalPanels,
            viewModel.Categories.ToArray(),
            "重排必须复用既有 CategoryViewModel 实例。");
        CollectionAssert.AreEqual(
            new[] { nameof(MainWindowViewModel.Categories) },
            notified);
    }

    [TestMethod]
    public void ViewModel_ApplyCategoryOrder_RejectsInvalidOrderAndKeepsIdentity()
    {
        var viewModel = new MainWindowViewModel(new BoardService(), WindowSettings.Default);
        var before = viewModel.Categories.ToArray();

        viewModel.ApplyCategoryOrder([BoardCategory.Prompt, BoardCategory.Prompt, BoardCategory.Reference, BoardCategory.Inbox]);

        CollectionAssert.AreEqual(
            BoardCategoryCatalog.Ordered.ToArray(),
            viewModel.Categories.Select(panel => panel.Category).ToArray());
        CollectionAssert.AreEqual(before, viewModel.Categories.ToArray());
    }
}
