using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FloatingTransferStation.Tests;

/// <summary>SendInput 真实键盘注入的机器级前置谓词（flake 台账 A1/A2，2026-11-02）。
/// SendInput 把按键投递给系统前台窗口：真实点击后前台激活由 OS 异步完成，既有的
/// 固定 300ms 泵不能保证本窗口已持有前台——未取得前台即注入＝按键送进别处或丢失
/// （PR #256 attempt1 同一环境窗口连伤 3 例真实输入用例后 rerun 全绿的实证）。所有
/// 「真实键盘注入旅程」的激活点先经 <see cref="WaitForForegroundActive"/>：谓词成立
/// 即返回（等待真实前置条件，非固定睡眠），超时才失败并携带当前前台窗口现场供归因。
/// 注意：ContextMenu 弹出持有前台是合法形态（键盘先归菜单 HWND），菜单内 Esc 等
/// 注入不得加本谓词。</summary>
internal static class RealInputGate
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>等待窗口持有 OS 前台（真实键盘注入的确定前置）。谓词已成立时零等待。</summary>
    public static void WaitForForegroundActive(Window window, string because)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (GetForegroundWindow() != hwnd)
        {
            if (DateTime.UtcNow >= deadline)
            {
                var foreground = GetForegroundWindow();
                var title = new StringBuilder(256);
                _ = GetWindowText(foreground, title, 256);
                GetWindowThreadProcessId(foreground, out var pid);
                Assert.Fail(
                    $"{because}：键盘注入前窗口未在五秒内取得前台（前台激活是 OS 异步完成的真实前置，"
                    + $"此处等谓词而非固定延时）。DIAG foreground=0x{foreground:X} "
                    + $"title=\"{title}\" pid={pid} 本窗口=0x{hwnd:X}。");
            }

            Pump(window.Dispatcher, TimeSpan.FromMilliseconds(20));
        }
    }

    private static void Pump(Dispatcher dispatcher, TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = duration
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
