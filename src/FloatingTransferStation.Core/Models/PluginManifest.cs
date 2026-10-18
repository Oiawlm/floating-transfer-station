using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloatingTransferStation.Models;

public enum PluginKind
{
    TextCleaner,
    TextActions
}

public enum PluginActionSelection
{
    Single,
    Multi
}

/// <summary>v1 只支持 "replace"（经宿主批量替换管线写回原卡）；"new-cards" 是保留字。</summary>
public enum PluginActionOutput
{
    Replace
}

public enum PluginSettingType
{
    Bool,
    Choice,
    Text
}

/// <summary>
/// 声明式文本整理规则：按清单顺序把正则匹配替换为固定文本。
/// 规则以 CultureInvariant + Multiline 编译（^/$ 按行匹配），带 100ms 执行超时；
/// 整理（text-cleaner）替换串只做字面替换，动作（text-actions）替换串采用 .NET
/// 替换语义（$1/${name} 捕获组）——两种语义见 docs/plugins.md 显著区分。
/// <see cref="When"/> 仅动作规则可用：引用本动作声明的 bool 设置 key，
/// 运行时按当前值门控该规则是否参与。
/// </summary>
public sealed record PluginTextRule(
    string Pattern,
    string Replacement,
    string? Description = null,
    string? When = null);

/// <summary>动作的声明式设置字段（L1）：值由用户在设置界面编辑、按插件 id 原子持久化。</summary>
public sealed record PluginActionSetting(
    string Key,
    string Label,
    PluginSettingType Type,
    IReadOnlyList<string> Options,
    string? Default);

/// <summary>
/// 声明式卡片动作（L1）：对有序选中的文字卡快照逐卡应用规则，输出只支持
/// replace（经宿主统一提交，全有或全无）。本记录是校验后的纯数据；
/// 执行语义见 PluginActionPipeline。
/// </summary>
public sealed record PluginActionDefinition(
    string Id,
    string Title,
    PluginActionSelection Selection,
    IReadOnlyList<PluginTextRule> Rules,
    IReadOnlyList<PluginActionSetting> Settings)
{
    /// <summary>按声明解析某设置项的生效值（用户覆盖优先，缺省回落清单默认）。</summary>
    public string ResolveSettingValue(IReadOnlyDictionary<string, string> overrides, string key)
    {
        var setting = Settings.FirstOrDefault(candidate => candidate.Key == key);
        if (setting is null)
        {
            return string.Empty;
        }

        return overrides.TryGetValue(key, out var value) ? value : setting.Default ?? string.Empty;
    }
}

