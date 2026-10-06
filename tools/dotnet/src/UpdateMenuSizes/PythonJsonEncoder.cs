using System.Globalization;
using System.Text.Encodings.Web;

namespace UpdateMenuSizes;

/// <summary>
/// Byte-for-byte match of Python's <c>json.dump(..., ensure_ascii=False)</c> string escaping
/// (issue #16, PR #224 review R2): escapes ONLY <c>"</c>, <c>\</c>, and the C0 control characters
/// U+0000-U+001F (using JSON's short forms <c>\b \t \n \f \r</c> where they exist, lowercase
/// <c>\u00xx</c> otherwise) -- nothing else. In particular this does NOT escape non-ASCII
/// characters (emoji, NBSP, (R), curly quotes, ...), which is the entire point of
/// <c>ensure_ascii=False</c>: the Python twin writes those raw as UTF-8 bytes.
///
/// Neither of System.Text.Json's built-in encoders matches this: the default encoder escapes all
/// non-ASCII characters (the opposite of <c>ensure_ascii=False</c>), and even
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> still unconditionally escapes
/// supplementary-plane scalars (emoji) and a handful of specific code points (e.g. NBSP, U+2028,
/// U+2029) for XSS-mitigation reasons that don't apply to a local JSON data file -- confirmed by
/// direct comparison against Python's own output for the same inputs. A custom encoder is the only
/// way to reproduce Python's narrower escape set exactly.
/// </summary>
internal sealed class PythonJsonEncoder : JavaScriptEncoder
{
    public static readonly PythonJsonEncoder Instance = new();

    private PythonJsonEncoder()
    {
    }

    /// <summary>Worst case is a lone C0 control character becoming <c>\u00xx</c> (6 chars).</summary>
    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (var i = 0; i < textLength; i++)
        {
            if (RequiresEscape(text[i]))
            {
                return i;
            }
        }
        return -1;
    }

    public override bool WillEncode(int unicodeScalar) => RequiresEscape(unicodeScalar);

    public override unsafe bool TryEncodeUnicodeScalar(
        int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var shortForm = unicodeScalar switch
        {
            '"' => '"',
            '\\' => '\\',
            '\b' => 'b',
            '\t' => 't',
            '\n' => 'n',
            '\f' => 'f',
            '\r' => 'r',
            _ => '\0',
        };

        if (shortForm != '\0')
        {
            if (bufferLength < 2)
            {
                numberOfCharactersWritten = 0;
                return false;
            }
            buffer[0] = '\\';
            buffer[1] = shortForm;
            numberOfCharactersWritten = 2;
            return true;
        }

        if (unicodeScalar is >= 0x00 and <= 0x1F)
        {
            if (bufferLength < 6)
            {
                numberOfCharactersWritten = 0;
                return false;
            }
            buffer[0] = '\\';
            buffer[1] = 'u';
            var hex = unicodeScalar.ToString("x4", CultureInfo.InvariantCulture);
            buffer[2] = hex[0];
            buffer[3] = hex[1];
            buffer[4] = hex[2];
            buffer[5] = hex[3];
            numberOfCharactersWritten = 6;
            return true;
        }

        numberOfCharactersWritten = 0;
        return false;
    }

    private static bool RequiresEscape(int unicodeScalar) =>
        unicodeScalar == '"' || unicodeScalar == '\\' || unicodeScalar is >= 0x00 and <= 0x1F;
}
