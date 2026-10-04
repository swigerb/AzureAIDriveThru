using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder;

/// <summary>
/// Byte-for-byte match of Python's <c>json.dumps(value)</c> with its DEFAULT arguments (issue #16:
/// setup_search_index.py's <c>prepare_documents</c> calls <c>json.dumps(item["sizes"])</c> with no
/// extra arguments -- the opposite default from update_menu_sizes.py's own output file, which
/// passes <c>ensure_ascii=False</c>; see UpdateMenuSizes/PythonJsonEncoder.cs for that case).
/// Default <c>json.dumps</c> behaviour reproduced here:
/// <list type="bullet">
/// <item>separators are <c>", "</c> between items and <c>": "</c> between a key and its value
/// (Python's default <c>(item_separator, key_separator)</c> when <c>indent</c> is <c>None</c>).</item>
/// <item><c>ensure_ascii=True</c> (the default): every character outside printable ASCII is
/// escaped as <c>\uXXXX</c>, in addition to the usual <c>"</c>/<c>\</c>/control-character escapes.
/// Python's C-accelerated encoder does this per UTF-16 code unit, so an astral character becomes a
/// \uXXXX\uYYYY surrogate pair -- which is exactly what iterating a .NET <see cref="string"/> as
/// <c>char</c> (UTF-16 code units, not Unicode scalars) and escaping each one individually already
/// produces, with no extra surrogate-pair handling needed.</item>
/// <item>a JSON number token that was parsed from source text containing <c>.</c>, <c>e</c>, or
/// <c>E</c> is a Python <c>float</c> and is re-serialized via float repr (see
/// <see cref="PythonFloatRepr"/>); one without any of those is a Python <c>int</c> and is written
/// back byte-for-byte unchanged -- same convention as
/// UpdateMenuSizes/MenuSizeUpdater.cs's <c>NormalizeNumberLiteralsLikePythonJsonDump</c>.</item>
/// </list>
/// </summary>
internal static class PythonJsonDumps
{
    /// <summary>Serializes <paramref name="node"/> the way Python's <c>json.dumps(node)</c>
    /// (default arguments) would, for embedding as a JSON STRING value (e.g. the "sizes" document
    /// field, which Azure AI Search stores as Edm.String, not a nested object).</summary>
    public static string Serialize(JsonNode? node)
    {
        var builder = new StringBuilder();
        Write(node, builder);
        return builder.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;
            case JsonObject obj:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in obj)
                {
                    if (!firstProperty)
                    {
                        builder.Append(", ");
                    }
                    firstProperty = false;
                    WriteString(property.Key, builder);
                    builder.Append(": ");
                    Write(property.Value, builder);
                }
                builder.Append('}');
                break;
            case JsonArray arr:
                builder.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }
                    Write(arr[i], builder);
                }
                builder.Append(']');
                break;
            case JsonValue value:
                WriteValue(value, builder);
                break;
        }
    }

    private static void WriteValue(JsonValue value, StringBuilder builder)
    {
        if (!value.TryGetValue(out JsonElement element))
        {
            throw new NotSupportedException(
                "Expected a JsonValue backed by JsonElement (i.e. produced by JsonNode.Parse).");
        }
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                WriteString(element.GetString()!, builder);
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            case JsonValueKind.Number:
                WriteNumber(element, builder);
                break;
            default:
                throw new NotSupportedException($"Unsupported JSON value kind '{element.ValueKind}'.");
        }
    }

    private static void WriteNumber(JsonElement element, StringBuilder builder)
    {
        var raw = element.GetRawText();
        if (raw.IndexOfAny(['.', 'e', 'E']) >= 0)
        {
            builder.Append(PythonFloatRepr(double.Parse(raw, CultureInfo.InvariantCulture)));
        }
        else
        {
            // A Python int -- written back exactly as the source text had it (no trailing ".0").
            builder.Append(raw);
        }
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                default:
                    if (c is >= (char)0x20 and <= (char)0x7E)
                    {
                        builder.Append(c);
                    }
                    else
                    {
                        // ensure_ascii=True: every other code unit (C0 controls not handled above,
                        // and anything outside printable ASCII) becomes \uXXXX -- including each
                        // half of an astral character's surrogate pair individually.
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    break;
            }
        }
        builder.Append('"');
    }

    /// <summary>Same algorithm as UpdateMenuSizes/MenuSizeUpdater.cs's <c>PythonFloatRepr</c>
    /// (duplicated rather than shared -- tools/dotnet has no common library project yet, see
    /// RepoRoot.cs): the shortest decimal string that round-trips to the same IEEE-754 double, with
    /// at least one digit after the point (Python's float repr always shows ".0" for a whole
    /// number; .NET's default shortest-round-trip double formatting does not).</summary>
    internal static string PythonFloatRepr(double value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'e', 'E']) < 0 ? text + ".0" : text;
    }
}
