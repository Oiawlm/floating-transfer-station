using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 1.41.0 发布证据（FTS_AI_EVIDENCE_DIR 设置时产出真实 WPF 渲染截图，Issue #146）：
/// 截断文字卡悬停出现「AI 整理」钮（浅/深主题各一）与整理前后对比。悬停走
/// 真实光标路径（契约 #13），渲染走 SaveVisualEvidence（RenderTargetBitmap
/// 实拍窗口壳层）；对比用正文为中性测试数据（语音转写式原文与整理后的
/// 干净文本），不携带任何真实密钥。
/// </summary>
public sealed partial class MainWindowInteractionTests
{
    /// <summary>中性测试数据：语音转写式原文（口语跳跃、重复、填充词，超 5 行截断）。</summary>
    private const string AiEvidenceTranscript =
        "嗯，这个关于新版发布流程我想说一下啊，就是首先呢我们周三要先冻结代码，就是你改完的东西周三之后就不能再进了，"
        + "然后呢测试那边会跑一轮回归，对，然后我想说的是发布会分两批推，先内测渠道再全量，中间隔一天，"
        + "呃，冻结代码这个事很重要，因为去年有一次就是冻结之后还有人往里合并，然后就把打包搞坏了，这个教训要记住。"
        + "然后全量推送之前一定要把公告写好，公告要找产品确认过才能发，不要自己直接就发了啊。"
        + "对了还有回滚方案，就是如果全量之后发现重大问题，我们直接切回旧版本，回滚脚本要提前演练一遍，"
        + "然后呢公告这个事，我记得上上次就是公告和版本没对上，用户看到了新功能但是公告没提，就有人来问了，"
        + "所以这次公告和包要一起准备好。嗯，大概就是这些，周三冻结、两批推送、公告产品确认、回滚演练，就这样。";

    /// <summary>中性测试数据：整理后的干净文本（忠于原意的目标形态）。</summary>
    private const string AiEvidenceOrganized =
        "关于新版发布流程：\n"
        + "- 周三冻结代码：冻结后不再合并任何改动（去年有人在冻结后合并导致打包损坏，教训要记住）。\n"
        + "- 发布分两批推送：先内测渠道，隔一天再全量。\n"
        + "- 公告与版本要对齐：全量推送前公告需经产品确认（避免用户看到新功能但公告未提）；公告和包要一起准备好。\n"
        + "- 回滚方案：全量后发现重大问题直接切回旧版本，回滚脚本要提前演练一遍。";

    [STATestMethod]
    public void Evidence_CaptureCardAiOrganize()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_AI_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_AI_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText(AiEvidenceTranscript, BoardCategory.Inbox);
        board.AddText("短卡示例：周五同步会改到下午三点。", BoardCategory.Inbox);
        var window = CreateWindow(directory, board);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Topmost = true;
            DesignThemeManager.Apply(window, DesignTheme.Light);
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = FindDescendants<ListBoxItem>(list)
                .Single(item => Equals(item.DataContext, board.Items(BoardCategory.Inbox)[0]));

            // 整理前：原始转写稿的常态（未悬停，无操作按钮）。
            CompleteLayout(window);
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "card-ai-organize-before.png",
                "FTS_AI_EVIDENCE_DIR");

            // 浅色主题：悬停揭示 AI 钮（截断卡右上追加行）。
            HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
            PumpDispatcherFor(
                window.Dispatcher,
                TimeSpan.FromMilliseconds(DesignTokens.FadeVisibilityMs + 120));
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "card-ai-organize-light.png",
                "FTS_AI_EVIDENCE_DIR");

            // 深色主题：同一悬停态。
            DesignThemeManager.Apply(window, DesignTheme.Dark);
            CompleteLayout(window);
            PumpDispatcherFor(
                window.Dispatcher,
                TimeSpan.FromMilliseconds(DesignTokens.FadeVisibilityMs + 120));
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "card-ai-organize-dark.png",
                "FTS_AI_EVIDENCE_DIR");

            // 整理后：正文替换为整理结果（中性测试数据），按钮回到常态。
            DesignThemeManager.Apply(window, DesignTheme.Light);
            board.UpdateText(
                ((BoardItem)container.DataContext).Id,
                AiEvidenceOrganized);
            CompleteLayout(window);
            PumpDispatcherFor(window.Dispatcher, TimeSpan.FromMilliseconds(200));
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "card-ai-organize-after.png",
                "FTS_AI_EVIDENCE_DIR");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindow(window);
        }
    }

    /// <summary>AI 整理总开关关闭态证据（同一 FTS_AI_EVIDENCE_DIR）：截断文字卡
    /// 悬停也不出现 AI 整理钮、追加行塌缩（与 card-ai-organize-light.png 的
    /// 开启态同构图对照）。</summary>
    [STATestMethod]
    public void Evidence_CaptureCardAiOrganizeToggleOff()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("FTS_AI_EVIDENCE_DIR")))
        {
            Assert.Inconclusive("未设置 FTS_AI_EVIDENCE_DIR，跳过发布证据产出。");
        }

        using var directory = new TestDirectory();
        var board = new BoardService();
        board.AddText(AiEvidenceTranscript, BoardCategory.Inbox);
        var handler = new CardAiOrganizeToggleOffHandler();
        var window = CreateCardAiWindow(directory, board, handler, aiOrganizeEnabled: false);
        var savedCursor = SaveCursorPosition();

        try
        {
            window.Show();
            window.Topmost = true;
            DesignThemeManager.Apply(window, DesignTheme.Light);
            ExpandCategory(window, BoardCategory.Inbox);
            CompleteLayout(window);
            var list = (ListBox)window.FindName("BoardList");
            var container = FindDescendants<ListBoxItem>(list)
                .Single(item => Equals(item.DataContext, board.Items(BoardCategory.Inbox)[0]));

            HoverRealAt(window, container, new Point(24, container.ActualHeight * 0.8));
            PumpDispatcherFor(
                window.Dispatcher,
                TimeSpan.FromMilliseconds(DesignTokens.FadeVisibilityMs + 120));
            SaveVisualEvidence(
                (Border)window.FindName("WindowShell"),
                "card-ai-organize-toggle-off.png",
                "FTS_AI_EVIDENCE_DIR");
            Assert.AreEqual(0, handler.CallCount, "关＝证据链路本身也必须零请求。");
        }
        finally
        {
            RestoreCursorPosition(savedCursor);
            CloseWindow(window);
        }
    }

    /// <summary>拒绝一切请求的假 handler（开关关闭态证据专用）。</summary>
    private sealed class CardAiOrganizeToggleOffHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new AssertFailedException("总开关关闭时不得发起任何请求。");
        }
    }
}
