using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 未处理异常观测日志（架构候选 C12 观测版，Issue #250）锁定测试：记录字段齐全
/// （类型/HResult/消息/堆栈/来源）与写失败静默（IO 失败绝不产生二次异常——处理器
/// 既有弹窗与 Handled=true 带伤续跑语义不因观测而变化）。经 internal 记录写点直测，
/// 不经 WPF Application 启动；临时目录沿 TestTempRoot 仓库工作流惯例。
/// </summary>
[TestClass]
public sealed class UnhandledExceptionObservabilityTests
{
    [TestMethod]
    public void UnhandledExceptionRecord_AppendsLineWithAllFields()
    {
        var directory = Path.Combine(TestTempRoot.Root, Guid.NewGuid().ToString("N"));
        var log = new UpdateDiagnosticsLog(Path.Combine(directory, "logs", "exceptions.log"));
        Exception thrown;
        try
        {
            throw new InvalidOperationException("观测样例消息");
        }
        catch (Exception captured)
        {
            thrown = captured;
        }

        App.WriteUnhandledExceptionRecord(log, thrown);

        var line = File.ReadAllText(Path.Combine(directory, "logs", "exceptions.log")).TrimEnd();
        StringAssert.Contains(line, "未处理异常");
        StringAssert.Contains(line, thrown.GetType().FullName);
        StringAssert.Contains(line, $"HResult=0x{thrown.HResult:X8}");
        StringAssert.Contains(line, "消息=观测样例消息");
        StringAssert.Matches(line, new System.Text.RegularExpressions.Regex(@"来源=\S"));
        var stack = line[(line.LastIndexOf("堆栈=", StringComparison.Ordinal) + "堆栈=".Length)..];
        Assert.IsFalse(string.IsNullOrWhiteSpace(stack), "堆栈字段不得为空。");
        StringAssert.Contains(stack, " at ");
    }

    [TestMethod]
    public void UnhandledExceptionRecord_SwallowsWriteFailureSilently()
    {
        // 路径异常注入：把已存在的普通文件充当日志路径的父目录段，建目录必然失败。
        var blocker = Path.Combine(TestTempRoot.Root, Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "占位文件：让后续 CreateDirectory 必然抛 IO 异常");
        var log = new UpdateDiagnosticsLog(Path.Combine(blocker, "logs", "exceptions.log"));

        // 断言即「调用未抛」：无二次异常＝处理器后续弹窗与 Handled 语义不受影响。
        App.WriteUnhandledExceptionRecord(log, new InvalidOperationException("写失败静默样例"));
    }
}
