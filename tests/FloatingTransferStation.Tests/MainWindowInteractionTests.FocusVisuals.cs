using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FloatingTransferStation.Design;
using FloatingTransferStation.Launcher;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 焦点视觉全局策略（W6 焦点视觉治理，2026-11-03，批次 2026-11-03-parallel-ui-evolution）：
/// 焦点框视觉只保留在文本输入控件（ThemedTextBoxStyle 家族聚焦描边＝打字定位的功能反馈）；
/// 其余一切焦点框清除——启动瓦片键盘焦点 Accent 紫框、分类标签/卡片容器
/// IsKeyboardFocusWithin→Accent 触发器、未覆盖控件上的 Aero2 默认虚线
/// FocusVisualStyle（根级隐式样式安全网＋共享显式样式置 null 双层覆盖）。
/// 键盘可达性逻辑（焦点移动/Focusable/导航/BringIntoView）不删——只删视觉。
/// 拖拽落点高亮（UpdateLauncherDropTarget 的 Accent 描边）是拖拽会话的功能性落位反馈，
/// 不在焦点框治理面内，保持原样。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>焦点框治理断言的控件谓词：排除文本输入白名单（功能性聚焦描边保留）与
    /// 不可聚焦控件（无法呈现焦点视觉）。</summary>
    private static IEnumerable<string> FocusVisualOffenders(DependencyObject root)
    {
        var offenders = new List<string>();
        foreach (var control in FindDescendants<Control>(root))
        {
            if (control is TextBoxBase or PasswordBox)
            {
                continue;
            }

            if (!control.Focusable)
            {
                continue;
            }

            if (control.GetValue(Control.FocusVisualStyleProperty) is not null)
            {
                offenders.Add($"{control.GetType().Name}#{control.Name ?? "(匿名)"}");
            }
        }

        return offenders;
    }

    [STATestMethod]
    public void FocusVisual_LauncherKeyboardNavigationLeavesNoAccentTileBorder()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 7)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();
            var accent = (Brush)window.FindResource("AccentBrush");

            // 键盘导航全旅程（含用户报障的偶发路径：改名进入后取消，方向键继续移动）。
            InvokePrivate(window, "MoveLauncherFocus", 1, 0);
            InvokePrivate(window, "MoveLauncherFocus", 0, 1);
            InvokePrivate(window, "BeginLauncherTileRename", catalog.Items[GetPrivateField<int>(window, "_launcherFocusedIndex")]);
            CompleteLayout(window);
            InvokePrivate(window, "CancelLauncherTileRename");
            InvokePrivate(window, "MoveLauncherFocus", -1, 0);
            InvokePrivate(window, "MoveLauncherFocus", 0, -1);
            CompleteLayout(window);

            foreach (var entry in catalog.Items)
            {
                var tile = FindLauncherTile(window, entry);
                Assert.IsNotNull(tile, $"前置：瓦片已生成（{entry.DisplayName}）。");
                Assert.IsFalse(
                    ReferenceEquals(tile.BorderBrush, accent),
                    $"键盘导航后任何瓦片不得再画 Accent 焦点描边（{entry.DisplayName}）。");
            }

            // 键盘可达性逻辑保留：焦点项的 IsKeyboardFocused 会话态照常同步
            // （勾选钮可见性绑定的既有驱动，#13 族）。
            var focusedIndex = GetPrivateField<int>(window, "_launcherFocusedIndex");
            Assert.IsTrue(
                focusedIndex >= 0 && focusedIndex < catalog.Items.Count,
                "前置：键盘焦点索引有效。");
            Assert.IsTrue(
                catalog.Items[focusedIndex].IsKeyboardFocused,
                "焦点指示逻辑（IsKeyboardFocused 会话态）随导航保留。");
            Assert.AreEqual(
                1,
                catalog.Items.Count(item => item.IsKeyboardFocused),
                "焦点指示恰有一项。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void FocusVisual_CategoryRenameFocusLeavesTabContainerClean()
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

            // 进入分类改名：编辑器持焦（IsKeyboardFocusWithin=true），标签容器
            // 不得再整圈 Accent 描边（原触发器已随 W6 焦点视觉治理移除）。
            customer.BeginNameEdit();
            CompleteLayout(window);

            var editor = FindDescendants<TextBox>(customerTab).Single(visible => visible.IsVisible);
            editor.Focus();
            CompleteLayout(window);
            Assert.IsTrue(editor.IsKeyboardFocused, "前置：改名编辑器持有键盘焦点。");
            Assert.IsTrue(customerTab.IsKeyboardFocusWithin, "前置：标签容器处于键盘焦点内。");
            Assert.AreEqual(
                Brushes.Transparent,
                customerTab.BorderBrush,
                "分类改名持焦期间标签容器保持透明描边（无 Accent 焦点框）。");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void FocusVisual_KeyboardFocusedCardContainerStaysNeutralBorder()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("键盘焦点卡");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var accent = (Brush)window.FindResource("AccentBrush");
            var list = FindDescendant<ListBox>(window);
            Assert.IsNotNull(list, "前置：板面列表已生成。");

            if (list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem container)
            {
                container.Focus();
                CompleteLayout(window);
                Assert.IsTrue(container.IsKeyboardFocused, "前置：卡片容器持有键盘焦点。");
                Assert.IsTrue(container.IsKeyboardFocusWithin, "前置：容器处于键盘焦点内。");
                // 卡片容器（CardContainerStyle 实例）不再随键盘焦点变 Accent 描边；
                // 勾选钮的装饰性悬停环静态携带 Accent 画刷但 Opacity=0（不可见），
                // 故按样式实例定位容器断言，并对可见描边做兜底扫描。
                var containerStyle = (Style)window.FindResource("CardContainerStyle");
                var cardContainer = FindDescendants<Border>(container)
                    .Single(border => ReferenceEquals(border.Style, containerStyle));
                Assert.IsFalse(
                    ReferenceEquals(cardContainer.BorderBrush, accent),
                    "键盘聚焦卡的容器描边保持中性（Accent 焦点触发器已移除）。");
                foreach (var border in FindDescendants<Border>(container))
                {
                    if (border.IsVisible && border.Opacity > 0.01 && border.BorderThickness.Top > 0)
                    {
                        Assert.IsFalse(
                            ReferenceEquals(border.BorderBrush, accent),
                            $"键盘聚焦卡上不得出现可见 Accent 描边（{border.Name ?? "(匿名)"}）。");
                    }
                }
            }
            else
            {
                Assert.Fail("前置：首卡容器应已生成。");
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void FocusVisual_TileRenameEditorCarriesThemedFunctionalFocusBorder()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var items = Enumerable.Range(0, 3)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.Application,
                $"{app.Path}.{index}",
                $"应用{index}",
                DateTimeOffset.UtcNow))
            .ToArray();
        var window = CreateLauncherWindowSync(directory, board, store, starter, items);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            InvokePrivate(window, "BeginLauncherTileRename", catalog.Items[0]);
            CompleteLayout(window);

            var tile = FindLauncherTile(window, catalog.Items[0]);
            Assert.IsNotNull(tile);
            var editor = FindDescendants<TextBox>(tile).Single(visible => visible.IsVisible);
            Assert.IsTrue(editor.IsKeyboardFocused, "前置：改名编辑器持有键盘焦点。");
            Assert.IsTrue(
                ReferenceEquals(editor.Style, (Style)window.FindResource("ThemedTextBoxStyle")),
                "瓦片改名编辑器套 ThemedTextBoxStyle（脱离系统默认模板）。");
            Assert.IsTrue(
                ReferenceEquals(editor.BorderBrush, (Brush)window.FindResource("AccentBrush")),
                "文本输入白名单：改名编辑器保持功能性聚焦描边（Themed 家族 Accent 边）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void FocusVisual_MainWindowFocusableControlsHaveNoFocusVisualStyle()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText("内容卡");
        var window = CreateWindow(directory, board);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            var offenders = FocusVisualOffenders(window).ToList();
            Assert.IsFalse(
                offenders.Count > 0,
                $"主窗可聚焦控件不得携带默认虚线 FocusVisualStyle：{string.Join("、", offenders)}");

            // 启动表面同检（瓦片网格/工具行/发现按钮等另一套控件面）。
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            offenders = FocusVisualOffenders(window).ToList();
            Assert.IsFalse(
                offenders.Count > 0,
                $"启动表面上可聚焦控件不得携带默认虚线 FocusVisualStyle：{string.Join("、", offenders)}");
        }
        finally
        {
            CloseWindow(window);
        }
    }

    [STATestMethod]
    public void FocusVisual_DialogWindowsHaveNoDefaultFocusVisualStyle()
    {
        using var directory = new TestDirectory();
        var window = CreateWindow(directory, new BoardService());
        try
        {
            window.Show();
            CompleteLayout(window);

            var urlDialog = new LauncherUrlInputWindow(DesignTheme.Light);
            var editDialog = new LauncherEditTargetWindow(
                DesignTheme.Light,
                new LauncherItem(
                    Guid.NewGuid(),
                    LauncherKind.Url,
                    "https://x.example.com/",
                    "x",
                    DateTimeOffset.UtcNow));
            var picker = CreateInstalledPicker(
                DesignTheme.Light,
                [FakeInstalledApp("aumid-1", "应用一"), FakeInstalledApp("aumid-2", "应用二")]);
            var trial = new PluginActionTrialWindow(window, "typography-tidy", "tidy-card");
            var settings = new SettingsWindow(window);

            try
            {
                foreach (var dialog in new Window[] { urlDialog, editDialog, picker, trial, settings })
                {
                    dialog.Show();
                    CompleteLayout(dialog);
                    var offenders = FocusVisualOffenders(dialog).ToList();
                    Assert.IsFalse(
                        offenders.Count > 0,
                        $"{dialog.GetType().Name} 内可聚焦控件不得携带默认虚线 FocusVisualStyle：{string.Join("、", offenders)}");
                    dialog.Close();
                }
            }
            finally
            {
                // 逐窗已 Close；这里兜底关掉可能因断言失败仍开着的窗。
                foreach (var dialog in new Window[] { urlDialog, editDialog, picker, trial, settings })
                {
                    if (dialog.IsLoaded)
                    {
                        dialog.Close();
                    }
                }
            }
        }
        finally
        {
            CloseWindow(window);
        }
    }
}
