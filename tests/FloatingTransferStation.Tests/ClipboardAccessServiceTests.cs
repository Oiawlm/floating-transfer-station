using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingTransferStation.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 剪贴板访问单入口服务的组件级锁定（2026-11-16 根治重构，R7）：挂死注入→硬超时
/// 生效＋线程弃置后新线程接管；物化编解码全格式往返；RAII 写时序（成功/失败路径
/// 必关、失败句柄必释放、成功句柄移交系统）；Acquire 有界重试；占用者遥测落
/// clipboard.log。全部经假 Win32 面与假数据源——harness 严禁真实剪贴板（契约 #53）。
/// </summary>
[TestClass]
public sealed class ClipboardAccessServiceTests
{
    private static readonly nint ClipboardOwnerHandle = 0x602A4;

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ClipboardAccess_HangingOperationTimesOutAndWorkerRecovers()
    {
        using var worker = new ClipboardStaWorker();
        var exception = await Assert.ThrowsExactlyAsync<ClipboardTimeoutException>(() =>
            worker.RunAsync<object?>(
                () =>
                {
                    Thread.Sleep(8_000);
                    return null;
                },
                TimeSpan.FromMilliseconds(150)));
        // 超时异常是 ExternalException 子类＝既有失败路径（采集有界重试/状态条）原样承接。
        Assert.IsInstanceOfType(exception, typeof(ExternalException));

        // 弃置的线程被换新：后续操作立即在新线程成功（不因挂死线程失去服务能力）。
        Assert.AreEqual(42, await worker.RunAsync(() => 42, TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ClipboardAccess_TimeoutFailureMapsOntoTheLegacyRetryPath()
    {
        var native = new RecordingClipboardNative { Sequence = 7 };
        using var service = new ClipboardAccessService(
            null,
            null,
            native,
            dataObjectSource: HangForever);
        var exception = await Assert.ThrowsExactlyAsync<ExternalException>(() =>
            service.ReadAsync(CancellationToken.None));
        Assert.IsInstanceOfType(exception, typeof(ClipboardTimeoutException));
    }

    [TestMethod]
    public async Task ClipboardAccess_UnstableSequenceFailsReadThroughLegacyException()
    {
        var native = new RecordingClipboardNative { Sequence = 7 };
        using var service = new ClipboardAccessService(
            null,
            null,
            native,
            dataObjectSource: () => new DataObject());
        // 读期间序号变化（ReadStable 稳定性守卫）→ExternalException＝既有采集重试路径。
        native.SequenceAdvancesDuringRead = true;
        await Assert.ThrowsExactlyAsync<ExternalException>(() => service.ReadAsync(CancellationToken.None));
    }

    [TestMethod]
    public void ClipboardAccess_SequenceNumberMapsZeroToNull()
    {
        var native = new RecordingClipboardNative { Sequence = 0 };
        using var service = new ClipboardAccessService(null, null, native, dataObjectSource: () => null);
        Assert.IsNull(service.GetSequenceNumber());
        native.Sequence = 245;
        Assert.AreEqual(245u, service.GetSequenceNumber());
    }

    [TestMethod]
    public async Task ClipboardAccess_BackupReadMaterializesTheThreeLegacyFormats()
    {
        var source = new DataObject();
        source.SetText("备份文本");
        source.SetFileDropList(new System.Collections.Specialized.StringCollection { @"C:\a b\图.png" });
        var native = new RecordingClipboardNative { Sequence = 7 };
        using var service = new ClipboardAccessService(null, null, native, dataObjectSource: () => source);

        var backup = await Task.Run(service.TryReadBackup);

        Assert.IsNotNull(backup);
        Assert.AreEqual("备份文本", backup.GetData(DataFormats.UnicodeText));
        CollectionAssert.AreEqual(
            new[] { @"C:\a b\图.png" },
            backup.GetFileDropList().Cast<string>().ToArray());
        Assert.IsFalse(backup.GetDataPresent(DataFormats.Bitmap));
    }

    [TestMethod]
    public async Task ClipboardAccess_EmptyClipboardBackupReturnsNull()
    {
        var native = new RecordingClipboardNative { Sequence = 7 };
        using var service = new ClipboardAccessService(null, null, native, dataObjectSource: () => new DataObject());
        Assert.IsNull(await Task.Run(service.TryReadBackup));
    }

    [TestMethod]
    public void ClipboardAccess_WriteFollowsRaiiOrderAndTransfersOwnership()
    {
        var native = new RecordingClipboardNative();
        using var service = new ClipboardAccessService(null, null, native, dataObjectSource: () => null);
        var payload = new DataObject();
        payload.SetData(DragPayloadService.InternalItemIdFormat, Guid.Empty.ToString("D"));
        payload.SetText("落板文本");

        Assert.IsTrue(service.TryWrite(payload));

        CollectionAssert.AreEqual(
            new[]
            {
                "open", "empty",
                "alloc", "lock", "unlock", "set:13",
                "alloc", "lock", "unlock", "set:4901",
                "close"
            },
            native.Calls.ToArray(),
            "写入必须按 Open→Empty→逐格式(Alloc→Lock→Unlock→Set)→Close 的 RAII 时序执行。");
        Assert.AreEqual(0, native.FreedHandles.Count, "成功句柄所有权移交系统，不得释放。");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ClipboardWriteFailure_AlwaysClosesTheClipboardAndFreesFailedHandles()
    {
        var native = new RecordingClipboardNative();
        using var service = new ClipboardAccessService(null, null, native, dataObjectSource: () => null);
        native.SetClipboardDataFails = true;
        var payload = new DataObject();
        payload.SetText("会失败");

        Assert.IsFalse(await TryWriteOnStaAsync(service, payload));

        Assert.AreEqual(1, native.Calls.Count(call => call == "close"), "失败路径必须 CloseClipboard（RAII 异常路径必关）。");
        Assert.AreEqual(1, native.FreedHandles.Count, "SetClipboardData 失败的句柄仍归本进程，必须释放。");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public async Task ClipboardWriteRetry_IsBoundedWhenTheClipboardStaysBusy()
    {
        var native = new RecordingClipboardNative { OpenResult = () => false };
        using var service = new ClipboardAccessService(null, null, native, dataObjectSource: () => null);
        var payload = new DataObject();
        payload.SetText("占用");

        Assert.IsFalse(await TryWriteOnStaAsync(service, payload));

        Assert.AreEqual(
            6,
            native.Calls.Count(call => call == "open"),
            "Acquire 重试必须有界：初次＋5 次重试后放弃，不得无限重试。");
        Assert.AreEqual(0, native.Calls.Count(call => call == "empty" || call.StartsWith("set:", StringComparison.Ordinal)),
            "未取得锁就不得触碰剪贴板内容。");
        Assert.AreEqual(0, native.Calls.Count(call => call == "close"), "从未 Open 成功就不该 Close。");
    }

    [TestMethod]
    public async Task ClipboardWriteFailure_RecordsOccupantTelemetry()
    {
        var logPath = Path.Combine(TestTempRoot.Root, $"clipboard-{Guid.NewGuid():N}.log");
        var native = new RecordingClipboardNative { OpenResult = () => false };
        native.DescribeResult = new ClipboardWindowSnapshot(
            (uint)Environment.ProcessId,
            "UURemoteClipboard",
            "clipboard");
        using (var service = new ClipboardAccessService(
            null,
            new DiagnosticsLog(logPath),
            native,
            dataObjectSource: () => null))
        {
            var payload = new DataObject();
            payload.SetText("遥测");
            Assert.IsFalse(await TryWriteOnStaAsync(service, payload));
        }

        var log = File.ReadAllText(logPath);
        Assert.IsTrue(log.Contains("OpenClipboard 重试耗尽", StringComparison.Ordinal), log);
        Assert.IsTrue(log.Contains("持锁=无", StringComparison.Ordinal), log);
        Assert.IsTrue(log.Contains($"类='UURemoteClipboard'", StringComparison.Ordinal), log);
        Assert.IsTrue(log.Contains("属主=PID=", StringComparison.Ordinal), log);
        Assert.IsTrue(log.Contains("序号=", StringComparison.Ordinal), log);
        Assert.IsTrue(log.Contains("遥测", StringComparison.Ordinal), log);
    }

    [TestMethod]
    public void ClipboardAccess_DrainTimeoutLogGoesThroughTheDiagnosticsComponent()
    {
        var logPath = Path.Combine(TestTempRoot.Root, $"clipboard-{Guid.NewGuid():N}.log");
        var native = new RecordingClipboardNative();
        using var service = new ClipboardAccessService(
            null,
            new DiagnosticsLog(logPath),
            native,
            dataObjectSource: () => null);

        service.LogDrainTimeout("关闭排空等待 10s 超时（2 项在途未收尾），继续关闭。");

        var log = File.ReadAllText(logPath);
        Assert.IsTrue(log.Contains("|排空|失败|", StringComparison.Ordinal), log);
        Assert.IsTrue(log.Contains("继续关闭", StringComparison.Ordinal), log);
    }

    // ---- 物化编解码全格式往返（DataObject 形状→字节形状→解码）----

    [TestMethod]
    public void ClipboardCodecs_UnicodeTextRoundTripsWithDoubleNullTerminator()
    {
        const string text = "中英 mixed\n第二行";
        var bytes = ClipboardFormatCodecs.EncodeUnicodeText(text);
        Assert.AreEqual((text.Length + 2) * 2, bytes.Length, "CF_UNICODETEXT＝UTF-16＋双 NUL 终止。");
        Assert.AreEqual(0, bytes[^1]);
        Assert.AreEqual(0, bytes[^2]);
        Assert.AreEqual(text, ClipboardFormatCodecs.DecodeUnicodeText(bytes));
    }

    [TestMethod]
    public void ClipboardCodecs_FileDropRoundTripsPreservingOrder()
    {
        var paths = new[]
        {
            @"C:\图片 副本\图片-2.png",
            @"D:\Download\App Data\悬浮中转站\images\a b.png",
            @"\\server\share\远程.png"
        };
        CollectionAssert.AreEqual(paths, ClipboardFormatCodecs.DecodeFileDrop(ClipboardFormatCodecs.EncodeFileDrop(paths)));
    }

    [TestMethod]
    public void ClipboardCodecs_StringMarkersRoundTrip()
    {
        var id = Guid.NewGuid().ToString("D");
        Assert.AreEqual(id, ClipboardFormatCodecs.DecodeStringMarker(ClipboardFormatCodecs.EncodeStringMarker(id)));

        var ids = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
        CollectionAssert.AreEqual(
            ids,
            ClipboardFormatCodecs.DecodeStringListMarker(ClipboardFormatCodecs.EncodeStringListMarker(ids)));
    }

    [TestMethod]
    public void ClipboardCodecs_DropEffectIsAFourByteDword()
    {
        var bytes = ClipboardFormatCodecs.EncodeDropEffect((int)System.Windows.DragDropEffects.Copy);
        Assert.AreEqual(4, bytes.Length);
        Assert.AreEqual((int)System.Windows.DragDropEffects.Copy, ClipboardFormatCodecs.DecodeDropEffect(bytes));
    }

    [STATestMethod]
    public void ClipboardCodecs_DibEncodesBottomUpBgraWithInfoHeader()
    {
        var source = CreateTwoByTwoBitmap();

        var dib = ClipboardFormatCodecs.EncodeDib(source);

        Assert.AreEqual(40, BitConverter.ToInt32(dib, 0), "CF_DIB 头＝BITMAPINFOHEADER(40)。");
        Assert.AreEqual(2, BitConverter.ToInt32(dib, 4));
        Assert.AreEqual(2, BitConverter.ToInt32(dib, 8), "正高度＝bottom-up。");
        Assert.AreEqual((short)1, BitConverter.ToInt16(dib, 12));
        Assert.AreEqual((short)32, BitConverter.ToInt16(dib, 14));
        Assert.AreEqual(0, BitConverter.ToInt32(dib, 16), "BI_RGB。");
        Assert.AreEqual(40 + 16, dib.Length, "2×2×4bpp 字节。");
        // bottom-up：底行（蓝）紧随头部，顶行（红）落在尾部。
        Assert.AreEqual(255, dib[40], "底行蓝：B=255。");
        Assert.AreEqual(0, dib[42], "底行蓝：R=0。");
        Assert.AreEqual(0, dib[48], "顶行红：B=0。");
        Assert.AreEqual(255, dib[50], "顶行红：R=255。");
    }

    [STATestMethod]
    public void ClipboardCodecs_DibV5EncodesExplicitBgraMasks()
    {
        var source = CreateTwoByTwoBitmap();

        var dib = ClipboardFormatCodecs.EncodeDibV5(source);

        Assert.AreEqual(124, BitConverter.ToInt32(dib, 0), "CF_DIBV5 头＝BITMAPV5HEADER(124)。");
        Assert.AreEqual(3, BitConverter.ToInt32(dib, 16), "BI_BITFIELDS。");
        Assert.AreEqual(0x00FF0000u, BitConverter.ToUInt32(dib, 40));
        Assert.AreEqual(0x0000FF00u, BitConverter.ToUInt32(dib, 44));
        Assert.AreEqual(0x000000FFu, BitConverter.ToUInt32(dib, 48));
        Assert.AreEqual(0xFF000000u, BitConverter.ToUInt32(dib, 52));
        Assert.AreEqual(2, ClipboardFormatCodecs.DecodeDibDimensions(dib).Height);
        Assert.AreEqual(255, dib[124], "bottom-up：底行蓝在前。");
        Assert.AreEqual(0, dib[124 + 8], "顶行红在后：B=0。");
        Assert.AreEqual(255, dib[124 + 10], "顶行红在后：R=255。");
    }

    [STATestMethod]
    public void ClipboardAccess_MaterializeCoversEveryProductionPayloadShape()
    {
        var native = new RecordingClipboardNative();
        using var service = new ClipboardAccessService(null, null, native, dataObjectSource: () => null);

        var textPayload = new DataObject();
        textPayload.SetData(DragPayloadService.InternalItemIdFormat, "00000000-0000-0000-0000-000000000201");
        textPayload.SetData(DataFormats.UnicodeText, "文字", autoConvert: true);
        textPayload.SetData(DataFormats.Text, "文字", autoConvert: true);
        var textFormats = service.Materialize(textPayload).ToList();
        Assert.AreEqual(2, textFormats.Count, "文本卡＝CF_UNICODETEXT＋内部标记（CF_TEXT 由系统自 Unicode 合成）。");
        Assert.AreEqual(13u, textFormats[0].Format);
        Assert.AreEqual(
            "00000000-0000-0000-0000-000000000201",
            ClipboardFormatCodecs.DecodeStringMarker(textFormats[^1].Bytes));

        var imagePayload = new DataObject();
        imagePayload.SetData(DragPayloadService.InternalItemIdFormat, "00000000-0000-0000-0000-000000000202");
        imagePayload.SetFileDropList(new System.Collections.Specialized.StringCollection { @"C:\m\图.png" });
        imagePayload.SetImage(CreateTwoByTwoBitmap());
        imagePayload.SetData(
            DragPayloadService.PreferredDropEffectFormat,
            BitConverter.GetBytes((int)System.Windows.DragDropEffects.Copy),
            autoConvert: false);
        var imageFormats = service.Materialize(imagePayload).Select(entry => entry.Format).ToArray();
        CollectionAssert.AreEqual(
            new uint[] { 15, 8, 17, 4903, 4901 },
            imageFormats,
            "图片卡＝CF_HDROP＋CF_DIB＋CF_DIBV5＋Preferred DropEffect＋内部标记。");

        var batchPayload = new DataObject();
        batchPayload.SetData(
            DragPayloadService.InternalItemIdsFormat,
            new[]
            {
                "00000000-0000-0000-0000-000000000203",
                "00000000-0000-0000-0000-000000000204"
            });
        batchPayload.SetFileDropList(new System.Collections.Specialized.StringCollection { @"C:\m\1.png", @"C:\m\2.png" });
        var batchFormats = service.Materialize(batchPayload).ToList();
        Assert.AreEqual(2, batchFormats.Count, "纯图片批＝CF_HDROP＋内部批量标记。");
        Assert.AreEqual(15u, batchFormats[0].Format);
        Assert.AreEqual(4902u, batchFormats[1].Format);
        CollectionAssert.AreEqual(
            new[]
            {
                "00000000-0000-0000-0000-000000000203",
                "00000000-0000-0000-0000-000000000204"
            },
            ClipboardFormatCodecs.DecodeStringListMarker(batchFormats[1].Bytes));
    }

    private static async Task<bool> TryWriteOnStaAsync(ClipboardAccessService service, DataObject payload)
    {
        // 写入在服务的 STA 线程上执行（与生产同路径）；这里仅把阻塞等待放到线程池。
        return await Task.Run(() => service.TryWrite(payload));
    }

    private static IDataObject? HangForever()
    {
        Thread.Sleep(8_000);
        return null;
    }

    private static BitmapSource CreateTwoByTwoBitmap()
    {
        var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        // 顶行红、底行蓝（BGRA）。
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, 2, 1), new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }, 8, 0);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 1, 2, 1), new byte[] { 255, 0, 0, 255, 255, 0, 0, 255 }, 8, 0);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>假 Win32 面：记录调用序、注入失败与占用者快照，绝不触碰真实剪贴板。</summary>
    private sealed class RecordingClipboardNative : IClipboardNative
    {
        private readonly List<string> _calls = [];
        private nint _nextHandle = 0x1000;
        private uint _nextRegisteredFormat = 4901;

        public List<string> Calls => _calls;
        public List<nint> FreedHandles { get; } = [];
        public Func<bool> OpenResult { get; set; } = () => true;
        public bool EmptyResult { get; set; } = true;
        public bool SetClipboardDataFails { get; set; }
        public bool SequenceAdvancesDuringRead { get; set; }
        public ClipboardWindowSnapshot? DescribeResult { get; set; }
        public uint Sequence { get; set; }

        public bool OpenClipboard(nint hwndNewOwner)
        {
            _calls.Add("open");
            return OpenResult();
        }

        public bool CloseClipboard()
        {
            _calls.Add("close");
            return true;
        }

        public bool EmptyClipboard()
        {
            _calls.Add("empty");
            return EmptyResult;
        }

        public nint SetClipboardData(uint format, nint hMem)
        {
            _calls.Add($"set:{format}");
            return SetClipboardDataFails ? 0 : hMem;
        }

        public uint RegisterClipboardFormat(string name) => name switch
        {
            DragPayloadService.InternalItemIdFormat => 4901,
            DragPayloadService.InternalItemIdsFormat => 4902,
            DragPayloadService.PreferredDropEffectFormat => 4903,
            _ => _nextRegisteredFormat++
        };

        public nint GlobalAlloc(uint flags, nuint bytes)
        {
            _calls.Add("alloc");
            return _nextHandle++;
        }

        public nint GlobalLock(nint hMem)
        {
            _calls.Add("lock");
            return hMem;
        }

        public bool GlobalUnlock(nint hMem)
        {
            _calls.Add("unlock");
            return true;
        }

        public nint GlobalFree(nint hMem)
        {
            FreedHandles.Add(hMem);
            return 0;
        }

        public uint GetClipboardSequenceNumber() => SequenceAdvancesDuringRead ? ++Sequence : Sequence;

        public nint GetOpenClipboardWindow() => 0;

        public nint GetClipboardOwner() => ClipboardOwnerHandle;

        public ClipboardWindowSnapshot? DescribeWindow(nint hwnd) =>
            hwnd == ClipboardOwnerHandle ? DescribeResult : null;
    }
}
