namespace FloatingTransferStation.Services;

/// <summary>
/// 更新流诊断日志（契约 #26 修订②）：自动检查/下载/安装启动各阶段的成败与里程碑
/// 落盘到注入路径（生产＝<c>&lt;数据根&gt;\logs\update.log</c>），使「自动检查对一切
/// 失败完全静默」的用户侧仍可诊断。行格式、追加式、256KB 截断与静默吞 IO 错由泛化
/// 基座 <see cref="DiagnosticsLog"/> 承载（2026-11-16 提取，行为零变化）；本类保留
/// 更新流的阶段与 kind 常量（写点统一取用，日志文本不随调用点漂移；FlowPhase 为
/// 流级兜底——更新流中无法归入单一阶段的事件，如未知异常）。UI 只做单点接线，
/// 不在各 catch 里散落文件写入。
/// </summary>
public sealed class UpdateDiagnosticsLog : DiagnosticsLog
{
    public const string CheckPhase = "检查";
    public const string DownloadPhase = "下载";
    public const string InstallPhase = "安装";
    public const string FlowPhase = "更新";
    public const string FailureKind = "失败";
    public const string MilestoneKind = "里程碑";

    public UpdateDiagnosticsLog(string logPath) : base(logPath)
    {
    }
}
