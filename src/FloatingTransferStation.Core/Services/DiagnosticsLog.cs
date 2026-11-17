using System.Globalization;
using System.Text;

namespace FloatingTransferStation.Services;

/// <summary>
/// 通用单文件诊断日志（自 UpdateDiagnosticsLog 泛化，2026-11-16 剪贴板访问层收口）：
/// 追加式行记录（<c>yyyy-MM-dd HH:mm:ss|阶段|kind|消息</c>）、单文件超容量上限即截断
/// 保尾部、一切 IO 失败静默吞——诊断日志绝不影响被诊断的流本身。阶段与 kind 由各
/// 使用方以常量约定（如 UpdateDiagnosticsLog 的检查/下载/安装、clipboard.log 的读/写/
/// 送达），写点统一取用，日志文本不随调用点漂移。线程安全（写与截断同锁串行）。
/// </summary>
public class DiagnosticsLog
{
    /// <summary>单文件容量上限：超出即截断保尾部（诊断价值集中在最近的失败）。</summary>
    private const int MaxFileBytes = 256 * 1024;

    private readonly object _gate = new();
    private readonly string _logPath;

    public DiagnosticsLog(string logPath) => _logPath = logPath;

    /// <summary>
    /// 追加一条记录：本地时间戳＋阶段＋kind＋单行消息（消息中的换行折叠为空格，
    /// 保证一行一记录的可读性与截断按行对齐）。任何 IO 失败（目录不可写、路径
    /// 非法、磁盘满等）静默吞——被诊断的流绝不因诊断日志而改变行为。
    /// </summary>
    public void Write(string phase, string kind, string message)
    {
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}|{phase}|{kind}|{Sanitize(message)}{Environment.NewLine}");
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_logPath, line, Encoding.UTF8);
                TruncateTailIfOversized();
            }
        }
        catch (Exception)
        {
            // 一切 IO 失败静默吞：诊断日志绝不影响被诊断的流（硬性边界）。
        }
    }

    /// <summary>超限截断：保留尾部一半（按行对齐，从完整行起），头部以截断标记行取代。</summary>
    private void TruncateTailIfOversized()
    {
        var info = new FileInfo(_logPath);
        if (!info.Exists || info.Length <= MaxFileBytes)
        {
            return;
        }

        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Seek(-MaxFileBytes / 2, SeekOrigin.End);
        // 从字节中点起的解码若落在多字节字符中间，首个替换字符随（被丢弃的）残行一并切除：
        // 尾部从下一个换行后的完整行开始。
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var tail = reader.ReadToEnd();
        var newline = tail.IndexOf('\n');
        if (newline >= 0 && newline + 1 <= tail.Length)
        {
            tail = tail[(newline + 1)..];
        }

        stream.SetLength(0);
        using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.WriteLine("（更早记录已因超出容量上限被截断）");
        writer.Write(tail);
    }

    private static string Sanitize(string message) =>
        message.Replace('\r', ' ').Replace('\n', ' ');
}
