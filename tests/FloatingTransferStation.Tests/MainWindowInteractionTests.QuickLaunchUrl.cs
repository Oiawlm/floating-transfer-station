using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 快捷启动网页条目（#30 修订，Issue #179）：＋菜单第三项「网页地址…」与输入对话框
/// （真实对话框逻辑驱动）、有效输入创建规范形 Url 瓦片＋持久化、无效输入内联报错、
/// 重复添加「已在启动列表中」、瓦片字形渲染（无 Image 实例）、启动失败灰态。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private static void ClickUrlConfirm(LauncherUrlInputWindow dialog) =>
        dialog.UrlConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, dialog.UrlConfirmButton));

    // ---- 输入对话框（真实 WPF 非模态驱动） ----

    [STATestMethod]
    public void UrlInput_ConfirmWithValidInput_ReturnsCanonicalResultAndCloses()
    {
        var dialog = new LauncherUrlInputWindow(DesignTheme.Light);
        dialog.Show();
        dialog.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            CompleteLayout(dialog);
            Assert.AreEqual(Visibility.Visible, dialog.UrlInputHint.Visibility, "空输入时占位提示可见。");

            dialog.UrlInputBox.Text = "github.com";
            CompleteLayout(dialog);
            Assert.AreEqual(Visibility.Collapsed, dialog.UrlInputHint.Visibility, "有输入后占位提示隐藏。");
            ClickUrlConfirm(dialog);
            CompleteLayout(dialog);

            Assert.IsFalse(dialog.IsLoaded, "有效输入确认即关窗（非模态路径）。");
            Assert.IsNotNull(dialog.Result);
            Assert.IsTrue(dialog.Result.Success);
            Assert.AreEqual("https://github.com/", dialog.Result.Url, "无协议输入补 https://，规范形回传。");
            Assert.AreEqual("github.com", dialog.Result.DefaultName);
        }
        finally
        {
            if (dialog.IsLoaded)
            {
                dialog.Close();
            }
        }
    }

    [STATestMethod]
    public void UrlInput_InvalidInput_ShowsInlineErrorAndStaysOpen()
    {
        var dialog = new LauncherUrlInputWindow(DesignTheme.Light);
        dialog.Show();
        dialog.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            CompleteLayout(dialog);

            dialog.UrlInputBox.Text = "ftp://example.com";
            ClickUrlConfirm(dialog);
            CompleteLayout(dialog);
            Assert.IsTrue(dialog.IsLoaded, "拒绝时不关窗。");
            Assert.IsNull(dialog.Result, "拒绝不产出结果、不建条目。");
            Assert.AreEqual(Visibility.Visible, dialog.UrlErrorText.Visibility, "失败就地内联报错。");
            StringAssert.Contains(dialog.UrlErrorText.Text, "http");

            // 修正输入后错误消失，确认回传规范形。
            dialog.UrlInputBox.Text = "example.com";
            CompleteLayout(dialog);
            Assert.AreEqual(Visibility.Collapsed, dialog.UrlErrorText.Visibility, "输入变化后内联错误消失。");
            ClickUrlConfirm(dialog);
            CompleteLayout(dialog);
            Assert.IsFalse(dialog.IsLoaded);
            Assert.AreEqual("https://example.com/", dialog.Result!.Url);
        }
        finally
        {
            if (dialog.IsLoaded)
            {
                dialog.Close();
            }
        }
    }

    [STATestMethod]
    public void UrlInput_EmptyInput_ShowsPromptWithoutResult()
    {
        var dialog = new LauncherUrlInputWindow(DesignTheme.Light);
        dialog.Show();
        dialog.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            CompleteLayout(dialog);
            ClickUrlConfirm(dialog);
            CompleteLayout(dialog);

            Assert.IsTrue(dialog.IsLoaded, "空输入确认不关窗。");
            Assert.IsNull(dialog.Result);
            Assert.AreEqual("请输入网页地址。", dialog.UrlErrorText.Text);
        }
        finally
        {
            if (dialog.IsLoaded)
            {
                dialog.Close();
            }
        }
    }

    [STATestMethod]
    public void UrlInput_EscapeClosesWithNoResult()
    {
        var dialog = new LauncherUrlInputWindow(DesignTheme.Light);
        dialog.Show();
        dialog.Resources[SystemParameters.ClientAreaAnimationKey] = false;
        try
        {
            CompleteLayout(dialog);
            dialog.UrlInputBox.Text = "github.com";
            dialog.RaiseEvent(NewKeyEventArgs(dialog, Key.Escape));
            CompleteLayout(dialog);

            Assert.IsFalse(dialog.IsLoaded, "Esc 关闭对话框。");
            Assert.IsNull(dialog.Result, "Esc=取消（空回传）。");
        }
        finally
        {
            if (dialog.IsLoaded)
            {
                dialog.Close();
            }
        }
    }

    // ---- ＋菜单第三项与添加流程 ----

    [STATestMethod]
    public void QuickLaunch_AddMenu_HasUrlEntry_AndRoutesThroughInjectedDialog()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        LauncherUrlInputWindow? shown = null;
        window._showUrlDialog = dialog =>
        {
            shown = dialog;
            return false; // 取消路径：无条目。
        };

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            window.LauncherAddButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, window.LauncherAddButton));
            CompleteLayout(window);
            Assert.IsTrue(window.LauncherAddMenu.IsOpen, "＋ 打开三入口菜单。");
            Assert.AreEqual("应用文件…", window.LauncherAddFileMenuButton.Content);
            Assert.AreEqual("已安装应用…", window.LauncherAddInstalledMenuButton.Content);
            Assert.AreEqual("网页地址…", window.LauncherAddUrlMenuButton.Content, "网页地址… 是第三入口。");

            window.LauncherAddUrlMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddUrlMenuButton));
            Assert.IsFalse(window.LauncherAddMenu.IsOpen, "入口点击后菜单关闭。");
            CompleteLayout(window);

            Assert.IsNotNull(shown, "对话框经注入委托展示。");
            Assert.AreEqual(0, window.LaunchersForTests().Items.Count, "取消路径不建条目。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_AddUrl_ValidInput_CreatesCanonicalTileAndPersists()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        window._showUrlDialog = dialog =>
        {
            dialog.UrlInputBox.Text = "github.com";
            ClickUrlConfirm(dialog);
            return dialog.Result is { Success: true };
        };

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            window.LauncherAddUrlMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddUrlMenuButton));
            PumpUntilCondition(
                window.Dispatcher,
                () => window.LaunchersForTests().Items.Count == 1);
            var viewModel = (MainWindowViewModel)window.DataContext;
            PumpUntilCondition(
                window.Dispatcher,
                () => viewModel.StatusText == "添加了 1 项。");
            CompleteLayout(window);

            var item = window.LaunchersForTests().Items.Single();
            Assert.AreEqual(LauncherKind.Url, item.Kind);
            Assert.AreEqual("https://github.com/", item.TargetPath, "TargetPath 为创建入口归一化规范形。");
            Assert.AreEqual("github.com", item.DisplayName, "默认名=域名。");
            Assert.AreEqual("添加了 1 项。", viewModel.StatusText);
            var persisted = store.LoadAsync().GetAwaiter().GetResult().Items.Single();
            Assert.AreEqual("https://github.com/", persisted.TargetPath, "Url 条目随 launchers.json 持久化。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_AddUrl_InvalidInput_ShowsNoTileAndNoAddStatus()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        window._showUrlDialog = dialog =>
        {
            dialog.UrlInputBox.Text = "ftp://example.com";
            ClickUrlConfirm(dialog);
            return dialog.Result is { Success: true };
        };

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            window.LauncherAddUrlMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddUrlMenuButton));
            CompleteLayout(window);

            Assert.AreEqual(0, window.LaunchersForTests().Items.Count, "无效输入不建条目。");
            Assert.AreEqual(string.Empty, ((MainWindowViewModel)window.DataContext).StatusText, "无添加汇报。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_AddUrl_Duplicate_ShowsAlreadyInListStatus()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var window = CreateLauncherWindowSync(directory, board, store, starter);
        window._showUrlDialog = dialog =>
        {
            dialog.UrlInputBox.Text = " github.com ";
            ClickUrlConfirm(dialog);
            return dialog.Result is { Success: true };
        };

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            window.LauncherAddUrlMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddUrlMenuButton));
            PumpUntilCondition(window.Dispatcher, () => window.LaunchersForTests().Items.Count == 1);
            var viewModel = (MainWindowViewModel)window.DataContext;
            PumpUntilCondition(window.Dispatcher, () => viewModel.StatusText == "添加了 1 项。");

            // 第二次（空白差异输入，归一化收敛同一规范形）→ 去重路径。
            window.LauncherAddUrlMenuButton.RaiseEvent(new RoutedEventArgs(
                Button.ClickEvent, window.LauncherAddUrlMenuButton));
            PumpUntilCondition(window.Dispatcher, () => viewModel.StatusText == "已在启动列表中");
            CompleteLayout(window);

            Assert.AreEqual(1, window.LaunchersForTests().Items.Count, "重复添加不新增。");
            Assert.AreEqual("已在启动列表中", viewModel.StatusText);
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    // ---- Url 瓦片渲染（字形、零 Image 实例）与失败灰态 ----

    [STATestMethod]
    public void QuickLaunch_UrlTile_RendersGlyphWithoutImage()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var seeded = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://github.com/", "github.com", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, seeded);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);

            var item = window.LaunchersForTests().Items.Single();
            var tile = FindLauncherTile(window, item);
            Assert.IsNotNull(tile);
            Assert.IsNull(FindDescendant<Image>(tile), "Url 分支不实例化 Image（图标管线零触达）。");
            Assert.IsTrue(
                FindDescendants<TextBlock>(tile).Any(glyph => glyph.Text == "\uE774"),
                "Url 瓦片渲染静态地球字形（模板层按 Kind 分支）。");
            Assert.AreEqual("github.com", tile.ToolTip, "瓦片 tooltip=显示名（既有 Border 样式绑定）。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_UrlTile_LaunchFailure_GrayStateVisible()
    {
        using var directory = new TestDirectory();
        var board = new BoardService();
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var starter = new RecordingLauncherStarter { Respond = _ => throw new System.ComponentModel.Win32Exception(31) };
        var launcher = new AppLauncher(starter.Invoke);
        var seeded = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.invalid/", "example.invalid", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, launcher, seeded);

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var item = window.LaunchersForTests().Items.Single();
            var tile = FindLauncherTile(window, item);
            Assert.IsNotNull(tile);

            // 键盘路径触发启动（与查看态 Enter 启动同管线）。
            Keyboard.ClearFocus();
            window.RaiseEvent(NewKeyEventArgs(window, Key.Right));
            window.RaiseEvent(NewKeyEventArgs(window, Key.Enter));
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            CompleteLayout(window);

            var viewModel = (MainWindowViewModel)window.DataContext;
            Assert.IsTrue(item.LaunchFailed, "失败进灰态（瓦片 Border 层 DataTrigger 承载）。");
            Assert.AreEqual("无法启动：没有找到能打开该地址的应用。", viewModel.StatusText, "无关联错误映射中文文案（D7）。");
            Assert.IsNull(FindDescendant<Image>(tile), "失败灰态同覆盖字形分支（仍无 Image）。");
            Assert.IsTrue(
                FindDescendants<TextBlock>(tile).Any(glyph => glyph.Text == "\uE774"),
                "失败灰态字形保留。");
        }
        finally
        {
            CloseWindowWithoutSaving(window);
        }
    }
}
