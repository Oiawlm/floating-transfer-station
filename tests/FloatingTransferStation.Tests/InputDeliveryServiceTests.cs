using System.Windows;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 输入送达子系统（2026-11-03 需求 6，契约 #36 第三递送通道）的管线单元
/// 测试：Win32 接缝注入假件，断言「备份剪贴板→写负载→目标前台化→延时→
/// Ctrl+V→延时→恢复剪贴板」的序列与各失败分支的归因收尾；不触碰真实
/// 剪贴板/前台/输入注入。
/// </summary>
[TestClass]
public sealed class InputDeliveryServiceTests
{
    [TestMethod]
    public void CaptureForegroundTarget_IgnoresOwnProcessWindow()
    {
        var native = new ScriptedNative { Foreground = 0x99 };
        native.OwnProcessWindows.Add(0x99);
        var service = new InputDeliveryService(native);

        service.CaptureForegroundTarget();

        Assert.IsFalse(service.HasTarget, "前台是本进程窗口时不得捕获为送达目标。");
    }

    [TestMethod]
    public void CaptureForegroundTarget_StoresForeignForegroundWindow()
    {
        var native = new ScriptedNative { Foreground = 0x77 };
        var service = new InputDeliveryService(native);

        service.CaptureForegroundTarget();

        Assert.IsTrue(service.HasTarget, "外部前台窗口必须可捕获为送达目标。");

        // 前台变为本进程后：保留既有快照（用户刚才打字的窗口）。
        native.Foreground = 0x99;
        native.OwnProcessWindows.Add(0x99);
        service.CaptureForegroundTarget();
        Assert.IsTrue(service.HasTarget, "面板激活后快照必须保留。");
    }

    [TestMethod]
    public async Task DeliverAsync_WithoutTargetReturnsNoTargetWithoutTouchingClipboard()
    {
        var native = new ScriptedNative();
        var service = new InputDeliveryService(native);

        var outcome = await service.DeliverAsync(new DataObject("负载"));

        Assert.AreEqual(InputDeliveryOutcome.NoTarget, outcome);
        CollectionAssert.DoesNotContain(native.Calls, "get-clipboard");
        CollectionAssert.DoesNotContain(native.Calls, "write-clipboard");
    }

    [TestMethod]
    public async Task DeliverAsync_DeadTargetWindowReturnsNoTarget()
    {
        var native = new ScriptedNative { Foreground = 0x77 };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        native.Foreground = 0; // 快照目标仍存在（IsWindow 恒真于已捕获句柄）
        var outcome = await service.DeliverAsync(new DataObject("负载"));
        Assert.AreEqual(InputDeliveryOutcome.Delivered, outcome);

        native.KnownWindows.Remove(0x77);
        var second = await service.DeliverAsync(new DataObject("负载"));
        Assert.AreEqual(InputDeliveryOutcome.NoTarget, second, "目标窗口已关闭必须归因为无目标。");
    }

    [TestMethod]
    public async Task DeliverAsync_FullSequenceDeliversAndRestoresBackup()
    {
        var native = new ScriptedNative { Foreground = 0x77 };
        var backup = new DataObject("原剪贴板内容");
        native.Backup = backup;
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();
        var payload = new DataObject("送达正文");
        native.Calls.Clear();

        var outcome = await service.DeliverAsync(payload);

        Assert.AreEqual(InputDeliveryOutcome.Delivered, outcome);
        CollectionAssert.AreEqual(
            new[]
            {
                "get-clipboard",
                "write-clipboard",
                "set-foreground:119",
                "delay",
                "send-ctrl-v",
                "delay",
                "set-clipboard"
            },
            native.Calls,
            "送达序列必须是：备份→写负载→前台化→延时→Ctrl+V→延时→恢复。");
        Assert.AreSame(payload, native.Written, "写入剪贴板的必须是送达负载本体。");
        Assert.AreSame(backup, native.Restored, "恢复的必须是捕获的原剪贴板备份。");
    }

