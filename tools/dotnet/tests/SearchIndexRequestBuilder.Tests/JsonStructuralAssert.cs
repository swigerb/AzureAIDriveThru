using System.Text.Json;
using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Structural (not literal-string) JSON equality: same keys with the same values recursively
/// (object property order irrelevant, array element order DOES matter -- these are ordered
/// request bodies, e.g. the "fields" array or a document batch's "value" array). Used instead of
/// byte/string comparison because these are ephemeral HTTP request bodies -- never written to
/// disk or diffed by a human/git, unlike update_menu_sizes.py's output file or
/// extract_production_items.py's stdout report, where literal byte-identity is the whole point.
/// String-valued leaves (including the "sizes" field, itself a JSON-encoded string written by
/// setup_search_index.py's own <c>json.dumps(item["sizes"])</c>, line ~279) ARE compared
/// character-for-character here, since a JSON string is still just a string -- so
/// PythonJsonDumps.cs's ensure_ascii escaping is exercised exactly as rigorously as a literal
/// byte comparison would, for that one field.
/// </summary>
internal static class JsonStructuralAssert
{
    public static void Equal(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (expected is null || actual is null)
        {
            Assert.True(expected is null && actual is null, $"At {path}: one side is null, the other is not.");
            return;
        }

        switch (expected)
        {
            case JsonObject expectedObject:
                var actualObject = Assert.IsType<JsonObject>(actual, exactMatch: false);
                var expectedKeys = expectedObject.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var actualKeys = actualObject.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
                Assert.True(
                    expectedKeys.SequenceEqual(actualKeys),
                    $"At {path}: key sets differ.\nexpected: [{string.Join(", ", expectedKeys)}]\nactual:   [{string.Join(", ", actualKeys)}]");
                foreach (var key in expectedKeys)
                {
                    Equal(expectedObject[key], actualObject[key], $"{path}.{key}");
                }
                break;

            case JsonArray expectedArray:
                var actualArray = Assert.IsType<JsonArray>(actual, exactMatch: false);
                Assert.True(
                    expectedArray.Count == actualArray.Count,
                    $"At {path}: array length differs. expected {expectedArray.Count}, actual {actualArray.Count}.");
                for (var i = 0; i < expectedArray.Count; i++)
                {
                    Equal(expectedArray[i], actualArray[i], $"{path}[{i}]");
                }
                break;

            case JsonValue expectedValue:
                var actualValue = Assert.IsType<JsonValue>(actual, exactMatch: false);
                AssertValuesEqual(expectedValue, actualValue, path);
                break;

            default:
                throw new InvalidOperationException($"Unexpected JsonNode subtype at {path}: {expected.GetType()}.");
        }
    }

    private static void AssertValuesEqual(JsonValue expected, JsonValue actual, string path)
    {
        var expectedKind = expected.GetValueKind();
        var actualKind = actual.GetValueKind();

        // A whole-number float (e.g. Python's json.load turning "200" into an int, "200.0" into a
        // float) must still compare equal to its C# counterpart regardless of which JsonValueKind
        // System.Text.Json happened to box it as -- this test cares about the REST body's semantic
        // number value, not whether the in-memory JsonNode is flagged as an integer or a double.
        if (expectedKind == JsonValueKind.Number && actualKind == JsonValueKind.Number)
        {
            // JsonValue.GetValue<double>() throws unless the underlying boxed CLR type is
            // EXACTLY double (no widening from int/long) when the node wasn't parsed from a
            // JsonElement -- which is exactly the case for every programmatically-built C# node
            // here (e.g. JsonValue.Create(10)). Round-tripping through the JSON text itself sidesteps
            // that entirely and works for any numeric node on either side.
            var expectedNumber = double.Parse(expected.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            var actualNumber = double.Parse(actual.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(
                expectedNumber == actualNumber,
                $"At {path}: number differs. expected {expectedNumber}, actual {actualNumber}.");
            return;
        }

        Assert.True(expectedKind == actualKind, $"At {path}: JSON value kind differs. expected {expectedKind}, actual {actualKind}.");
        switch (expectedKind)
        {
            case JsonValueKind.String:
                // Exact character-for-character comparison -- this is the one place a formatting
                // divergence (e.g. PythonJsonDumps.cs's ensure_ascii escaping landing on the wrong
                // code units) would otherwise slip through a looser "ToString()"-based comparison.
                Assert.Equal(expected.GetValue<string>(), actual.GetValue<string>());
                break;
            case JsonValueKind.True or JsonValueKind.False:
                Assert.Equal(expected.GetValue<bool>(), actual.GetValue<bool>());
                break;
            case JsonValueKind.Null:
                break;
            default:
                throw new InvalidOperationException($"Unexpected JsonValueKind at {path}: {expectedKind}.");
        }
    }
}
