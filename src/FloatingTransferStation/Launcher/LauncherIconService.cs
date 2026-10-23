using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Launcher;

/// <summary>.lnk 解析结果（图标戳指纹与导入默认名共用；参数原样不 split）。</summary>
internal sealed record ShellLinkInfo(
    string? ResolvedTargetPath,
    string? Arguments,
    string? WorkingDirectory,
    string? IconLocation,
    int IconIndex,
    string? Description);

/// <summary>
/// Shell 快捷方式解析（#30）：IShellLinkW + IPersistFile 仅用于展示解析与指纹
/// （GetPath 32768 缓冲、GetArguments/GetWorkingDirectory/GetIconLocation/GetDescription）；
/// RPC_E_CHANGED_MODE 显式失败（调用方必须处于 STA）。启动 .lnk 一律走本体
/// UseShellExecute（覆盖 MSI 广告快捷方式），不经本解析。
/// </summary>
internal static class ShellLinkHelper
{
    private const int MaxPathBuffer = 32768;
    private const uint StgmRead = 0x0;

    internal static ShellLinkInfo? TryRead(string shortcutPath)
    {
        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(ShellInterop.ClsidShellLink);
            if (shellLinkType is null)
            {
                return null;
            }

            if (Activator.CreateInstance(shellLinkType) is not ShellInterop.IShellLinkW shellLink ||
                shellLink is not ShellInterop.IPersistFile persistFile ||
                persistFile.Load(shortcutPath, StgmRead) != 0)
            {
                return null;
            }

            var target = new StringBuilder(MaxPathBuffer);
            var arguments = new StringBuilder(MaxPathBuffer);
            var workingDirectory = new StringBuilder(MaxPathBuffer);
            var iconLocation = new StringBuilder(MaxPathBuffer);
            var description = new StringBuilder(1024);
            _ = shellLink.GetPath(target, MaxPathBuffer, IntPtr.Zero, 0);
            _ = shellLink.GetArguments(arguments, MaxPathBuffer);
            _ = shellLink.GetWorkingDirectory(workingDirectory, MaxPathBuffer);
            _ = shellLink.GetIconLocation(iconLocation, MaxPathBuffer, out var iconIndex);
            _ = shellLink.GetDescription(description, 1024);
            return new ShellLinkInfo(
                target.Length > 0 ? target.ToString() : null,
                arguments.Length > 0 ? arguments.ToString() : null,
                workingDirectory.Length > 0 ? workingDirectory.ToString() : null,
                iconLocation.Length > 0 ? iconLocation.ToString() : null,
                iconIndex,
                description.Length > 0 ? description.ToString() : null);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>导入默认名：.lnk 取描述（缺失回退文件名去扩展），其余取文件名去扩展（≤64 截断）。</summary>
    internal static string DefaultDisplayName(string filePath)
    {
        string name;
        if (filePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var description = TryRead(filePath)?.Description;
            name = string.IsNullOrWhiteSpace(description)
                ? Path.GetFileNameWithoutExtension(filePath)
                : description;
        }
        else
        {
            name = Path.GetFileNameWithoutExtension(filePath);
        }

        return LauncherItem.LimitDisplayName(name);
    }
}

/// <summary>
/// 快捷启动图标服务（#30，Issue #162）：专用 STA WPF Dispatcher 线程承载全部 Shell COM
/// （自带消息泵，满足 STA 重入）。主路径 SHCreateItemFromParsingName →
/// IShellItemImageFactory.GetImage(256, ICONONLY|BIGGERSIZEOK)（网络路径加 MEMORYONLY）；
/// Application 类直提取阶梯（实机实证 2026-10-20，Issue #206）：Shell 大尺寸图标走异步
/// 缓存管线，缓存未热时 GetImage 返回 E_PENDING(0x8000000A)，而 SHGetImageList 在部分
/// 机器整链 E_NOINTERFACE——两环同时失效即「添加应用永远占位」（合成可达、实机不可达）。
/// 直提取（SHDefExtractIconW→SHGetFileInfoW(SHGFI_ICON)）不经图标缓存与 image list，
/// 机器环境无关；「应用未注册」的 PackagedApp 终回退（IExtractIconW）同理机器无关。
/// 回退链 SHGetFileInfo(SHGFI_SYSICONINDEX) → SHGetImageList(JUMBO→EXTRALARGE→LARGE)
/// → IImageList.GetIcon（校验 index &lt; count）保留为健康系统的最后一级。缓存发布协议：
/// 提取 PNG 字节 → tmp → File.Move(overwrite) → 内存字典 → 最后经目录协议更新
/// IconStamp——stamp 只在图标成功发布后推进，任何前置失败=旧图继续有效（陈旧可接受）。
/// 目标删除→保留最后成功 PNG 进失效灰态。孤儿清扫仅在启动装载成功后运行一次（此后
/// 新增 PNG 到下次启动才可能被清，杜绝与在途提取竞态）。排队等待方放弃（瓦片保持占位）
/// ≠ 执行超时：卡住的 Shell COM 调用阻塞图标管线属可接受降级（启动/添加/主 UI 不受影响）。
/// 提取请求的 pending 生命周期由请求自身 finally 承载：任何异常类型都完成等待者并清理
/// 状态（失败可观测＋状态可恢复），不依赖异常类型过滤器或派发器兜底。
/// </summary>
internal sealed class LauncherIconService : IDisposable
{
    /// <summary>Url 条目已缓存戳（常量语义：null=未取到＝启动重试轮判定面；"ok"=已缓存；
    /// 编辑目标经 AdoptEditedTarget 置空即重抓）。</summary>
    internal const string UrlIconStampOk = "ok";

