using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>剪贴板操作硬超时（STA 工作线程已弃置）——<see cref="ExternalException"/>
/// 子类，使既有失败路径（采集有界重试、送达写失败分支、状态条提示）原样承接。</summary>
internal sealed class ClipboardTimeoutException(string message) : ExternalException(message);

/// <summary>占用者遥测的窗口快照（PID/类名/标题；进程名由服务解析）。</summary>
internal sealed record ClipboardWindowSnapshot(uint ProcessId, string ClassName, string WindowTitle);

/// <summary>
/// Win32 剪贴板面接缝（生产＝<see cref="Win32ClipboardNative"/>，测试＝假件注入断言
/// RAII 时序与重试边界；harness 严禁真实剪贴板——契约 #53 密封惯例）。
/// </summary>
internal interface IClipboardNative
{
    bool OpenClipboard(nint hwndNewOwner);

    bool CloseClipboard();

    bool EmptyClipboard();

    /// <summary>成功后 HGLOBAL 所有权移交系统（不得再释放）；失败时调用方自行释放。</summary>
    nint SetClipboardData(uint format, nint hMem);

    uint RegisterClipboardFormat(string name);

    nint GlobalAlloc(uint flags, nuint bytes);

    nint GlobalLock(nint hMem);

    bool GlobalUnlock(nint hMem);

    nint GlobalFree(nint hMem);

    uint GetClipboardSequenceNumber();

    /// <summary>当前持锁窗口（与数据属主通常不是同一窗口——诊断看错对象即误诊）。</summary>
    nint GetOpenClipboardWindow();

    /// <summary>数据属主窗口（延迟渲染义务方；全物化写路径下本进程短暂持有）。</summary>
    nint GetClipboardOwner();

    /// <summary>窗口归属快照（null＝窗口已消亡的竞态，遥测容忍）。</summary>
    ClipboardWindowSnapshot? DescribeWindow(nint hwnd);
}

internal sealed class Win32ClipboardNative : IClipboardNative
{
    public bool OpenClipboard(nint hwndNewOwner) => NativeMethods.OpenClipboard(hwndNewOwner);

    public bool CloseClipboard() => NativeMethods.CloseClipboard();

    public bool EmptyClipboard() => NativeMethods.EmptyClipboard();

    public nint SetClipboardData(uint format, nint hMem) => NativeMethods.SetClipboardData(format, hMem);

    public uint RegisterClipboardFormat(string name) => NativeMethods.RegisterClipboardFormat(name);

    public nint GlobalAlloc(uint flags, nuint bytes) => NativeMethods.GlobalAlloc(flags, bytes);

    public nint GlobalLock(nint hMem) => NativeMethods.GlobalLock(hMem);

    public bool GlobalUnlock(nint hMem) => NativeMethods.GlobalUnlock(hMem);

    public nint GlobalFree(nint hMem) => NativeMethods.GlobalFree(hMem);

    public uint GetClipboardSequenceNumber() => NativeMethods.GetClipboardSequenceNumber();

    public nint GetOpenClipboardWindow() => NativeMethods.GetOpenClipboardWindow();

    public nint GetClipboardOwner() => NativeMethods.GetClipboardOwner();

    public ClipboardWindowSnapshot? DescribeWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        var processId = NativeMethods.GetWindowProcessId(hwnd);
        if (processId == 0)
        {
            return null;
        }

        var className = new StringBuilder(256);
        var title = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, className, className.Capacity);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        return new ClipboardWindowSnapshot(processId, className.ToString(), title.ToString());
    }
}

/// <summary>
/// 系统剪贴板访问单入口（2026-11-16 锁死事故根治，R1–R6）：fts 对系统剪贴板的一切
/// 读/写/备份读全部经本服务——生产代码其余位置禁止出现 <c>System.Windows.Clipboard</c>
/// 直接调用（grep 可验收）。读：专用 STA 后台线程＋每操作硬超时（超时＝线程弃置＋
/// 新线程接管＋既有失败路径承接；UI 线程永不因邻居属主挂死而冻结）。写：Win32 RAII
///（Open→Empty→SetClipboardData×N→Close，Acquire 有界重试、异常路径必关）＋全格式
/// HGLOBAL 物化——写完成即零渲染义务（无 OleFlushClipboard 路径、无 Stream 支撑
/// 格式；进程被杀不悬任何人，内容天然存续＝copy:true 等价语义）。失败/超时记录占用
/// 者遥测（GetOpenClipboardWindow/GetClipboardOwner→PID/进程/窗口类＋序号）到
/// <c>&lt;数据根&gt;\logs\clipboard.log</c>（经泛化 <see cref="DiagnosticsLog"/>，无 PII）。
/// 实现既有读接口 <see cref="IClipboardReader"/>（接替 WpfClipboardReader，假件族零改动）。
/// </summary>
public sealed class ClipboardAccessService : IClipboardReader, IDisposable
{
    public const string ReadPhase = "读";
    public const string WritePhase = "写";
    public const string DrainPhase = "排空";
    public const string FailureKind = "失败";
    public const string MilestoneKind = "里程碑";
    public const string TelemetryKind = "遥测";

