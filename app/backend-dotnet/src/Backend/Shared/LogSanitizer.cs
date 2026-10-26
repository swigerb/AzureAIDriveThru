using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Backend.Shared;

/// <summary>
/// Neutralises a value that arrived from outside the process before it is handed to an
/// <see cref="ILogger"/> message template (CWE-117, log injection). The default console formatter
/// writes a CR/LF inside a message verbatim (continuation lines are only indented), so an attacker
/// who controls a logged value could otherwise start extra, forged-looking rows in Log Analytics.
/// Wrap the value at the call site -- the <c>[LoggerMessage]</c> templates themselves stay
/// untouched, because conformance rows match on their text.
/// <para>
/// Use it for anything that crosses the trust boundary: query-string values, request headers,
/// fields of browser WebSocket frames, and model-generated tool names / call ids. Server-issued or
/// already-validated values (session ids, catalog ids that passed a lookup, values that went
/// through <c>ClientServerFilter</c>) do not need it.
/// </para>
/// </summary>
internal static class LogSanitizer
{
    /// <summary>Longest value that is logged in full; anything longer is cut and marked. Generous
    /// enough for a full DNS host name (253 chars), so a legitimate Host/Origin is never altered.</summary>
    public const int MaxLength = 256;

    private const string TruncationMarker = "...";

    /// <summary>
    /// Returns <paramref name="value"/> with every control, format (including bidi overrides and
    /// zero-width characters), line-separator and paragraph-separator code point replaced by
    /// <c>_</c>, cut to <see cref="MaxLength"/> characters plus a <c>...</c> marker. A value that
    /// needs no change is returned as the same instance, so the common case allocates nothing.
    /// <c>null</c> stays <c>null</c> so the logger still renders it as its usual "(null)".
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static string? Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        ReadOnlySpan<char> span = value;
        int keep = Math.Min(span.Length, MaxLength);
        if (keep < span.Length && char.IsHighSurrogate(span[keep - 1]))
        {
            keep--;
        }

        bool truncated = keep < span.Length;
        if (!truncated && !ContainsUnsafe(span))
        {
            return value;
        }

        return string.Create(
            truncated ? keep + TruncationMarker.Length : keep,
            (value, keep, truncated),
            static (destination, state) =>
            {
                ReadOnlySpan<char> source = state.value.AsSpan(0, state.keep);
                for (int i = 0; i < source.Length;)
                {
                    bool unsafeRune = IsUnsafeAt(source, i, out int consumed);
                    for (int j = 0; j < consumed; j++)
                    {
                        destination[i + j] = unsafeRune ? '_' : source[i + j];
                    }

                    i += consumed;
                }

                if (state.truncated)
                {
                    TruncationMarker.CopyTo(destination[state.keep..]);
                }
            });
    }

    private static bool ContainsUnsafe(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length;)
        {
            if (IsUnsafeAt(span, i, out int consumed))
            {
                return true;
            }

            i += consumed;
        }

        return false;
    }

    // Printable ASCII (the overwhelmingly common case) never reaches the Unicode tables. Anything
    // else is decoded as a whole scalar so supplementary-plane format characters (for example the
    // invisible tag block) are caught as well; an unpaired surrogate is unsafe too.
    private static bool IsUnsafeAt(ReadOnlySpan<char> span, int index, out int consumed)
    {
        char c = span[index];
        if (c is >= ' ' and < '\u007F')
        {
            consumed = 1;
            return false;
        }

        if (c < ' ')
        {
            consumed = 1;
            return true;
        }

        if (Rune.DecodeFromUtf16(span[index..], out Rune rune, out consumed) != OperationStatus.Done)
        {
            consumed = Math.Max(consumed, 1);
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }
}
