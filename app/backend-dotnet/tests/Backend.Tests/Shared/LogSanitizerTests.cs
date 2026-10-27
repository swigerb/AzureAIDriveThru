using System.Globalization;
using Backend.Shared;

namespace Backend.Tests.Shared;

/// <summary>
/// Unit tests for <see cref="LogSanitizer"/>, the CWE-117 guard applied at every logging call site
/// whose value is controlled by the browser (query string, request headers, WebSocket frame
/// fields) or by the model (tool names, call ids). Hostile payloads are built from escapes inside
/// the test bodies rather than theory data, so test display names stay free of raw control
/// characters.
/// </summary>
public sealed class LogSanitizerTests
{
    private const string Ellipsis = "...";

    [Theory]
    [InlineData("")]
    [InlineData("acme")]
    [InlineData("gpt-realtime-2.1-mini")]
    [InlineData("localhost:5173")]
    [InlineData("[::1]:5000")]
    [InlineData("https://capps-dotnet-pwvzk3t22wttm.orangeisland-69fbd3a0.eastus2.azurecontainerapps.io")]
    [InlineData("printable ASCII !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~")]
    [InlineData("caf\u00E9 \u65E5\u672C\u8A9E \U0001F354")]
    public void Sanitize_CleanValue_IsReturnedAsTheSameInstance(string value) =>
        Assert.Same(value, LogSanitizer.Sanitize(value));

    [Fact]
    public void Sanitize_Null_StaysNull() => Assert.Null(LogSanitizer.Sanitize(null));

