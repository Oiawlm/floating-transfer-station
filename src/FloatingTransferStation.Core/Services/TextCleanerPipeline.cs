using System.Text.RegularExpressions;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>
/// 不可变的文本清理管线：按序应用启用插件的全部规则。
/// 任何规则执行失败（如回溯超时）或结果被清成空白时，整体回退原文——
/// 插件只允许"整理"内容，不允许吃掉内容。
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
                current = pattern.Replace(current, replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                return text;
            }
        }

        return string.IsNullOrWhiteSpace(current) ? text : current;
    }
}