/// <summary>
/// 插件清单（plugin.json）：支持 text-cleaner（声明式文本整理）与 text-actions
/// （L1 声明式卡片动作）。所有约束在解析时一次性校验，非法清单整包拒绝，
/// 不部分应用规则/动作；错误信息带字段与规则/动作索引定位。
/// </summary>
public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string? Description,
    PluginKind Kind,
    IReadOnlyList<PluginTextRule> TextRules,
    IReadOnlyList<PluginActionDefinition>? Actions = null)
{
    public const string TextCleanerKindToken = "text-cleaner";
    public const string TextActionsKindToken = "text-actions";
    public const string ReservedOutputToken = "new-cards";
    public const int MaximumTextRules = 32;
    public const int MaximumPatternLength = 512;
    public const int MaximumReplacementLength = 512;
    public const int MaximumActions = 16;
    public const int MaximumActionSettings = 8;

    internal static readonly TimeSpan RuleMatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex IdPattern =
        new("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SettingKeyPattern =
        new("^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>解析并校验插件清单 JSON。失败时给出面向插件作者的一句错误说明。</summary>
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
                kindElement.ValueKind != JsonValueKind.String)
            {
                error = $"kind 必填，目前支持 \"{TextCleanerKindToken}\" 与 \"{TextActionsKindToken}\"。";
                return false;
            }

            var kindToken = kindElement.GetString()!;
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
                    if (!TryParseRule(ruleElement, allowWhen: false, index, rules, out error))
                    {
                        return false;
                    }
                }
            }

            var actions = new List<PluginActionDefinition>();
            if (kindToken == TextActionsKindToken)
            {
                if (!root.TryGetProperty("actions", out var actionsElement) ||
                    actionsElement.ValueKind != JsonValueKind.Array ||
                    actionsElement.GetArrayLength() == 0 ||
                    actionsElement.GetArrayLength() > MaximumActions)
                {
                    error = $"kind 为 \"{TextActionsKindToken}\" 时 actions 必须是 1–{MaximumActions} 个动作的数组。";
                    return false;
                }

                var actionIndex = 0;
                var actionIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var actionElement in actionsElement.EnumerateArray())
                {
                    actionIndex++;
                    if (!TryParseAction(actionElement, actionIndex, actionIds, actions, out error))
                    {
                        return false;
                    }
                }
            }
            else if (kindToken != TextCleanerKindToken)
            {
                error = $"kind 目前支持 \"{TextCleanerKindToken}\" 与 \"{TextActionsKindToken}\"。";
                return false;
            }

            manifest = new PluginManifest(
                id,
                name,
                version,
                description,
                kindToken == TextActionsKindToken ? PluginKind.TextActions : PluginKind.TextCleaner,
                rules,
                actions);
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "plugin.json 不是合法 JSON。";
            return false;
        }
    }

    private static bool TryParseAction(
        JsonElement actionElement,
        int actionIndex,
        HashSet<string> actionIds,
        List<PluginActionDefinition> actions,
        out string error)
    {
        error = string.Empty;
        if (actionElement.ValueKind != JsonValueKind.Object)
        {
            error = $"第 {actionIndex} 个动作必须是 JSON 对象。";
            return false;
        }

        if (!TryReadString(actionElement, "id", out var actionId) || !IdPattern.IsMatch(actionId))
        {
            error = $"第 {actionIndex} 个动作的 id 必须是 3–64 位小写字母、数字或连字符，且以字母数字开头结尾。";
            return false;
        }

        if (!actionIds.Add(actionId))
        {
            error = $"第 {actionIndex} 个动作的 id \"{actionId}\" 与前面的动作重复（动作 id 必须在插件内唯一）。";
            return false;
        }

        if (!TryReadString(actionElement, "title", out var title) || title.Length > 40)
        {
            error = $"动作 \"{actionId}\" 的 title 必填，且不超过 40 个字符。";
            return false;
        }

        if (!TryReadString(actionElement, "accept", out var accept) || accept != "text")
        {
            error = $"动作 \"{actionId}\" 的 accept 目前只支持 \"text\"。";
            return false;
        }

        if (!TryReadString(actionElement, "output", out var output))
        {
            error = $"动作 \"{actionId}\" 的 output 必填，目前只支持 \"replace\"。";
            return false;
        }

        if (output == ReservedOutputToken)
        {
            error = $"动作 \"{actionId}\" 的 output \"{ReservedOutputToken}\" 是保留字（预留给后续版本的「生成新卡片」能力），当前版本只支持 \"replace\"。";
            return false;
        }

        if (output != "replace")
        {
            error = $"动作 \"{actionId}\" 的 output 目前只支持 \"replace\"。";
            return false;
        }

        if (!TryReadString(actionElement, "selection", out var selectionToken) ||
            (selectionToken != "single" && selectionToken != "multi"))
        {
            error = $"动作 \"{actionId}\" 的 selection 必须是 \"single\" 或 \"multi\"。";
            return false;
        }

        var settings = new List<PluginActionSetting>();
        if (actionElement.TryGetProperty("settings", out var settingsElement))
        {
            if (settingsElement.ValueKind != JsonValueKind.Array ||
                settingsElement.GetArrayLength() > MaximumActionSettings)
            {
                error = $"动作 \"{actionId}\" 的 settings 必须是数组，且最多 {MaximumActionSettings} 个字段。";
                return false;
            }

            var settingIndex = 0;
            var settingKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var settingElement in settingsElement.EnumerateArray())
            {
                settingIndex++;
                if (!TryParseActionSetting(settingElement, actionId, settingIndex, settingKeys, settings, out error))
                {
                    return false;
                }
            }
        }

        if (!actionElement.TryGetProperty("rules", out var actionRulesElement) ||
            actionRulesElement.ValueKind != JsonValueKind.Array ||
            actionRulesElement.GetArrayLength() == 0 ||
            actionRulesElement.GetArrayLength() > MaximumTextRules)
        {
            error = $"动作 \"{actionId}\" 的 rules 必须是 1–{MaximumTextRules} 条规则的数组。";
            return false;
        }

        var actionRules = new List<PluginTextRule>();
        var ruleIndex = 0;
        foreach (var ruleElement in actionRulesElement.EnumerateArray())
        {
            ruleIndex++;
            if (!TryParseRule(ruleElement, allowWhen: true, ruleIndex, actionRules, out error, actionId, settings))
            {
                return false;
            }
        }

        actions.Add(new PluginActionDefinition(
            actionId,
            title,
            selectionToken == "multi" ? PluginActionSelection.Multi : PluginActionSelection.Single,
            actionRules,
            settings));
        return true;
    }

    private static bool TryParseActionSetting(
        JsonElement settingElement,
        string actionId,
        int settingIndex,
        HashSet<string> settingKeys,
        List<PluginActionSetting> settings,
        out string error)
    {
        error = string.Empty;
        if (settingElement.ValueKind != JsonValueKind.Object ||
            !TryReadString(settingElement, "key", out var key) ||
            !SettingKeyPattern.IsMatch(key))
        {
            error = $"动作 \"{actionId}\" 第 {settingIndex} 个设置的 key 必须是 3–32 位小写字母、数字或连字符，且以字母数字开头结尾。";
            return false;
        }

        if (!settingKeys.Add(key))
        {
            error = $"动作 \"{actionId}\" 第 {settingIndex} 个设置的 key \"{key}\" 与前面的设置重复（设置 key 必须在动作内唯一）。";
            return false;
        }

        if (!TryReadString(settingElement, "label", out var label) || label.Length > 40)
        {
            error = $"动作 \"{actionId}\" 设置 \"{key}\" 的 label 必填，且不超过 40 个字符。";
            return false;
        }

        if (!TryReadString(settingElement, "type", out var typeToken))
        {
            error = $"动作 \"{actionId}\" 设置 \"{key}\" 的 type 必填（bool/choice/text）。";
            return false;
        }

        string? defaultRaw = null;
        if (settingElement.TryGetProperty("default", out var defaultElement))
        {
            defaultRaw = defaultElement.ValueKind switch
            {
                JsonValueKind.String => defaultElement.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
            if (defaultRaw is null)
            {
                error = $"动作 \"{actionId}\" 设置 \"{key}\" 的 default 必须是与 type 吻合的标量（bool 用 true/false，choice/text 用字符串）。";
                return false;
            }
        }

        string? normalizedDefault = null;
        IReadOnlyList<string> options = Array.Empty<string>();
        switch (typeToken)
        {
            case "bool":
                if (defaultRaw is not null && defaultRaw is not ("true" or "false"))
                {
                    error = $"动作 \"{actionId}\" 设置 \"{key}\"（bool）的 default 只能是 true 或 false。";
                    return false;
                }

                normalizedDefault = defaultRaw ?? "false";
                break;
            case "choice":
                if (!settingElement.TryGetProperty("options", out var optionsElement) ||
                    optionsElement.ValueKind != JsonValueKind.Array)
                {
                    error = $"动作 \"{actionId}\" 设置 \"{key}\"（choice）必须声明 options 数组。";
                    return false;
                }

                var parsedOptions = new List<string>();
                foreach (var optionElement in optionsElement.EnumerateArray())
                {
                    if (optionElement.ValueKind != JsonValueKind.String ||
                        optionElement.GetString() is not { } optionText ||
                        optionText.Length == 0 || optionText.Length > 40)
                    {
                        error = $"动作 \"{actionId}\" 设置 \"{key}\"（choice）的每个 option 必须是 1–40 个字符的文本。";
                        return false;
                    }

                    parsedOptions.Add(optionText);
                }

                if (parsedOptions.Count is < 2 or > 8)
                {
                    error = $"动作 \"{actionId}\" 设置 \"{key}\"（choice）的 options 必须是 2–8 项。";
                    return false;
                }

                options = parsedOptions;
                if (defaultRaw is null || !parsedOptions.Contains(defaultRaw, StringComparer.Ordinal))
                {
                    error = $"动作 \"{actionId}\" 设置 \"{key}\"（choice）的 default 必填，且必须是 options 中的一项。";
                    return false;
                }

                normalizedDefault = defaultRaw;
                break;
            case "text":
                if (defaultRaw is not null && defaultRaw.Length > 200)
                {
                    error = $"动作 \"{actionId}\" 设置 \"{key}\"（text）的 default 不超过 200 个字符。";
                    return false;
                }

                normalizedDefault = defaultRaw ?? string.Empty;
                break;
            default:
                error = $"动作 \"{actionId}\" 设置 \"{key}\" 的 type 必须是 bool/choice/text。";
                return false;
        }

        settings.Add(new PluginActionSetting(key, label, ParseSettingType(typeToken), options, normalizedDefault));
        return true;
    }

    private static PluginSettingType ParseSettingType(string token) => token switch
    {
        "choice" => PluginSettingType.Choice,
        "text" => PluginSettingType.Text,
        _ => PluginSettingType.Bool
    };

    private static bool TryParseRule(
        JsonElement ruleElement,
        bool allowWhen,
        int ruleIndex,
        List<PluginTextRule> rules,
        out string error,
        string? actionId = null,
        IReadOnlyList<PluginActionSetting>? actionSettings = null)
    {
        error = string.Empty;
        if (ruleElement.ValueKind != JsonValueKind.Object ||
            !TryReadString(ruleElement, "pattern", out var pattern) ||
            pattern.Length == 0 || pattern.Length > MaximumPatternLength)
        {
            error = $"{Scope(actionId)}第 {ruleIndex} 条规则缺少有效 pattern（1–{MaximumPatternLength} 字符）。";
            return false;
        }

        if (!IsPatternValid(pattern))
        {
            error = $"{Scope(actionId)}第 {ruleIndex} 条规则的 pattern 不是有效正则表达式。";
            return false;
        }

        var replacement = string.Empty;
        if (ruleElement.TryGetProperty("replacement", out var replacementElement))
        {
            if (replacementElement.ValueKind != JsonValueKind.String ||
                replacementElement.GetString()!.Length > MaximumReplacementLength)
            {
                error = $"{Scope(actionId)}第 {ruleIndex} 条规则的 replacement 必须是不超过 {MaximumReplacementLength} 字符的文本。";
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
                error = $"{Scope(actionId)}第 {ruleIndex} 条规则的 description 必须不超过 120 个字符。";
                return false;
            }

            ruleDescription = ruleDescriptionElement.GetString();
        }

        string? when = null;
        if (ruleElement.TryGetProperty("when", out var whenElement))
        {
            if (whenElement.ValueKind != JsonValueKind.String ||
                whenElement.GetString() is not { } whenKey ||
                whenKey.Length == 0)
            {
                error = $"{Scope(actionId)}第 {ruleIndex} 条规则的 when 必须是本动作已声明的 bool 设置 key。";
                return false;
            }

            if (!allowWhen)
            {
                error = $"第 {ruleIndex} 条规则声明了 when：when 只在 \"{TextActionsKindToken}\" 动作规则中可用（text-cleaner 规则无条件生效）。";
                return false;
            }

            var setting = actionSettings!.FirstOrDefault(candidate => candidate.Key == whenKey);
            if (setting is null || setting.Type != PluginSettingType.Bool)
            {
                error = $"{Scope(actionId)}第 {ruleIndex} 条规则的 when \"{whenKey}\" 未声明或不是 bool 类型设置。";
                return false;
            }

            when = whenKey;
        }

        rules.Add(new PluginTextRule(pattern, replacement, ruleDescription, when));
        return true;
    }

    private static string Scope(string? actionId) => actionId is null ? string.Empty : $"动作 \"{actionId}\" ";

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
