using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FloatingTransferStation.Design;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;
using FloatingTransferStation.ViewModels;
using FloatingTransferStation.Views;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 设置窗口几何契约（#24，Issue #106）：冻结 min/max 约束、持久化往返、
/// 越界钳制、负坐标合法、非法值/全屏外回落默认定位、「结束才落盘」时机与
/// 最大化不落盘。多屏/显示器断开经 WorkAreaResolverOverride 假工作区注入。
/// </summary>
[TestClass]
public sealed class SettingsWindowGeometryTests
{
    [STATestMethod]
    public void Geometry_FrozenConstraints_AreDeclaredAndEnforced()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            Assert.AreEqual(SizeToContent.Manual, settings.SizeToContent, "SizeToContent 冻结为 Manual（内容驱动测量会把高度撑回内容高度）。");
            Assert.AreEqual(ResizeMode.CanResize, settings.ResizeMode, "冻结为 CanResize（系统级 8 向缩放）。");
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(settings);
            Assert.IsNotNull(chrome);
            Assert.AreEqual(6d, chrome.ResizeBorderThickness.Left, "无边框缩放命中区冻结为 6。");
            Assert.AreEqual(560d, settings.MinWidth, 0.01, "最小宽冻结值 560（声明于 Window，程序化下限仍由 WPF 布局钳制）。");
            Assert.AreEqual(460d, settings.MinHeight, 0.01, "最小高冻结值 460。");
            Assert.AreEqual(
                double.PositiveInfinity,
                settings.MaxWidth,
                "不得声明 Window.MaxWidth（1.50.0 教训：属性会把根视觉钳出死区；交互上限改由 WM_GETMINMAXINFO 轨道尺寸强制）。");
            Assert.AreEqual(
                double.PositiveInfinity,
                settings.MaxHeight,
                "不得声明 Window.MaxHeight（同上；恢复路径由 Normalize 按工作区冻结式钳制）。");
            Assert.AreEqual(680d, settings.ActualWidth, 0.01, "默认宽冻结值 680。");
            Assert.AreEqual(600d, settings.ActualHeight, 0.01, "默认高冻结值 600。");

            // 程序化下限仍由 MinWidth/MinHeight（Window 属性）兜底。
            settings.Width = 400;
            CompleteLayout(settings);
            Assert.IsTrue(settings.ActualWidth >= 560 - 0.01, "宽度不得低于最小值。");
            settings.Height = 300;
            CompleteLayout(settings);
            Assert.IsTrue(settings.ActualHeight >= 460 - 0.01, "高度不得低于最小值。");

            // 程序化超宽同样被轨道尺寸钳回 900：轨道是窗口管理器对一切尺寸变更
            //（拖拽/程序化 SetWindowPos/贴靠/最大化之外）的统一边界——上限现在由
            // 真实消息路径强制，而不再依赖会把最大化根视觉钳出死区的 MaxWidth 属性。
            settings.Width = 1200;
            CompleteLayout(settings);
            Assert.IsTrue(
                settings.ActualWidth <= 900 + 0.01,
                $"程序化超宽必须被消息路径轨道尺寸钳制（≤900），ActualWidth={settings.ActualWidth}。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_PersistedRectWithinWorkArea_IsRestoredVerbatim()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = 60,
            SettingsWindowTop = 80,
            SettingsWindowWidth = 860,
            SettingsWindowHeight = 520
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.AreEqual(860d, settings.ActualWidth, 0.01);
            Assert.AreEqual(520d, settings.ActualHeight, 0.01);
            Assert.AreEqual(60d, settings.Left, 0.01, "工作区内的持久化位置应原样恢复。");
            Assert.AreEqual(80d, settings.Top, 0.01);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_NegativeCoordinatesLegalWithinTargetMonitorWorkArea()
    {
        // 左侧副屏布局（虚拟桌面 [-1920..0)+主屏，副屏工作区 DIP [-1920, 0)）：
        // 完全落在副屏内的持久化负坐标须原样恢复，不钳非负、不因越主屏缘被收紧。
        SettingsWindowPlacement.WorkAreaResolverOverride = (_, _) =>
            new SettingsWindowPlacement.WorkArea(-1920, 0, 0, 1040);
        SettingsWindowPlacement.VirtualScreenBoundsOverride = () => (-1920, 0, 1920, 1040);
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -700,
            SettingsWindowTop = 100,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.AreEqual(-700d, settings.Left, 0.01, "左侧副屏内负坐标合法，不得被钳到非负。");
            Assert.AreEqual(100d, settings.Top, 0.01);
            Assert.AreEqual(680d, settings.ActualWidth, 0.01);
            Assert.AreEqual(600d, settings.ActualHeight, 0.01, "未超目标屏冻结最大高（1040 − 48）的尺寸原样恢复。");
        }
        finally
        {
            SettingsWindowPlacement.WorkAreaResolverOverride = null;
            SettingsWindowPlacement.VirtualScreenBoundsOverride = null;
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_PersistedHeightExceedingTargetWorkArea_IsClampedByFrozenFormula()
    {
        // 目标显示器工作区（1040 高）重算最大高（1040 − 48 = 992）并在恢复路径
        // Normalize 收敛：超高的持久化尺寸被钳到冻结式上限。这里对 Normalize 纯函数
        // 断言——SetWindowPos 会再被真实显示器轨道尺寸钳制（无法在集成路径完全
        // 虚拟化，CI 实测 788 = CI 真实工作区 − 48）；消息路径由
        // SettingsWindow_RealHwndMinMaxInfo_EnforcesTrackSizes 与单元锁定测试覆盖，
        // 目标屏重算的端到端行为由负坐标用例（目标屏范围内）覆盖。
        var rect = SettingsWindowPlacement.Normalize(
            -700, 20, 680, 1010,
            SettingsWindowMetrics.MinWidth, SettingsWindowMetrics.MaxWidth,
            SettingsWindowMetrics.MinHeight,
            Math.Max(SettingsWindowMetrics.MinHeight, 1040 - SettingsWindowMetrics.MaxHeightMargin),
            new SettingsWindowPlacement.WorkArea(-1920, 0, 0, 1040));
        Assert.AreEqual(-700d, rect.Left, 0.01, "负坐标位置语义不变。");
        Assert.AreEqual(20d, rect.Top, 0.01, "钳制后仍在工作区内时位置原样保留。");
        Assert.AreEqual(680d, rect.Width, 0.01);
        Assert.AreEqual(992d, rect.Height, 0.01, "超高持久化尺寸应被目标屏冻结式（工作区 − 48）钳制。");
    }

    [STATestMethod]
    public void Geometry_PartiallyOffscreen_ClampsIntoWorkArea()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -300,
            SettingsWindowTop = 50,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            Assert.AreEqual(work.Left, settings.Left, 0.01, "左越界部分应钳到工作区左缘。");
            Assert.AreEqual(50d, settings.Top, 0.01);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_InvalidValues_FallBackToDefaultOwnerRelativePlacement()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = double.NaN,
            SettingsWindowTop = 50,
            SettingsWindowWidth = 860,
            SettingsWindowHeight = 520
        };
        AssertDefaultPlacement(preferences, "NaN 属非法值，整组回落默认定位。");
    }

