<#
.SYNOPSIS
  剪贴板锁死取证探针（只读）：看清当前谁持有剪贴板锁、谁是数据 owner、序号变化。
.DESCRIPTION
  「复制粘贴全系统失效、重启恢复、过段时间复发」事故（2026-11-16）的复发捕获工具。
  两个角色必须分开看：GetOpenClipboardWindow＝当前持锁窗口（谁占着剪贴板独占锁），
  GetClipboardOwner＝数据 owner（延迟渲染义务方）——二者通常不是同一窗口，看错对象即误诊
  （Raymond Chen 2024-04-10）。占用状态无变更通知，只能轮询（-Watch 默认 1Hz）。
  竞态容忍：持锁窗口转瞬即逝或进程已退出时如实记「无/已释放/?」，不报错。
  本脚本默认只读：绝不结束进程、绝不改机器配置、绝不读写剪贴板内容本身。
  完整判定阶梯见 -Guided；规格出处＝docs/research/2026-11-16-clipboard-lockdown.md §6（本地维护文档）。
.PARAMETER Once
  单次快照：锁持有者/数据 owner（各含 HWND/TID/PID/进程名/窗口类/标题）＋剪贴板序号＋时间。默认动作。
.PARAMETER Watch
  1Hz 轮询直至 Ctrl+C；行式追加 %TEMP%\FloatingTransferStation\forensics\clipboard-incident-<yyyyMMdd-HHmmss>.log
  （UTF-8），每行＝时间|持锁者 PID/进程/类|owner PID/进程/类|序号。持锁者非「无」时整行黄字高亮＝关键信号。
.PARAMETER Guided
  打印复发判定阶梯（先采集后处置、不要先重启；GameViewer→fts→cbdhsvc 顺序）。
.EXAMPLE
  .\diagnose-clipboard.ps1 -Once
  单次快照当前锁持有者与数据 owner。
.EXAMPLE
  .\diagnose-clipboard.ps1 -Watch
  1Hz 轮询取证，Ctrl+C 停止；日志自动留档 %TEMP%\FloatingTransferStation\forensics\。
.EXAMPLE
  .\diagnose-clipboard.ps1 -Guided
  查看复发时的处置阶梯（下次复发先看这个指引）。