    [Fact]
    public void Sanitize_ForgedLogLine_CannotStartAnotherRow()
    {
        string result = LogSanitizer.Sanitize("acme\r\nfail: Backend[0] forged entry");

        Assert.Equal("acme__fail: Backend[0] forged entry", result);
        Assert.DoesNotContain("\r", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_AnsiEscapeSequence_LosesItsEscapeCharacter() =>
        Assert.Equal("_[2J_[31mred", LogSanitizer.Sanitize("\u001B[2J\u001B[31mred"));

    [Fact]
    public void Sanitize_BidiOverride_CannotReorderTheRestOfTheLine() =>
        Assert.Equal("invoice_txt.exe", LogSanitizer.Sanitize("invoice\u202Etxt.exe"));

    [Theory]
    [InlineData(0x0000)] // NUL
    [InlineData(0x0009)] // TAB
    [InlineData(0x000A)] // LF
    [InlineData(0x000B)] // VT
    [InlineData(0x000C)] // FF
    [InlineData(0x000D)] // CR
    [InlineData(0x001B)] // ESC
    [InlineData(0x001F)] // US
    [InlineData(0x007F)] // DEL
    [InlineData(0x0080)] // first C1 control
    [InlineData(0x0085)] // NEL, treated as a line break by several consumers
    [InlineData(0x009F)] // last C1 control
    [InlineData(0x00AD)] // soft hyphen (Cf)
    [InlineData(0x200B)] // zero-width space (Cf)
    [InlineData(0x200E)] // left-to-right mark (Cf)
    [InlineData(0x200F)] // right-to-left mark (Cf)
    [InlineData(0x2028)] // LINE SEPARATOR (Zl)
    [InlineData(0x2029)] // PARAGRAPH SEPARATOR (Zp)
    [InlineData(0x202A)] // left-to-right embedding (Cf)
    [InlineData(0x202E)] // right-to-left override (Cf)
    [InlineData(0x2060)] // word joiner (Cf)
    [InlineData(0x2066)] // left-to-right isolate (Cf)
    [InlineData(0x2069)] // pop directional isolate (Cf)
    [InlineData(0xFEFF)] // byte-order mark / zero-width no-break space (Cf)
    [InlineData(0xD800)] // lone high surrogate
    [InlineData(0xDBFF)] // lone high surrogate, last
    [InlineData(0xDC00)] // lone low surrogate
    [InlineData(0xDFFF)] // lone low surrogate, last
    public void Sanitize_UnsafeCodeUnit_IsReplacedWithAnUnderscore(int codeUnit) =>
        Assert.Equal("a_b", LogSanitizer.Sanitize($"a{(char)codeUnit}b"));

    [Fact]
    public void Sanitize_EveryBasicMultilingualPlaneCharacter_FollowsTheUnicodeCategoryRule()
    {
        List<string> failures = [];
        for (int unit = 0; unit <= char.MaxValue; unit++)
        {
            char c = (char)unit;
            string input = $"a{c}b";
            string actual = LogSanitizer.Sanitize(input);

            bool mustReplace = char.IsSurrogate(c)
                || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control
                    or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator;
            bool correct = mustReplace ? actual == "a_b" : ReferenceEquals(actual, input);
            if (!correct)
            {
                failures.Add(unit.ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Sanitize_SupplementaryPlaneFormatCharacter_IsReplacedPerUtf16Unit()
    {
        // U+E0041 TAG LATIN CAPITAL LETTER A is invisible (category Cf) and encodes as a surrogate pair.
        string tag = char.ConvertFromUtf32(0xE0041);

        Assert.Equal(2, tag.Length);
        Assert.Equal("a__b", LogSanitizer.Sanitize($"a{tag}b"));
    }

    [Fact]
    public void Sanitize_UnpairedSurrogates_AreReplaced()
    {
        Assert.Equal("ab_", LogSanitizer.Sanitize("ab\uD800"));
        Assert.Equal("a_b", LogSanitizer.Sanitize("a\uD800b"));
        Assert.Equal("__", LogSanitizer.Sanitize("\uDC00\uD800"));
    }

    [Fact]
    public void Sanitize_FullLengthHostName_IsKept()
    {
        // 253 characters is the longest legal DNS name; a real Host header must never be altered.
        string host = string.Join(
            '.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));

        Assert.Equal(253, host.Length);
        Assert.Same(host, LogSanitizer.Sanitize(host));
    }

    [Fact]
    public void Sanitize_ExactlyMaxLength_IsReturnedAsTheSameInstance()
    {
        string value = new('x', LogSanitizer.MaxLength);

        Assert.Same(value, LogSanitizer.Sanitize(value));
    }

    [Fact]
    public void Sanitize_OneOverMaxLength_IsCutAndMarked() =>
        Assert.Equal(
            new string('x', LogSanitizer.MaxLength) + Ellipsis,
            LogSanitizer.Sanitize(new string('x', LogSanitizer.MaxLength + 1)));

    [Fact]
    public void Sanitize_VeryLongValue_IsBoundedAtMaxLengthPlusMarker()
    {
        string result = LogSanitizer.Sanitize(new string('y', 1_000_000));

        Assert.Equal(LogSanitizer.MaxLength + Ellipsis.Length, result.Length);
        Assert.EndsWith(Ellipsis, result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_CutThroughASurrogatePair_DropsTheDanglingHalf()
    {
        // The emoji's high surrogate would be the 256th kept character with its low half cut off.
        string value = new string('x', LogSanitizer.MaxLength - 1) + "\U0001F354tail";

        Assert.Equal(new string('x', LogSanitizer.MaxLength - 1) + Ellipsis, LogSanitizer.Sanitize(value));
    }

    [Fact]
    public void Sanitize_SurrogatePairEndingExactlyAtTheLimit_IsKeptWhole()
    {
        string value = new string('x', LogSanitizer.MaxLength - 2) + "\U0001F354tail";

        Assert.Equal(
            new string('x', LogSanitizer.MaxLength - 2) + "\U0001F354" + Ellipsis, LogSanitizer.Sanitize(value));
    }

    [Fact]
    public void Sanitize_ControlCharactersInsideTheLimit_AreReplacedBeforeTheCut()
    {
        string value = "a\r\n" + new string('x', LogSanitizer.MaxLength);

        Assert.Equal(
            "a__" + new string('x', LogSanitizer.MaxLength - 3) + Ellipsis, LogSanitizer.Sanitize(value));
    }

    [Fact]
    public void Sanitize_ControlCharactersBeyondTheLimit_AreCutAway()
    {
        string value = new string('x', LogSanitizer.MaxLength) + "\r\nforged";

        Assert.Equal(new string('x', LogSanitizer.MaxLength) + Ellipsis, LogSanitizer.Sanitize(value));
    }
}