    /// <summary>启动重试轮的每条目排队上限（超时放弃并计失败，防风暴无限等待）。</summary>
    private static readonly TimeSpan StartupQueueCap = TimeSpan.FromSeconds(30);

    /// <summary>单条目 favicon 网络总预算（取得首个执行资格后启动）。</summary>
    private static readonly TimeSpan FaviconEntryBudget = TimeSpan.FromSeconds(20);

    private readonly LauncherCatalog _catalog;
    private readonly string _iconsDirectory;
    private readonly Thread _iconThread;
    private readonly Dispatcher _iconDispatcher;
    private readonly Dispatcher _uiDispatcher;
    private readonly Dictionary<string, BitmapSource> _memory = [];
    private readonly Dictionary<string, List<Action<BitmapSource?>>> _pending = [];
    private readonly FaviconClient? _faviconClient;
    private readonly FaviconFetchScheduler? _faviconScheduler;
    private readonly FaviconNegativeCache _faviconNegativeCache = new();
    private readonly HashSet<string> _startupRetryAttempted = [];

    private LauncherIconService(
        LauncherCatalog catalog,
        string iconsDirectory,
        Dispatcher uiDispatcher,
        Thread iconThread,
        Dispatcher iconDispatcher,
        FaviconClient? faviconClient,
        FaviconFetchScheduler? faviconScheduler)
    {
        _catalog = catalog;
        _iconsDirectory = iconsDirectory;
        _uiDispatcher = uiDispatcher;
        _iconThread = iconThread;
        _iconDispatcher = iconDispatcher;
        _faviconClient = faviconClient;
        _faviconScheduler = faviconScheduler;
    }

