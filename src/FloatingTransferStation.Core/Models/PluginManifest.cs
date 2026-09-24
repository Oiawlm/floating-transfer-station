using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloatingTransferStation.Models;

public enum PluginKind
{
    TextCleaner
}

/// <summary>
/// 声明式文本整理规则：按清单顺序把正则匹配替换为固定文本。
/// 规则以 CultureInvariant + Multiline 编译（^/$ 按行匹配），带 100ms 执行超时；
/// 替换串只做字面替换。
/// </summary>
public sealed record PluginTextRule(
    string Pattern,
    string Replacement,
    string? Description = null);

/// <summary>
/// 插件清单（plugin.json）首版：只支持文本整理插件。
/// 所有约束在解析时一次性校验，非法清单整包拒绝，不部分应用规则。
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string? Description,
    PluginKind Kind,
    IReadOnlyList<PluginTextRule> TextRules)
{
    public const string TextCleanerKindToken = "text-cleaner";
    public const int MaximumTextRules = 32;
    public const int MaximumPatternLength = 512;
    public const int MaximumReplacementLength = 512;

    internal static readonly TimeSpan RuleMatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex IdPattern =
        new("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 解析并校验插件清单 JSON。失败时给出面向插件作者的一句错误说明。
    /// </summary>
    public static bool TryParse(string json, out PluginManifest? manifest, out string error)
    {
        manifest = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "plugin.json 必须是 JSON 对象。";
                return false;
            }

            if (!TryReadString(root, "id", out var id) ||
                !IdPattern.IsMatch(id))
            {
                error = "id 必须是 3–64 位小写字母、数字或连字符，且以字母数字开头结尾。";
                return false;
            }

            if (!TryReadString(root, "name", out var name) || name.Length > 40)
            {
                error = "name 必填，且不超过 40 个字符。";
                return false;
            }

            if (!root.TryGetProperty("version", out var versionElement) ||
                versionElement.ValueKind != JsonValueKind.String ||
                !global::System.Version.TryParse(versionElement.GetString(), out _))
            {
                error = "version 必填，必须是如 1.0.0 的版本号。";
                return false;
            }

            var version = versionElement.GetString()!;
            string? description = null;
            if (root.TryGetProperty("description", out var descriptionElement))
            {
                if (descriptionElement.ValueKind != JsonValueKind.String ||
                    descriptionElement.GetString() is not { } descriptionText ||
                    descriptionText.Length > 200)
                {
                    error = "description 必须是不超过 200 个字符的文本。";
                    return false;
                }

                description = string.IsNullOrWhiteSpace(descriptionText) ? null : descriptionText;
            }

            if (!root.TryGetProperty("kind", out var kindElement) ||
                kindElement.ValueKind != JsonValueKind.String ||
                kindElement.GetString() != TextCleanerKindToken)
            {
                error = $"kind 目前只支持 \"{TextCleanerKindToken}\"。";
                return false;
            }

            var rules = new List<PluginTextRule>();
            if (root.TryGetProperty("textRules", out var rulesElement))
            {
                if (rulesElement.ValueKind != JsonValueKind.Array ||
                    rulesElement.GetArrayLength() > MaximumTextRules)
                {
                    error = $"textRules 必须是数组，且最多 {MaximumTextRules} 条。";
                    return false;
                }

                var index = 0;
                foreach (var ruleElement in rulesElement.EnumerateArray())
                {
                    index++;
                    if (ruleElement.ValueKind != JsonValueKind.Object ||
                        !TryReadString(ruleElement, "pattern", out var pattern) ||
                        pattern.Length == 0 || pattern.Length > MaximumPatternLength)
                    {
                        error = $"第 {index} 条规则缺少有效 pattern（1–{MaximumPatternLength} 字符）。";
                        return false;
                    }

                    if (!IsPatternValid(pattern))
                    {
                        error = $"第 {index} 条规则的 pattern 不是有效正则表达式。";
                        return false;
                    }

                    var replacement = string.Empty;
                    if (ruleElement.TryGetProperty("replacement", out var replacementElement))
                    {
                        if (replacementElement.ValueKind != JsonValueKind.String ||
                            replacementElement.GetString()!.Length > MaximumReplacementLength)
                        {
                            error = $"第 {index} 条规则的 replacement 必须是不超过 {MaximumReplacementLength} 字符的文本。";
                            return false;
                        }

                        replacement = replacementElement.GetString()!;
                    }

                    string? ruleDescription = null;
                    if (ruleElement.TryGetProperty("description", out var ruleDescriptionElement))
                    {
                        if (ruleDescriptionElement.ValueKind != JsonValueKind.String ||
                            ruleDescriptionElement.GetString()!.Length > 120)
                        {
                            error = $"第 {index} 条规则的 description 必须不超过 120 个字符。";
                            return false;
                        }

                        ruleDescription = ruleDescriptionElement.GetString();
                    }

                    rules.Add(new PluginTextRule(pattern, replacement, ruleDescription));
                }
            }

            manifest = new PluginManifest(
                id,
                name,
                version,
                description,
                PluginKind.TextCleaner,
                rules);
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "plugin.json 不是合法 JSON。";
            return false;
        }
    }

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(property, out var child) ||
            child.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = child.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool IsPatternValid(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, RuleMatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
