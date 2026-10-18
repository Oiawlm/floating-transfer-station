using System.Diagnostics;
using System.Text.RegularExpressions;
using FloatingTransferStation.Models;

namespace FloatingTransferStation.Services;

/// <summary>插件动作资源预算（Core 单点常量，契约「插件动作协议」）。</summary>
public static class PluginActionLimits
{
    /// <summary>整次动作调用的总时长预算（叠加在既有单规则 100ms 超时之上）。</summary>
    public static readonly TimeSpan TotalDuration = TimeSpan.FromMilliseconds(2000);

    /// <summary>单次调用选集总输入字符上限。</summary>
    public const int TotalInputCharacters = 100_000;

    /// <summary>单卡输出字符上限（对齐「不得静默截断」：超限整批失败）。</summary>
    public const int MaxOutputCharactersPerCard = 50_000;

    /// <summary>试运行输入上限（诊断 UI，独立于正式执行预算）。</summary>
    public const int TrialRunInputCharacters = 10_000;
}

/// <summary>动作输入：一张文字卡的稳定 id 与全文快照。</summary>
public sealed record PluginActionCardInput(Guid ItemId, string Text);

/// <summary>动作输出：一张卡的新文本与规则命中次数。</summary>
public sealed record PluginActionCardOutput(Guid ItemId, string NewText, int RuleHits);

/// <summary>
/// 一次动作调用的结果：成功时给出逐卡新文本（trim 后为空的卡跳过保留原文并计数）、
/// 跳过数与耗时；失败时给一句可读原因。超预算/超时/输出超限都是整批失败——
/// 宿主在提交前拿到全部结果，全有或全无，不静默截断。
/// </summary>
public sealed record PluginActionRunResult(
    bool IsSuccess,
    string? FailureReason,
    IReadOnlyList<PluginActionCardOutput>? Cards,
    int SkippedEmptyCount,
    TimeSpan Elapsed)
{
    public static PluginActionRunResult Fail(string reason) =>
        new(false, reason, null, 0, TimeSpan.Zero);
}

/// <summary>
/// 不可变的动作执行管线（L1，声明式卡片动作）：按当前设置值物化 when 门控后的
/// 规则表，逐卡顺序应用。动作替换串采用 .NET 替换语义（$1/${name}，开发者预期）；
/// 与 text-cleaner 的字面替换语义显著区分（见 docs/plugins.md）。管线无状态、
/// 只读——设置变化时由 PluginCatalog 重建整个实例（对齐 TextCleanerPipeline
/// 快照家族），执行期间不被启停影响。
/// </summary>
public sealed class PluginActionPipeline
{
    private readonly IReadOnlyList<(Regex Pattern, string Replacement)> _rules;

    internal PluginActionPipeline(IReadOnlyList<(Regex Pattern, string Replacement)> rules) =>
        _rules = rules;

    public int RuleCount => _rules.Count;

    /// <summary>按当前设置值物化 when 门控并编译规则；返回 null 表示所有规则都被门控关闭。</summary>
    internal static PluginActionPipeline? TryBuild(
        PluginActionDefinition action,
        IReadOnlyDictionary<string, string> settingValues)
    {
        var rules = new List<(Regex, string)>();
        foreach (var rule in action.Rules)
        {
            if (rule.When is { } whenKey &&
                !string.Equals(
                    settingValues.TryGetValue(whenKey, out var value) ? value : string.Empty,
                    "true",
                    StringComparison.Ordinal))
            {
                continue;
            }

            rules.Add((
                new Regex(
                    rule.Pattern,
                    RegexOptions.CultureInvariant | RegexOptions.Multiline,
                    PluginManifest.RuleMatchTimeout),
                rule.Replacement));
        }

        return rules.Count == 0 ? null : new PluginActionPipeline(rules);
    }

    /// <summary>
    /// 对有序文字卡快照执行动作：任一规则超时、总时长/输入字符超预算、或单卡输出
    /// 超限 → 整批失败（可读原因）；结果 trim 后为空的卡跳过（保留原文）并计数。
    /// 本方法不做任何持久化——宿主拿到全部输出后经批量替换管线统一提交。
    /// </summary>
    public PluginActionRunResult Apply(IReadOnlyList<PluginActionCardInput> cards)
    {
        if (cards.Count == 0)
        {
            return new PluginActionRunResult(true, null, [], 0, TimeSpan.Zero);
        }

        var totalInput = 0L;
        foreach (var card in cards)
        {
            totalInput += card.Text?.Length ?? 0;
        }

        if (totalInput > PluginActionLimits.TotalInputCharacters)
        {
            return PluginActionRunResult.Fail(
                $"选集总字数 {totalInput} 超过单次动作输入上限 {PluginActionLimits.TotalInputCharacters}，未做任何修改。");
        }

        var stopwatch = Stopwatch.StartNew();
        var outputs = new List<PluginActionCardOutput>(cards.Count);
        var skippedEmpty = 0;
        foreach (var card in cards)
        {
            var current = card.Text ?? string.Empty;
            var hits = 0;
            foreach (var (pattern, replacement) in _rules)
            {
                try
                {
                    // MatchEvaluator 同时承担 .NET 替换语义（$1/${name}）与命中计数。
                    current = pattern.Replace(
                        current,
                        match =>
                        {
                            hits++;
                            return match.Result(replacement);
                        });
                }
                catch (RegexMatchTimeoutException)
                {
                    return PluginActionRunResult.Fail(
                        $"规则执行超时（单条 {PluginManifest.RuleMatchTimeout.TotalMilliseconds:0}ms），已放弃整批修改。");
                }

                if (stopwatch.Elapsed > PluginActionLimits.TotalDuration)
                {
                    return PluginActionRunResult.Fail(
                        $"动作执行超过 {(int)PluginActionLimits.TotalDuration.TotalMilliseconds}ms 预算，已放弃整批修改（未做任何修改）。");
                }
            }

            if (string.IsNullOrWhiteSpace(current))
            {
                // 动作同受「不得清空」约束：trim 后为空的卡跳过，保留原文并计数。
                skippedEmpty++;
                continue;
            }

            if (current.Length > PluginActionLimits.MaxOutputCharactersPerCard)
            {
                return PluginActionRunResult.Fail(
                    $"整理结果超过单卡输出上限 {PluginActionLimits.MaxOutputCharactersPerCard} 字符，已放弃整批修改。");
            }

            outputs.Add(new PluginActionCardOutput(card.ItemId, current, hits));
        }

        stopwatch.Stop();
        return new PluginActionRunResult(true, null, outputs, skippedEmpty, stopwatch.Elapsed);
    }
}