    // 超时常量表（每一项有界；数值依《底稿》§4 业界量级）：
    // 读 2s——覆盖邻居属主短暂不响应后的自愈，同时远小于系统渲染请求 30s 硬超时；
    // 写 0.5s——RAII 写本身是持锁内存拷贝（µs 级），预算只留给 OpenClipboard 重试
    //（25ms）与外部前任属主短暂挂死时 EmptyClipboard 的同步等待；
    // 读前一拍 30ms——避开外部写方 Flush 重试窗（dotnet/wpf#9106）；
    // Acquire 5×5ms——Chromium ScopedClipboard 量级。
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReadSettleDelay = TimeSpan.FromMilliseconds(30);
    private const int AcquireRetryCount = 5;
    private static readonly TimeSpan AcquireRetryDelay = TimeSpan.FromMilliseconds(5);

    private readonly ClipboardPayloadReader _payloadReader;
    private readonly DiagnosticsLog? _diagnostics;
    private readonly IClipboardNative _native;
    private readonly Func<IDataObject?> _dataObjectSource;
    private readonly ClipboardStaWorker _sta = new();

    public ClipboardAccessService(
        ClipboardPayloadReader? payloadReader = null,
        DiagnosticsLog? diagnostics = null)
        : this(payloadReader, diagnostics, null, null)
    {
    }

    /// <summary>测试注入接缝（假 Win32 面＋假数据对象源）；生产走双参构造。</summary>
    internal ClipboardAccessService(
        ClipboardPayloadReader? payloadReader,
        DiagnosticsLog? diagnostics,
        IClipboardNative? native,
        Func<IDataObject?>? dataObjectSource)
    {
        _payloadReader = payloadReader ?? new ClipboardPayloadReader();
        _diagnostics = diagnostics;
        _native = native ?? new Win32ClipboardNative();
        // 生产唯一的数据对象来源；测试注入假件（挂死注入等）——本赋值是全仓唯一的
        // Clipboard.GetDataObject 直呼点（R1 grep 验收锚）。
        _dataObjectSource = dataObjectSource ?? Clipboard.GetDataObject;
    }

    /// <summary>系统剪贴板序号（Win32 直查，无 OLE、无超时需求；0＝异常态记 null）。</summary>
    public uint? GetSequenceNumber()
    {
        var sequence = _native.GetClipboardSequenceNumber();
        return sequence == 0 ? null : sequence;
    }

