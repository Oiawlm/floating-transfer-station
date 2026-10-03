namespace FloatingTransferStation.Services;

/// <summary>
/// 应用内搬迁旧目录的延迟清理：仅在下一次实例成功拿到单实例锁（即已在新目录启动）
/// 之后运行——读取新 Data 内的清理标记，对旧受管目录重复形状守卫校验后删除并清标记；
/// 校验不过或删除失败时保留旧目录并返回提示文案。禁止立即删旧（崩溃窗口丢数据），
/// 也禁止永久保留（迁回旧位置会被残留目录卡死）。
/// </summary>
public static class DataDirectoryCleanupProcessor
{
    /// <summary>返回 null 表示无事可做或清理完成；否则为需要向用户提示的保留原因。</summary>
    public static string? Run(string currentDataDirectory)
    {
        if (!RelocationCleanupMarker.TryRead(currentDataDirectory, out var oldDataDirectory))
        {
            return null;
        }

        // TryDeleteManagedDataDirectory 内部按形状守卫校验：只删登记过且形状属实的受管目录。
        if (!DataDirectoryRelocator.TryDeleteManagedDataDirectory(oldDataDirectory, out var failureReason))
        {
            return $"上次迁移的旧目录未自动删除（{failureReason}），确认不需要后可手动删除：{oldDataDirectory}";
        }

        RelocationCleanupMarker.TryDelete(currentDataDirectory);
        return null;
    }
}
