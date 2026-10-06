using System.Globalization;
using System.Text.Json.Nodes;

namespace Backend.Prompts;

/// <summary>
/// Converts YamlDotNet's untyped deserialisation graph (nested
/// <c>IDictionary&lt;object, object&gt;</c> / <c>IEnumerable&lt;object&gt;</c> / scalars) into a
/// <see cref="JsonNode"/> tree, so a persona pack's YAML-authored data (tool schemas, greeting,
/// hints) can be embedded directly into an outgoing realtime wire frame. There is no YAML-native
/// concept on the wire -- everything the browser/upstream sockets exchange is JSON -- so this is
/// the one conversion point <see cref="PromptLoader"/>'s consumers (issue #13's RealtimeProcessor)
/// need.
/// </summary>
public static class YamlJson
{
    public static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        IDictionary<object, object> map => ToJsonObject(map),
        IEnumerable<object> list => ToJsonArray(list),
        bool b => JsonValue.Create(b),
        // WithAttemptingUnquotedStringTypeDeserialization picks the smallest numeric type that fits
        // (\3\ comes back as Byte), so every integral and floating type must map to a JSON number.
        byte or sbyte or short or ushort or int or uint or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong u => JsonValue.Create(u),
        float or double or decimal => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(value.ToString()),
    };

    private static JsonObject ToJsonObject(IDictionary<object, object> map)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in map)
        {
            obj[key.ToString() ?? ""] = ToJsonNode(value);
        }
        return obj;
    }

    private static JsonArray ToJsonArray(IEnumerable<object> list)
    {
        var array = new JsonArray();
        foreach (var item in list)
        {
            array.Add(ToJsonNode(item));
        }
        return array;
    }
}
