using System.Diagnostics;

namespace FloatingTransferStation.Tests;

public sealed partial class LifecycleTests
{
    [TestMethod]
    [TestCategory("Adversarial")]
    [DataRow("## 未发布\n\n## 1.4.1\nReleased entry", 0)]
    [DataRow("## 未发布\n\n- A change for the next release\n\n## 1.4.1\nReleased entry", 1)]
    [DataRow("## 1.4.1\nReleased entry", 1)]
    [DataRow("## 未发布\n\n## 未发布\n\n## 1.4.1\nReleased entry", 1)]
    public async Task ReleaseReadiness_OnlyAcceptsOneEmptyUnreleasedSection(
        string changelog,
        int expectedExitCode)
    {
        using var directory = new TestDirectory();
        var changelogPath = Path.Combine(directory.Root, "CHANGELOG.md");
        await File.WriteAllTextAsync(changelogPath, changelog);
        var scriptPath = Path.Combine(FindRepositoryRoot(), "scripts", "test-release-readiness.ps1");
        Assert.IsTrue(File.Exists(scriptPath), "An explicit release-readiness check must exist.");

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", scriptPath, "-ChangelogPath", changelogPath,
        })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(expectedExitCode, process.ExitCode, await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
