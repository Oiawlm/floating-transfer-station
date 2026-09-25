using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class MainWindowViewModelSearchTests
{
    [TestMethod]
    public void EnterExitSearch_TogglesStateAndClearsConditions()
    {
        var board = new BoardService();
        var viewModel = new MainWindowViewModel(board);

        Assert.IsFalse(viewModel.IsSearchActive);
        Assert.AreEqual(string.Empty, viewModel.SearchText);
        Assert.AreEqual(SearchTypeFilter.All, viewModel.SearchType);

        viewModel.EnterSearch();
        viewModel.SearchText = "关键词";
        viewModel.SearchType = SearchTypeFilter.Images;
        Assert.IsTrue(viewModel.IsSearchActive);

        viewModel.ExitSearch();

        Assert.IsFalse(viewModel.IsSearchActive);
        Assert.AreEqual(string.Empty, viewModel.SearchText);
        Assert.AreEqual(SearchTypeFilter.All, viewModel.SearchType);
    }

    [TestMethod]
    public void ExitSearch_WithoutActiveSearch_IsNoOp()
    {
        var board = new BoardService();
        var viewModel = new MainWindowViewModel(board);

        viewModel.ExitSearch();

        Assert.IsFalse(viewModel.IsSearchActive);
    }

    [TestMethod]
    public void SearchProperties_RaiseChangeNotification()
    {
        var board = new BoardService();
        var viewModel = new MainWindowViewModel(board);
        var notifications = new List<string?>();

        viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        viewModel.EnterSearch();
        viewModel.SearchText = "词";
        viewModel.SearchType = SearchTypeFilter.TextOnly;
        viewModel.ExitSearch();

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(MainWindowViewModel.IsSearchActive),
                nameof(MainWindowViewModel.SearchText),
                nameof(MainWindowViewModel.SearchType),
                nameof(MainWindowViewModel.IsSearchActive),
                nameof(MainWindowViewModel.SearchText),
                nameof(MainWindowViewModel.SearchType)
            },
            notifications);
    }
}
