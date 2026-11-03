using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 复盘 Markdown 阅读视图（实验性）交互回归：默认关＝表面与现状一致；开启后
/// 阅读/编辑切换可用；阅读态内容与源文对应；编辑保存往返与外部变更都同步阅读态；
/// 排版走主题语义键（浅深两主题解析为字典实例）。保存/防抖/合并/Esc 契约
/// （#4/#20）由既有 DailyReview 族锁定——本文件只锁新增面。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    private const string ReadingSampleMarkdown =
        "# 每日复盘\n\n完成了 **功能A**，遇到 `边界情况`。\n\n- 完成了功能\n- 记录了问题\n\n> 引用一句\n\n```\nfenced code\n```\n\n[链接](https://example.com/a)";

    private static MainWindow CreateReviewMarkdownWindow(
        TestDirectory directory,
        bool enabled = true,
        IPreferencesStore? preferencesStore = null)
    {
        var paths = AppPaths.ForTests(directory.Root);
        var store = new LocalStore(paths, new AtomicTextWriter());
        var settings = WindowSettings.Default with
        {
            ReviewMigrationVersion = DailyReviewMigration.CurrentVersion,
            CategoryNames = WindowSettings.Default.WithCategoryName(BoardCategory.Reference, "复盘").CategoryNames
        };
        return CreateWindow(
            new BoardService(),
            store,
            settings,
            new DefaultCaptureCategoryState(),
            new ImageNormalizer(store.ImagesDirectory),
            preferences: AppPreferences.Default with { ReviewMarkdownViewEnabled = enabled },
            preferencesStore: preferencesStore);
    }

    private static FlowDocumentScrollViewer GetReadingView(MainWindow window) =>
        (FlowDocumentScrollViewer)window.FindName("ReviewReadingView")!;

    private static void EnterReadingMode(MainWindow window) =>
        ((RadioButton)window.FindName("ReviewReadingModeButton")!).IsChecked = true;

    private static void EnterEditMode(MainWindow window) =>
        ((RadioButton)window.FindName("ReviewEditModeButton")!).IsChecked = true;

    /// <summary>提取阅读文档全部叶块文本（递归展开 Section/ListItem；TextRange 含
    /// 嵌套 Span/Hyperlink 内容）。</summary>
    private static List<string> GetReadingBlockTexts(MainWindow window)
    {
        var document = GetReadingView(window).Document;
        Assert.IsNotNull(document, "阅读态激活时文档必须已渲染。");
        var texts = new List<string>();
        CollectBlockTexts(document.Blocks, texts);
        return texts;
    }

    private static void CollectBlockTexts(BlockCollection blocks, List<string> texts)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Section section:
                    CollectBlockTexts(section.Blocks, texts);
                    break;
                case System.Windows.Documents.List list:
                    foreach (var item in list.ListItems)
                    {
                        var itemTexts = new List<string>();
                        CollectBlockTexts(item.Blocks, itemTexts);
                        // TextRange 把列表标记（“•\t”/“1.\t”）带进文本容器——
                        // 剥掉首块的前导「标记＋制表符」，只取内容文本。
                        texts.AddRange(itemTexts.Select((text, index) =>
                            index == 0 && text.Contains('\t')
                                ? text[(text.IndexOf('\t') + 1)..]
                                : text));
                    }

                    break;
                default:
                    texts.Add(new TextRange(block.ContentStart, block.ContentEnd).Text);
                    break;
            }
        }
    }

    private static void FlushReview(MainWindow window)
    {
        var save = window.Dispatcher.InvokeAsync(
            () => (Task)typeof(MainWindow).GetMethod(
                "FlushReviewAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!).Task.Unwrap();
        PumpDispatcherUntil(window.Dispatcher, save);
    }

    [STATestMethod]
    public void ReviewMarkdownView_DisabledByDefault_SurfaceIdenticalToCurrent()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewMarkdownWindow(directory, enabled: false);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);

            // 默认关闭：切换控件与阅读视图都不出现，编辑器独占（逐像素一致的结构前提）。
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)window.FindName("ReviewModeSwitch")!).Visibility);
            Assert.AreEqual(Visibility.Collapsed, GetReadingView(window).Visibility);
            Assert.IsNull(GetReadingView(window).Document);
            Assert.AreEqual(Visibility.Visible, ((TextBox)window.FindName("ReviewEditor")).Visibility);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewMarkdownView_Enabled_ToggleSwitchesSurfacesAndReleasesEditorFocus()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewMarkdownWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var editor = (TextBox)window.FindName("ReviewEditor")!;

            Assert.AreEqual(Visibility.Visible, ((Border)window.FindName("ReviewModeSwitch")!).Visibility);

            Assert.IsTrue(editor.Focus());
            Assert.AreEqual(editor, Keyboard.FocusedElement);
            EnterReadingMode(window);
            CompleteLayout(window);

            Assert.AreEqual(Visibility.Visible, GetReadingView(window).Visibility);
            Assert.AreEqual(Visibility.Collapsed, editor.Visibility);
            Assert.AreNotEqual(
                editor,
                Keyboard.FocusedElement,
                "切到阅读态必须按 #20 同链释放编辑器焦点（阅读视图不承载编辑保持原因）。");

            // 编辑器文本原样保留（阅读态只是另一个呈现面，不清防抖、不丢内容）。
            Assert.AreEqual(string.Empty, editor.Text);

            EnterEditMode(window);
            CompleteLayout(window);
            Assert.AreEqual(Visibility.Collapsed, GetReadingView(window).Visibility);
            Assert.AreEqual(Visibility.Visible, editor.Visibility);
            Assert.IsNull(GetReadingView(window).Document, "编辑态不得持有渲染文档。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewMarkdownView_ReadingContentMatchesSourceMarkdown()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var window = CreateReviewMarkdownWindow(directory);

        try
        {
            Directory.CreateDirectory(paths.ReviewsDirectory);
            var date = DateOnly.FromDateTime(DateTime.Now);
            File.WriteAllText(
                Path.Combine(paths.ReviewsDirectory, $"{date:yyyy-MM-dd}.md"),
                ReadingSampleMarkdown);
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            EnterReadingMode(window);
            CompleteLayout(window);

            var texts = GetReadingBlockTexts(window);
            CollectionAssert.Contains(texts, "每日复盘", "H1 必须渲染为独立标题块文本。");
            CollectionAssert.Contains(texts, "完成了 功能A，遇到 边界情况。", "段落含粗体/行内代码的合并文本。");
            CollectionAssert.Contains(texts, "完成了功能", "无序列表项一。");
            CollectionAssert.Contains(texts, "记录了问题", "无序列表项二。");
            CollectionAssert.Contains(texts, "引用一句", "块引用内容。");
            // 代码块空格以 NBSP 代换保缩进（FlowDocument 折叠普通空格序列）。
            CollectionAssert.Contains(texts, "fenced\u00A0code", "围栏代码块内容（行间保留，空格 NBSP 化）。");
            CollectionAssert.Contains(texts, "链接", "链接文本渲染在文档中。");

            // 标题排版走 token：H1 字号=ReadingHeading1FontSize、SemiBold。
            var heading = GetReadingView(window).Document.Blocks.OfType<Paragraph>().Single(
                paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text == "每日复盘");
            Assert.AreEqual(DesignTokens.ReadingHeading1FontSize, heading.FontSize);
            Assert.AreEqual(FontWeights.SemiBold, heading.FontWeight);

            // 唯一 http 链接渲染为 Hyperlink（NavigateUri＝源文 URL）。
            var hyperlinks = GetReadingView(window).Document.Blocks.OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>())
                .ToArray();
            Assert.AreEqual(1, hyperlinks.Length);
            Assert.AreEqual(new Uri("https://example.com/a"), hyperlinks[0].NavigateUri);
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewMarkdownView_NonHttpLinks_DoNotBecomeClickable()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var window = CreateReviewMarkdownWindow(directory);

        try
        {
            Directory.CreateDirectory(paths.ReviewsDirectory);
            var date = DateOnly.FromDateTime(DateTime.Now);
            File.WriteAllText(
                Path.Combine(paths.ReviewsDirectory, $"{date:yyyy-MM-dd}.md"),
                "[好链接](https://example.com) 与 [脚本](javascript:alert(1)) 与 [相对](/本地路径)");
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            EnterReadingMode(window);
            CompleteLayout(window);

            var hyperlinks = GetReadingView(window).Document.Blocks.OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>())
                .ToArray();
            Assert.AreEqual(1, hyperlinks.Length, "仅 http/https 可点击；javascript: 与相对路径按普通文本呈现。");
            // Uri 对无路径 URL 规范化补尾斜杠——两侧同用 Uri 构造比较，避开字符串形态差异。
            Assert.AreEqual(new Uri("https://example.com"), hyperlinks[0].NavigateUri);
            var joined = string.Join(
                "\n",
                GetReadingBlockTexts(window).Where(text => text.Contains("脚本")));
            StringAssert.Contains(joined, "脚本", "非 http 链接的文本必须保留。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewMarkdownView_EditSaveRoundTripThenReadingShowsNewContent()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var window = CreateReviewMarkdownWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var editor = (TextBox)window.FindName("ReviewEditor")!;
            var date = DateOnly.FromDateTime(DateTime.Now);

            // 编辑态输入→防抖保存→切阅读态：文档反映已保存的新内容。
            editor.Text = "## 新标题\n\n- 新条目";
            FlushReview(window);
            Assert.AreEqual(
                "## 新标题\n\n- 新条目",
                File.ReadAllText(Path.Combine(paths.ReviewsDirectory, $"{date:yyyy-MM-dd}.md")));

            EnterReadingMode(window);
            CompleteLayout(window);
            var texts = GetReadingBlockTexts(window);
            CollectionAssert.Contains(texts, "新标题", "编辑保存往返后阅读态必须更新。");
            CollectionAssert.Contains(texts, "新条目");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewMarkdownView_ExternalChange_RefreshesReadingView()
    {
        using var directory = new TestDirectory();
        var paths = AppPaths.ForTests(directory.Root);
        var window = CreateReviewMarkdownWindow(directory);

        try
        {
            Directory.CreateDirectory(paths.ReviewsDirectory);
            var date = DateOnly.FromDateTime(DateTime.Now);
            File.WriteAllText(
                Path.Combine(paths.ReviewsDirectory, $"{date:yyyy-MM-dd}.md"),
                "# 初始标题\n\n初始正文");
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            EnterReadingMode(window);
            CompleteLayout(window);
            CollectionAssert.Contains(GetReadingBlockTexts(window), "初始标题");

            // 应用外改写当天文件（阅读态、无未保存修改）：文件监听路径刷新阅读文档。
            File.WriteAllText(
                Path.Combine(paths.ReviewsDirectory, $"{date:yyyy-MM-dd}.md"),
                "# 外部标题\n\n外部正文");
            InvokePrivateTask(
                window,
                "HandleDailyReviewChangeAsync",
                new DailyReviewFileChangedEventArgs(date, DailyReviewFileChangeKind.Changed));

            var texts = GetReadingBlockTexts(window);
            CollectionAssert.Contains(texts, "外部标题", "外部变更必须同步到阅读态。");
            CollectionAssert.Contains(texts, "外部正文");
            Assert.IsFalse(texts.Contains("初始标题"), "旧内容不得残留。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewMarkdownView_ReadingModeRememberedAcrossDateSwitch()
    {
        using var directory = new TestDirectory();
        var window = CreateReviewMarkdownWindow(directory);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var editor = (TextBox)window.FindName("ReviewEditor")!;
            editor.Text = "# 今天内容";
            FlushReview(window);
            EnterReadingMode(window);
            CompleteLayout(window);

            // 切到昨天再切回：会话内记忆阅读模式（自选实现，写入报告）。
            var previous = (Button)window.FindName("ReviewPreviousButton")!;
            var state = (TextBlock)window.FindName("ReviewDateState")!;
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, previous));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (state.Text == "今天" && DateTime.UtcNow < deadline)
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            }

            Assert.AreEqual(Visibility.Visible, GetReadingView(window).Visibility, "切日期后阅读模式必须保持。");
            var next = (Button)window.FindName("ReviewNextButton")!;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, next));
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (state.Text != "今天" && DateTime.UtcNow < deadline)
            {
                PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(20));
            }

            Assert.AreEqual(Visibility.Visible, GetReadingView(window).Visibility);
            CollectionAssert.Contains(GetReadingBlockTexts(window), "今天内容", "切回今天阅读态内容正确。");
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewMarkdownView_SettingsToggleAppliesImmediatelyPersistsAndSurvivesReopen()
    {
        using var directory = new TestDirectory();
        var preferencesStore = new RecordingPreferencesStore();
        var window = CreateReviewMarkdownWindow(directory, enabled: false, preferencesStore: preferencesStore);

        try
        {
            window.Show();
            ExpandCategory(window, BoardCategory.Reference);
            CompleteLayout(window);
            LoadReview(window);
            var modeSwitch = (Border)window.FindName("ReviewModeSwitch")!;
            Assert.AreEqual(Visibility.Collapsed, modeSwitch.Visibility, "默认关闭：复盘表面无切换控件。");

            var settings = OpenSettingsWindow(window);
            ((RadioButton)settings.FindName("SettingsNavGeneral")!).IsChecked = true;
            CompleteLayout(window);
            var toggle = (CheckBox)settings.FindName("ReviewMarkdownViewToggle")!;
            Assert.IsFalse(toggle.IsChecked!.Value, "实验开关默认关闭。");

            toggle.IsChecked = true;
            CompleteLayout(window);
            Assert.IsTrue(window.CurrentPreferences.ReviewMarkdownViewEnabled, "改动必须立即生效（契约 #5）。");
            Assert.IsTrue(preferencesStore.LastSaved!.ReviewMarkdownViewEnabled, "改动必须立即落偏好存储。");
            Assert.AreEqual(Visibility.Visible, modeSwitch.Visibility, "开启后复盘表面切换控件即时出现。");

            toggle.IsChecked = false;
            CompleteLayout(window);
            Assert.IsFalse(window.CurrentPreferences.ReviewMarkdownViewEnabled);
            Assert.AreEqual(Visibility.Collapsed, modeSwitch.Visibility, "关闭即时回到现状表面（逐像素一致前提）。");

            // 重开设置窗：开关状态必须来自已保存偏好而非控件残留。
            toggle.IsChecked = true;
            CompleteLayout(window);
            settings.Close();
            var reopened = OpenSettingsWindow(window);
            ((RadioButton)reopened.FindName("SettingsNavGeneral")!).IsChecked = true;
            var reopenedToggle = (CheckBox)reopened.FindName("ReviewMarkdownViewToggle")!;
            Assert.IsTrue(reopenedToggle.IsChecked!.Value, "重开设置窗必须反映已保存的新值。");
            reopened.Close();
        }
        finally
        {
            CloseReviewWindow(window);
        }
    }

    [STATestMethod]
    public void ReviewMarkdownView_TypographyBrushesFollowThemeDictionary()
    {
        AssertReadingBrushesFollowThemeDictionary(window =>
        {
            var document = GetReadingView(window).Document!;
            Assert.AreSame(
                window.FindResource("PrimaryTextBrush"),
                document.GetValue(System.Windows.Documents.TextElement.ForegroundProperty),
                "正文前景必须解析为主题字典画刷（防写死颜色）。");
            var codeBlock = document.Blocks.OfType<Paragraph>().Single(
                paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains("fenced"));
            Assert.AreSame(window.FindResource("HoverSurfaceBrush"), codeBlock.Background, "代码块底色＝HoverSurfaceBrush。");
            Assert.AreSame(window.FindResource("BorderBrush"), codeBlock.BorderBrush, "代码块描边＝BorderBrush。");
            var quote = document.Blocks.OfType<Section>().Single();
            Assert.AreSame(window.FindResource("BorderBrush"), quote.BorderBrush, "引用竖条＝BorderBrush。");
            var link = document.Blocks.OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>())
                .Single();
            Assert.AreSame(window.FindResource("AccentBrush"), link.Foreground, "链接前景＝AccentBrush。");
        });
    }

    private static void AssertReadingBrushesFollowThemeDictionary(Action<MainWindow> assertBrushes)
    {
        foreach (var (theme, evidenceFileName) in new[]
                 {
                     (DesignTheme.Dark, "review-reading-dark.png"),
                     (DesignTheme.Light, "review-reading-light.png")
                 })
        {
            using var directory = new TestDirectory();
            var paths = AppPaths.ForTests(directory.Root);
            var window = CreateReviewMarkdownWindow(directory);

            try
            {
                Directory.CreateDirectory(paths.ReviewsDirectory);
                var date = DateOnly.FromDateTime(DateTime.Now);
                File.WriteAllText(
                    Path.Combine(paths.ReviewsDirectory, $"{date:yyyy-MM-dd}.md"),
                    ReadingSampleMarkdown);
                window.Show();
                DesignThemeManager.Apply(window, theme);
                ExpandCategory(window, BoardCategory.Reference);
                CompleteLayout(window);
                LoadReview(window);
                EnterReadingMode(window);
                CompleteLayout(window);
                assertBrushes(window);
                SaveVisualEvidence(
                    (Border)window.FindName("WindowShell")!,
                    evidenceFileName,
                    "FTS_DAILY_REVIEW_EVIDENCE_DIR");
            }
            finally
            {
                CloseReviewWindow(window);
            }
        }
    }
}
