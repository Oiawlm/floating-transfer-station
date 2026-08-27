using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class PanelStateMachineTests
{
    [TestMethod]
    public void Switch_ThenCollapse_KeepsActiveCategoryWhileClosingPanel()
    {
        var state = new PanelStateMachine();

        state.Switch(BoardCategory.Prompt);
        state.LeaveSurface();

        Assert.IsTrue(state.TryCollapse());
        Assert.IsFalse(state.IsExpanded);
        Assert.AreEqual(BoardCategory.Prompt, state.ActiveCategory);
    }

    [TestMethod]
    public void HoverCommit_RequiresPointerToRemainInsideSurface()
    {
        var state = new PanelStateMachine();

        state.BeginHover(BoardCategory.Reference);
        Assert.IsTrue(state.TryCommitHover(out var committedCategory));
        Assert.AreEqual(BoardCategory.Reference, committedCategory);
        Assert.AreEqual(BoardCategory.Reference, state.ActiveCategory);
        Assert.IsTrue(state.IsExpanded);

        state.LeaveSurface();
        Assert.IsTrue(state.TryCollapse());
        state.BeginHover(BoardCategory.Inbox);
        state.LeaveSurface();

        Assert.IsNull(state.PendingCategory);
        state.EnterSurface();
        Assert.IsFalse(state.TryCommitHover(out _));
        Assert.AreEqual(BoardCategory.Reference, state.ActiveCategory);
        Assert.IsFalse(state.IsExpanded);
    }

    [TestMethod]
    public void CancelHover_IgnoresLateLeaveForSupersededCategory()
    {
        var state = new PanelStateMachine();

        state.BeginHover(BoardCategory.Reference);
        state.BeginHover(BoardCategory.Inbox);

        Assert.IsFalse(state.TryCancelHover(BoardCategory.Reference));
        Assert.AreEqual(BoardCategory.Inbox, state.PendingCategory);
        Assert.IsTrue(state.TryCommitHover(out var committedCategory));
        Assert.AreEqual(BoardCategory.Inbox, committedCategory);
    }

    [TestMethod]
    public void CategoryEntryPoints_RejectUndefinedCategory()
    {
        var state = new PanelStateMachine();
        var invalid = (BoardCategory)99;

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => state.BeginHover(invalid));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => state.Switch(invalid));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Drag_KeepsPanelOpenAcrossPointerLeaveUntilDragEnds()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginDrag();
        state.LeaveSurface();

        Assert.IsFalse(state.TryCollapse());
        state.EndDrag();
        Assert.IsTrue(state.TryCollapse());
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    public void DragState_IsObservableWithoutAllowingExternalMutation()
    {
        var state = new PanelStateMachine();

        Assert.IsFalse(state.IsDragInProgress);
        state.BeginDrag();
        Assert.IsTrue(state.IsDragInProgress);
        state.EndDrag();
        Assert.IsFalse(state.IsDragInProgress);
        Assert.IsFalse(typeof(PanelStateMachine)
            .GetProperty(nameof(PanelStateMachine.IsDragInProgress))!
            .CanWrite);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void RapidEnterAfterLeaveCancelsPendingCollapseCondition()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.CustomerOriginal);
        state.LeaveSurface();
        state.EnterSurface();
        state.Switch(BoardCategory.Inbox);

        Assert.IsFalse(state.TryCollapse());
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    public void WindowSettings_NormalizeToVisibleWorkArea()
    {
        var settings = new WindowSettings(5000, 5000, 5000).Normalize(1920, 1040);

        Assert.AreEqual(WindowSettings.MaxPanelWidth, settings.PanelWidth);
        Assert.AreEqual(1040, settings.WindowHeight);
        Assert.AreEqual(0, settings.Top);
    }

    [TestMethod]
    public void ViewModel_UsesSameLockedNamesForTabsAndPanels()
    {
        var viewModel = new MainWindowViewModel(new BoardService());

        CollectionAssert.AreEqual(
            new[] { "客户原图", "对标图", "提示词", "待分类" },
            viewModel.Categories.Select(category => category.DisplayName).ToArray());
        viewModel.Activate(BoardCategory.Reference);
        Assert.AreEqual("对标图", viewModel.ActivePanel!.DisplayName);
    }

    [TestMethod]
    public void Activate_MarksExactlyOneCategoryActive()
    {
        var viewModel = new MainWindowViewModel(new BoardService());
        viewModel.SetPanelExpanded(true);

        viewModel.Activate(BoardCategory.CustomerOriginal);
        viewModel.Activate(BoardCategory.Reference);

        Assert.AreEqual(BoardCategory.Reference, viewModel.ActivePanel!.Category);
        Assert.AreEqual(1, viewModel.Categories.Count(category => category.IsActive));
        Assert.IsTrue(viewModel.Categories.Single(
            category => category.Category == BoardCategory.Reference).IsActive);
        Assert.IsFalse(viewModel.Categories.Single(
            category => category.Category == BoardCategory.CustomerOriginal).IsActive);
    }

    [TestMethod]
    public void CollapsingPanel_HidesActiveFeedbackWithoutForgettingActivePanel()
    {
        var viewModel = new MainWindowViewModel(new BoardService());
        viewModel.Activate(BoardCategory.Reference);

        Assert.IsFalse(viewModel.IsPanelExpanded);
        Assert.AreEqual(0, viewModel.Categories.Count(category => category.IsActive));

        viewModel.SetPanelExpanded(true);
        Assert.AreEqual(1, viewModel.Categories.Count(category => category.IsActive));

        viewModel.SetPanelExpanded(false);
        Assert.AreEqual(BoardCategory.Reference, viewModel.ActivePanel!.Category);
        Assert.AreEqual(0, viewModel.Categories.Count(category => category.IsActive));
    }

    [TestMethod]
    public void DefaultCaptureCategory_InitializesInboxAndSwitchesWithoutChangingActivePanel()
    {
        var state = new DefaultCaptureCategoryState();
        var viewModel = new MainWindowViewModel(new BoardService(), state);
        viewModel.Activate(BoardCategory.CustomerOriginal);

        Assert.AreEqual(BoardCategory.Inbox, viewModel.DefaultCapturePanel.Category);
        Assert.AreEqual(1, viewModel.Categories.Count(category => category.IsDefaultCapture));

        Assert.IsTrue(viewModel.SetDefaultCaptureCategory(BoardCategory.Prompt));
        Assert.IsFalse(viewModel.SetDefaultCaptureCategory(BoardCategory.Prompt));

        Assert.AreEqual(BoardCategory.Prompt, state.Current);
        Assert.AreEqual(BoardCategory.Prompt, viewModel.DefaultCapturePanel.Category);
        Assert.AreEqual(BoardCategory.CustomerOriginal, viewModel.ActivePanel!.Category);
        Assert.AreEqual(1, viewModel.Categories.Count(category => category.IsDefaultCapture));
    }

    [TestMethod]
    public void ActiveDefaultAndDropTarget_CanBelongToThreeDifferentCategories()
    {
        var viewModel = new MainWindowViewModel(
            new BoardService(),
            new DefaultCaptureCategoryState());
        viewModel.SetPanelExpanded(true);
        viewModel.Activate(BoardCategory.CustomerOriginal);
        viewModel.SetDefaultCaptureCategory(BoardCategory.Prompt);
        viewModel.Categories.Single(
            category => category.Category == BoardCategory.Reference).IsDropTarget = true;

        Assert.IsTrue(viewModel.Categories.Single(
            category => category.Category == BoardCategory.CustomerOriginal).IsActive);
        Assert.IsTrue(viewModel.Categories.Single(
            category => category.Category == BoardCategory.Prompt).IsDefaultCapture);
        Assert.IsTrue(viewModel.Categories.Single(
            category => category.Category == BoardCategory.Reference).IsDropTarget);
    }

    [TestMethod]
    public void CategoryFlags_RaisePropertyChangedOnlyWhenValueChanges()
    {
        var category = new CategoryViewModel(
            BoardCategory.Inbox,
            new System.Collections.ObjectModel.ObservableCollection<BoardItem>());
        var changedProperties = new List<string?>();
        category.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        category.IsActive = true;
        category.IsActive = true;
        category.IsDefaultCapture = true;
        category.IsDropTarget = true;

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(category.IsActive),
                nameof(category.IsDefaultCapture),
                nameof(category.IsDropTarget)
            },
            changedProperties);
    }
}
