using System.Text;

namespace FloatingTransferStation.Services;

/// <summary>
/// AI 识图命名结果的文件名清洗：把模型输出的任意文本收敛为可安全落盘的
/// Windows 文件名基名（不含扩展名，也不含任何路径段）。步骤序固定
/// （空白→首行→包裹引号→非法字符删除→保留名→限长截断），每步只做确定性
/// 变换；返回 null＝放弃命名（调用方按未命名处理），绝不产出空名或路径。
/// 与 <see cref="ImageFileSupport"/>/<see cref="ManagedImagePath"/> 同层的
/// 纯函数工具，无状态无 IO。
/// </summary>
public static class FileNameSanitizer
{
    /// <summary>清洗产物的长度上限（UTF-16 码元；截断点代理对安全，截断后重剪首尾）。</summary>
    public const int MaxTitleLength = 40;

    // ④ 成对包裹引号：同字符闭合（"…" / '…'）或左右弯引号自然配对（“…” / ‘…’）。
    private static readonly string WrappingQuoteCharacters = "\"'“”‘’";

    // ⑦ 首尾收敛的句读（与空白一起从两端剪除；全角顿号/省略号等 AI 输出常见收尾）。
    private static readonly string EdgePunctuation = "。，、；：！？!?,.;:…";

    // ⑤/⑥ 要整体删除的字符：路径分隔符（GetInvalidFileNameChars 已含，显式
    // 列出防平台差异）＋ Windows 非法文件名字符全集＋引号字符（半角引号在
    // 非法集内，全角弯引号可作文件名但属包裹噪声，一并删除）。派生字段声明在
    // 其数据源（WrappingQuoteCharacters）之后，静态初始化序才可靠。
    private static readonly HashSet<char> StrippedCharacters = BuildStrippedCharacters();

    // ⑧ Windows 保留设备名（整名相等，OrdinalIgnoreCase；COM1.png 这类带扩展
    // 形态不构成整名相等，不受影响）。
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    /// <summary>
    /// 清洗一个候选标题。null 或纯空白输入、清洗后为空、整名命中 Windows 保留
    /// 设备名（OrdinalIgnoreCase）均返回 null；否则返回 ≤40 UTF-16 码元、不含
    /// 路径段与非法文件名字符的基名。
    /// </summary>
    public static string? Sanitize(string? input)
    {
        // ① 空输入直接放弃。
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        // ② 只取首行（模型偶尔在名字后追加解释行）。
        var firstLine = TakeFirstLine(input);

        // ③④ 剥成对包裹引号，剥除后重新 Trim，循环到不再有包裹对为止。
        var unwrapped = UnwrapQuotes(firstLine);

        // ⑤⑥ 删除路径分隔符、非法文件名字符、其余控制字符与引号字符。
        var cleaned = RemoveStrippedCharacters(unwrapped);

        // ⑦ 首尾空白与句读收敛。
        cleaned = TrimEdges(cleaned);

        // ⑧ 保留设备名整名相等即放弃（产物是基名，恰为保留名才拒）。
        if (ReservedNames.Contains(cleaned))
        {
            return null;
        }

        // ⑨ 超长截断（截点落在高代理项后回退 1，不拆代理对），截断后可能暴露
        // 新的首尾句读，再执行一次⑦。
        if (cleaned.Length > MaxTitleLength)
        {
            cleaned = TrimEdges(TruncateSurrogateSafe(cleaned));
        }

        // ⑩ 清洗后为空＝放弃。
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    /// <summary>按最先出现的换行分隔截断（\r\n、\r、\n 任一形态）。</summary>
    private static string TakeFirstLine(string text)
    {
        var length = text.Length;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '\r' or '\n')
            {
                length = index;
                break;
            }
        }

        return length == text.Length ? text : text[..length];
    }

    /// <summary>
    /// 循环：Trim 首尾空白 → 首尾恰构成包裹引号对则整对剥除 → 重新 Trim；
    /// 直到不再有包裹对（多层引号「“‘名字’”」逐层剥净）。
    /// </summary>
    private static string UnwrapQuotes(string text)
    {
        while (true)
        {
            var trimmed = text.Trim();
            if (trimmed.Length < 2 || !IsWrappingQuotePair(trimmed[0], trimmed[^1]))
            {
                return trimmed;
            }

            text = trimmed[1..^1];
        }
    }

    private static bool IsWrappingQuotePair(char first, char last) =>
        (first == last || IsNaturalCurlyPair(first, last)) && WrappingQuoteCharacters.Contains(first);

    private static bool IsNaturalCurlyPair(char first, char last) =>
        (first == '“' && last == '”') || (first == '‘' && last == '’');

    private static string RemoveStrippedCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            // GetInvalidFileNameChars 已含 0x00–0x1F 控制字符；char.IsControl 再
            // 兜住 0x7F 等其余控制字符。
            if (!StrippedCharacters.Contains(character) && !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string TrimEdges(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsEdgeTrimCharacter(text[start]))
        {
            start++;
        }

        while (end > start && IsEdgeTrimCharacter(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    private static bool IsEdgeTrimCharacter(char character) =>
        char.IsWhiteSpace(character) || EdgePunctuation.Contains(character);

    /// <summary>
    /// 截断至 <see cref="MaxTitleLength"/> 码元：截点前一个字符是高代理项
    /// （emoji 等增补平面字符的第一个码元）则回退 1，不拆代理对——照
    /// BoardItem.PreviewText 的 SurrogateSafeLength 口径。
    /// </summary>
    private static string TruncateSurrogateSafe(string text)
    {
        var length = MaxTitleLength;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private static HashSet<char> BuildStrippedCharacters()
    {
        var characters = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\' };
        foreach (var quote in WrappingQuoteCharacters)
        {
            characters.Add(quote);
        }

        return characters;
    }

    private static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL"
        };
        foreach (var index in Enumerable.Range(1, 9))
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }
}
