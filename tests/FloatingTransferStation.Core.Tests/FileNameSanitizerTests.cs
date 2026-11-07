using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

/// <summary>
/// FileNameSanitizer 的确定性清洗契约（AI 识图命名结果的文件名基化）：
/// 非法字符删除、Windows 保留名整名拒、限长截断代理对安全、CJK 全保留、
/// 首尾空白/句读收敛、包裹引号剥除、多行取首行、路径段删除、空产物放弃。
/// </summary>
[TestClass]
public sealed class FileNameSanitizerTests
{
    [TestMethod]
    public void Sanitize_NullOrWhitespaceInput_ReturnsNull()
    {
        foreach (var blank in new string?[] { null, "", " ", " \t\r\n " })
        {
            Assert.IsNull(FileNameSanitizer.Sanitize(blank), $"输入 {blank ?? "null"} 必须放弃。");
        }
    }

    [TestMethod]
    public void Sanitize_RemovesInvalidFilenameCharacters()
    {
        Assert.AreEqual("abcdefg", FileNameSanitizer.Sanitize("a<b>|c:d*e?f\"g"));
        Assert.AreEqual("ab", FileNameSanitizer.Sanitize("a\u0001b\u001F"));
    }

    [TestMethod]
    public void Sanitize_RejectsWindowsReservedNamesCaseInsensitively()
    {
        foreach (var reserved in new[] { "CON", "con", "NUL", "Aux", "COM3", "lpt9" })
        {
            Assert.IsNull(FileNameSanitizer.Sanitize(reserved), $"保留名 {reserved} 必须整名拒。");
        }
    }

    [TestMethod]
    public void Sanitize_ReservedNameWithExtensionShapeIsNotExactMatch()
    {
        // COM1.png 不是保留名整名相等——清洗产物是基名，带扩展形态的不拒绝。
        Assert.AreEqual("COM1.png", FileNameSanitizer.Sanitize("COM1.png"));
    }

    [TestMethod]
    public void Sanitize_TruncatesOverlongTitlesWithSurrogateSafety()
    {
        var overlong = new string('名', 45);
        var truncated = FileNameSanitizer.Sanitize(overlong);
        Assert.AreEqual(FileNameSanitizer.MaxTitleLength, truncated!.Length, "超长按 40 码元截断。");
        Assert.AreEqual(new string('名', 40), truncated);

        // 代理对恰在 40 码元内（0 基 38/39）：不截断，emoji 完整保留。
        var fittingEmoji = string.Concat(new string('名', 38), "\U0001F600");
        Assert.AreEqual(40, fittingEmoji.Length);
        Assert.AreEqual(fittingEmoji, FileNameSanitizer.Sanitize(fittingEmoji));

        // 高代理项恰落在第 40 码元（0 基 39）：截断点回退 1，不拆 emoji。
        var splitRisk = string.Concat(new string('名', 39), "\U0001F600", "尾");
        Assert.AreEqual(42, splitRisk.Length);
        var backed = FileNameSanitizer.Sanitize(splitRisk);
        Assert.AreEqual(new string('名', 39), backed, "截点落在高代理项后必须回退 1。");
    }

    [TestMethod]
    public void Sanitize_TruncatedTailPunctuationIsRetrimmed()
    {
        // 40 码元截断后新暴露的收尾句读要再剪一次：39 名 + ! + …（总 41）→
        // 截到 40 以 '!' 收尾 → 重剪 → 39 名。
        var tailed = new string('名', 39) + "!" + new string('字', 5);
        Assert.AreEqual(45, tailed.Length);
        Assert.AreEqual(new string('名', 39), FileNameSanitizer.Sanitize(tailed));
    }

    [TestMethod]
    public void Sanitize_PreservesCjkTitlesVerbatim()
    {
        Assert.AreEqual("会议纪要截图", FileNameSanitizer.Sanitize("会议纪要截图"));
        Assert.AreEqual("2026年度报表", FileNameSanitizer.Sanitize("2026年度报表"));
    }

    [TestMethod]
    public void Sanitize_TrimsEdgeWhitespaceDotsAndPunctuation()
    {
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize(" 名字. ."));
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize("名字。"));
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize("，名字；"));
    }

    [TestMethod]
    public void Sanitize_StripsNestedWrappingQuotes()
    {
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize("\"名字\""));
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize("“名字”"));
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize("\"‘名字’\""), "多层包裹引号逐层剥净。");
        // 引号必须成对包裹：孤立引号走删除而非剥除路径，不影响内文。
        Assert.AreEqual("a名字b", FileNameSanitizer.Sanitize("a“名字”b"));
    }

    [TestMethod]
    public void Sanitize_TakesFirstLineOnly()
    {
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize("名字\r\n第二行解释"));
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize("名字\n解释"));
        Assert.AreEqual("名字", FileNameSanitizer.Sanitize("名字\r尾巴"));
    }

    [TestMethod]
    public void Sanitize_RemovesPathSeparators()
    {
        Assert.AreEqual("etcpasswd", FileNameSanitizer.Sanitize("../../etc/passwd"));
        Assert.AreEqual("foldername", FileNameSanitizer.Sanitize("folder\\name"));
    }

    [TestMethod]
    public void Sanitize_EmptyAfterCleaning_ReturnsNull()
    {
        Assert.IsNull(FileNameSanitizer.Sanitize("???"), "非法字符删空必须放弃。");
        Assert.IsNull(FileNameSanitizer.Sanitize("。、；"), "纯句读剪空必须放弃。");
    }
}