    [STATestMethod]
    public void Geometry_InfinityNonPositiveAndPartialNull_FallBackAsWholeGroup()
    {
        // 整组语义：任一字段非法（±∞/非正尺寸/部分 null）→ 整组回落，不逐字段抢救。
        var cases = new[]
        {
            (
                "Left=+∞",
                AppPreferences.Default with
                {
                    SettingsWindowLeft = double.PositiveInfinity,
                    SettingsWindowTop = 50,
                    SettingsWindowWidth = 860,
                    SettingsWindowHeight = 520
                }
            ),
            (
                "Width=0",
                AppPreferences.Default with
                {
                    SettingsWindowLeft = 60,
                    SettingsWindowTop = 50,
                    SettingsWindowWidth = 0,
                    SettingsWindowHeight = 520
                }
            ),
            (
                "Height<0",
                AppPreferences.Default with
                {
                    SettingsWindowLeft = 60,
                    SettingsWindowTop = 50,
                    SettingsWindowWidth = 860,
                    SettingsWindowHeight = -520
                }
            ),
            (
                "Height=null（部分字段缺失）",
                AppPreferences.Default with
                {
                    SettingsWindowLeft = 60,
                    SettingsWindowTop = 50,
                    SettingsWindowWidth = 860,
                    SettingsWindowHeight = null
                }
            )
        };
        foreach (var (name, preferences) in cases)
        {
            AssertDefaultPlacement(preferences, $"{name}：非法/不完整几何必须整组回落默认定位。");
        }
    }

    [STATestMethod]
    public void Geometry_FullyOffscreen_FallsBackToDefaultOwnerRelativePlacement()
    {
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -99999,
            SettingsWindowTop = -99999,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        AssertDefaultPlacement(preferences, "完全落在所有显示器之外，整组回落默认定位。");
    }

    [STATestMethod]
    public void Geometry_PersistOnlyAtOperationEnd_MaximizedStateNotPersisted()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var store = GetPreferencesStore(window);
            Assert.IsNull(store.LastSaved, "打开设置窗口本身不得触发几何落盘。");

