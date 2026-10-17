using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 就地改名真实输入路径（契约 #13「真实输入可达性」在 #30 改名语义上的落实）：
/// 既有改名测试全部直接写模型侧 DraftName（合成可达），没有任何一条向 TileNameEditor
/// 真实键入——「Explicit 绑定＋提交读模型字段」使实机键入永远到不了模型（1.49.0 缺陷，
/// 与 #176 × 删除钮同属合成可达、实机不可达族）。本文件经 SendInput 硬件级注入向编辑器
/// 真实键入，并经真实 Enter / Tab（焦点离开＝失焦提交）/ Esc 提交或取消，覆盖 Url 与
/// 非 Url（Application）两类瓦片，证明改名提交路径与瓦片类型无关。真实键入用
/// KEYEVENTF_UNICODE 注入：复用仓内 SendInput 真实输入设施族（P/Invoke 与结构布局
/// 照 CardAi.cs 既有先例，同 partial 类共享其 SendKeyboardInput/KeyboardInput）。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VkReturn = 0x0D;
    private const ushort VkTab = 0x09;
    private const ushort VkEscape = 0x1B;
    private const ushort VkA = 0x41;
    private const ushort VkDelete = 0x2E;

    /// <summary>真实键盘键入文本：KEYEVENTF_UNICODE 逐字符 down/up（候选窗之外的真实打字路径）。</summary>
    private static void TypeTextBySendInput(MainWindow window, string text)
    {
        var inputs = new KeyboardInput[text.Length * 2];
        var index = 0;
        foreach (var ch in text)
        {
            inputs[index].Type = InputKeyboard;
            inputs[index].Ki.ScanCode = ch;
            inputs[index].Ki.Flags = KeyeventfUnicode;
            inputs[index + 1].Type = InputKeyboard;
            inputs[index + 1].Ki.ScanCode = ch;
            inputs[index + 1].Ki.Flags = KeyeventfUnicode | KeyEventfKeyup;
            index += 2;
        }

        Assert.AreEqual(
            (uint)inputs.Length,
            SendKeyboardInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardInput>()),
            "真实键入注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>真实键盘单击一个虚拟键（down＋up）。</summary>
    private static void TapVkBySendInput(MainWindow window, ushort virtualKey)
    {
        var inputs = new KeyboardInput[2];
        inputs[0].Type = InputKeyboard;
        inputs[0].Ki.VirtualKey = virtualKey;
        inputs[1].Type = InputKeyboard;
        inputs[1].Ki.VirtualKey = virtualKey;
        inputs[1].Ki.Flags = KeyEventfKeyup;
        Assert.AreEqual(
            2u,
            SendKeyboardInput(2, inputs, Marshal.SizeOf<KeyboardInput>()),
            "真实按键注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>真实键盘组合键（修饰键按住期间单击并抬起主键，再松开修饰键）。</summary>
    private static void ChordVkBySendInput(MainWindow window, ushort modifier, ushort key)
    {
        var inputs = new KeyboardInput[4];
        inputs[0].Type = InputKeyboard;
        inputs[0].Ki.VirtualKey = modifier;
        inputs[1].Type = InputKeyboard;
        inputs[1].Ki.VirtualKey = key;
        inputs[2].Type = InputKeyboard;
        inputs[2].Ki.VirtualKey = key;
        inputs[2].Ki.Flags = KeyEventfKeyup;
        inputs[3].Type = InputKeyboard;
        inputs[3].Ki.VirtualKey = modifier;
        inputs[3].Ki.Flags = KeyEventfKeyup;
        Assert.AreEqual(
            4u,
            SendKeyboardInput(4, inputs, Marshal.SizeOf<KeyboardInput>()),
            "真实组合键注入必须成功。");
        PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>真实点击编辑态瓦片进入就地改名（窗口随之成为前台，后续键入可达）。</summary>
    private static TextBox BeginRenameByRealClick(MainWindow window, Border tile, string expectedPrefill)
    {
        HoverRealAt(window, tile, new Point(tile.ActualWidth / 2, tile.ActualHeight / 2));
        ClickBySendInput(window);
        CompleteLayout(window);
        var editor = FindDescendant<TextBox>(tile);
        Assert.IsNotNull(editor, "就地改名编辑器出现。");
        Assert.AreEqual(expectedPrefill, editor.Text, "进入编辑预填旧名。");
        return editor;
    }

    [STATestMethod]
    public void QuickLaunch_Rename_RealTyping_EnterCommitsAndPersists_UrlTile()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.com/", "example.com", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            EnterLauncherEdit(window);
            CompleteLayout(window);
            window.Topmost = true;
            CompleteLayout(window);

            var tile = FindLauncherTile(window, catalog.Items.Single());
            Assert.IsNotNull(tile);
            var editor = BeginRenameByRealClick(window, tile, "example.com");

            TypeTextBySendInput(window, "测试改名");
            CompleteLayout(window);
            Assert.AreEqual("测试改名", editor.Text, "真实键入到达编辑器。");

            TapVkBySendInput(window, VkReturn);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.AreEqual(
                "测试改名",
                catalog.Items.Single().DisplayName,
                "真实 Enter 提交改名（修复前键入到不了模型、显示弹回旧名）。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(
                "测试改名",
                persisted.Items.Single().DisplayName,
                "改名持久化到 launchers.json。");
            Assert.AreEqual(Visibility.Collapsed, editor.Visibility, "提交后编辑器收起。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_Rename_RealTyping_FocusLossCommitsAndPersists_ApplicationTile()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "应用甲", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            EnterLauncherEdit(window);
            CompleteLayout(window);
            window.Topmost = true;
            CompleteLayout(window);

            var tile = FindLauncherTile(window, catalog.Items.Single());
            Assert.IsNotNull(tile);
            var editor = BeginRenameByRealClick(window, tile, "应用甲");

            TypeTextBySendInput(window, "改名后的应用");
            CompleteLayout(window);
            Assert.AreEqual("改名后的应用", editor.Text, "真实键入到达编辑器。");

            // 真实 Tab＝焦点离开编辑器（失焦提交的实机路径：非可聚焦瓦片不接收焦点，
            // Tab 把焦点交还窗口级焦点域，LostKeyboardFocus 进同一条提交路径）。
            TapVkBySendInput(window, VkTab);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.AreEqual(
                "改名后的应用",
                catalog.Items.Single().DisplayName,
                "失焦提交改名（Application 瓦片与 Url 同路径，类型无关）。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(
                "改名后的应用",
                persisted.Items.Single().DisplayName,
                "失焦提交持久化到 launchers.json。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_Rename_EmptyText_RealCommit_RevertsToOriginalName()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Application, app.Path, "原名", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            EnterLauncherEdit(window);
            CompleteLayout(window);
            window.Topmost = true;
            CompleteLayout(window);

            var tile = FindLauncherTile(window, catalog.Items.Single());
            Assert.IsNotNull(tile);
            var editor = BeginRenameByRealClick(window, tile, "原名");

            // 真实 Ctrl+A 全选＋Delete 清空，再真实 Enter＝空提交回退原名。
            ChordVkBySendInput(window, VkControl, VkA);
            TapVkBySendInput(window, VkDelete);
            CompleteLayout(window);
            Assert.IsEmpty(editor.Text, "真实清空编辑器。");

            TapVkBySendInput(window, VkReturn);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(400));
            CompleteLayout(window);

            Assert.AreEqual(
                "原名",
                catalog.Items.Single().DisplayName,
                "空文本提交回退原名。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(
                "原名",
                persisted.Items.Single().DisplayName,
                "回退原名后 launchers.json 保持原名。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }

    [STATestMethod]
    public void QuickLaunch_Rename_Esc_RealCancel_DiscardsTypedText()
    {
        using var directory = new TestDirectory();
        using var app = new StubAppFile();
        var board = new BoardService();
        var store = new LocalStore(AppPaths.ForTests(directory.Root), new AtomicTextWriter());
        var starter = new RecordingLauncherStarter();
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.org/", "example.org", DateTimeOffset.UtcNow);
        var window = CreateLauncherWindowSync(directory, board, store, starter, item);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Resources[SystemParameters.ClientAreaAnimationKey] = false;
            ExpandCategory(window, QuickLaunchMigration.LauncherCategory);
            CompleteLayout(window);
            var catalog = window.LaunchersForTests();

            EnterLauncherEdit(window);
            CompleteLayout(window);
            window.Topmost = true;
            CompleteLayout(window);

            var tile = FindLauncherTile(window, catalog.Items.Single());
            Assert.IsNotNull(tile);
            var editor = BeginRenameByRealClick(window, tile, "example.org");

            TypeTextBySendInput(window, "被取消的名字");
            CompleteLayout(window);
            Assert.AreEqual("被取消的名字", editor.Text, "真实键入到达编辑器。");

            TapVkBySendInput(window, VkEscape);
            CompleteLayout(window);

            Assert.AreEqual(
                "example.org",
                catalog.Items.Single().DisplayName,
                "Esc 取消不提交已键入文本。");
            var persisted = store.LoadAsync().GetAwaiter().GetResult();
            Assert.AreEqual(
                "example.org",
                persisted.Items.Single().DisplayName,
                "取消后 launchers.json 保持原名。");
            Assert.AreEqual(Visibility.Collapsed, editor.Visibility, "取消后编辑器收起。");
            var nameText = FindDescendants<TextBlock>(tile).FirstOrDefault(candidate => candidate.Name == "TileNameText");
            Assert.IsNotNull(nameText, "名称文本恢复显示。");
            Assert.AreEqual(Visibility.Visible, nameText.Visibility, "取消后名称文本恢复显示。");
            Assert.AreEqual("example.org", nameText.Text, "界面名称保持原名。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindowWithoutSaving(window);
        }
    }
}
