namespace FloatingTransferStation.Tests;

public sealed class TestDirectory : IDisposable
{
    // CI runner 的防御软件会瞬时持有新写入文件（如原子写的 *.tmp）的句柄：
    // 递归删除撞上即 IOException。瞬时占用重试退避即可穿过；持续占用
    // （真实泄漏）在时限后原样抛出，不静默吞掉。
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(10);

    public TestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "悬浮中转站-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var deadline = DateTime.UtcNow + DisposeTimeout;
        while (true)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }
}
