using System.Windows;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 输入送达子系统（2026-11-03 需求 6，契约 #36 第三递送通道）的管线单元
/// 测试：Win32 接缝注入假件，断言「备份剪贴板→写负载→目标前台化→有界等待目标
/// 真正取得前台→Ctrl+V→粘贴稳定期→守卫恢复剪贴板」的序列与各失败分支的归因
/// 收尾；不触碰真实剪贴板/前台/输入注入。2026-11-05（W1）随可靠性修复扩测：
/// 注入前台谓词（未前台不注入、超时归因收尾）、恢复序号守卫（外部改写不覆写）、
/// 剪贴板占用有界重试。
/// </summary>
[TestClass]
public sealed class InputDeliveryServiceTests
{
    private const nint PanelWindow = 0x99;

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
        // 轮询/重试的延时条目（delay）不参与序列断言：谓词成立即注入，序列稳定为
        // 「备份→写→记序号→前台化→谓词查询→注入→稳定期→查序号→恢复」。
        CollectionAssert.AreEqual(
            new[]
            {
                "get-clipboard",
                "write-clipboard",
                "get-sequence",
                "set-foreground:119",
                "get-foreground",
                "get-focus",
                "send-ctrl-v",
                "get-sequence",
                "set-clipboard"
            },
            native.Calls.Where(call => call != "delay").ToArray(),
            "送达序列必须是：备份→写负载→记序号→前台化→谓词查询→Ctrl+V→稳定期→查序号→恢复。");
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
        Assert.AreEqual(3, native.WriteAttempts, "剪贴板被占用必须有界重试（1＋2 次）后才归因失败。");
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
        Assert.AreEqual(3, native.SetAttempts, "恢复被占用必须有界重试（1＋2 次）后才归因 DeliveredRestoreFailed。");
    }

    // ---- 2026-11-05（W1）可靠性修复的锁定用例 ----

    [TestMethod]
    public async Task DeliverAsync_WaitsUntilTargetActuallyForegroundBeforeInjecting()
    {
        // 锁定根因 S1：SetForegroundWindow 受理 ≠ 前台已切换（OS 异步完成）——
        // 注入必须发生在前台谓词成立之后，而不是固定延时后盲目注入。
        var native = new ScriptedNative
        {
            Foreground = 0x77,
            ForegroundSwitchPendingPolls = 2,
        };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();
        var pollsBeforeDelivery = native.ForegroundPolls;
        native.Calls.Clear();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.Delivered, outcome);
        Assert.AreEqual(3, native.ForegroundPollsAtInject!.Value - pollsBeforeDelivery,
            "注入必须发生在前台谓词成立的那次轮询（前两次轮询仍报告面板）。");
        Assert.AreEqual(3, native.Calls.Count(call => call == "delay"),
            "谓词未成立的两次间隙各让出一次＋注入后粘贴稳定期一次。");
        Assert.IsTrue(
            native.Calls.IndexOf("send-ctrl-v") > native.Calls.LastIndexOf("get-foreground"),
            "注入必须晚于最后一次前台谓词查询。");
    }

    [TestMethod]
    public async Task DeliverAsync_ForegroundNeverActivatesTimesOutToFocusFailedAndRestores()
    {
        // 锁定根因 S1 的超时分支：前台始终不成立 → 有界超时归因 FocusFailed、
        // 不注入、尽力恢复剪贴板（任何分支必收尾，in-flight 不悬挂）。
        var native = new ScriptedNative
        {
            Foreground = 0x77,
            SwitchForegroundOnSet = false,
            Backup = new DataObject("原内容"),
            UseRealDelays = true,
        };
        var service = new InputDeliveryService(native, foregroundWaitTimeout: TimeSpan.FromMilliseconds(60));
        service.CaptureForegroundTarget();
        native.Foreground = PanelWindow; // 前台恒为面板，谓词永不成立

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.FocusFailed, outcome, "前台谓词超时必须归因为焦点失败。");
        CollectionAssert.DoesNotContain(native.Calls, "send-ctrl-v", "未取得前台绝不注入。");
        CollectionAssert.Contains(native.Calls, "set-clipboard", "失败路径必须尽力恢复原剪贴板。");
    }

    [TestMethod]
    public async Task DeliverAsync_WaitsForTargetFocusAfterForegroundSwitch()
    {
        // 锁定 D4 修复后验证发现的精化：前台记账先于目标处理激活（焦点仍 0）——
        // 稳定期起算过早会与目标延迟读取竞态（粘到恢复后的原内容），注入必须
        // 同时等「前台＝目标 ∧ 焦点就绪」。
        var native = new ScriptedNative
        {
            Foreground = 0x77,
            ForegroundSwitchPendingPolls = 0,
            FocusPendingExtraPolls = 2,
        };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();
        var pollsBeforeDelivery = native.ForegroundPolls;
        native.Calls.Clear();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.Delivered, outcome);
        Assert.AreEqual(3, native.ForegroundPollsAtInject!.Value - pollsBeforeDelivery,
            "前台第 1 次轮询即成立，但焦点再滞后 2 次——注入必须发生在焦点就绪的那次轮询。");
        Assert.AreEqual(3, native.Calls.Count(call => call == "delay"),
            "焦点未就绪的两次间隙各让出一次＋注入后粘贴稳定期一次。");
    }

    [TestMethod]
    public async Task DeliverAsync_SkipsRestoreWhenClipboardChangedExternally()
    {
        // 锁定根因 S2 的数据面守卫：送达窗口期内剪贴板被外部改写（用户复制新
        // 内容）→ 恢复即覆写，必须跳过。
        var native = new ScriptedNative
        {
            Foreground = 0x77,
            Backup = new DataObject("原内容"),
            BumpSequenceOnInject = true,
        };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.Delivered, outcome, "粘贴已生效，剪贴板属用户新内容。");
        CollectionAssert.DoesNotContain(native.Calls, "set-clipboard", "外部已改写剪贴板时恢复＝覆写，必须跳过。");
    }

    [TestMethod]
    public async Task DeliverAsync_ClipboardBusyRetriesWriteThenSucceeds()
    {
        // 锁定根因 S3：写剪贴板被占用时有界重试可救回（不再一次性失败＝卡住）。
        var native = new ScriptedNative { Foreground = 0x77 };
        native.WriteScript.Enqueue(false);
        native.WriteScript.Enqueue(false);
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.Delivered, outcome);
        Assert.AreEqual(3, native.WriteAttempts, "前两次占用失败后第三次必须成功送达。");
    }

    [TestMethod]
    public async Task DeliverAsync_ClipboardBusyWriteRetriesExhausted()
    {
        var native = new ScriptedNative { Foreground = 0x77, WriteResult = false };
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.ClipboardWriteFailed, outcome);
        Assert.AreEqual(3, native.WriteAttempts, "重试必须有界：1＋2 次后归因，不无限占用。");
        Assert.AreEqual(2, native.Calls.Count(call => call == "delay"), "两次重试间隔各让出一次。");
    }

    [TestMethod]
    public async Task DeliverAsync_RestoreBusyRetriesThenSucceeds()
    {
        var native = new ScriptedNative { Foreground = 0x77, Backup = new DataObject("原内容") };
        native.RestoreScript.Enqueue(false);
        var service = new InputDeliveryService(native);
        service.CaptureForegroundTarget();

        var outcome = await service.DeliverAsync(new DataObject("送达正文"));

        Assert.AreEqual(InputDeliveryOutcome.Delivered, outcome);
        Assert.AreEqual(2, native.SetAttempts, "恢复占用一次失败后重试成功，剪贴板不再被负载顶替。");
    }

    /// <summary>脚本化假件：调用序列逐条记录，结果可注入；前台切换可建模为
    /// 「SetForegroundWindow 受理后还需 N 次轮询才真正生效」（OS 异步激活），
    /// 剪贴板序号可注入外部改写。</summary>
    private sealed class ScriptedNative : IInputDeliveryNative
    {
        public List<string> Calls { get; } = [];

        public HashSet<nint> OwnProcessWindows { get; } = [];

        public HashSet<nint> KnownWindows { get; } = [];

        public nint Foreground { get; set; }

        /// <summary>SetForegroundWindow 受理后前台是否真正会切换（默认开；关闭＝
        /// 模拟切换永不完成，走谓词超时分支）。</summary>
        public bool SwitchForegroundOnSet { get; set; } = true;

        /// <summary>受理后还需多少次前台轮询才真正切换（模拟 OS 异步激活完成时延）。</summary>
        public int ForegroundSwitchPendingPolls { get; set; }

        /// <summary>前台已切换后，焦点还需多少次轮询才就绪（模拟目标处理激活设置
        /// 焦点的滞后——W1 D4 修复后验证实测：前台已＝目标而焦点＝0）。</summary>
        public int FocusPendingExtraPolls { get; set; }

        public IDataObject? Backup { get; set; }

        public IDataObject? Written { get; private set; }

        public IDataObject? Restored { get; private set; }

        public bool SetForegroundResult { get; set; } = true;

        public bool SendResult { get; set; } = true;

        public bool WriteResult { get; set; } = true;

        public bool RestoreResult { get; set; } = true;

        /// <summary>逐次写结果脚本（空则回落 WriteResult）——模拟「占用两次后成功」。</summary>
        public Queue<bool> WriteScript { get; } = [];

        /// <summary>逐次恢复结果脚本（空则回落 RestoreResult）。</summary>
        public Queue<bool> RestoreScript { get; } = [];

        /// <summary>注入时把剪贴板序号 +1——模拟外部在送达窗口期内改写剪贴板。</summary>
        public bool BumpSequenceOnInject { get; set; }

        /// <summary>延时走真实 Task.Delay（默认同步完成）——谓词超时分支需要真实
        /// 时间推进，避免同步自旋空转撑爆调用日志。</summary>
        public bool UseRealDelays { get; set; }

        public uint Sequence { get; set; } = 1;

        public int WriteAttempts { get; private set; }

        public int SetAttempts { get; private set; }

        /// <summary>GetForegroundWindow 累计查询次数（含捕获阶段）。</summary>
        public int ForegroundPolls { get; private set; }

        /// <summary>注入发生时的前台轮询累计序号。</summary>
        public int? ForegroundPollsAtInject { get; private set; }

        private bool _foregroundRequested;
        private int _switchTakesEffectAtPoll;
        private int _focusTakesEffectAtPoll;

        public ScriptedNative() => KnownWindows.Add(0x77);

        public nint GetForegroundWindow()
        {
            Calls.Add("get-foreground");
            ForegroundPolls++;
            if (_foregroundRequested && ForegroundPolls < _switchTakesEffectAtPoll)
            {
                return PanelWindow;
            }

            return Foreground;
        }

        public nint GetForegroundFocus()
        {
            Calls.Add("get-focus");
            return _foregroundRequested && ForegroundPolls < _focusTakesEffectAtPoll ? 0 : 0x88;
        }

        public bool IsWindow(nint hwnd) => KnownWindows.Contains(hwnd);

        public bool IsOwnProcessWindow(nint hwnd) => OwnProcessWindows.Contains(hwnd);

        public bool SetForegroundWindow(nint hwnd)
        {
            Calls.Add($"set-foreground:{hwnd}");
            _foregroundRequested = true;
            // 受理即排队：再经 ForegroundSwitchPendingPolls 次轮询后前台才真正切换
            //（OS 异步激活）；关闭切换＝模拟切换永不完成（超时分支）。
            _switchTakesEffectAtPoll = SwitchForegroundOnSet
                ? ForegroundPolls + 1 + ForegroundSwitchPendingPolls
                : int.MaxValue;
            _focusTakesEffectAtPoll = _switchTakesEffectAtPoll + FocusPendingExtraPolls;
            return SetForegroundResult;
        }

        public bool SendControlV()
        {
            Calls.Add("send-ctrl-v");
            ForegroundPollsAtInject = ForegroundPolls;
            if (BumpSequenceOnInject)
            {
                Sequence++;
            }

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
            WriteAttempts++;
            return WriteScript.Count > 0 ? WriteScript.Dequeue() : WriteResult;
        }

        public bool SetClipboard(IDataObject data)
        {
            Calls.Add("set-clipboard");
            Restored = data;
            SetAttempts++;
            return RestoreScript.Count > 0 ? RestoreScript.Dequeue() : RestoreResult;
        }

        public uint GetClipboardSequenceNumber()
        {
            Calls.Add("get-sequence");
            return Sequence;
        }

        public Task DelayAsync(TimeSpan delay)
        {
            Calls.Add("delay");
            return UseRealDelays ? Task.Delay(delay) : Task.CompletedTask;
        }
    }
}