    internal static LauncherIconService Start(
        LauncherCatalog catalog,
        string iconsDirectory,
        Dispatcher uiDispatcher,
        FaviconClient? faviconClient = null,
        FaviconFetchScheduler? faviconScheduler = null)
    {
        var dispatcherReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? iconDispatcher = null;
        var thread = new Thread(() =>
        {
            iconDispatcher = Dispatcher.CurrentDispatcher;
            // 图标管线任何未处理异常不得崩溃进程：提取失败=保持占位（可接受降级），
            // 派发器层兜底吞掉并记录诊断日志。
            iconDispatcher.UnhandledException += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "fts-launcher-icon-failure.log"),
                        eventArgs.Exception.ToString());
                }
                catch (IOException)
                {
                }
            };
            dispatcherReady.TrySetResult();
            try
            {
                Dispatcher.Run();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                dispatcherReady.TrySetException(exception);
            }
        })
        {
            Name = "FTS 启动项图标 STA",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        dispatcherReady.Task.Wait(TimeSpan.FromSeconds(5));
        return new LauncherIconService(
            catalog,
            iconsDirectory,
            uiDispatcher,
            thread,
            iconDispatcher ?? uiDispatcher,
            faviconClient,
            faviconScheduler);
    }

    /// <summary>UI 线程同步读内存缓存（命中为纯内存查询，零磁盘 I/O）。</summary>
    internal BitmapSource? TryGetCached(string iconKey)
    {
        lock (_memory)
        {
            return _memory.GetValueOrDefault(iconKey);
        }
    }

    /// <summary>
    /// 请求图标：内存未命中后按 Kind 分派——Application/PackagedApp 派发到 STA 线程
    /// （stamp 未变优先读缓存文件）；Url 走线程池 favicon 抓取（HTTP 绝不进 STA Shell
    /// 线程）。结果经 UI Dispatcher 回调。等待方放弃＝瓦片保持占位（回调引用失效即
    /// no-op）。
    /// </summary>
    internal void RequestIcon(LauncherItem item, Action<BitmapSource?> onCompleted) =>
        RequestIconCore(item, onCompleted, bypassNegativeCache: false);

    private void RequestIconCore(LauncherItem item, Action<BitmapSource?> onCompleted, bool bypassNegativeCache)
    {
        var iconKey = item.IconKey;
        if (iconKey.Length == 0)
        {
            return;
        }

        lock (_memory)
        {
            if (_memory.TryGetValue(iconKey, out var cached))
            {
                _uiDispatcher.BeginInvoke(() => onCompleted(cached));
                return;
            }
        }

        lock (_pending)
        {
            if (_pending.TryGetValue(iconKey, out var waiters))
            {
                waiters.Add(onCompleted);
                return;
            }

            _pending[iconKey] = [onCompleted];
        }

        if (item.Kind == LauncherKind.Url)
        {
            // 目标快照：发布前复核（编辑目标后旧抓取结果不得覆盖新目标——gate 落盘与 stamp）。
            var capturedTarget = item.TargetPath;
            _ = Task.Run(() => FetchUrlIconAsync(item, iconKey, capturedTarget, bypassNegativeCache));
            return;
        }

        _iconDispatcher.BeginInvoke(() =>
        {
            BitmapSource? icon = null;
            try
            {
                icon = LoadOverrideForTests is { } attempt ? attempt(item) : LoadOrCreate(item, iconKey);
            }
            catch (Exception exception)
            {
                // 提取失败＝保持占位（任何异常类型都不得让等待者悬挂或崩溃进程——
                // #30 :247「未处理异常不得崩溃进程」由本 catch 前移到请求自身承载）。
                LogFailure(iconKey, exception);
            }
            finally
            {
                CompleteRequest(iconKey, icon);
            }
        });
    }

    /// <summary>pending 生命周期单点：取出等待者并逐一经 UI 线程完成（任何结果形态）。</summary>
    private void CompleteRequest(string iconKey, BitmapSource? icon)
    {
        List<Action<BitmapSource?>> completions;
        lock (_pending)
        {
            completions = _pending.GetValueOrDefault(iconKey) ?? [];
            _pending.Remove(iconKey);
        }

        foreach (var completion in completions)
        {
            _uiDispatcher.BeginInvoke(() => completion(icon));
        }
    }

    /// <summary>仅供测试注入：替换 STA 提取尝试体（异常毒化回归的注入点；null=真实提取）。</summary>
    internal Func<LauncherItem, BitmapSource?>? LoadOverrideForTests { get; set; }

    /// <summary>仅供测试观察：重枚举取图请求的 IconKey 记录（#30 切片 4）。</summary>
    internal System.Collections.Concurrent.ConcurrentQueue<string> RefreshRequestsForTests { get; } = new();

    /// <summary>仅供测试注入：重置启动重试轮的去重集合（多轮语义验证用）。</summary>
    internal void ResetStartupRetryRoundForTests() => _startupRetryAttempted.Clear();

    /// <summary>
    /// favicon 抓取管线（线程池）：磁盘缓存读（stamp="ok"）→ 负缓存闸 → 同源阶梯抓取
    /// （首个执行资格由本方法取得并作为持有许可交给客户端，条目预算在资格取得后启动）
    /// → WIC 解码选帧 → PNG 重编码 → 发布（目标快照复核 gate 落盘与 stamp，复用
    /// tmp→Move 协议）。取消/被取代不记负缓存；预算耗尽/阶梯耗尽记负缓存。
    /// </summary>
    private async Task FetchUrlIconAsync(LauncherItem item, string iconKey, string capturedTarget, bool bypassNegativeCache)
    {
        BitmapSource? icon = null;
        try
        {
            var pngPath = Path.Combine(_iconsDirectory, iconKey + ".png");
            if (File.Exists(pngPath) && item.IconStamp == UrlIconStampOk)
            {
                icon = DecodePng(File.ReadAllBytes(pngPath));
                if (icon is not null)
                {
                    Remember(iconKey, icon);
                    return;
                }
            }

            if (!bypassNegativeCache && _faviconNegativeCache.IsNegative(iconKey, DateTimeOffset.UtcNow))
            {
                return;
            }

            if (_faviconClient is null || _faviconScheduler is null ||
                Uri.TryCreate(item.TargetPath, UriKind.Absolute, out var pageUrl) is false ||
                pageUrl.Scheme is not ("http" or "https"))
            {
                return;
            }

            // 首个执行资格（排队上限防风暴；此阶段不启动条目预算）。
            IAsyncDisposable heldPermit;
            try
            {
                heldPermit = await _faviconScheduler
                    .AcquireAsync(OriginOf(pageUrl), new CancellationTokenSource(StartupQueueCap).Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _faviconNegativeCache.Record(iconKey, DateTimeOffset.UtcNow);
                return;
            }

            await using (heldPermit.ConfigureAwait(false))
            {
                using var budget = new CancellationTokenSource(FaviconEntryBudget);
                var result = await _faviconClient
                    .FetchPageIconAsync(item.TargetPath, budget.Token, heldPermit)
                    .ConfigureAwait(false);

                if (result.Candidate is not { } candidate)
                {
                    LogFaviconFailure(iconKey, pageUrl, result.Failure);
                    if (result.Failure is not FaviconFailureKind.Network)
                    {
                        _faviconNegativeCache.Record(iconKey, DateTimeOffset.UtcNow);
                    }

                    return;
                }

                var pngBytes = EncodePngFromCandidate(candidate);
                if (pngBytes is null)
                {
                    LogFaviconFailure(iconKey, pageUrl, FaviconFailureKind.Exhausted);
                    _faviconNegativeCache.Record(iconKey, DateTimeOffset.UtcNow);
                    return;
                }

                // 目标快照复核 gate 落盘与 stamp（编辑目标后旧抓取结果不得覆盖新目标）。
                if (item.TargetPath != capturedTarget || item.IconKey != iconKey)
                {
                    return;
                }

                Directory.CreateDirectory(_iconsDirectory);
                var temporaryPath = pngPath + ".tmp";
                File.WriteAllBytes(temporaryPath, pngBytes);
                File.Move(temporaryPath, pngPath, overwrite: true);
                var published = DecodePng(pngBytes);
                if (published is null)
                {
                    return;
                }

                Remember(iconKey, published);
                icon = published;
                if (item.IconStamp != UrlIconStampOk)
                {
                    _ = _catalog.UpdateIconStampAsync(item.Id, UrlIconStampOk);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 条目预算耗尽＝真实失败（记负缓存防抖）；被取代的取消由快照复核剔除。
            if (item.TargetPath == capturedTarget)
            {
                _faviconNegativeCache.Record(iconKey, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception exception)
        {
            // favicon 管线任何异常不得波及完成语义：等待者照常完成（保持占位）。
            LogFailure(iconKey, exception);
        }
        finally
        {
            CompleteRequest(iconKey, icon);
        }
    }

    private static Uri OriginOf(Uri pageUrl) => new($"{pageUrl.Scheme}://{pageUrl.IdnHost}:{pageUrl.Port}/");

    /// <summary>favicon 失败分类日志：只记 host＋失败类别（隐私红线：不含路径与 query）。</summary>
    private static void LogFaviconFailure(string iconKey, Uri pageUrl, FaviconFailureKind failure) =>
        LogFailure(iconKey, new InvalidOperationException($"favicon 阶梯未产出 kind={failure} host={pageUrl.IdnHost}"));

    /// <summary>WIC 解码选帧（线程池）：逐帧读元数据（只检查前 16 帧、只解码入选帧）；
    /// ≥96px 取最小者、无则取最大者；>256px 经 WIC 高质量缩放解码到 ≤256；坏帧跳过；
    /// SVG/WebP 等不支持格式已在客户端按签名淘汰。产出统一 PNG 字节（缓存格式唯一）。</summary>
    private static byte[]? EncodePngFromCandidate(FaviconCandidate candidate)
    {
        var stream = new MemoryStream(candidate.Bytes);
        BitmapDecoder decoder;
        try
        {
            decoder = candidate.Format == FaviconImageFormat.Ico
                ? new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
                : BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch
        {
            stream.Dispose();
            throw;
        }

        try
        {
            var frameCount = Math.Min(decoder.Frames.Count, FaviconClient.MaxIcoFramesInspected);
            BitmapFrame? chosen = null;
            for (var i = 0; i < frameCount; i++)
            {
                var frame = decoder.Frames[i];
                if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
                    (long)frame.PixelWidth * frame.PixelHeight > FaviconClient.MaxFramePixels)
                {
                    continue;
                }

                chosen = chosen is null ? frame : PreferFrame(chosen, frame);
            }

            if (chosen is null)
            {
                return null;
            }

            BitmapSource source = chosen;
            var longest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longest > 256)
            {
                var scale = 256.0 / longest;
                source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(scale, scale));
            }

            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or NotSupportedException or COMException)
        {
            // 坏帧/不支持格式＝候选失败（调用方记负缓存）。
            return null;
        }
    }

    /// <summary>选帧规则（#30 修订）：≥目标尺寸中取最小；不足则取最大；同档取像素积更小。</summary>
    private static BitmapFrame PreferFrame(BitmapFrame current, BitmapFrame candidate)
    {
        var currentSufficient = Math.Min(current.PixelWidth, current.PixelHeight) >= FaviconClient.TargetSize;
        var candidateSufficient = Math.Min(candidate.PixelWidth, candidate.PixelHeight) >= FaviconClient.TargetSize;
        if (candidateSufficient != currentSufficient)
        {
            return candidateSufficient ? candidate : current;
        }

        var currentMetric = currentSufficient
            ? Math.Min(current.PixelWidth, current.PixelHeight)
            : -Math.Min(current.PixelWidth, current.PixelHeight);
        var candidateMetric = candidateSufficient
            ? Math.Min(candidate.PixelWidth, candidate.PixelHeight)
            : -Math.Min(candidate.PixelWidth, candidate.PixelHeight);
        if (candidateMetric != currentMetric)
        {
            return candidateMetric > currentMetric ? candidate : current;
        }

        return (long)candidate.PixelWidth * candidate.PixelHeight < (long)current.PixelWidth * current.PixelHeight
            ? candidate
            : current;
    }

    /// <summary>
    /// 启动重试轮：对无图标（stamp≠"ok" 或 PNG 缺失）的 Url 条目静默重抓，每进程
    /// 每条目至多一次（按 IconKey 去重），2/s 节流排队。全部在池上执行（UI 线程零
    /// 磁盘/网络）；瓦片 Loaded 的常规请求经 pending 去重与本轮合并。
    /// </summary>
    internal void RunStartupRetryRoundOnce()
    {
        if (_faviconClient is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var index = 0;
            foreach (var item in _catalog.Items.ToList())
            {
                if (item.Kind != LauncherKind.Url)
                {
                    continue;
                }

                var iconKey = item.IconKey;
                if (iconKey.Length == 0 || !_startupRetryAttempted.Add(iconKey))
                {
                    continue;
                }

                var pngPath = Path.Combine(_iconsDirectory, iconKey + ".png");
                if (item.IconStamp == UrlIconStampOk && File.Exists(pngPath))
                {
                    continue;
                }

                var captured = item;
                var delay = TimeSpan.FromMilliseconds(500 * Interlocked.Increment(ref index));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    RequestIconCore(captured, _ => { }, bypassNegativeCache: false);
                });
            }
        });
    }

    /// <summary>
    /// 强制刷新（#30）：「应用未注册」激活失败触发的重枚举取图——Application/
    /// PackagedApp 绕过内存与 pending 去重直发提取线程；Url 走 favicon 抓取的
    /// 负缓存绕行通道（编辑目标/显式重取语义）。失败时保留最后成功 PNG（失效灰态
    /// 由 UI 层承载）。
    /// </summary>
    internal void RequestRefresh(LauncherItem item, Action<BitmapSource?> onCompleted)
    {
        var iconKey = item.IconKey;
        if (iconKey.Length == 0)
        {
            // 空 key无从提取：仍回调空结果，调用方不被静默悬挂。
            onCompleted(null);
            return;
        }

        if (item.Kind == LauncherKind.Url)
        {
            // 显式重取＝绕过负缓存（Added/TargetEdited 同语义）；pending 去重照常合并。
            RequestIconCore(item, onCompleted, bypassNegativeCache: true);
            return;
        }

        RefreshRequestsForTests.Enqueue(iconKey);
        _iconDispatcher.BeginInvoke(() =>
        {
            BitmapSource? icon = null;
            try
            {
                icon = LoadOverrideForTests is { } attempt ? attempt(item) : LoadOrCreate(item, iconKey);
            }
            catch (Exception exception)
            {
                // 提取失败＝保持现状（旧图继续有效）；等待者照常完成，不悬挂。
                LogFailure(iconKey, exception);
            }

            _uiDispatcher.BeginInvoke(() => onCompleted(icon));
        });
    }

    /// <summary>孤儿清扫：仅启动装载成功后运行一次；顺带清理 >1h 的半成品 tmp。</summary>
    internal void SweepOrphansOnce()
    {
        var referenced = new HashSet<string>(
            _catalog.Items.Select(item => item.IconKey).Where(key => key.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_iconsDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_iconsDirectory, "*.png"))
            {
                if (!referenced.Contains(Path.GetFileNameWithoutExtension(file)))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // 下次启动再清。
                    }
                }
            }

            foreach (var temporary in Directory.EnumerateFiles(_iconsDirectory, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(temporary) < DateTime.UtcNow.AddHours(-1))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private BitmapSource? LoadOrCreate(LauncherItem item, string iconKey)
    {
        var pngPath = Path.Combine(_iconsDirectory, iconKey + ".png");
        var currentStamp = ComputeStamp(item);
        if (File.Exists(pngPath) && currentStamp is not null && item.IconStamp == currentStamp)
        {
            var cached = DecodePng(File.ReadAllBytes(pngPath));
            if (cached is not null)
            {
                Remember(iconKey, cached);
                return cached;
            }
        }

        var extracted = ExtractPng(item);
        if (extracted is null)
        {
            // 目标已删除/提取失败：保留最后成功 PNG 进失效灰态（UI 层灰态显示）。
            if (File.Exists(pngPath))
            {
                var stale = DecodePng(File.ReadAllBytes(pngPath));
                if (stale is not null)
                {
                    Remember(iconKey, stale);
                    return stale;
                }
            }

            return null;
        }

        Directory.CreateDirectory(_iconsDirectory);
        var temporaryPath = pngPath + ".tmp";
        File.WriteAllBytes(temporaryPath, extracted);
        File.Move(temporaryPath, pngPath, overwrite: true);
        var published = DecodePng(extracted);
        if (published is null)
        {
            return null;
        }

        Remember(iconKey, published);

        // stamp 只在图标成功发布后推进（经目录串行协议持久化；失败静默＝陈旧可接受）。
        if (currentStamp is not null && item.IconStamp != currentStamp)
        {
            _ = _catalog.UpdateIconStampAsync(item.Id, currentStamp);
        }

        return published;
    }

    private void Remember(string iconKey, BitmapSource icon)
    {
        lock (_memory)
        {
            _memory[iconKey] = icon;
        }
    }

    /// <summary>主路径 + 回退链，产出 PNG 字节（GDI 句柄按类型只删一次）。</summary>
    private static byte[]? ExtractPng(LauncherItem item)
    {
        var (handle, isIcon) = ExtractIconHandle(item);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var source = isIcon
                ? System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    handle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions())
                : System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    handle,
                    IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            if (isIcon)
            {
                _ = ShellInterop.DestroyIcon(handle);
            }
            else
            {
                _ = ShellInterop.DeleteObject(handle);
            }
        }
    }

    /// <summary>主路径：IShellItemImageFactory（PackagedApp 解析名 shell:AppsFolder\{AUMID}，
    /// 解析失败时枚举 AppsFolder 按 AUMID 匹配后对项 RCW 直接 QI 图标工厂）；
    /// 系统位置（ShellLocation）不走该工厂（调研定案选型），在入口直接分流。</summary>
    private static (IntPtr Handle, bool IsIcon) ExtractIconHandle(LauncherItem item)
    {
        if (item.Kind == LauncherKind.ShellLocation)
        {
            // 系统位置（回收站/「此电脑」）：SHGetFileInfo 对 CLSID 解析名取图标索引 →
            // 系统图像列表大档（JUMBO/EXTRALARGE ≥48px）取 HICON；图像列表链不可用时
            // 回退 SHGetFileInfo 直取 HICON（与 Application 直提取同族、机器无关）。
            // 回收站空/满态取提取时当前态，不追会话内切换（登记已知边界）。
            var listHandle = ExtractViaSystemImageList(item);
            if (listHandle != IntPtr.Zero)
            {
                return (listHandle, true);
            }

            return (TryGetAssociatedIcon(item) ?? IntPtr.Zero, true);
        }

        var parsingName = item.Kind switch
        {
            LauncherKind.Application => item.TargetPath,
            LauncherKind.PackagedApp => $"shell:AppsFolder\\{item.TargetPath}",
            _ => null
        };
        if (parsingName is null)
        {
            return (IntPtr.Zero, false);
        }

        var flags = ShellInterop.SiigbfIconOnly | ShellInterop.SiigbfBiggerSizeOk;
        if (item.Kind == LauncherKind.Application &&
            (item.TargetPath.StartsWith(@"\\", StringComparison.Ordinal) ||
             item.TargetPath.StartsWith(@"//", StringComparison.Ordinal)))
        {
            // UNC 死路径：MEMORYONLY 防 shell 层挂起等待。
            flags |= ShellInterop.SiigbfMemoryOnly;
        }

        var factoryId = typeof(ShellInterop.IShellItemImageFactory).GUID;
        if (ShellInterop.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, factoryId, out var primaryPointer) == 0)
        {
            try
            {
                var factory = (ShellInterop.IShellItemImageFactory)Marshal.GetObjectForIUnknown(primaryPointer);
                if (factory.GetImage(
                        new ShellInterop.Size(ShellInterop.ShellIconSize, ShellInterop.ShellIconSize),
                        flags,
                        out var bitmapHandle) == 0 &&
                    bitmapHandle != IntPtr.Zero)
                {
                    return (bitmapHandle, false);
                }
            }
            catch (InvalidCastException)
            {
            }
            finally
            {
                Marshal.Release(primaryPointer);
            }
        }

        if (item.Kind == LauncherKind.PackagedApp &&
            TryGetAppsFolderItemByAumid(item.TargetPath) is { } enumerated)
        {
            // shell:AppsFolder\{AUMID} 解析对个别打包应用不可行（非契约）：
            // 枚举命中后对项本身 QI 图标工厂（RCW 强制转换即 QI）。
            if (enumerated is ShellInterop.IShellItemImageFactory directFactory &&
                directFactory.GetImage(
                    new ShellInterop.Size(ShellInterop.ShellIconSize, ShellInterop.ShellIconSize),
                    flags,
                    out var enumeratedHandle) == 0 &&
                enumeratedHandle != IntPtr.Zero)
            {
                return (enumeratedHandle, false);
            }
        }

        // PackagedApp 终回退（实现期修订）：AppsFolder 条目在实际系统不实现
        // IShellItemImageFactory（E_NOINTERFACE，探针实证 2026-10-14）——枚举命中项经
        // 文件夹 GetUIObjectOf(IExtractIconW) 提取 HICON。
        if (item.Kind == LauncherKind.PackagedApp &&
            InstalledAppsEnumerator.TryExtractIconForAumid(item.TargetPath) is { } iconHandle &&
            iconHandle != IntPtr.Zero)
        {
            return (iconHandle, true);
        }

        if (item.Kind == LauncherKind.Application)
        {
            // 直提取阶梯（#30 修订，Issue #206 实机实证）：GetImage(256) 在图标缓存未热时
            // 返回 E_PENDING、SHGetImageList 在部分机器整链 E_NOINTERFACE——既有两级
            // 同时失效即「添加应用永远占位」。SHDefExtractIconW/SHGetFileInfoW(SHGFI_ICON)
            // 不经图标缓存与 image list，机器环境无关；均返回 HICON（alpha 由
            // CreateBitmapSourceFromHIcon 路径正确承载）。
            if (TryExtractFileIconDirect(item) is { } direct)
            {
                return (direct, true);
            }

            if (TryGetAssociatedIcon(item) is { } associated)
            {
                return (associated, true);
            }
        }

        return (ExtractViaSystemImageList(item), true);
    }

    /// <summary>SHDefExtractIconW 直提取（不经图标缓存与 image list）：exe 目标本体 →
    /// .lnk 解析目标 → .lnk IconLocation（负索引按 0）。返回 HICON；未提取返回 null。</summary>
    private static IntPtr? TryExtractFileIconDirect(LauncherItem item)
    {
        if (!item.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractDirectIcon(item.TargetPath, 0);
        }

        if (ShellLinkHelper.TryRead(item.TargetPath) is not { } link)
        {
            return null;
        }

        if (link.ResolvedTargetPath is { Length: > 0 } resolved &&
            File.Exists(resolved) &&
            ExtractDirectIcon(resolved, 0) is { } resolvedHandle)
        {
            return resolvedHandle;
        }

        if (link.IconLocation is { Length: > 0 } iconSource &&
            File.Exists(iconSource) &&
            ExtractDirectIcon(iconSource, Math.Max(link.IconIndex, 0)) is { } iconSourceHandle)
        {
            return iconSourceHandle;
        }

        return null;
    }

    private static IntPtr? ExtractDirectIcon(string path, int iconIndex)
    {
        try
        {
            var result = ShellInterop.SHDefExtractIconW(path, iconIndex, 0, out var handle, out _, ShellInterop.ShellIconSize);
            if (result != 0)
            {
                // S_FALSE＝请求源没有真图标（shell 默认兜底不是目标图标），照失败降级。
                if (handle != IntPtr.Zero)
                {
                    _ = ShellInterop.DestroyIcon(handle);
                }

                return null;
            }

            return handle != IntPtr.Zero ? handle : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>SHGetFileInfoW(SHGFI_ICON) 直取关联 HICON（32px 档；.lnk 自动解析目标；
    /// 低分辨率成功仍算成功——「有小不用大」与 BIGGERSIZEOK 同语义取向）。</summary>
    private static IntPtr? TryGetAssociatedIcon(LauncherItem item)
    {
        try
        {
            var info = new ShellInterop.ShFileInfo();
            var result = ShellInterop.SHGetFileInfoW(
                item.TargetPath,
                0,
                ref info,
                Marshal.SizeOf<ShellInterop.ShFileInfo>(),
                ShellInterop.ShgfiIcon | ShellInterop.ShgfiLargeIcon);
            return result == IntPtr.Zero || info.IconHandle == IntPtr.Zero ? null : info.IconHandle;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>回退链：SHGetFileInfo(SHGFI_SYSICONINDEX) → SHGetImageList(JUMBO→EXTRALARGE→LARGE)
    /// → IImageList.GetIcon（校验 index &lt; count；不用 SHGFI_USEFILEATTRIBUTES）。
    /// 系统位置（ShellLocation）以此为大图标主路径（对 CLSID 解析名同样有效）。</summary>
    private static IntPtr ExtractViaSystemImageList(LauncherItem item)
    {
        if (item.Kind is not (LauncherKind.Application or LauncherKind.ShellLocation))
        {
            return IntPtr.Zero;
        }

        var info = new ShellInterop.ShFileInfo();
        var result = ShellInterop.SHGetFileInfoW(
            item.TargetPath,
            0,
            ref info,
            Marshal.SizeOf<ShellInterop.ShFileInfo>(),
            ShellInterop.ShgfiSysiconIndex | ShellInterop.ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.IconIndex < 0)
        {
            return IntPtr.Zero;
        }

        var imageListId = typeof(ShellInterop.IImageList).GUID;
        foreach (var listSize in new[]
                 {
                     ShellInterop.ShilJumbo, ShellInterop.ShilExtraLarge, ShellInterop.ShilLarge
                 })
        {
            if (ShellInterop.SHGetImageList(listSize, imageListId, out var imageList) != 0)
            {
                continue;
            }

            try
            {
                if (imageList.GetImageCount(out var count) != 0 || info.IconIndex >= count)
                {
                    continue;
                }

                if (imageList.GetIcon(info.IconIndex, ShellInterop.IldTransparent, out var iconHandle) == 0 &&
                    iconHandle != IntPtr.Zero)
                {
                    return iconHandle;
                }
            }
            catch (InvalidComObjectException)
            {
                // 试下一档尺寸。
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>AppsFolder 枚举按 AUMID 匹配取条目（shell:AppsFolder\{AUMID} 解析失败的
    /// 回退；与已安装应用选择器共用经典 IShellFolder 枚举链——BindToHandler(BHID_
    /// EnumItems)/属性系统字符串读取在实际环境不可依赖，探针实证 2026-10-14）。</summary>
    private static object? TryGetAppsFolderItemByAumid(string appUserModelId) =>
        InstalledAppsEnumerator.TryGetItemByAumid(appUserModelId);

    /// <summary>
    /// 图标戳（#30）：Application=shortHash(lnk.mtime|size ‖ 解析目标.mtime|size ‖
    /// GetIconLocation 解析出的图标源文件.mtime|size)；PackagedApp 恒 null（不自动失效；
    /// 刷新触发=激活失败重枚举或重新添加覆盖）；Url=常量 "ok"（null=未取到＝启动重试
    /// 轮判定面；"ok"=已缓存；编辑目标置空由 AdoptEditedTarget 承载）；ShellLocation=
    /// 静态描述符 shortHash（无文件源目标，缓存跨会话稳定——回收站空/满态取提取时
    /// 当前态，不随切换失效）。
    /// </summary>
    internal static string? ComputeStamp(LauncherItem item)
    {
        if (item.Kind == LauncherKind.Url)
        {
            return UrlIconStampOk;
        }

        if (item.Kind == LauncherKind.ShellLocation)
        {
            var descriptor = $"shell-location:{item.TargetPath}";
            var stamp = SHA256.HashData(Encoding.Unicode.GetBytes(descriptor));
            return Convert.ToHexStringLower(stamp)[..12];
        }

        if (item.Kind != LauncherKind.Application)
        {
            return null;
        }

        var fingerprint = new StringBuilder();
        AppendFileFingerprint(fingerprint, item.TargetPath);
        if (item.TargetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
            ShellLinkHelper.TryRead(item.TargetPath) is { } link)
        {
            if (link.ResolvedTargetPath is { Length: > 0 } resolved)
            {
                AppendFileFingerprint(fingerprint, resolved);
            }

            if (link.IconLocation is { Length: > 0 } iconSource &&
                File.Exists(iconSource))
            {
                AppendFileFingerprint(fingerprint, iconSource);
            }
        }

        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(fingerprint.ToString()));
        return Convert.ToHexStringLower(hash)[..12];

        static void AppendFileFingerprint(StringBuilder builder, string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    builder.Append(path).Append("|missing|0\u0001");
                    return;
                }

                var info = new FileInfo(path);
                builder.Append(path)
                    .Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks)
                    .Append('|')
                    .Append(info.Length)
                    .Append('\u0001');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                builder.Append(path).Append("|unavailable|0\u0001");
            }
        }
    }

    private static void LogFailure(string iconKey, Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "fts-launcher-icon-failure.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] key={iconKey[..Math.Min(12, iconKey.Length)]}… {exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}\n---\n");
        }
        catch (Exception writerException) when (writerException is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static BitmapSource? DecodePng(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _iconDispatcher.InvokeShutdown();
        _ = _iconThread.Join(TimeSpan.FromSeconds(2));
        _faviconScheduler?.Dispose();
        _faviconClient?.Dispose();
    }
}
