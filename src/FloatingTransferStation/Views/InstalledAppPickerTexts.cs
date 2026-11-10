namespace FloatingTransferStation.Views;

/// <summary>
/// 已安装应用选择器的固定文案（复审轮 S3 收敛，2026-11-10）：快捷启动「已安装应用…」
/// 入口与编辑目标窗的打包应用改选是同一选择器流程，此前两处构造点逐字重复本组文案
/// ——现以本类为唯一来源，调用方不得再写第二份字面量。
/// </summary>
internal static class InstalledAppPickerTexts
{
    /// <summary>选择器标题（添加入口与改选入口同词）。</summary>
    public const string Title = "添加已安装应用";

    /// <summary>搜索框占位提示。</summary>
    public const string SearchPlaceholder = "搜索应用";

    /// <summary>源枚举为空时的空态提示。</summary>
    public const string EmptySourceHint = "未枚举到已安装应用";
}
