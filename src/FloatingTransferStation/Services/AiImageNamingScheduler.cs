using System.Collections.Concurrent;
using System.Text;
using System.Windows.Threading;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 「AI 识图命名」的后台调度器（c2 管线接线，唯一编排点）：两条入库管线
/// （剪贴板采集/外部拖入）提交成功后经 <see cref="TryEnqueue"/> 逐卡入队，
/// 存量卡由装载完成与开关翻转两触发补扫（<see cref="EnqueueExistingUnnamedImages"/>）；
/// 排空轮次串行执行（全局并发=1 定案：识图是大 payload 请求，串行控成本、
/// 降 429 概率，沿 FaviconFetchScheduler 限并发先例）。守卫链照
/// <c>MainWindow.CardAi</c> 全范式：开关关闭＝零动作（<see cref="Enabled"/>
/// 是唯一门控镜像，管线与 UI 零开关感知）、无 Key＝零请求＋每轮状态条恰一次
/// ＋清空剩余队列、会话级一次性尝试标记（失败卡本会话不重试，跨会话重启后
/// 存量扫描再给一次机会）、每卡单飞（<see cref="BoardItem.IsAiNaming"/>）、
/// 乐观并发守卫（命名在途卡被删或已有名→放弃）。落板恒经
/// <see cref="BoardMutationService.UpdateItemImageTitleAsync"/> 唯一管线；
/// 失败只计数汇总状态条、不自动重试。执行体恒经 <see cref="Dispatcher"/>
/// 启动（板面状态与落板快照构建所属线程契约），续体回投 UI 线程；退出排空
/// 由注入的 <paramref name="trackOperation"/>（TrackPendingOperation）承接。
/// </summary>
public sealed class AiImageNamingScheduler(
    AiImageNamingOrganizer organizer,
    BoardService board,
    BoardMutationService mutations,
    ISecretProtector secretProtector,
    Func<AppPreferences> preferencesProvider,
    Action<string> showStatus,
    Action<Task> trackOperation,
    Func<CancellationToken> windowOperationToken,
    Dispatcher dispatcher)
{
    /// <summary>单卡命中的结果归类：成功计数、失败计数、放弃（守卫拦截）不计。</summary>
    private enum NamingOutcome
    {
        Named,
        Failed,
        Abandoned
    }

    private readonly AiImageNamingOrganizer _organizer = organizer;
    private readonly BoardService _board = board;
    private readonly BoardMutationService _mutations = mutations;
    private readonly ISecretProtector _secretProtector = secretProtector;
    private readonly Func<AppPreferences> _preferencesProvider = preferencesProvider;
    private readonly Action<string> _showStatus = showStatus;
    private readonly Action<Task> _trackOperation = trackOperation;
    private readonly Func<CancellationToken> _windowOperationToken = windowOperationToken;
    private readonly Dispatcher _dispatcher = dispatcher;
    private readonly ConcurrentQueue<Guid> _pendingItems = [];
    private readonly HashSet<Guid> _attemptedThisSession = [];
    private readonly object _roundLock = new();

    /// <summary>
    /// 轮次任务：非 null 且未完成＝一轮排空在途。生命周期与队列进出同锁协调
    /// （入队与轮次退出判定互斥），结构上保证任一时刻至多一轮排空在处理队列
    /// ＝全局并发 1，且不因「入队恰逢轮次收尾」丢卡。
    /// </summary>
    private Task? _activeRound;

    /// <summary>
    /// 门控镜像（默认关＝AI 零打扰）：开关判断唯一存在处。由组合根经
    /// ApplyPreferences 唯一管线与窗口构造段镜像下发（契约 #5 即时生效）。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 入队一张待命名图片卡：开关关＝零动作；卡不存在、非图片卡、已有名或已在
    /// 命名＝跳过；会话内已尝试过＝跳过（失败卡本会话不重试）。未在排空则启动
    /// 一轮并经 trackOperation 挂入退出排空（TrackPendingOperation）。
    /// </summary>
    public void TryEnqueue(Guid itemId)
    {
        if (!Enabled)
        {
            return;
        }

        if (_board.FindItem(itemId) is not
            { Kind: BoardItemKind.Image, ImageTitle: null, IsAiNaming: false })
        {
            return;
        }

        Task? round = null;
        lock (_roundLock)
        {
            if (!_attemptedThisSession.Add(itemId))
            {
                return;
            }

            _pendingItems.Enqueue(itemId);
            if (_activeRound is { IsCompleted: false })
            {
                return;
            }

            _activeRound = round = StartDrainRound();
        }

        _trackOperation(round!);
    }

    /// <summary>
    /// 存量未命名图片补扫（装载成功后的 ApplicationIdle 与开关 false→true 翻转
    /// 两触发共用）：全分类过滤图片卡且无标题逐卡入队——会话级尝试标记天然防重，
    /// 开关关闭时 <see cref="TryEnqueue"/> 自零动作。
    /// </summary>
    public void EnqueueExistingUnnamedImages()
    {
        foreach (var category in BoardCategoryCatalog.Ordered)
        {
            foreach (var item in _board.Items(category))
            {
                if (item is { Kind: BoardItemKind.Image, ImageTitle: null })
                {
                    TryEnqueue(item.Id);
                }
            }
        }
    }

    private Task StartDrainRound()
    {
        try
        {
            return _dispatcher.InvokeAsync(DrainRoundAsync).Task.Unwrap();
        }
        catch (Exception exception) when (exception is TaskCanceledException or OperationCanceledException)
        {
            // 调度器已在关闭（窗口退出中）：本轮放弃，剩余卡留待下个会话。
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 一轮排空：每轮开始读一次最新偏好里的 Key（空＝状态条恰一次＋清空本轮
    /// 剩余队列、零请求；Unprotect 失败按未配置降级），逐卡串行执行，结束时
    /// 汇总状态条（成功/失败计数，放弃不计）；开关中途关闭＝放弃剩余静默退出；
    /// 取消（窗口关闭/搬迁静默）＝静默结束。
    /// </summary>
    private async Task DrainRoundAsync()
    {
        try
        {
            var apiKey = _secretProtector.Unprotect(
                _preferencesProvider().DeepSeekApiKeyProtected ?? string.Empty);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                lock (_roundLock)
                {
                    _pendingItems.Clear();
                }

                _showStatus("尚未配置 DeepSeek API Key：设置 → AI 设置。");
                return;
            }

            var namedCount = 0;
            var failedCount = 0;
            while (TryDequeueNext() is { } itemId)
            {
                if (!Enabled)
                {
                    lock (_roundLock)
                    {
                        _pendingItems.Clear();
                    }

                    return;
                }

                switch (await NameSingleItemAsync(itemId, apiKey))
                {
                    case NamingOutcome.Named:
                        namedCount++;
                        break;
                    case NamingOutcome.Failed:
                        failedCount++;
                        break;
                }
            }

            var summary = new StringBuilder();
            if (namedCount > 0)
            {
                summary.Append($"已为 {namedCount} 张图片命名。");
            }

            if (failedCount > 0)
            {
                summary.Append($"{failedCount} 张图片命名失败，已跳过。");
            }

            if (summary.Length > 0)
            {
                _showStatus(summary.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            // 退出排空：取消即静默结束（在途标志由单卡 finally 清理），不打扰收尾。
        }
    }

    /// <summary>取出下一张待命名卡；队列已空＝本轮结束（与入队同锁判定，不丢卡）。</summary>
    private Guid? TryDequeueNext()
    {
        lock (_roundLock)
        {
            if (_pendingItems.TryDequeue(out var itemId))
            {
                return itemId;
            }

            _activeRound = null;
            return null;
        }
    }

    /// <summary>
    /// 单卡命名：入口复查守卫（卡仍存在且无标题且未在整理）→在途标志→识图
    /// （组织器守卫前置：图片缺失/编码失败零请求本地失败）→乐观并发守卫（卡
    /// 被删或已有名→放弃）→经 BoardMutationService 唯一管线落板。业务失败不
    /// 抛异常，由轮次汇总计数；取消向上传播为静默退出。
    /// </summary>
    private async Task<NamingOutcome> NameSingleItemAsync(Guid itemId, string apiKey)
    {
        var item = _board.FindItem(itemId);
        if (item is not
            { Kind: BoardItemKind.Image, ImageTitle: null, IsAiNaming: false, ImageAbsolutePath: not null })
        {
            return NamingOutcome.Abandoned;
        }

        item.BeginAiNaming();
        try
        {
            var outcome = await _organizer.NameImageAsync(
                item.ImageAbsolutePath!,
                apiKey,
                _windowOperationToken());
            if (!outcome.IsSuccess)
            {
                return NamingOutcome.Failed;
            }

            // 乐观并发守卫：命名在途卡被删或已有名（另一次命名/外部写入）→
            // 放弃本次结果，不覆盖用户新状态。
            if (_board.FindItem(itemId) is not { ImageTitle: null })
            {
                _showStatus("图片已变化，命名结果已放弃。");
                return NamingOutcome.Abandoned;
            }

            return await _mutations.UpdateItemImageTitleAsync(itemId, outcome.Title)
                ? NamingOutcome.Named
                : NamingOutcome.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 非取消异常（保存管线对非 IO 异常原样上抛等）计为失败交轮次汇总，
            // 不中断本轮其余卡片的处理。
            return NamingOutcome.Failed;
        }
        finally
        {
            item.EndAiNaming();
        }
    }
}
