using FloatingTransferStation.Services;

namespace FloatingTransferStation.Core.Tests;

[TestClass]
public sealed class ProbeLaunchersFile
{
    [TestMethod]
    public async Task PROBE_DeserializePreviewFile()
    {
        var path = Path.Combine(
            Environment.GetEnvironmentVariable("LOCALAPPDATA")!,
            "Temp", "fts-p6shot", "data", "launchers.json");
        var store = new LocalStore(AppPaths.ForTests(Path.GetDirectoryName(path)!), new AtomicTextWriter());
        var data = await ((LocalStore)store).LoadAsync();
        Assert.Fail($"PROBE version={data.Version} count={data.Items.Count} targets={string.Join("|", data.Items.Select(i => i.TargetPath))}");
    }
}
