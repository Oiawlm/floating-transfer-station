namespace FloatingTransferStation.Models;

/// <summary>
/// DeepSeek 官方现行模型 ID 目录（单一事实源）：偏好默认值、客户端默认模型、设置页
/// 候选与提示行全部从此派生，生产代码中每个模型 ID 字面量恰好出现一处。
/// 2026-09-10 官方发布 DeepSeek-V4.1-Flash 起，现行 Flash 的 API model ID 由旧的
/// 版本式命名改为 <see cref="Flash"/>；官方对旧 Flash ID 声明暂时兼容路由，存量偏好
/// 原样保留、不迁移。
/// </summary>
public static class DeepSeekModels
{
    /// <summary>现行轻量模型（显示名 DeepSeek V4.1 Flash），设置页默认选择。</summary>
    public const string Flash = "deepseek-flash";

    /// <summary>现行 Pro 模型（显示名 DeepSeek V4 Pro，版本快照 V4-Pro-0813）。</summary>
    public const string Pro = "deepseek-v4-pro";

    /// <summary>设置页下拉候选，默认在前（只读包装，消费方不得改写）。</summary>
    public static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[] { Flash, Pro });

    /// <summary>设置页模型提示行（含官方显示名映射）。static readonly：const 会跨程序集
    /// 内联，提示文案改动将无法随 Core 单独生效。</summary>
    public static readonly string HintText =
        Flash + "（DeepSeek V4.1 Flash）快速省钱，适合日常文本整理；" +
        Pro + "（DeepSeek V4 Pro）更强，适合复杂任务。";
}
