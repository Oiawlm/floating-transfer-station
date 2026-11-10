using System.Text.RegularExpressions;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// UpdateDiagnosticsLog 离线锁定测试（契约 #26 修订②）：格式化追加
/// （yyyy-MM-dd HH:mm:ss|阶段|kind|消息）、单文件容量上限截断保尾部、
/// 一切 IO 失败静默吞（诊断日志绝不影响更新流）。
/// </summary>
[TestClass]
public sealed class UpdateDiagnosticsLogTests
{
    private const int MaxFileBytes = 256 * 1024;

    [TestMethod]
    public void UpdateDiagnosticsLog_AppendsFormattedEntries()
    {
        using var directory = new TestDirectory();
        var logPath = Path.Combine(directory.Root, "logs", "update.log");
        var log = new UpdateDiagnosticsLog(logPath);

        log.Write(UpdateDiagnosticsLog.CheckPhase, UpdateDiagnosticsLog.FailureKind, "自动检查失败：无法连接更新服务");
        log.Write(
            UpdateDiagnosticsLog.InstallPhase,
            UpdateDiagnosticsLog.MilestoneKind,
            "版本 99.0.0 安装器已启动");

        var lines = File.ReadAllLines(logPath);
        Assert.AreEqual(2, lines.Length, "每条记录必须恰占一行。");
        Assert.IsTrue(
            Regex.IsMatch(lines[0], @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\|检查\|失败\|自动检查失败：无法连接更新服务$"),
            $"检查阶段失败记录必须符合四段格式，实际：{lines[0]}");
        Assert.IsTrue(
            Regex.IsMatch(lines[1], @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\|安装\|里程碑\|版本 99\.0\.0 安装器已启动$"),
            $"安装阶段里程碑记录必须符合四段格式，实际：{lines[1]}");
    }

    [TestMethod]
    public void UpdateDiagnosticsLog_TruncatesTailBeyondCap()
    {
        using var directory = new TestDirectory();
        var logPath = Path.Combine(directory.Root, "update.log");
        var log = new UpdateDiagnosticsLog(logPath);
        var filler = new string('x', 120);
        for (var index = 0; index < 3000; index++)
        {
            log.Write(
                UpdateDiagnosticsLog.DownloadPhase,
                UpdateDiagnosticsLog.MilestoneKind,
                $"记录 {index:D6} {filler}");
        }

        Assert.IsTrue(
            new FileInfo(logPath).Length <= MaxFileBytes,
            $"超限后单文件必须回落到容量上限内，实际 {new FileInfo(logPath).Length} 字节。");
        var text = File.ReadAllText(logPath);
        StringAssert.Contains(text, "（更早记录已因超出容量上限被截断）", "截断后头部必须有截断标记行。");
        StringAssert.Contains(text, "记录 002999", "截断必须保尾部（最新记录仍在）。");
        Assert.IsFalse(text.Contains("记录 000000", StringComparison.Ordinal), "截断必须丢弃最早的记录。");
    }

    [TestMethod]
    public void UpdateDiagnosticsLog_SwallowsIoFailures()
    {
        // 路径含 NUL 字符（Path API 必抛 ArgumentException）与「日志路径是目录」
        //（写文件必抛 UnauthorizedAccessException）两类 IO 失败都不得外泄——
        // 诊断日志绝不影响更新流（本测试以「走到断言即未抛」为通过判据）。
        var invalidPathLog = new UpdateDiagnosticsLog("Z:\\bad\0path\\update.log");
        invalidPathLog.Write(UpdateDiagnosticsLog.CheckPhase, UpdateDiagnosticsLog.FailureKind, "任何失败");

        using var directory = new TestDirectory();
        var directoryAsLog = Path.Combine(directory.Root, "occupied");
        Directory.CreateDirectory(directoryAsLog);
        var occupiedLog = new UpdateDiagnosticsLog(directoryAsLog);
        occupiedLog.Write(UpdateDiagnosticsLog.CheckPhase, UpdateDiagnosticsLog.FailureKind, "任何失败");
        Assert.IsFalse(
            File.Exists(directoryAsLog),
            "目标位置是目录时不得产生任何文件（写失败被吞，不留痕）。");

        // IO 失败被吞后，同一进程内的后续日志写入照常工作（失败不毒化机制）。
        var healthyPath = Path.Combine(directory.Root, "healthy", "update.log");
        var healthyLog = new UpdateDiagnosticsLog(healthyPath);
        healthyLog.Write(UpdateDiagnosticsLog.CheckPhase, UpdateDiagnosticsLog.FailureKind, "恢复后的记录");
        Assert.IsTrue(File.Exists(healthyPath), "IO 失败静默吞之后，后续写入必须照常落盘。");
    }
}