#>
# 剪贴板锁死取证探针（只读；批次 2026-11-16 clipboard-lockdown E1，底稿 §6.1/§6.3 规格）。
param(
    [switch]$Once,
    [switch]$Watch,
    [switch]$Guided
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 默认 OEM 代码页，中文输出需切 UTF-8（主机不支持时静默容忍）。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$selectedModes = @($Once, $Watch, $Guided) | Where-Object { $PSItem }
if ($selectedModes.Count -gt 1) {
    throw '参数冲突：-Once/-Watch/-Guided 三选一（无参＝-Once 单次快照）。'
}

# P/Invoke 探针（底稿 §6.1 已在本机验证可用的实现扩展而来；类名带 Fts 前缀避免与内联版同会话撞类型）。
if (-not ([System.Management.Automation.PSTypeName]'FtsClipboardProbe').Type) {
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class FtsClipboardProbe {
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetOpenClipboardWindow();
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetClipboardOwner();
  [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll", SetLastError=true)] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
  static string ProcName(uint pid) {
    try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
  }
  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "0";
    uint pid; uint tid = GetWindowThreadProcessId(h, out pid);
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    var t = new StringBuilder(256); GetWindowText(h, t, 256);
    return string.Format("HWND=0x{0:X} TID={1} PID={2} Class='{3}' Title='{4}' Proc={5}",
        h.ToInt64(), tid, pid, c, t, ProcName(pid));
  }
  public static string Brief(IntPtr h) {
    if (h == IntPtr.Zero) return "无";
    uint pid; GetWindowThreadProcessId(h, out pid);
    var c = new StringBuilder(256); GetClassName(h, c, 256);
    return string.Format("{0}/{1}/{2}", pid, ProcName(pid), c);
  }
}
'@
}

function Get-ClipboardSnapshot {
    $opener = [FtsClipboardProbe]::GetOpenClipboardWindow()
    $owner = [FtsClipboardProbe]::GetClipboardOwner()
    [pscustomobject]@{
        Stamp    = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        Opener   = $opener
        Owner    = $owner
        Sequence = [FtsClipboardProbe]::GetClipboardSequenceNumber()
    }
}

if (-not ($Watch -or $Guided)) {
    # -Once（默认动作，含显式 -Once）。
    $snap = Get-ClipboardSnapshot
    Write-Host ('== 剪贴板快照 {0} ==' -f $snap.Stamp)
    $openerText = if ($snap.Opener -eq [IntPtr]::Zero) { '无（当前无人持锁）' } else { [FtsClipboardProbe]::Describe($snap.Opener) }
    $ownerText = if ($snap.Owner -eq [IntPtr]::Zero) { '无（剪贴板为空）' } else { [FtsClipboardProbe]::Describe($snap.Owner) }
    Write-Host ('锁持有者 GetOpenClipboardWindow：{0}' -f $openerText)
    Write-Host ('数据 owner GetClipboardOwner：{0}' -f $ownerText)
    Write-Host ('序号 GetClipboardSequenceNumber：{0}' -f $snap.Sequence)
    if ($snap.Sequence -eq 0) {
        Write-Warning '序号为 0 视为异常态（底稿 §6.1），建议跑 -Watch 连续观察。'
    }
    return
}

if ($Guided) {
    Write-Host @'

== 剪贴板锁死复发判定阶梯（先采集后处置，不要先重启） ==
出处：docs/research/2026-11-16-clipboard-lockdown.md §6.3（本地维护文档）

第 0 步（先采集留档）：PowerShell 跑本脚本 -Watch 30–60 秒
        日志自动留档 %TEMP%\FloatingTransferStation\forensics\clipboard-incident-*.log，
        复发当场它能看清「谁持锁（GetOpenClipboardWindow）/谁是 owner（GetClipboardOwner）」。

第 1 步：任务管理器先只结束 GameViewer.exe（UU 远程）→ 剪贴板恢复＝H1 定罪
        （UURemoteClipboard 属主不响应渲染请求，拖累全系统读剪贴板挂起）。
第 2 步：未恢复 → 只结束 悬浮中转站.exe → 恢复＝fts 侧（H2/H4）实锤。
第 3 步：未恢复 → 管理员 PowerShell 执行 Restart-Service cbdhsvc_*（剪贴板历史服务，名字带随机后缀）。
第 4 步：仍坏 → 重启电脑；把以上每步试验结果＋日志文件交给任一维护窗口，归档进底稿 §9。

判读提示：
- 两个角色别看混——「持锁者」＝谁占着锁；「owner」＝谁欠渲染义务。锁显示在正在粘贴的
  普通应用且约 30 秒后自行恢复＝owner 无响应（查 owner 而非持锁者）。
- -Watch 行内持锁者非「无」＝有进程正占着剪贴板锁；瞬时出现属正常（读写本就短暂持锁），
  持续多行不释放才是锁死现场。
- 缓解选项（自决）：近期不用 UU 远程时，退出/禁用 GameViewer 自启可立刻显著降低复发概率。
'@
    return
}

# -Watch：1Hz 轮询取证。
$forensicsDir = Join-Path $env:TEMP 'FloatingTransferStation\forensics'
New-Item -ItemType Directory -Force -Path $forensicsDir | Out-Null
$logPath = Join-Path $forensicsDir ('clipboard-incident-{0}.log' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
# 带文件头 BOM 的 UTF-8：Windows PowerShell 5.1 的 Get-Content 与旧版记事本按 BOM 识别编码，
# 否则「无」等中文字段会被按 ANSI 误读成乱码；逐行追加仍用无 BOM 编码避免 BOM 混入行间。
[System.IO.File]::WriteAllText($logPath, [string]::Empty, (New-Object System.Text.UTF8Encoding($true)))
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
Write-Host ('1Hz 轮询中……Ctrl+C 停止。日志留档：{0}' -f $logPath) -ForegroundColor Cyan
Write-Host '每行＝时间|持锁者 PID/进程/类|owner PID/进程/类|序号；持锁者非「无」整行黄字＝有人正占着锁。'
$nextTick = [DateTime]::UtcNow
try {
    while ($true) {
        $snap = Get-ClipboardSnapshot
        $line = '{0}|{1}|{2}|{3}' -f $snap.Stamp,
            [FtsClipboardProbe]::Brief($snap.Opener),
            [FtsClipboardProbe]::Brief($snap.Owner),
            $snap.Sequence
        # 逐行立即落盘（Ctrl+C 随时可停，已写行不丢）。
        [System.IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8NoBom)
        if ($snap.Opener -ne [IntPtr]::Zero) {
            Write-Host $line -ForegroundColor Yellow
        } else {
            Write-Host $line
        }
        $nextTick = $nextTick.AddMilliseconds(1000)
        $delay = ($nextTick - [DateTime]::UtcNow).TotalMilliseconds
        if ($delay -gt 0) {
            Start-Sleep -Milliseconds $delay
        } else {
            $nextTick = [DateTime]::UtcNow   # 采样耗时超出节拍时重新对齐，防止漂移。
        }
    }
} finally {
    Write-Host ''
    Write-Host ('已停止。日志已留档：{0}' -f $logPath) -ForegroundColor Cyan
}