            // 头部拖拽中间态不落盘，DragCompleted（结束）才落盘。
            var thumb = (Thumb)settings.FindName("HeaderDragRegion");
            thumb.RaiseEvent(new DragDeltaEventArgs(30, 20));
            CompleteLayout(settings);
            Assert.IsNull(store.LastSaved, "拖拽进行中不得写盘（结束才落盘）。");
            thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false));
            CompleteLayout(settings);
            Assert.IsNotNull(store.LastSaved, "拖拽结束必须把几何写回偏好。");
            Assert.AreEqual(settings.Left, store.LastSaved!.SettingsWindowLeft!.Value, 0.01);
            Assert.AreEqual(settings.ActualWidth, store.LastSaved.SettingsWindowWidth!.Value, 0.01);

            // 系统缩放结束（WM_EXITSIZEMOVE 路径）同样落盘。
            store.Reset();
            settings.Width = 760;
            CompleteLayout(settings);
            InvokeCommitPlacement(settings);
            CompleteLayout(settings);
            Assert.IsNotNull(store.LastSaved, "系统缩放结束必须把几何写回偏好。");
            Assert.AreEqual(760d, store.LastSaved!.SettingsWindowWidth!.Value, 0.01);

            // 最大化状态不落盘：恢复的是 Normal 几何。
            settings.WindowState = WindowState.Maximized;
            store.Reset();
            InvokeCommitPlacement(settings);
            CompleteLayout(settings);
            Assert.IsNull(store.LastSaved, "最大化状态不得落盘。");
            settings.WindowState = WindowState.Normal;

            // 最小化同属非 Normal 态：不落盘。
            settings.WindowState = WindowState.Minimized;
            InvokeCommitPlacement(settings);
            CompleteLayout(settings);
            Assert.IsNull(store.LastSaved, "最小化状态不得落盘。");
            settings.WindowState = WindowState.Normal;
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_SystemSizeMoveCommitsAtEndAndSuspendsClampDuringSizing()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            var store = GetPreferencesStore(window);
            var handle = new System.Windows.Interop.WindowInteropHelper(settings).Handle;
            Assert.IsTrue(handle != IntPtr.Zero);
            const int WmEnterSizeMove = 0x0231;
            const int WmExitSizeMove = 0x0232;

            // 系统缩放开始：安全网钳制挂起——把窗口推到工作区外，位置不得被立刻拉回。
            PostMessage(handle, WmEnterSizeMove, IntPtr.Zero, IntPtr.Zero);
            CompleteLayout(settings);
            settings.Top = SystemParameters.WorkArea.Bottom + 500;
            CompleteLayout(settings);
            Assert.IsTrue(
                settings.Top > SystemParameters.WorkArea.Bottom,
                "WM_SIZEMOVE 期间安全网必须挂起（不与交互缩放打架）。");

            // 系统缩放结束：收口一次——钳回工作区 + 落盘。
            PostMessage(handle, WmExitSizeMove, IntPtr.Zero, IntPtr.Zero);
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            Assert.IsTrue(
                settings.Top <= work.Bottom + 0.01,
                "WM_EXITSIZEMOVE 后必须钳回工作区。");
            Assert.IsNotNull(store.LastSaved, "系统缩放结束必须落盘（EXITSIZEMOVE 路径）。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_MaximizeStaysWithinWorkArea()
    {
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            settings.WindowState = WindowState.Maximized;
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            // WM_GETMINMAXINFO 按所在显示器工作区修正无边框最大化（留 resize 边框容差）。
            Assert.IsTrue(settings.Top >= work.Top - 8, $"最大化上缘不得探出工作区，Top={settings.Top}。");
            Assert.IsTrue(settings.Left >= work.Left - 8, $"最大化左缘不得探出工作区，Left={settings.Left}。");
            Assert.IsTrue(
                settings.Top + settings.ActualHeight <= work.Bottom + 8,
                $"最大化下缘不得探出工作区（含任务栏），Bottom={settings.Top + settings.ActualHeight}。");
            settings.WindowState = WindowState.Normal;
            CompleteLayout(settings);
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_TargetMonitorIsResolvedByRectCenter()
    {
        // 中心选屏接线：解析入参必须是持久化矩形的中心（而非左上角/最大交集）。
        double? resolvedX = null;
        double? resolvedY = null;
        SettingsWindowPlacement.WorkAreaResolverOverride = (x, y) =>
        {
            resolvedX = x;
            resolvedY = y;
            return new SettingsWindowPlacement.WorkArea(-1920, 0, 0, 1040);
        };
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = -300,
            SettingsWindowTop = 100,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.IsNotNull(resolvedX);
            Assert.AreEqual(-300d + 680d / 2, resolvedX!.Value, 0.01, "选屏必须以矩形中心 X 为准。");
            Assert.AreEqual(100d + 600d / 2, resolvedY!.Value, 0.01, "选屏必须以矩形中心 Y 为准。");
        }
        finally
        {
            SettingsWindowPlacement.WorkAreaResolverOverride = null;
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_SmallDisplayWorkArea_ClampsRestoredHeightToMinHeight()
    {
        // 矮工作区（400 − 48 < 460）冻结式抬到最小高：恢复路径 Normalize 把超高
        // 持久化尺寸钳到 460 并把 Top 收回工作区内，禁止 MaxHeight < MinHeight 语义。
        SettingsWindowPlacement.WorkAreaResolverOverride = (_, _) =>
            new SettingsWindowPlacement.WorkArea(0, 0, 1000, 400);
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = 10,
            SettingsWindowTop = 10,
            SettingsWindowWidth = 680,
            SettingsWindowHeight = 600
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.AreEqual(
                460d,
                settings.ActualHeight,
                0.01,
                "矮工作区（400 − 48 < 460）下恢复高度必须收敛到最小高 460。");
            Assert.AreEqual(
                0d,
                settings.Top,
                0.01,
                "钳制后 Top 必须收回工作区内（工作区下缘 − 高度）。");
        }
        finally
        {
            SettingsWindowPlacement.WorkAreaResolverOverride = null;
            CloseAll(window, settings);
        }
    }

    [STATestMethod]
    public void Geometry_DisconnectedMonitor_FallsBackToPrimaryWorkAreaClamp()
    {
        // 显示器断开：查询不可用（null）→ 按契约退主屏工作区；持久化矩形（越出主屏
        // 右缘）被钳进主屏工作区，而不是丢回默认定位。种子相对当前工作区右缘构造，
        // 并注入覆盖幽灵显示器的假虚拟桌面界——否则单屏环境按「完全出所有显示器」
        // 契约正确回落默认定位，构不成钳制分支（CI 1024 宽虚机实测教训）。
        var work = SystemParameters.WorkArea;
        SettingsWindowPlacement.WorkAreaResolverOverride = (_, _) => null;
        SettingsWindowPlacement.VirtualScreenBoundsOverride = () =>
            (0, 0, work.Right + 100 + 900 + 60, work.Height);
        var preferences = AppPreferences.Default with
        {
            SettingsWindowLeft = work.Right + 100,
            SettingsWindowTop = work.Top + 60,
            SettingsWindowWidth = 860,
            SettingsWindowHeight = 520
        };
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            Assert.AreEqual(
                Math.Max(work.Right - 860, work.Left),
                settings.Left,
                0.01,
                "显示器断开后位置应钳进主屏工作区（右缘对齐）而非回落默认定位。");
            Assert.AreEqual(860d, settings.ActualWidth, 0.01, "尺寸合法时应原样保留。");
        }
        finally
        {
            SettingsWindowPlacement.WorkAreaResolverOverride = null;
            SettingsWindowPlacement.VirtualScreenBoundsOverride = null;
            CloseAll(window, settings);
        }
    }

    // ---- 锁定测试：MINMAXINFO 封送与真实消息路径（1.51.0，1.50.0 失守回归）----

    /// <summary>结构体必须与 Win32 MINMAXINFO 严格同构：5×POINT（int×2）＝40 字节，
    /// 偏移 0/8/16/24/32。1.50.0 用 short（每 POINT 4 字节、整体 20 字节）导致
    /// ptMinTrackSize/ptMaxTrackSize 从未被写入、交互缩放上下限完全失守。</summary>
    [TestMethod]
    public void MinMaxInfo_MarshalLayout_MatchesWin32()
    {
        Assert.AreEqual(8, Marshal.SizeOf<SettingsWindowPlacement.Pt>(), "POINT 是两个 32 位 LONG，每点 8 字节。");
        Assert.AreEqual(40, Marshal.SizeOf<SettingsWindowPlacement.MinMaxInfo>(), "MINMAXINFO = 5×POINT = 40 字节。");
        Assert.AreEqual(0, (int)Marshal.OffsetOf<SettingsWindowPlacement.MinMaxInfo>(nameof(SettingsWindowPlacement.MinMaxInfo.PtReserved)));
        Assert.AreEqual(8, (int)Marshal.OffsetOf<SettingsWindowPlacement.MinMaxInfo>(nameof(SettingsWindowPlacement.MinMaxInfo.PtMaxSize)));
        Assert.AreEqual(16, (int)Marshal.OffsetOf<SettingsWindowPlacement.MinMaxInfo>(nameof(SettingsWindowPlacement.MinMaxInfo.PtMaxPosition)));
        Assert.AreEqual(24, (int)Marshal.OffsetOf<SettingsWindowPlacement.MinMaxInfo>(nameof(SettingsWindowPlacement.MinMaxInfo.PtMinTrackSize)));
        Assert.AreEqual(32, (int)Marshal.OffsetOf<SettingsWindowPlacement.MinMaxInfo>(nameof(SettingsWindowPlacement.MinMaxInfo.PtMaxTrackSize)));
    }

    /// <summary>真实写入路径：40 字节清零缓冲 + DPI 1.5 + 假显示器（工作区物理高 1272
    /// → DIP 848 → 冻结最大高 800 → 轨道 1200 物理），各字段落在正确偏移上。</summary>
    [STATestMethod]
    public void TryApplyMaximizeWorkArea_WritesTrackSizesAtNativeOffsets()
    {
        SettingsWindowPlacement.MonitorInfoResolverOverride = _ => new SettingsWindowPlacement.MonitorInfoEx
        {
            RcMonitor = new SettingsWindowPlacement.Rect { Left = 0, Top = 0, Right = 2100, Bottom = 1320 },
            RcWork = new SettingsWindowPlacement.Rect { Left = 0, Top = 0, Right = 2100, Bottom = 1272 }
        };
        var buffer = Marshal.AllocHGlobal(40);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                Marshal.WriteInt32(buffer, i * 4, 0);
            }

            var applied = SettingsWindowPlacement.TryApplyMaximizeWorkArea(
                IntPtr.Zero, buffer,
                SettingsWindowMetrics.MinWidth, SettingsWindowMetrics.MinHeight, SettingsWindowMetrics.MaxWidth,
                1.5, 1.5,
                isSystemMaximizing: false);
            Assert.IsTrue(applied, "缝合显示器可用时必须返回 true。");
            Assert.AreEqual(0, Marshal.ReadInt32(buffer, 0), "ptReserved 保持入参原样（清零缓冲=0）。");
            Assert.AreEqual(2100, Marshal.ReadInt32(buffer, 8), "ptMaxSize.X = 工作区宽。");
            Assert.AreEqual(1272, Marshal.ReadInt32(buffer, 12), "ptMaxSize.Y = 工作区高。");
            Assert.AreEqual(0, Marshal.ReadInt32(buffer, 16), "ptMaxPosition.X = RcWork − RcMonitor（任务栏在底为 0）。");
            Assert.AreEqual(0, Marshal.ReadInt32(buffer, 20), "ptMaxPosition.Y。");
            Assert.AreEqual(840, Marshal.ReadInt32(buffer, 24), "ptMinTrackSize.X = 560 × 1.5。");
            Assert.AreEqual(690, Marshal.ReadInt32(buffer, 28), "ptMinTrackSize.Y = 460 × 1.5。");
            Assert.AreEqual(1350, Marshal.ReadInt32(buffer, 32), "ptMaxTrackSize.X = 900 × 1.5。");
            Assert.AreEqual(1200, Marshal.ReadInt32(buffer, 36), "ptMaxTrackSize.Y = 工作区 1272 − 48×1.5（最大高 800 DIP）。");
        }
        finally
        {
            SettingsWindowPlacement.MonitorInfoResolverOverride = null;
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>真实 HWND 全链路：已显示的设置窗口收到 WM_GETMINMAXINFO 后，缓冲区
    /// 的轨道尺寸必须等于冻结值 × 窗口 DPI（±2 物理 px，覆盖跨 DPI 取整）。缝合点注入
    /// 与恢复路径同坐标系的假显示器（DIP×窗口实际 DPI），使期望值不依赖真实显示器。
    /// </summary>
    [STATestMethod]
    public void SettingsWindow_RealHwndMinMaxInfo_EnforcesTrackSizes()
    {
        UseFakeMonitorDip((0, 0, 1600, 1040));
        var (window, settings) = OpenSettings();
        var buffer = Marshal.AllocHGlobal(40);
        try
        {
            CompleteLayout(settings);
            var handle = new System.Windows.Interop.WindowInteropHelper(settings).Handle;
            Assert.IsTrue(handle != IntPtr.Zero);
            for (var i = 0; i < 10; i++)
            {
                Marshal.WriteInt32(buffer, i * 4, 0);
            }

            SendMessage(handle, WmGetMinMaxInfo, IntPtr.Zero, buffer);
            var dpi = GetDpiForWindow(handle) / 96d;
            Assert.AreEqual((int)Math.Round(560 * dpi), Marshal.ReadInt32(buffer, 24), 2, "真实消息路径的最小轨道宽必须 = 560 DIP × DPI。");
            Assert.AreEqual((int)Math.Round(460 * dpi), Marshal.ReadInt32(buffer, 28), 2, "真实消息路径的最小轨道高必须 = 460 DIP × DPI。");
            Assert.AreEqual((int)Math.Round(900 * dpi), Marshal.ReadInt32(buffer, 32), 2, "真实消息路径的最大轨道宽必须 = 900 DIP × DPI。");
            Assert.AreEqual((int)Math.Round(992 * dpi), Marshal.ReadInt32(buffer, 36), 2, "真实消息路径的最大轨道高必须 = 工作区 1040 − 48 边距。");
        }
        finally
        {
            SettingsWindowPlacement.MonitorInfoResolverOverride = null;
            Marshal.FreeHGlobal(buffer);
            CloseAll(window, settings);
        }
    }

    /// <summary>最大化意图（WM_SYSCOMMAND SC_MAXIMIZE）下轨道上限让位给工作区：
    /// 轨道尺寸是窗口管理器对一切尺寸变更（含最大化）的钳制边界，若不让位，
    /// 最大化会被 900/工作区−48 重新钳出死区。</summary>
    [STATestMethod]
    public void TryApplyMaximizeWorkArea_SystemMaximizing_TrackYieldsToWorkArea()
    {
        SettingsWindowPlacement.MonitorInfoResolverOverride = _ => new SettingsWindowPlacement.MonitorInfoEx
        {
            RcMonitor = new SettingsWindowPlacement.Rect { Left = 0, Top = 0, Right = 2400, Bottom = 1560 },
            RcWork = new SettingsWindowPlacement.Rect { Left = 0, Top = 0, Right = 2400, Bottom = 1560 }
        };
        var buffer = Marshal.AllocHGlobal(40);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                Marshal.WriteInt32(buffer, i * 4, 0);
            }

            var applied = SettingsWindowPlacement.TryApplyMaximizeWorkArea(
                IntPtr.Zero, buffer,
                SettingsWindowMetrics.MinWidth, SettingsWindowMetrics.MinHeight, SettingsWindowMetrics.MaxWidth,
                1.5, 1.5,
                isSystemMaximizing: true);
            Assert.IsTrue(applied);
            Assert.AreEqual(2400, Marshal.ReadInt32(buffer, 8), "ptMaxSize.X = 工作区宽。");
            Assert.AreEqual(1560, Marshal.ReadInt32(buffer, 12), "ptMaxSize.Y = 工作区高。");
            Assert.AreEqual(2400, Marshal.ReadInt32(buffer, 32), "最大化意图下最大轨道宽必须让位给工作区宽（≥ 900×1.5）。");
            Assert.AreEqual(1560, Marshal.ReadInt32(buffer, 36), "最大化意图下最大轨道高必须让位给工作区高（≥ 工作区−48）。");
        }
        finally
        {
            SettingsWindowPlacement.MonitorInfoResolverOverride = null;
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>大有限值不被 short 窄化（MINMAXINFO 字段是 32 位 LONG）：
    /// 40000 DIP × 1.5 = 60000 物理，1.50.0 会窄化到 short.MaxValue=32767。</summary>
    [STATestMethod]
    public void TryApplyMaximizeWorkArea_LargeMaxWidth_NotNarrowedByShort()
    {
        SettingsWindowPlacement.MonitorInfoResolverOverride = _ => new SettingsWindowPlacement.MonitorInfoEx
        {
            RcMonitor = new SettingsWindowPlacement.Rect { Left = 0, Top = 0, Right = 64000, Bottom = 64000 },
            RcWork = new SettingsWindowPlacement.Rect { Left = 0, Top = 0, Right = 64000, Bottom = 64000 }
        };
        var buffer = Marshal.AllocHGlobal(40);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                Marshal.WriteInt32(buffer, i * 4, 0);
            }

            var applied = SettingsWindowPlacement.TryApplyMaximizeWorkArea(
                IntPtr.Zero, buffer, 560, 460, 40000, 1.5, 1.5,
                isSystemMaximizing: false);
            Assert.IsTrue(applied);
            Assert.AreEqual(60000, Marshal.ReadInt32(buffer, 32), "最大轨道宽必须保留 32 位精度，不得窄化为 short。");
        }
        finally
        {
            SettingsWindowPlacement.MonitorInfoResolverOverride = null;
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>PtMaxPosition 符号语义（1.50.0 方向写反）：它＝工作区原点 − 显示器
    /// 原点，任务栏在左/上时为正； maximizing 位置 = 显示器原点 + 该偏移。</summary>
    [STATestMethod]
    public void TryApplyMaximizeWorkArea_TaskbarLeftOrTop_PtMaxPositionSign()
    {
        var buffer = Marshal.AllocHGlobal(40);
        try
        {
            // 任务栏在左：RcWork 相对 RcMonitor 右移 48。
            SettingsWindowPlacement.MonitorInfoResolverOverride = _ => new SettingsWindowPlacement.MonitorInfoEx
            {
                RcMonitor = new SettingsWindowPlacement.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 },
                RcWork = new SettingsWindowPlacement.Rect { Left = 48, Top = 0, Right = 1872, Bottom = 1040 }
            };
            var applied = SettingsWindowPlacement.TryApplyMaximizeWorkArea(
                IntPtr.Zero, buffer, 560, 460, 900, 1.0, 1.0,
                isSystemMaximizing: false);
            Assert.IsTrue(applied);
            Assert.AreEqual(48, Marshal.ReadInt32(buffer, 16), "任务栏在左时 ptMaxPosition.X 必须为 +48。");
            Assert.AreEqual(0, Marshal.ReadInt32(buffer, 20));
            Assert.AreEqual(1824, Marshal.ReadInt32(buffer, 8), "ptMaxSize.X = 工作区宽。");
            Assert.AreEqual(1040, Marshal.ReadInt32(buffer, 12));

            // 任务栏在上：RcWork 相对 RcMonitor 下移 48，工作区底缘仍到显示器底缘。
            SettingsWindowPlacement.MonitorInfoResolverOverride = _ => new SettingsWindowPlacement.MonitorInfoEx
            {
                RcMonitor = new SettingsWindowPlacement.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 },
                RcWork = new SettingsWindowPlacement.Rect { Left = 0, Top = 48, Right = 1920, Bottom = 1040 }
            };
            applied = SettingsWindowPlacement.TryApplyMaximizeWorkArea(
                IntPtr.Zero, buffer, 560, 460, 900, 1.0, 1.0,
                isSystemMaximizing: false);
            Assert.IsTrue(applied);
            Assert.AreEqual(0, Marshal.ReadInt32(buffer, 16));
            Assert.AreEqual(48, Marshal.ReadInt32(buffer, 20), "任务栏在上时 ptMaxPosition.Y 必须为 +48。");
            Assert.AreEqual(992, Marshal.ReadInt32(buffer, 12), "ptMaxSize.Y = 工作区高。");
        }
        finally
        {
            SettingsWindowPlacement.MonitorInfoResolverOverride = null;
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>非有限输入策略：任一入参 NaN/±∞（或 DPI ≤ 0）→ 返回 false 交回
    /// WPF 默认处理，且缓冲区保持原样（不被部分写入）。</summary>
    [STATestMethod]
    public void TryApplyMaximizeWorkArea_NonFiniteInput_FallsBackToWpfDefault()
    {
        var buffer = Marshal.AllocHGlobal(40);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                Marshal.WriteInt32(buffer, i * 4, 0);
            }

            var cases = new (string Name, double MinW, double MinH, double MaxW, double ScaleX, double ScaleY)[]
            {
                ("NaN maxWidth", 560, 460, double.NaN, 1.0, 1.0),
                ("+∞ minWidth", double.PositiveInfinity, 460, 900, 1.0, 1.0),
                ("−∞ minHeight", 560, double.NegativeInfinity, 900, 1.0, 1.0),
                ("NaN dpiX", 560, 460, 900, double.NaN, 1.0),
                ("zero dpiY", 560, 460, 900, 1.0, 0.0)
            };
            foreach (var (name, minW, minH, maxW, scaleX, scaleY) in cases)
            {
                var applied = SettingsWindowPlacement.TryApplyMaximizeWorkArea(
                    IntPtr.Zero, buffer, minW, minH, maxW, scaleX, scaleY,
                    isSystemMaximizing: false);
                Assert.IsFalse(applied, $"{name} 必须返回 false。");
                for (var i = 0; i < 10; i++)
                {
                    Assert.AreEqual(0, Marshal.ReadInt32(buffer, i * 4), $"{name} 失败时缓冲区必须保持原样。");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>布局语义（根因层）：最大化（HWND 超过 900）时壳层必须铺满整个
    /// HWND（无死区），内容列以 720 上限水平居中——1.50.0 的 Window.MaxWidth/
    /// MaxHeight 会把根视觉钳在 900/工作区−48，右侧与底部成死背景。</summary>
    [STATestMethod]
    public void Geometry_MaximizedShellSpansWindowAndContentStaysBounded()
    {
        // 假显示器工作区 1600×1040 DIP（经缝合点注入，物理值按窗口实际 DPI 换算）：
        // 最大化尺寸 = 工作区整幅，必然超过旧 900 上限，且不受本机真实显示器影响。
        UseFakeMonitorDip((0, 0, 1600, 1040));
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            settings.WindowState = WindowState.Maximized;
            CompleteLayout(settings);
            Assert.IsTrue(settings.ActualWidth > 900.01, $"前提：最大化宽度必须超过旧 900 上限，实际 {settings.ActualWidth}。");
            Assert.AreEqual(1600d, settings.ActualWidth, 0.5, "最大化尺寸必须是假显示器工作区整幅宽（轨道让位）。");
            Assert.AreEqual(1040d, settings.ActualHeight, 0.5, "最大化尺寸必须是假显示器工作区整幅高。");

            var shell = (System.Windows.Controls.Border)settings.FindName("WindowShell");
            Assert.IsNotNull(shell);
            Assert.AreEqual(settings.ActualWidth, shell.ActualWidth, 0.01, "壳层宽度必须铺满 HWND（不得被 900 钳出死区）。");
            Assert.AreEqual(settings.ActualHeight, shell.ActualHeight, 0.01, "壳层高度必须铺满 HWND（不得被工作区−48 钳出底部死区）。");

            var content = (Grid)settings.FindName("SettingsContentHost");
            Assert.IsNotNull(content);
            Assert.IsTrue(
                content.ActualWidth <= SettingsWindowMetrics.ContentColumnMaxWidth + 0.01,
                $"内容列必须有界（≤ 720 DIP），实际 {content.ActualWidth}。");
            var origin = content.TransformToVisual(settings).Transform(new Point(0, 0));
            // 元素原点在自身边距盒内侧（左缘 + 16 水平边距）。
            var expectedLeft = 148 + (settings.ActualWidth - 148 - (content.ActualWidth + 32)) / 2 + 16;
            Assert.AreEqual(
                expectedLeft,
                origin.X,
                0.5,
                "内容列（含水平边距的盒）必须在导航轨右侧水平居中。");

            settings.WindowState = WindowState.Normal;
            CompleteLayout(settings);
        }
        finally
        {
            SettingsWindowPlacement.MonitorInfoResolverOverride = null;
            CloseAll(window, settings);
        }
    }

    /// <summary>交互缩放与最大化三态证据（契约 #24，2026-10-19 根因修复的视觉留档）：
    /// 环境变量 FTS_GEOMETRY_EVIDENCE_DIR 门控（未设置时跳过），产出 560 下限、
    /// 900 上限与最大化内容居中三张壳层截图。</summary>
    [STATestMethod]
    public void Geometry_Evidence_CaptureResizeBoundsAndMaximizedStates()
    {
        var directory = Environment.GetEnvironmentVariable("FTS_GEOMETRY_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            Assert.Inconclusive("未设置 FTS_GEOMETRY_EVIDENCE_DIR，跳过几何证据产出。");
        }

        UseFakeMonitorDip((0, 0, 1600, 1040));
        var (window, settings) = OpenSettings();
        try
        {
            CompleteLayout(settings);
            settings.Width = SettingsWindowMetrics.MinWidth;
            settings.Height = SettingsWindowMetrics.MinHeight;
            CompleteLayout(settings);
            SaveGeometryEvidence(settings, directory!, "settings-interactive-min-560.png");

            settings.Width = SettingsWindowMetrics.MaxWidth;
            settings.Height = 800;
            CompleteLayout(settings);
            SaveGeometryEvidence(settings, directory!, "settings-interactive-max-900.png");

            settings.WindowState = WindowState.Maximized;
            CompleteLayout(settings);
            SaveGeometryEvidence(settings, directory!, "settings-maximized-content-centered.png");

            settings.WindowState = WindowState.Normal;
            CompleteLayout(settings);
        }
        finally
        {
            SettingsWindowPlacement.MonitorInfoResolverOverride = null;
            CloseAll(window, settings);
        }
    }

    private static void SaveGeometryEvidence(SettingsWindow settings, string directory, string fileName)
    {
        var shell = (System.Windows.Controls.Border)settings.FindName("WindowShell");
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(shell.ActualWidth),
            (int)Math.Ceiling(shell.ActualHeight),
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private static void AssertDefaultPlacement(AppPreferences preferences, string message)
    {
        var (window, settings) = OpenSettings(preferences);
        try
        {
            CompleteLayout(settings);
            var work = SystemParameters.WorkArea;
            var expectedLeft = Math.Max(work.Left, Math.Min(window.Left - settings.Width - 12, work.Right - settings.Width));
            var expectedTop = Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - settings.ActualHeight));
            Assert.AreEqual(expectedLeft, settings.Left, 0.01, message);
            Assert.AreEqual(expectedTop, settings.Top, 0.01, message);
            Assert.AreEqual(680d, settings.ActualWidth, 0.01, "回落默认定位时尺寸应为冻结默认值。");
        }
        finally
        {
            CloseAll(window, settings);
        }
    }

    private static void InvokeCommitPlacement(SettingsWindow settings)
    {
        var method = typeof(SettingsWindow).GetMethod(
            "CommitPlacement",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "CommitPlacement 是落盘唯一入口。");
        method.Invoke(settings, null);
    }

    private const int WmGetMinMaxInfo = 0x0024;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>
    /// 把 DIP 假显示器按窗口实际 DPI 换算成物理矩形注入消息路径缝合点
    /// （MonitorInfoResolverOverride），与 WorkAreaResolverOverride 的 DIP 假工作区
    /// 保持同一坐标系——否则真实显示器几何（本机左侧副屏等）会混入断言。
    /// </summary>
    private static void UseFakeMonitorDip((double Left, double Top, double Right, double Bottom) monitorDip)
    {
        SettingsWindowPlacement.MonitorInfoResolverOverride = hwnd =>
        {
            var dpi = GetDpiForWindow(hwnd) / 96d;
            int Scale(double dip) => (int)Math.Round(dip * dpi);
            return new SettingsWindowPlacement.MonitorInfoEx
            {
                CbSize = Marshal.SizeOf<SettingsWindowPlacement.MonitorInfoEx>(),
                RcMonitor = new SettingsWindowPlacement.Rect
                {
                    Left = Scale(monitorDip.Left),
                    Top = Scale(monitorDip.Top),
                    Right = Scale(monitorDip.Right),
                    Bottom = Scale(monitorDip.Bottom)
                },
                RcWork = new SettingsWindowPlacement.Rect
                {
                    Left = Scale(monitorDip.Left),
                    Top = Scale(monitorDip.Top),
                    Right = Scale(monitorDip.Right),
                    Bottom = Scale(monitorDip.Bottom)
                }
            };
        };
    }

    private static RecordingPreferencesStore GetPreferencesStore(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_preferencesStore",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, "MainWindow._preferencesStore 应存在。");
        return (RecordingPreferencesStore)field.GetValue(window)!;
    }

    // ---- 基建（按本测试套既有各文件自持 helper 的模式复制）----

    private static MainWindow CreateWindow(
        TestDirectory directory,
        RecordingPreferencesStore preferencesStore,
        FakeStartupManager startupManager,
        AppPreferences? preferences = null)
    {
        var store = new RecordingSettingsBoardStore(directory.Root);
        var board = new BoardService();
        var normalizer = new ImageNormalizer(store.ImagesDirectory);
        var operationGate = new BoardOperationGate();
        MainWindow? window = null;
        void ShowStatus(string message) => window?.ShowStatus(message);
        var clipboard = new ClipboardCaptureService(
            new IdleClipboardReader(),
            normalizer,
            board,
            store,
            ShowStatus,
            operationGate: operationGate);
        window = new MainWindow(
            board,
            store,
            WindowSettings.Default,
            clipboard,
            new BoardMutationService(board, store, ShowStatus, operationGate),
            new DragPayloadService(),
            new ExternalDropPayloadReader(new WindowsDataImageReader()),
            new ExternalDropImportService(
                normalizer,
                board,
                store,
                ShowStatus,
                operationGate),
            preferences: preferences,
            preferencesStore: preferencesStore,
            startupManager: startupManager,
            dataDirectory: directory.Root);
        return window;
    }

    private static (MainWindow Window, SettingsWindow Settings) OpenSettings(AppPreferences? preferences = null)
    {
        var directory = new TestDirectory();
        var store = new RecordingPreferencesStore();
        var startup = new FakeStartupManager();
        var window = CreateWindow(directory, store, startup, preferences);
        window.Show();
        var gear = window.FindName("SettingsButton") as Button;
        Assert.IsNotNull(gear);
        gear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var field = typeof(MainWindow).GetField(
            "_settingsWindow",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var settings = (SettingsWindow?)field.GetValue(window);
        Assert.IsNotNull(settings);
        return (window, settings);
    }

    private static void CompleteLayout(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void CloseAll(MainWindow window, SettingsWindow settings)
    {
        settings.Close();
        window.Close();
    }

    private sealed class IdleClipboardReader : IClipboardReader
    {
        public Task<ClipboardSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipboardSnapshot(0, null, [], null));
    }

    private sealed class RecordingPreferencesStore : IPreferencesStore
    {
        public AppPreferences? LastSaved { get; private set; }
        public AppPreferences Seeded { get; set; } = AppPreferences.Default;

        public Task<AppPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Seeded);

        public Task SavePreferencesAsync(
            AppPreferences preferences,
            CancellationToken cancellationToken = default)
        {
            LastSaved = preferences;
            return Task.CompletedTask;
        }

        public void Reset() => LastSaved = null;
    }

    private sealed class FakeStartupManager : IStartupManager
    {
        public bool Enabled { get; set; }

        public bool IsEnabled() => Enabled;

        public void Enable() => Enabled = true;

        public void Disable() => Enabled = false;
    }

    private sealed class RecordingSettingsBoardStore(string root) : IBoardStore
    {
        public string ImagesDirectory { get; } = Path.Combine(root, "images");

        public Task<BoardSnapshot> LoadBoardAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new BoardSnapshot());

        public Task SaveBoardAsync(BoardSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<WindowSettings> LoadSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(WindowSettings.Default);

        public Task SaveSettingsAsync(WindowSettings settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool TryDeleteImage(string? absolutePath) => true;
    }
}
