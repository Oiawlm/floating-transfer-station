using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;

namespace FloatingTransferStation.Tests;

public sealed partial class MainWindowInteractionTests
{
    [STATestMethod]
    public void CategoryTab_NameEditingKeepsDefaultMarkerAboveTheEditor()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);

            Assert.IsTrue(customer.IsEditingName);
            Assert.IsTrue(FindDescendants<TextBox>(customerTab).Single().IsVisible);
            var markerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");
            // 2026-11-05 W2 重设计：标记由三同心圆靶心缩小为单段圆弧紫色圆环——
            // 锁定单圆几何与描边档（10×10 + 1.25，Data 含 A8,8 且无旧靶心内圈），
            // 杜绝回退到多层同心圆或字体字符歧义。
            var marker = FindDescendants<System.Windows.Shapes.Path>(customerTab)
                .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
            var markerData = marker.Data.ToString();
            StringAssert.Contains(markerData, "A8,8");
            StringAssert.DoesNotContain(markerData, "A5,5");
            StringAssert.DoesNotContain(markerData, "A1.5,1.5");
            Assert.AreEqual(1.25d, marker.StrokeThickness);
            Assert.AreEqual(10d, marker.Width);
            Assert.AreEqual(10d, marker.Height);
            Assert.AreEqual(customer.IsDefaultCapture ? 1d : 0d, marker.Opacity);
            Assert.IsTrue(
                Panel.GetZIndex(marker) > Panel.GetZIndex(FindDescendants<TextBox>(customerTab).Single()));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryTab_DefaultMarkerEvidenceBothThemes()
    {
        // 2026-11-03 W7（Issue #280）：默认接收标记由字符「◎」改为描边靶心 Path——
        // 浅/深双主题渲染证据（默认标记落在待分类标签上＝默认接收分类 Inbox）。
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_DEFAULT_MARKER_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_DEFAULT_MARKER_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            window.Show();
            window.Topmost = true;
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.AreEqual(BoardCategory.Inbox, viewModel.DefaultCapturePanel!.Category, "前置：默认接收＝待分类。");

            var markerStyle = (Style)window.FindResource("CategoryDefaultMarkerStyle");
            System.Windows.Shapes.Path MarkerOf(FloatingTransferStation.Views.MainWindow owner, CategoryViewModel category)
            {
                var tab = FindCategoryTab(owner, category);
                return FindDescendants<System.Windows.Shapes.Path>(tab)
                    .Single(candidate => ReferenceEquals(candidate.Style, markerStyle));
            }

            DesignThemeManager.Apply(window, DesignTheme.Light);
            CompleteLayout(window);
            Assert.AreEqual(1d, MarkerOf(window, viewModel.DefaultCapturePanel!).Opacity, "前置：浅色主题默认接收标记在场。");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "default-marker-light.png",
                "FTS_DEFAULT_MARKER_EVIDENCE_DIR");

            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            Assert.AreEqual(1d, MarkerOf(window, viewModel.DefaultCapturePanel!).Opacity, "前置：深色主题默认接收标记在场。");
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "default-marker-dark.png",
                "FTS_DEFAULT_MARKER_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryTab_RailLabelEvidenceBothThemes()
    {
        // 2026-11-04 轨道宽度回归修复证据（设计契约 #54）：轨道宽＝内容无关结构
        // 常量、标签两字/行自适应——{收起把手, 展开轨道}×{浅色, 深色}×
        // {默认名集, 6 字自定义名} 八宫格矩阵：「快捷启动」快捷/启动 两行居中、
        // 「重要/记录」单行、6 字名至多 3 行无截断、轨道不为内容加宽。
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_TAB_RAIL_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_TAB_RAIL_EVIDENCE_DIR，跳过发布证据产出。");
        }

        CaptureRailLabelEvidence(
            WindowSettings.Default,
            "default-names",
            "默认名集");
        CaptureRailLabelEvidence(
            new WindowSettings(
                WindowSettings.Default.PanelWidth,
                WindowSettings.Default.WindowHeight,
                WindowSettings.Default.Top,
                new Dictionary<BoardCategory, string> { [BoardCategory.Inbox] = "剪贴板收藏夹" }),
            "custom-six-char-name",
            "6 字自定义名（待分类＝剪贴板收藏夹）");
    }

    private void CaptureRailLabelEvidence(WindowSettings settings, string fileSuffix, string variantName)
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(
            new BoardService(),
            new RecordingBoardStore(directory.Root),
            settings,
            state);

        try
        {
            window.Show();
            window.Topmost = true;
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;

            // 收起把手：窗口默认收起态，宽＝轨道结构常量（内容无关，不随名长变化）。
            Assert.IsFalse(viewModel.IsPanelExpanded, $"{variantName} 前置：窗口默认收起。");
            Assert.IsTrue(viewModel.IsCollapsedCategoryHandleVisible, $"{variantName} 前置：把手在场。");
            Assert.AreEqual(
                WindowSettings.TabWidth, window.Width, 0.5,
                $"{variantName} 前置：收起宽＝轨道结构常量。");
            DesignThemeManager.Apply(window, DesignTheme.Light);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                $"handle-light-{fileSuffix}.png",
                "FTS_TAB_RAIL_EVIDENCE_DIR");
            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                $"handle-dark-{fileSuffix}.png",
                "FTS_TAB_RAIL_EVIDENCE_DIR");

            // 展开轨道：宽＝面板＋轨道结构常量，四标签两字/行自适应居中。
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Assert.AreEqual(
                settings.PanelWidth + WindowSettings.TabWidth, window.Width, 0.5,
                $"{variantName} 前置：展开宽＝面板＋轨道结构常量。");
            DesignThemeManager.Apply(window, DesignTheme.Light);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                $"rail-light-{fileSuffix}.png",
                "FTS_TAB_RAIL_EVIDENCE_DIR");
            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                $"rail-dark-{fileSuffix}.png",
                "FTS_TAB_RAIL_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryTabLabelAndEditor_LockDisplayTextFormattingForCrispSmallText()
    {
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            foreach (var styleKey in new[] { "CategoryLabelTextStyle", "CategoryNameEditorStyle" })
            {
                var style = (Style)window.FindResource(styleKey);
                var formatting = style.Setters.OfType<Setter>().SingleOrDefault(
                    setter => setter.Property == System.Windows.Media.TextOptions.TextFormattingModeProperty);
                Assert.IsNotNull(formatting, $"{styleKey} 必须显式设置文本格式化模式。");
                Assert.AreEqual(
                    System.Windows.Media.TextFormattingMode.Display,
                    formatting.Value,
                    $"{styleKey} 的小字号文本必须按像素对齐(Display)保持清晰。");
                var rendering = style.Setters.OfType<Setter>().SingleOrDefault(
                    setter => setter.Property == System.Windows.Media.TextOptions.TextRenderingModeProperty);
                Assert.IsNotNull(rendering, $"{styleKey} 必须显式设置文本渲染模式。");
                Assert.AreEqual(System.Windows.Media.TextRenderingMode.ClearType, rendering.Value);
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryTab_RailLabelRendersTwoCharactersPerLineAdaptively()
    {
        // 设计契约 #54（2026-11-04 回归修复）：标签＝两字宽文本列（30 DIP）＋
        // Wrap 自然两字/行——「快捷启动」快捷/启动 两行居中、「重要/记录」单行、
        // 6 字名至多 3 行无截断（全文仍在 Text 中）；改名编辑器收窄至嵌套可用宽
        // 36（58 轨道内容宽 46−Margin 5×2），无溢出。
        using var directory = new TestDirectory();
        var state = new DefaultCaptureCategoryState();
        var window = CreateWindow(directory, new BoardService(), state);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var labelStyle = (Style)window.FindResource("CategoryLabelTextStyle");
            TextBlock LabelOf(CategoryViewModel category)
            {
                var tab = FindCategoryTab(window, category);
                return FindDescendants<TextBlock>(tab)
                    .Single(candidate => ReferenceEquals(candidate.Style, labelStyle));
            }

            var prompt = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Prompt);
            var content = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var promptLabel = LabelOf(prompt);
            Assert.AreEqual("快捷启动", promptLabel.Text, "两字/行＝纯布局约束，文本零手术。");
            Assert.AreEqual(30d, promptLabel.Width, "文本列＝两字宽结构档 30。");
            // 行数以「单行参照高」自标定（TextBlock 无公开行数 API）：两字名＝单行基准。
            var contentLabel = LabelOf(content);
            Assert.AreEqual("重要", contentLabel.Text);
            var singleLineHeight = contentLabel.ActualHeight;
            Assert.IsTrue(singleLineHeight > 0, "单行参照标签必须已完成布局。");
            Assert.AreEqual(
                2 * singleLineHeight, promptLabel.ActualHeight, 2d,
                "四字默认名＝快捷/启动 两行。");

            prompt.ApplyDisplayName("剪贴板收藏夹");
            CompleteLayout(window);
            var sixCharLabel = LabelOf(prompt);
            Assert.AreEqual("剪贴板收藏夹", sixCharLabel.Text, "无截断：全文保留在文本里。");
            Assert.AreEqual(
                3 * singleLineHeight, sixCharLabel.ActualHeight, 2d,
                "6 字名＝恰 3 行（两字/行的上限）。");

            prompt.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(FindCategoryTab(window, prompt)).Single();
            Assert.IsTrue(editor.IsVisible);
            Assert.AreEqual(36d, editor.Width, "编辑器＝嵌套可用宽 36（内容宽 46−Margin 10）。");
            Assert.IsLessThanOrEqualTo(46d, editor.Width + editor.Margin.Left + editor.Margin.Right, "编辑器足迹不得溢出标签内容宽。");
            prompt.CancelNameEdit();
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameSave_UpdatesOnlySettingsAndTheSharedDisplayName()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);

            // Let the panel fade and category reveal reach their final visible state.
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(250));
            CompleteLayout(window);
            SaveVisualEvidence(
                window,
                "01-default-category-names.png",
                "FTS_CATEGORY_NAMES_EVIDENCE_DIR");
            InvokePrivateTask(window, "SaveCategoryNameAsync", customer, "客户");
            CompleteLayout(window);
            SaveVisualEvidence(
                window,
                "02-custom-category-name.png",
                "FTS_CATEGORY_NAMES_EVIDENCE_DIR");

            Assert.AreEqual("客户", customer.DisplayName);
            Assert.AreEqual("客户", viewModel.ActivePanel!.DisplayName);
            Assert.AreEqual("客户", store.LastSavedSettings!.CategoryName(
                BoardCategory.CustomerOriginal));
            Assert.IsNull(store.LastPersistedSnapshot);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameSaveFailure_RestoresNameAndKeepsDefaultMarker()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root)
        {
            SettingsSaveFailure = new IOException("Injected settings failure.")
        };
        var state = new DefaultCaptureCategoryState();
        state.Set(BoardCategory.CustomerOriginal);
        var window = CreateWindow(new BoardService(), store, WindowSettings.Default, state);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);

            InvokePrivateTask(window, "SaveCategoryNameAsync", customer, "客户");

            Assert.AreEqual("重要", customer.DisplayName);
            Assert.AreEqual("分类名称未保存，已恢复原名称。", viewModel.StatusText);
            Assert.IsTrue(customer.IsDefaultCapture);
            Assert.AreEqual(BoardCategory.CustomerOriginal, state.Current);
            store.SettingsSaveFailure = null;
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditing_PointerLeaveDoesNotCollapseOrSaveDraft()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            customer.DraftName = "T";
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            Assert.IsTrue(editor.Focus());

            InvokePrivate(window, "Root_MouseLeave", window, NewMouseEventArgs());
            var collapseTimer = GetPrivateField<DispatcherTimer>(window, "_collapseTimer");
            Assert.IsFalse(collapseTimer.IsEnabled);
            InvokePrivate(window, "CollapseTimer_Tick", null, EventArgs.Empty);
            CompleteLayout(window);

            Assert.IsTrue(viewModel.IsPanelExpanded);
            Assert.IsTrue(customer.IsEditingName);
            Assert.AreEqual("重要", customer.DisplayName);
            Assert.AreEqual("T", customer.DraftName);
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_DefersSourceUpdatesUntilCommitForImeComposition()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            var binding = BindingOperations.GetBinding(editor, TextBox.TextProperty);
            Assert.IsNotNull(binding);
            Assert.AreEqual(UpdateSourceTrigger.Explicit, binding.UpdateSourceTrigger);

            editor.Text = "这是";
            editor.CaretIndex = editor.Text.Length;
            CompleteLayout(window);

            Assert.AreEqual("重要", customer.DraftName);
            Assert.AreEqual(editor.Text.Length, editor.CaretIndex);

            InvokePrivate(window, "CommitCategoryNameEdit", customer, editor.Text);
            InvokePrivateTask(window, "DrainPendingOperationsAsync");

            Assert.AreEqual("这是", customer.DisplayName);
            Assert.AreEqual(
                "这是",
                store.LastSavedSettings!.CategoryName(BoardCategory.CustomerOriginal));
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_DoesNotTruncateActiveImeCompositionText()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zheshide"));

            editor.Text = "zheshide";
            Assert.AreEqual("zheshide", editor.Text);

            editor.Text = "这是的";
            editor.CaretIndex = editor.Text.Length;
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputEvent,
                editor,
                "这是的"));
            CompleteLayout(window);

            Assert.AreEqual("这是的", editor.Text);
            Assert.AreEqual(editor.Text.Length, editor.CaretIndex);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void CategoryNameEditor_EnterDuringImeCompositionDoesNotSaveTheLabel()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(
            new BoardService(),
            store,
            WindowSettings.Default,
            new DefaultCaptureCategoryState());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.CustomerOriginal);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var customer = viewModel.Categories.Single(
                category => category.Category == BoardCategory.CustomerOriginal);
            var customerTab = FindCategoryTab(window, customer);

            customer.BeginNameEdit();
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(customerTab).Single();
            editor.RaiseEvent(NewTextCompositionEventArgs(
                TextCompositionManager.PreviewTextInputStartEvent,
                editor,
                "zhe"));
            editor.Text = "zhe";
            var source = PresentationSource.FromVisual(editor);
            Assert.IsNotNull(source);
            var enter = new KeyEventArgs(
                Keyboard.PrimaryDevice,
                source,
                0,
                Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
                Source = editor
            };

            InvokePrivate(window, "CategoryNameEditor_KeyDown", editor, enter);

            Assert.IsFalse(enter.Handled);
            Assert.IsTrue(customer.IsEditingName);
            Assert.AreEqual("重要", customer.DisplayName);
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void F2Rename_ExpandedPanelEditsOnlyTheActiveCategoryWithoutSaving()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var selected = board.AddText("selected");
        var store = new RecordingBoardStore(directory.Root);
        var defaultCategory = new DefaultCaptureCategoryState();
        defaultCategory.Set(BoardCategory.CustomerOriginal);
        var window = CreateWindow(board, store, WindowSettings.Default, defaultCategory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var active = viewModel.Categories.Single(
                category => category.Category == BoardCategory.Inbox);
            var list = (ListBox)window.FindName("BoardList");
            list.SelectedItems.Add(selected);
            Keyboard.ClearFocus();

            var rename = NewKeyEventArgs(window, Key.F2);
            window.RaiseEvent(rename);
            CompleteLayout(window);

            var editor = FindDescendants<TextBox>(FindCategoryTab(window, active)).Single();
            Assert.IsTrue(rename.Handled);
            Assert.IsTrue(active.IsEditingName);
            Assert.IsTrue(editor.IsKeyboardFocusWithin);
            Assert.AreEqual(0, editor.SelectionStart);
            Assert.AreEqual(active.DisplayName.Length, editor.SelectionLength);
            CollectionAssert.AreEqual(
                new[] { selected },
                list.SelectedItems.Cast<BoardItem>().ToArray());
            Assert.AreEqual(BoardCategory.CustomerOriginal, defaultCategory.Current);
            Assert.AreEqual(0, store.SaveCount);
            Assert.IsNull(store.LastSavedSettings);

            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "f2-category-rename.png",
                "FTS_F2_RENAME_EVIDENCE_DIR");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void F2Rename_CollapsedPanelIsIgnored()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(new BoardService(), store, WindowSettings.Default);

        try
        {
            window.Show();
            CompleteLayout(window);
            Keyboard.ClearFocus();
            var rename = NewKeyEventArgs(window, Key.F2);

            window.RaiseEvent(rename);
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsFalse(rename.Handled);
            Assert.IsFalse(viewModel.IsPanelExpanded);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsEditingName));
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    [DataRow(ModifierKeys.Control)]
    [DataRow(ModifierKeys.Shift)]
    [DataRow(ModifierKeys.Alt)]
    public void F2Rename_ModifiedShortcutIsIgnored(ModifierKeys modifiers)
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(new BoardService(), store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Keyboard.ClearFocus();
            var rename = NewModifiedKeyEventArgs(window, Key.F2, modifiers);

            window.RaiseEvent(rename);
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsFalse(rename.Handled);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsEditingName));
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void F2Rename_ExistingTextEditorKeepsItsDraft()
    {
        using var directory = new TestDirectory();
        var store = new RecordingBoardStore(directory.Root);
        var window = CreateWindow(new BoardService(), store, WindowSettings.Default);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var active = viewModel.ActivePanel!;
            active.BeginNameEdit();
            active.DraftName = "草稿";
            CompleteLayout(window);
            var editor = FindDescendants<TextBox>(FindCategoryTab(window, active)).Single();
            Assert.IsTrue(editor.Focus());
            Keyboard.Focus(editor);
            var rename = NewKeyEventArgs(window, Key.F2);
            rename.Source = editor;

            editor.RaiseEvent(rename);
            CompleteLayout(window);

            Assert.IsFalse(rename.Handled);
            Assert.IsTrue(active.IsEditingName);
            Assert.AreEqual("草稿", active.DraftName);
            Assert.IsTrue(editor.IsKeyboardFocusWithin);
            Assert.IsNull(store.LastSavedSettings);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void F2Rename_DoubleClickKeepsFocusedSelectedEditorBehavior()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var viewModel = (MainWindowViewModel)window.DataContext;
            var active = viewModel.ActivePanel!;
            var tab = FindCategoryTab(window, active);
            var doubleClick = NewMouseButtonEventArgs(
                UIElement.MouseLeftButtonDownEvent,
                tab);
            typeof(MouseButtonEventArgs)
                .GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(doubleClick, 2);

            tab.RaiseEvent(doubleClick);
            CompleteLayout(window);

            var editor = FindDescendants<TextBox>(tab).Single();
            Assert.IsTrue(doubleClick.Handled);
            Assert.IsTrue(active.IsEditingName);
            Assert.IsTrue(editor.IsKeyboardFocusWithin);
            Assert.AreEqual(0, editor.SelectionStart);
            Assert.AreEqual(active.DisplayName.Length, editor.SelectionLength);
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void F2Rename_ClosingWindowIsIgnored()
    {
        using var directory = new TestDirectory();
        var store = new BlockingSettingsSaveBoardStore(directory.Root);
        var window = CreateWindow(new BoardService(), store, WindowSettings.Default);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        var closeStarted = false;

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            Keyboard.ClearFocus();
            window.Close();
            closeStarted = true;
            PumpDispatcherUntil(window.Dispatcher, store.SettingsSaveStarted.Task);
            var rename = NewKeyEventArgs(window, Key.F2);

            InvokePrivate(window, "MainWindow_PreviewKeyDown", window, rename);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsFalse(rename.Handled);
            Assert.IsFalse(viewModel.Categories.Any(category => category.IsEditingName));
        }
        finally
        {
            if (closeStarted)
            {
                store.ReleaseSettingsSave();
                if (!closed.Task.IsCompleted)
                {
                    PumpDispatcherUntil(window.Dispatcher, closed.Task);
                }
            }
            else
            {
                CloseWindow(window);
            }
        }
    }
}