    [TestMethod]
    public async Task DeliverAsync_EmptyClipboardBackupSkipsRestore()
    {
        var native = new ScriptedNative { Foreground = 0x77, Backup = null };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.Delivered, outcome);
        CollectionAssert.DoesNotContain(native.Calls, "set-clipboard", "空剪贴板无需恢复。");
    }

    [TestMethod]
    public async Task DeliverAsync_ClipboardWriteFailureAbortsBeforeForeground()
    {
        var native = new ScriptedNative { Foreground = 0x77, WriteResult = false };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.ClipboardWriteFailed, outcome);
        CollectionAssert.DoesNotContain(native.Calls, "set-foreground:119");
        CollectionAssert.DoesNotContain(native.Calls, "send-ctrl-v");
    }

    [TestMethod]
    public async Task DeliverAsync_ForegroundFailureRestoresClipboardAndSkipsPaste()
    {
        var native = new ScriptedNative
        {
            Foreground = 0x77,
            SetForegroundResult = false,
            Backup = new DataObject("原内容")
        };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.FocusFailed, outcome);
        CollectionAssert.DoesNotContain(native.Calls, "send-ctrl-v");
        Assert.IsTrue(native.Calls.IndexOf("write-clipboard") < native.Calls.IndexOf("set-clipboard"),
            "已写负载的失败路径必须先恢复备份再返回。");
    }

    [TestMethod]
    public async Task DeliverAsync_SendFailureRestoresClipboard()
    {
        var native = new ScriptedNative
        {
            Foreground = 0x77,
            SendResult = false,
            Backup = new DataObject("原内容")
        };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.SendFailed, outcome);
        CollectionAssert.Contains(native.Calls, "send-ctrl-v");
        CollectionAssert.Contains(native.Calls, "set-clipboard");
    }

    [TestMethod]
    public async Task DeliverAsync_RestoreFailureReportsDeliveredRestoreFailed()
    {
        var native = new ScriptedNative
        {
            Foreground = 0x77,
            Backup = new DataObject("原内容"),
            RestoreResult = false
        };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.DeliveredRestoreFailed, outcome, "粘贴已生效，仅恢复失败。");
    }

    /// <summary>脚本化假件：调用序列逐条记录，结果可注入。</summary>
    private sealed class ScriptedNative : IInputDeliveryNative
    {
        public List<string> Calls { get; } = [];

        public HashSet<nint> OwnProcessWindows { get; } = [];

        public HashSet<nint> KnownWindows { get; } = [];

        public nint Foreground { get; set; }

        public IDataObject? Backup { get; set; }

        public IDataObject? Written { get; private set; }

        public IDataObject? Restored { get; private set; }

        public bool SetForegroundResult { get; set; } = true;

        public bool SendResult { get; set; } = true;

        public bool WriteResult { get; set; } = true;

        public bool RestoreResult { get; set; } = true;

        public ScriptedNative() => KnownWindows.Add(0x77);

        public nint GetForegroundWindow()
        {
            Calls.Add("get-foreground");
            return Foreground;
        }

        public bool IsWindow(nint hwnd) => KnownWindows.Contains(hwnd);

        public bool IsOwnProcessWindow(nint hwnd) => OwnProcessWindows.Contains(hwnd);

        public bool SetForegroundWindow(nint hwnd)
        {
            Calls.Add($"set-foreground:{hwnd}");
            return SetForegroundResult;
        }

        public bool SendControlV()
        {
            Calls.Add("send-ctrl-v");
            return SendResult;
        }

        public IDataObject? GetClipboard()
        {
            Calls.Add("get-clipboard");
            return Backup;
        }

        public bool WriteClipboard(IDataObject data)
        {
            Calls.Add("write-clipboard");
            Written = data;
            return WriteResult;
        }

        public bool SetClipboard(IDataObject data)
        {
            Calls.Add("set-clipboard");
            Restored = data;
            return RestoreResult;
        }

        public Task DelayAsync(TimeSpan delay)
        {
            Calls.Add("delay");
            return Task.CompletedTask;
        }
    }
}
