namespace FloatingTransferStation.Services;

/// <summary>
/// 「AI 识图命名」系统提示词的单一事实源（照 <see cref="AiOrganizePrompt"/> 的
/// 定位，但无覆盖机制——命名没有用户定制面，提示词常量逐字发送，防注入面
/// 更小）。仓库惯例是代码常量承载（<see cref="DeepSeekClient.DefaultModel"/>
/// 先例），不做外置资源文件。
/// </summary>
public static class AiImageNamingPrompt
{
    public const string SystemPrompt =
        "你是图片文件命名助手。观察这张图片，用简体中文起一个简短的描述性名字作为文件名：只输出名字本身，不超过 20 个字，描述图片中最主要、最易识别的内容；不要扩展名、不要路径、不要标点符号、不要引号、不要任何解释。";
}
