using System.Text.RegularExpressions;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 不可变的文本清理管线：按序应用启用插件的全部规则。
/// 任何规则执行失败（如回溯超时）或结果被清成空白时，整体回退原文——
/// 插件只允许"整理"内容，不允许吃掉内容。
/// 替换串只做字面替换：`$` 转义后交给 Regex.Replace（1.51.0 修正——此前含 $ 的
/// 替换串会被按 .NET 替换语义解释，与清单注释宣称的字面替换不符；动作插件的
/// .NET 替换语义另见 PluginActionPipeline，两种语义在 docs/plugins.md 显著区分）。
/// </summary>
public sealed class TextCleanerPipeline
{
    public static TextCleanerPipeline Empty { get; } = new([]);

    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _rules;

    internal TextCleanerPipeline(IReadOnlyList<(Regex Pattern, string Replacement)> rules) =>
        _rules = rules;

    public int RuleCount => _rules.Count;

    public string Apply(string text)
    {
        if (_rules.Count == 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        var current = text;
        foreach (var (pattern, replacement) in _rules)
        {
            try
            {
                current = pattern.Replace(current, EscapeLiteralReplacement(replacement));
            }
            catch (RegexMatchTimeoutException)
            {
                return text;
            }
        }

        return string.IsNullOrWhiteSpace(current) ? text : current;
    }

    /// <summary>把声明替换串转为字面替换：`$$` 在 .NET 替换语义中表示字面 `$`。</summary>
    private static string EscapeLiteralReplacement(string replacement) =>
        replacement.Replace("$", "$$", StringComparison.Ordinal);
}