    /// <summary>
    /// 采集读：延迟一拍（避外部写方 Flush 窗）→STA 线程执行
    /// <see cref="ClipboardPayloadReader.ReadStable"/>（读前后序号比对语义不变）→
    /// 2s 硬超时。超时/失败以 <see cref="ExternalException"/> 形态抛出＝既有采集
    /// 重试路径与状态条文案零变化；外部属主挂起时 UI 线程完全无感。
    /// </summary>
    public async Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(ReadSettleDelay, cancellationToken);
        try
        {
            return await _sta.RunAsync(
                () => _payloadReader.ReadStable(_dataObjectSource, _native.GetClipboardSequenceNumber),
                ReadTimeout,
                cancellationToken);
        }
        catch (ClipboardTimeoutException exception)
        {
            WriteLog(ReadPhase, FailureKind, $"读取硬超时，STA 线程已弃置并换新线程：{exception.Message}");
            LogOccupant(ReadPhase, "读超时");
            throw;
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            WriteLog(ReadPhase, FailureKind, $"读取失败：{exception.Message}");
            LogOccupant(ReadPhase, "读失败");
            throw;
        }
    }

    /// <summary>
    /// 写入剪贴板（RAII＋全物化，R3）：在 STA 线程上 OpenClipboard(属主窗口)
    /// →EmptyClipboard→逐格式 SetClipboardData(HGLOBAL)→CloseClipboard；Acquire 有界
    /// 重试（5×5ms）、异常路径必关、失败句柄必释放。可恢复失败返回 false；硬超时
    /// （外部前任属主挂死拖住 EmptyClipboard 等）抛 <see cref="ExternalException"/>——
    /// 调用方既有 catch 子句（COM/External/InvalidOperation）原样承接。调用线程短暂
    /// 阻塞（写预算 0.5s 上界，正常路径 µs–ms 级）。
    /// </summary>
    public bool TryWrite(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var owner = _sta.OwnerWindowHandle;
        if (owner == 0)
        {
            WriteLog(WritePhase, FailureKind, "剪贴板属主窗口不可用，本次写入未执行。");
            return false;
        }

        try
        {
            if (_sta.Run(() => WriteCore(data, owner), WriteTimeout))
            {
                return true;
            }
        }
        catch (ClipboardTimeoutException exception)
        {
            WriteLog(WritePhase, FailureKind, $"写入硬超时，STA 线程已弃置并换新线程：{exception.Message}");
            LogOccupant(WritePhase, "写超时");
            throw;
        }

        return false;
    }

    /// <summary>
    /// 送达备份读：原剪贴板物化为独立 <see cref="DataObject"/>（UnicodeText/FileDrop/
    /// Bitmap——系统剪贴板返回的延迟渲染视图不能整体回写，SetDataObject 回写实测得到
    /// 空剪贴板，2026-11-04 实测）；空剪贴板/失败/超时返回 null（送达语义：无备份＝
    /// 跳过恢复）。OLE 读与位图转换均在 STA 线程＋2s 硬超时内完成；调用方须在后台线程
    ///（送达侧经 Task.Run 包裹）。
    /// </summary>
    public DataObject? TryReadBackup()
    {
        try
        {
            return _sta.Run(MaterializeBackup, ReadTimeout);
        }
        catch (ClipboardTimeoutException exception)
        {
            WriteLog(ReadPhase, FailureKind, $"备份读硬超时，STA 线程已弃置并换新线程：{exception.Message}");
            LogOccupant(ReadPhase, "备份读超时");
            return null;
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>关闭排空超时等窗口侧事件的诊断落盘入口（阶段＝排空）。</summary>
    internal void LogDrainTimeout(string description) =>
        WriteLog(DrainPhase, FailureKind, description);

    /// <summary>DataObject→Win32 格式物化清单（落板顺序即枚举顺序）。全部格式即时
    /// 物化——无延迟渲染承诺、无 Stream 支撑格式（R3 根因消除）。</summary>
    internal IEnumerable<(uint Format, byte[] Bytes)> Materialize(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.UnicodeText) &&
            data.GetData(DataFormats.UnicodeText) is string { Length: > 0 } text)
        {
            yield return (NativeMethods.CfUnicodeText, ClipboardFormatCodecs.EncodeUnicodeText(text));
        }

        if (data.GetDataPresent(DataFormats.FileDrop) &&
            EnumerateFileDrop(data) is { Count: > 0 } files)
        {
            yield return (NativeMethods.CfHdrop, ClipboardFormatCodecs.EncodeFileDrop(files));
        }

        if (data.GetDataPresent(DataFormats.Bitmap) &&
            data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            yield return (NativeMethods.CfDib, ClipboardFormatCodecs.EncodeDib(bitmap));
            yield return (NativeMethods.CfDibV5, ClipboardFormatCodecs.EncodeDibV5(bitmap));
        }

        if (data.GetDataPresent(DragPayloadService.PreferredDropEffectFormat, autoConvert: false) &&
            ReadPreferredDropEffect(data) is { } dropEffect)
        {
            yield return (
                _native.RegisterClipboardFormat(DragPayloadService.PreferredDropEffectFormat),
                ClipboardFormatCodecs.EncodeDropEffect(dropEffect));
        }

        if (data.GetDataPresent(DragPayloadService.InternalItemIdFormat, autoConvert: false) &&
            data.GetData(DragPayloadService.InternalItemIdFormat) is string itemId)
        {
            yield return (
                _native.RegisterClipboardFormat(DragPayloadService.InternalItemIdFormat),
                ClipboardFormatCodecs.EncodeStringMarker(itemId));
        }

        if (data.GetDataPresent(DragPayloadService.InternalItemIdsFormat, autoConvert: false) &&
            data.GetData(DragPayloadService.InternalItemIdsFormat) is string[] { Length: > 0 } itemIds)
        {
            yield return (
                _native.RegisterClipboardFormat(DragPayloadService.InternalItemIdsFormat),
                ClipboardFormatCodecs.EncodeStringListMarker(itemIds));
        }
    }

    /// <summary>关闭排空诊断等通用写点（内部）。</summary>
    internal void WriteLog(string phase, string kind, string message) =>
        _diagnostics?.Write(phase, kind, message);

    /// <summary>占用者遥测：持锁窗口＋数据属主（二者通常不同）→PID/进程/类/标题＋
    /// 序号（竞态窗口消亡与查询失败如实标注，不编造）。</summary>
    internal void LogOccupant(string phase, string context)
    {
        try
        {
            var opener = DescribeWindow(_native.GetOpenClipboardWindow());
            var owner = DescribeWindow(_native.GetClipboardOwner());
            var sequence = _native.GetClipboardSequenceNumber();
            WriteLog(phase, TelemetryKind, $"{context}|持锁={opener}|属主={owner}|序号={sequence}");
        }
        catch (Exception exception)
        {
            WriteLog(phase, TelemetryKind, $"{context}|遥测查询失败：{exception.Message}");
        }
    }

    public void Dispose() => _sta.Dispose();

    private bool WriteCore(IDataObject data, nint ownerWindow)
    {
        if (!AcquireClipboard(ownerWindow))
        {
            WriteLog(WritePhase, FailureKind, "OpenClipboard 重试耗尽（剪贴板被占用）。");
            LogOccupant(WritePhase, "写占用");
            return false;
        }

        try
        {
            if (!_native.EmptyClipboard())
            {
                WriteLog(WritePhase, FailureKind, "EmptyClipboard 失败。");
                return false;
            }

            foreach (var (format, bytes) in Materialize(data))
            {
                if (!TryPlaceFormat(format, bytes))
                {
                    WriteLog(WritePhase, FailureKind, $"SetClipboardData 失败（格式 {format}，{bytes.Length} 字节）。");
                    return false;
                }
            }

            return true;
        }
        finally
        {
            if (!_native.CloseClipboard())
            {
                WriteLog(WritePhase, FailureKind, "CloseClipboard 失败（锁由系统兜底回收）。");
            }
        }
    }

    private bool AcquireClipboard(nint ownerWindow)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (_native.OpenClipboard(ownerWindow))
            {
                return true;
            }

            if (attempt >= AcquireRetryCount)
            {
                return false;
            }

            Thread.Sleep(AcquireRetryDelay);
        }
    }

    private bool TryPlaceFormat(uint format, byte[] bytes)
    {
        var handle = _native.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)bytes.LongLength);
        if (handle == 0)
        {
            return false;
        }

        var placed = false;
        try
        {
            var target = _native.GlobalLock(handle);
            if (target != 0)
            {
                try
                {
                    Marshal.Copy(bytes, 0, target, bytes.Length);
                }
                finally
                {
                    _native.GlobalUnlock(handle);
                }

                placed = _native.SetClipboardData(format, handle) != 0;
            }
        }
        finally
        {
            // 成功＝所有权移交系统（不得释放）；失败＝句柄仍归本进程，必须释放。
            if (!placed)
            {
                _native.GlobalFree(handle);
            }
        }

        return placed;
    }

    private DataObject? MaterializeBackup()
    {
        if (_dataObjectSource() is not { } current)
        {
            return null;
        }

        var snapshot = new DataObject();
        var hasAny = false;
        if (current.GetDataPresent(DataFormats.UnicodeText) &&
            current.GetData(DataFormats.UnicodeText) is string text)
        {
            snapshot.SetText(text);
            hasAny = true;
        }

        if (current.GetDataPresent(DataFormats.FileDrop) &&
            EnumerateFileDrop(current) is { Count: > 0 } files)
        {
            snapshot.SetFileDropList([.. files]);
            hasAny = true;
        }

        if (current.GetDataPresent(DataFormats.Bitmap) &&
            current.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            snapshot.SetImage(bitmap);
            hasAny = true;
        }

        return hasAny ? snapshot : null;
    }

    private static Collection<string>? EnumerateFileDrop(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) switch
        {
            string[] paths => [.. paths.Where(path => !string.IsNullOrWhiteSpace(path))],
            StringCollection collection when collection.Count > 0 =>
                [.. collection.Cast<string>().Where(path => !string.IsNullOrWhiteSpace(path))],
            _ => null
        };

    private static int? ReadPreferredDropEffect(IDataObject data) =>
        data.GetData(DragPayloadService.PreferredDropEffectFormat, autoConvert: false) switch
        {
            byte[] bytes => ClipboardFormatCodecs.DecodeDropEffect(bytes),
            MemoryStream stream when stream.Length >= sizeof(int) =>
                ClipboardFormatCodecs.DecodeDropEffect(stream.ToArray()),
            _ => null
        };

    private string DescribeWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "无";
        }

        if (_native.DescribeWindow(hwnd) is not { } info)
        {
            return "未知（窗口已消亡）";
        }

        var processName = "未知";
        try
        {
            if (info.ProcessId != 0)
            {
                processName = Process.GetProcessById((int)info.ProcessId).ProcessName;
            }
        }
        catch (Exception)
        {
            // 进程已退出的竞态：如实标注未知。
        }

        return $"PID={info.ProcessId} 进程={processName} 类='{info.ClassName}' 标题='{info.WindowTitle}'";
    }
}
