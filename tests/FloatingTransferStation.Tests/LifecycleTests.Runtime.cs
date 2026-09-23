using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

public sealed partial class LifecycleTests
{
    [TestMethod]
    [TestCategory("Adversarial")]
    public void Application_ConfinesRegistryWritesToTheUserTriggeredStartupToggle()
    {
        using var assembly = File.OpenRead(typeof(AppLifecycleService).Assembly.Location);
        using var executable = new PEReader(assembly);
        var metadata = executable.GetMetadataReader();
        var registryWrites = metadata.MemberReferences
            .Select(metadata.GetMemberReference)
            .Where(member => member.Parent.Kind == HandleKind.TypeReference)
            .Where(member =>
            {
                var declaringType = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                return metadata.GetString(declaringType.Namespace) == "Microsoft.Win32" &&
                       metadata.GetString(declaringType.Name) is "Registry" or "RegistryKey" &&
                       metadata.GetString(member.Name) is "SetValue" or "CreateSubKey" or "DeleteValue" or "DeleteSubKey" or "DeleteSubKeyTree";
            })
            .Select(member => metadata.GetString(member.Name))
            .ToArray();

        // 1.8.0 起唯一的例外是设置界面的开机自启开关（WindowsStartupManager，仅用户显式切换时写）；
        // 启动路径、剪贴板采集与安装登记仍然不得触碰注册表写入。新增任何写入 API 都会让这里失败。
        CollectionAssert.AreEquivalent(
            new[] { "CreateSubKey", "SetValue", "DeleteValue" },
            registryWrites,
            "Registry writes must stay confined to the user-triggered startup toggle (WindowsStartupManager).");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void SingleInstance_SecondGuardFailsUntilFirstIsDisposed()
    {
        var name = $@"Local\FloatingTransferStation.Tests.{Guid.NewGuid():N}";

        Assert.IsTrue(SingleInstanceGuard.TryAcquire(name, out var first));
        Assert.IsFalse(SingleInstanceGuard.TryAcquire(name, out var second));
        Assert.IsNull(second);
        first!.Dispose();

        Assert.IsTrue(SingleInstanceGuard.TryAcquire(name, out var third));
        third!.Dispose();
    }

    [TestMethod]
    public void AppLifecycle_OwnsSingleInstance()
    {
        using var lifecycle = new AppLifecycleService();
        var mutexName = $@"Local\FloatingTransferStation.Tests.{Guid.NewGuid():N}";

        Assert.IsTrue(lifecycle.TryStart(mutexName));
        Assert.IsFalse(SingleInstanceGuard.TryAcquire(mutexName, out var competing));
        Assert.IsNull(competing);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void AppLifecycle_RepeatedStartKeepsOwnershipUntilDisposed()
    {
        var mutexName = $@"Local\FloatingTransferStation.Tests.{Guid.NewGuid():N}";
        using var lifecycle = new AppLifecycleService();

        Assert.IsTrue(lifecycle.TryStart(mutexName));
        Assert.IsTrue(lifecycle.TryStart(mutexName));
        Assert.IsFalse(SingleInstanceGuard.TryAcquire(mutexName, out var competing));
        Assert.IsNull(competing);

        lifecycle.Dispose();

        Assert.IsTrue(SingleInstanceGuard.TryAcquire(mutexName, out var afterDispose));
        afterDispose!.Dispose();
    }
}
