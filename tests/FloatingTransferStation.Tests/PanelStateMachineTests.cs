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
    public void TextEditing_KeepsPanelOpenAcrossPointerLeaveUntilEditingEnds()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginTextEditing();
        state.LeaveSurface();

        Assert.IsFalse(state.WouldCollapse);
        Assert.IsFalse(state.TryCollapse());
        state.EndTextEditing();
        Assert.IsTrue(state.WouldCollapse);
        Assert.IsTrue(state.TryCollapse());
        Assert.IsFalse(state.IsExpanded);
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    public void TextEditingState_IsObservableWithoutAllowingExternalMutation()
    {
        var state = new PanelStateMachine();

        Assert.IsFalse(state.IsTextEditingActive);
        state.BeginTextEditing();
        Assert.IsTrue(state.IsTextEditingActive);
        state.EndTextEditing();
        Assert.IsFalse(state.IsTextEditingActive);
        Assert.IsFalse(typeof(PanelStateMachine)
            .GetProperty(nameof(PanelStateMachine.IsTextEditingActive))!
            .CanWrite);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void PanelHold_KeepsPanelOpenAcrossPointerLeaveUntilHoldEnds()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginPanelHold();
        state.LeaveSurface();

        Assert.IsFalse(state.WouldCollapse);
        Assert.IsFalse(state.TryCollapse());
        state.EndPanelHold();
        Assert.IsTrue(state.WouldCollapse);
        Assert.IsTrue(state.TryCollapse());
        Assert.IsFalse(state.IsExpanded);
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    public void PanelHoldState_IsObservableWithoutAllowingExternalMutation()
    {
        var state = new PanelStateMachine();

        Assert.IsFalse(state.IsPanelHoldActive);
        state.BeginPanelHold();
        Assert.IsTrue(state.IsPanelHoldActive);
        state.EndPanelHold();
        Assert.IsFalse(state.IsPanelHoldActive);
        Assert.IsFalse(typeof(PanelStateMachine)
            .GetProperty(nameof(PanelStateMachine.IsPanelHoldActive))!
            .CanWrite);
    }

    [TestMethod]
    public void PanelHold_UnionsWithTextEditingAcrossPointerLeave()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginPanelHold();
        state.BeginTextEditing();
        state.LeaveSurface();

        Assert.IsFalse(state.TryCollapse());
        state.EndTextEditing();
        Assert.IsFalse(state.TryCollapse());
        state.EndPanelHold();
        Assert.IsTrue(state.TryCollapse());
        Assert.IsFalse(state.IsExpanded);
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    public void PanelHold_UnionsWithDragAcrossPointerLeave()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginPanelHold();
        state.BeginDrag();
        state.LeaveSurface();

        Assert.IsFalse(state.TryCollapse());
        state.EndDrag();
        Assert.IsFalse(state.TryCollapse());
        state.EndPanelHold();
        Assert.IsTrue(state.TryCollapse());
        Assert.IsFalse(state.IsExpanded);
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void PanelHold_SurvivesExternalDropCollapse()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginPanelHold();
        state.LeaveSurface();

        state.CollapseForExternalDrop();

        Assert.IsFalse(state.IsExpanded);
        Assert.IsTrue(state.IsPanelHoldActive);
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void OverlayInteraction_SingleReasonSuppressesCollapseUntilEnded()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginOverlayInteraction(PanelOverlayReason.Menu);
        state.LeaveSurface();

        Assert.IsFalse(state.WouldCollapse);
        Assert.IsFalse(state.TryCollapse());
        state.EndOverlayInteraction(PanelOverlayReason.Menu);
        Assert.IsTrue(state.WouldCollapse);
        Assert.IsTrue(state.TryCollapse());
        Assert.IsFalse(state.IsExpanded);
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void OverlayInteraction_OverlappingReasonsClearedOnlyWhenAllEnded()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginOverlayInteraction(PanelOverlayReason.Menu);
        state.BeginOverlayInteraction(PanelOverlayReason.ModalDialog);
        state.LeaveSurface();

        Assert.IsFalse(state.WouldCollapse);
        Assert.IsFalse(state.TryCollapse());
        state.EndOverlayInteraction(PanelOverlayReason.Menu);
        Assert.IsFalse(state.WouldCollapse, "对话框仍在：迟到的菜单关闭不得清掉对话框的持有。");
        Assert.IsFalse(state.TryCollapse());
        state.EndOverlayInteraction(PanelOverlayReason.ModalDialog);
        Assert.IsTrue(state.WouldCollapse, "全部覆盖交互结束后才解除抑制。");
        Assert.IsTrue(state.TryCollapse());
        Assert.IsFalse(state.IsExpanded);
        Assert.AreEqual(BoardCategory.Inbox, state.ActiveCategory);
    }

    [TestMethod]
    public void OverlayInteraction_BeginAndEndAreIdempotentPerReason()
    {
        var state = new PanelStateMachine();
        state.BeginOverlayInteraction(PanelOverlayReason.Menu);
        state.BeginOverlayInteraction(PanelOverlayReason.Menu);
        Assert.IsTrue(state.HasOverlayInteraction);

        state.EndOverlayInteraction(PanelOverlayReason.Menu);
        Assert.IsFalse(state.HasOverlayInteraction, "重复 Begin 同因不累计，一次 End 即清。");
        state.EndOverlayInteraction(PanelOverlayReason.Menu);
        Assert.IsFalse(state.HasOverlayInteraction, "多余 End 无副作用。");
    }

    [TestMethod]
    public void OverlayInteractionState_IsObservableWithoutAllowingExternalMutation()
    {
        var state = new PanelStateMachine();

        Assert.IsFalse(state.HasOverlayInteraction);
        state.BeginOverlayInteraction(PanelOverlayReason.ModalDialog);
        Assert.IsTrue(state.HasOverlayInteraction);
        state.EndOverlayInteraction(PanelOverlayReason.ModalDialog);
        Assert.IsFalse(state.HasOverlayInteraction);
        Assert.IsFalse(typeof(PanelStateMachine)
            .GetProperty(nameof(PanelStateMachine.HasOverlayInteraction))!
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

    // ---- 再展开抑制会话（#30，Issue #170）：布防=启动收起提交点记录原区域；
    // ---- 解除=轮询观测到指针离场；终止=观测离场或权威展开；捕获失败不布防 ----

    [TestMethod]
    public void ReexpandSuppression_ArmedAfterBegin_AndHoldsWhilePointerInside()
    {
        var state = new PanelStateMachine();
        state.BeginReexpandSuppression(100, 200, 500, 800);

        Assert.IsTrue(state.IsReexpandSuppressed);
        // 指针仍在收起前原区域内（含左上边界、不含右下边界）：多个观测周期持续抑制。
        Assert.IsFalse(state.ObservePointer(100, 200));
        Assert.IsFalse(state.ObservePointer(499, 799));
        Assert.IsFalse(state.ObservePointer(300, 500));
        Assert.IsTrue(state.IsReexpandSuppressed);
    }

    [TestMethod]
    public void ReexpandSuppression_ObservedPointerLeavingLiftsSuppression()
    {
        var state = new PanelStateMachine();
        state.BeginReexpandSuppression(100, 200, 500, 800);

        Assert.IsFalse(state.ObservePointer(300, 500), "区域内观测不解。");
        Assert.IsTrue(state.ObservePointer(500, 800), "区域外观测（右/下边界即外）解除并回报本次完成。");
        Assert.IsFalse(state.IsReexpandSuppressed);
        Assert.IsFalse(state.ObservePointer(100, 200), "解除后观测为无操作。");
    }

    [TestMethod]
    public void ReexpandSuppression_AuthoritativeExpandViaSwitchTerminatesSession()
    {
        var state = new PanelStateMachine();
        state.BeginReexpandSuppression(100, 200, 500, 800);

        state.Switch(BoardCategory.Inbox);

        Assert.IsTrue(state.IsExpanded);
        Assert.IsFalse(state.IsReexpandSuppressed, "权威展开（如热键）终止抑制会话。");
    }

    [TestMethod]
    public void ReexpandSuppression_ForceCollapseKeepsSessionArmed()
    {
        var state = new PanelStateMachine();
        state.Switch(BoardCategory.Inbox);
        state.BeginReexpandSuppression(100, 200, 500, 800);

        state.ForceCollapse();

        Assert.IsFalse(state.IsExpanded);
        Assert.IsTrue(state.IsReexpandSuppressed, "armed 与 collapsed 正交：强制收起不清闩。");
    }

    [TestMethod]
    public void ReexpandSuppression_DegenerateRectangleDoesNotArm()
    {
        var state = new PanelStateMachine();

        state.BeginReexpandSuppression(0, 0, 0, 0);
        Assert.IsFalse(state.IsReexpandSuppressed, "捕获失败形态（退化矩形）不布防（fail-open）。");

        state.BeginReexpandSuppression(500, 800, 100, 200);
        Assert.IsFalse(state.IsReexpandSuppressed, "倒置矩形同属退化形态，不布防。");
        Assert.IsFalse(state.ObservePointer(300, 500), "未布防时观测为无操作。");
    }

    [TestMethod]
    public void ReexpandSuppression_DegenerateRearmDisarmsStaleSession()
    {
        var state = new PanelStateMachine();
        state.BeginReexpandSuppression(100, 200, 500, 800);
        Assert.IsTrue(state.IsReexpandSuppressed);

        state.BeginReexpandSuppression(0, 0, 0, 0);

        Assert.IsFalse(state.IsReexpandSuppressed, "重布防遇退化矩形＝新会话捕获失败：旧会话的陈旧矩形一并解除（fail-open 完整语义）。");
    }

    [TestMethod]
    public void ReexpandSuppression_RearmingTakesLatestRectangle()
    {
        var state = new PanelStateMachine();
        state.BeginReexpandSuppression(100, 200, 500, 800);
        state.BeginReexpandSuppression(1000, 1200, 1400, 1800);

        Assert.IsTrue(state.ObservePointer(300, 500), "重复布防总取当次收起前矩形：旧矩形内的点已在当次矩形外，观测即解除。");

        state.BeginReexpandSuppression(1000, 1200, 1400, 1800);
        Assert.IsFalse(state.ObservePointer(1200, 1500), "当次矩形内保持抑制。");
        Assert.IsTrue(state.ObservePointer(999, 1500), "离开当次矩形即解除。");
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
    public void ViewModel_UsesSameDefaultNamesForTabsAndPanels()
    {
        var viewModel = new MainWindowViewModel(new BoardService());

        CollectionAssert.AreEqual(
            new[] { "图片", "文本1", "启动", "待分类" },
            viewModel.Categories.Select(category => category.DisplayName).ToArray());
        viewModel.Activate(BoardCategory.Reference);
        Assert.AreEqual("文本1", viewModel.ActivePanel!.DisplayName);
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

        Assert.IsTrue(viewModel.SetDefaultCaptureCategory(BoardCategory.CustomerOriginal));
        Assert.IsFalse(viewModel.SetDefaultCaptureCategory(BoardCategory.CustomerOriginal));

        Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
        Assert.AreEqual(BoardCategory.CustomerOriginal, viewModel.DefaultCapturePanel.Category);
        Assert.AreEqual(BoardCategory.CustomerOriginal, viewModel.ActivePanel!.Category);
        Assert.AreEqual(1, viewModel.Categories.Count(category => category.IsDefaultCapture));
    }

    [TestMethod]
    public void DefaultCaptureCategory_RejectsNonContentBucketRoles()
    {
        // #30（Issue #162）：默认接收只能落在内容桶——复盘与快捷启动标签拒绝。
        var state = new DefaultCaptureCategoryState();
        var viewModel = new MainWindowViewModel(new BoardService(), state);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => viewModel.SetDefaultCaptureCategory(BoardCategory.Reference));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => viewModel.SetDefaultCaptureCategory(BoardCategory.Prompt));
        Assert.AreEqual(BoardCategory.Inbox, state.Current);
        Assert.AreEqual(BoardCategory.Inbox, viewModel.DefaultCapturePanel.Category);
    }

    [TestMethod]
    public void DefaultCaptureCategory_LoadTimeRepairsNonContentBucketCurrent()
    {
        // 装载期修复（#30）：历史会话态指向非内容桶时，构造归位 Inbox
        // （反射播种历史值——Set 已有角色守卫无法直达）。
        var state = new DefaultCaptureCategoryState();
        var currentField = typeof(DefaultCaptureCategoryState).GetField(
            "_current",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(currentField);
        currentField.SetValue(state, (int)BoardCategory.Prompt);

        var viewModel = new MainWindowViewModel(new BoardService(), state);

        Assert.AreEqual(BoardCategory.Inbox, viewModel.DefaultCapturePanel.Category);
        Assert.AreEqual(
            1,
            viewModel.Categories.Count(category => category.IsDefaultCapture));
    }

    [TestMethod]
    public void ActiveDefaultAndDropTarget_CanBelongToThreeDifferentCategories()
    {
        var viewModel = new MainWindowViewModel(
            new BoardService(),
            new DefaultCaptureCategoryState());
        viewModel.SetPanelExpanded(true);
        viewModel.Activate(BoardCategory.CustomerOriginal);
        viewModel.SetDefaultCaptureCategory(BoardCategory.Inbox);
        viewModel.Categories.Single(
            category => category.Category == BoardCategory.Reference).IsDropTarget = true;

        Assert.IsTrue(viewModel.Categories.Single(
            category => category.Category == BoardCategory.CustomerOriginal).IsActive);
        Assert.IsTrue(viewModel.Categories.Single(
            category => category.Category == BoardCategory.Inbox).IsDefaultCapture);
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
