namespace FloatingTransferStation.Tests;

/// <summary>
/// 仓库工作流临时文件的统一根：&lt;repo&gt;\TestResults\tmp\（gitignored，不进系统
/// %TEMP%）。测试与脚本的临时写入一律挂在本根下，沿用各自的 GUID 隔离与清理语义；
/// 找不到仓库根（脱离源码树运行）时回退 %TEMP%，保持可运行。
/// </summary>
public static class TestTempRoot
{
    public static string Root { get; } = Resolve();

    private static string Resolve()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FloatingTransferStation.slnx"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                var root = Path.Combine(dir.FullName, "TestResults", "tmp");
                Directory.CreateDirectory(root);
                return root;
            }
        }

        return Path.GetTempPath();
    }
}
