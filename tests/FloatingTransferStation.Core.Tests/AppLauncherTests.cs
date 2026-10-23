using System.ComponentModel;
using System.Diagnostics;
using FloatingTransferStation.Models;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// 启动器编排（#30，Issue #162）：假启动器成功/失败折叠/参数与工作目录、
/// 文件目标 File.Exists 预检、Win32 错误码中文映射、AUMID 激活与限定回退类别
/// （回退=已派发不显成功；未注册/拒绝/取消明确失败不回退）。
/// </summary>
[TestClass]
public sealed class AppLauncherTests
{
    private sealed class RecordingStarter
    {
        public List<ProcessStartInfo> Started { get; } = [];
        public Func<ProcessStartInfo, bool> Respond { get; set; } = _ => true;

        public bool Invoke(ProcessStartInfo info)
        {
            Started.Add(info);
            return Respond(info);
        }
    }

    private static LauncherItem App(
        string target = @"C:\Apps\Tool.exe",
        string? arguments = null,
        string? workingDirectory = null) =>
        new(
            Guid.NewGuid(),
            LauncherKind.Application,
            target,
            "Tool",
            DateTimeOffset.UtcNow,
            arguments,
            workingDirectory);

    private sealed class TempAppFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"fts-launch-{Guid.NewGuid():N}.exe");

        public TempAppFile() => File.WriteAllText(Path, "stub");

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }

    [TestMethod]
    public void Application_StartsWithShellExecuteAndDefaultWorkingDirectory()
    {
        using var file = new TempAppFile();
        var starter = new RecordingStarter();
        var launcher = new AppLauncher(starter.Invoke);

        var result = launcher.Launch(App(file.Path, "--flag"));

        Assert.AreEqual(LauncherLaunchStatus.Launched, result.Status);
        var started = starter.Started.Single();
        Assert.IsTrue(started.UseShellExecute, "exe/.lnk 一律 UseShellExecute 启动目标本体。");
        Assert.AreEqual("--flag", started.Arguments);
        Assert.AreEqual(
            System.IO.Path.GetDirectoryName(file.Path),
            started.WorkingDirectory,
            "工作目录空 = 目标所在目录。");
    }

    [TestMethod]
    public void Application_ExplicitWorkingDirectoryWins()
    {
        using var file = new TempAppFile();
        var starter = new RecordingStarter();
        var launcher = new AppLauncher(starter.Invoke);

        launcher.Launch(App(file.Path, workingDirectory: @"C:\Work"));

        Assert.AreEqual(@"C:\Work", starter.Started.Single().WorkingDirectory);
    }

    [TestMethod]
    public void Application_MissingTarget_FailsBeforeStarter()
    {
        var starter = new RecordingStarter();
        var launcher = new AppLauncher(starter.Invoke);

        var result = launcher.Launch(App(@"C:\Missing\None.exe"));

        Assert.AreEqual(LauncherLaunchStatus.Failed, result.Status);
        Assert.AreEqual("目标不存在。", result.FailureMessage);
        Assert.AreEqual(0, starter.Started.Count, "文件类目标点击前 File.Exists 预检。");
    }

    [TestMethod]
    public void Application_StarterReturnsFalse_FoldsToFailure()
    {
        using var file = new TempAppFile();
        var starter = new RecordingStarter { Respond = _ => false };
        var launcher = new AppLauncher(starter.Invoke);

        var result = launcher.Launch(App(file.Path));

        Assert.AreEqual(LauncherLaunchStatus.Failed, result.Status);
        Assert.IsNotNull(result.FailureMessage);
    }

    [TestMethod]
    public void Application_Win32Errors_MapToChineseMessages()
    {
        using var file = new TempAppFile();
        var expectations = new Dictionary<int, string>
        {
            [2] = "目标不存在。",
            [3] = "路径不存在。",
            [5] = "拒绝访问。",
            [740] = "需要管理员权限。",
            [1223] = "已取消授权。"
        };
        foreach (var (code, message) in expectations)
        {
            var starter = new RecordingStarter
            {
                Respond = _ => throw new Win32Exception(code)
            };
            var launcher = new AppLauncher(starter.Invoke);

            var result = launcher.Launch(App(file.Path));

            Assert.AreEqual(LauncherLaunchStatus.Failed, result.Status, $"Win32 错误 {code}。");
            Assert.AreEqual(message, result.FailureMessage, $"Win32 错误 {code} 的中文映射。");
        }
    }

    [TestMethod]
    public void Url_LaunchesWithoutFilePrecheck()
    {
        var starter = new RecordingStarter();
        var launcher = new AppLauncher(starter.Invoke);
        var item = new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.Url,
            "https://example.invalid/",
            "网页",
            DateTimeOffset.UtcNow);

        var result = launcher.Launch(item);

        Assert.AreEqual(LauncherLaunchStatus.Launched, result.Status);
        Assert.AreEqual(1, starter.Started.Count, "Url 条目不进文件路径预检。");
    }

    [TestMethod]
    public void PackagedApp_ActivatedReportsLaunched()
    {
        var launcher = new AppLauncher(
            _ => true,
            (_, _) => PackagedAppActivationResult.Success(4242));

        var result = launcher.Launch(new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.PackagedApp,
            "App!Publisher",
            "打包应用",
            DateTimeOffset.UtcNow));

        Assert.AreEqual(LauncherLaunchStatus.Launched, result.Status);
    }

    [TestMethod]
    public void PackagedApp_NotRegistered_FailsExplicitlyAndSuggestsReenumeration()
    {
        var launcher = new AppLauncher(
            _ => true,
            (_, _) => PackagedAppActivationResult.Failure(unchecked((int)0x80270254)));

        var result = launcher.Launch(new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.PackagedApp,
            "App!Publisher",
            "打包应用",
            DateTimeOffset.UtcNow));

        Assert.AreEqual(LauncherLaunchStatus.Failed, result.Status, "未注册属明确失败，不回退。");
        Assert.IsTrue(result.SuggestReenumerate, "未注册触发已安装应用重枚举。");
        StringAssert.Contains(result.FailureMessage, "应用未注册");
    }

    [TestMethod]
    public void PackagedApp_LimitedFallbackCategory_DispatchesViaExplorerWithoutSuccess()
    {
        var starter = new RecordingStarter();
        var launcher = new AppLauncher(
            starter.Invoke,
            (_, _) => PackagedAppActivationResult.Failure(unchecked((int)0x80004005)));
        var aumid = "App!Publisher";

        var result = launcher.Launch(new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.PackagedApp,
            aumid,
            "打包应用",
            DateTimeOffset.UtcNow));

        Assert.AreEqual(LauncherLaunchStatus.Dispatched, result.Status, "限定回退=请求已派发、结果未知，不显成功。");
        var dispatched = starter.Started.Single();
        Assert.AreEqual("explorer.exe", dispatched.FileName);
        Assert.AreEqual($"shell:AppsFolder\\{aumid}", dispatched.Arguments);
    }

    [TestMethod]
    public void PackagedApp_ExplicitFailureCategories_DoNotFallBack()
    {
        var starter = new RecordingStarter();
        var explicitCodes = new[]
        {
            unchecked((int)0x80070005), // 拒绝访问
            unchecked((int)0x800704C7), // 已取消授权
            unchecked((int)0x80070490)  // 未注册
        };
        foreach (var code in explicitCodes)
        {
            var launcher = new AppLauncher(
                starter.Invoke,
                (_, _) => PackagedAppActivationResult.Failure(code));

            var result = launcher.Launch(new LauncherItem(
                Guid.NewGuid(),
                LauncherKind.PackagedApp,
                "App!Publisher",
                "打包应用",
                DateTimeOffset.UtcNow));

            Assert.AreEqual(LauncherLaunchStatus.Failed, result.Status, $"错误 0x{code:X8} 属明确失败。");
        }

        Assert.AreEqual(0, starter.Started.Count, "明确失败绝不回退 explorer 派发。");
    }

    // ---- 无默认浏览器关联映射（#30 修订 D7，Issue #179）：SE_ERR_NOASSOC(31)/
    // ERROR_NO_ASSOCIATION(1155) → 中文文案（融合进既有映射表，惠及各类 ShellExecute 失败）----

    [TestMethod]
    public void DescribeError_NoAssociation_MapsToNoOpeningAppMessage()
    {
        Assert.AreEqual("没有找到能打开该地址的应用。", AppLauncher.DescribeError(31), "SE_ERR_NOASSOC。");
        Assert.AreEqual("没有找到能打开该地址的应用。", AppLauncher.DescribeError(1155), "ERROR_NO_ASSOCIATION。");
    }

    [TestMethod]
    public void Url_Win32NoAssociation_FailsWithMappedChineseMessage()
    {
        var starter = new RecordingStarter { Respond = _ => throw new Win32Exception(1155) };
        var launcher = new AppLauncher(starter.Invoke);
        var item = new LauncherItem(
            Guid.NewGuid(), LauncherKind.Url, "https://example.invalid/", "网页", DateTimeOffset.UtcNow);

        var result = launcher.Launch(item);

        Assert.AreEqual(LauncherLaunchStatus.Failed, result.Status);
        Assert.AreEqual("没有找到能打开该地址的应用。", result.FailureMessage, "无关联失败不再落十六进制兜底文案。");
    }

    // ---- 系统位置（#30 扩展）：UseShellExecute 直发注册表 CLSID 解析名；失败限定
    // 回退注册表 explorer shell: 别名（回退=已派发不显成功）；非注册表目标不回退。----

    [TestMethod]
    public void ShellLocation_LaunchesShellExecuteOnRegistryClsidPath()
    {
        foreach (var location in SystemLauncherLocations.All)
        {
            var starter = new RecordingStarter();
            var launcher = new AppLauncher(starter.Invoke);

            var result = launcher.Launch(location.CreateItem());

            Assert.AreEqual(LauncherLaunchStatus.Launched, result.Status, $"{location.DisplayName}。");
            var started = starter.Started.Single();
            Assert.IsTrue(started.UseShellExecute, "CLSID 解析名走 UseShellExecute 直接派发。");
            Assert.AreEqual(location.TargetPath, started.FileName, "启动目标=注册表 CLSID 解析名。");
            Assert.AreEqual(string.Empty, started.Arguments, "系统位置条目无参数段。");
            Assert.AreEqual(string.Empty, started.WorkingDirectory, "不设工作目录（非文件目标）。");
        }
    }

    [TestMethod]
    public void ShellLocation_DirectFailure_DispatchesViaRegistryExplorerAlias()
    {
        foreach (var location in SystemLauncherLocations.All)
        {
            var starter = new RecordingStarter
            {
                // 直发（CLSID）失败、explorer 回退成功——回退后状态=Dispatched。
                Respond = info => info.FileName == "explorer.exe"
            };
            var launcher = new AppLauncher(starter.Invoke);

            var result = launcher.Launch(location.CreateItem());

            Assert.AreEqual(LauncherLaunchStatus.Dispatched, result.Status, $"{location.DisplayName} 回退=已派发不显成功。");
            Assert.AreEqual(2, starter.Started.Count, "直发失败后恰一次 explorer 回退。");
            var dispatched = starter.Started[1];
            Assert.AreEqual("explorer.exe", dispatched.FileName);
            Assert.AreEqual(location.ExplorerFallbackArgument, dispatched.Arguments, "回退参数=注册表别名。");
        }
    }

    [TestMethod]
    public void ShellLocation_UnknownTarget_FailsWithoutFallback()
    {
        var starter = new RecordingStarter { Respond = _ => false };
        var launcher = new AppLauncher(starter.Invoke);
        var item = new LauncherItem(
            Guid.NewGuid(),
            LauncherKind.ShellLocation,
            "::{00000000-0000-0000-0000-000000000000}",
            "未知位置",
            DateTimeOffset.UtcNow);

        var result = launcher.Launch(item);

        Assert.AreEqual(LauncherLaunchStatus.Failed, result.Status, "非注册表目标不回退。");
        Assert.AreEqual(1, starter.Started.Count, "绝不派发 explorer。");
    }
}
