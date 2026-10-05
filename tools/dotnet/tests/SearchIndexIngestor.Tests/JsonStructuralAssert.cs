using System.Text.Json;
using System.Text.Json.Nodes;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// Structural (not literal-string) JSON equality -- duplicated from
/// SearchIndexRequestBuilder.Tests/JsonStructuralAssert.cs (tools/dotnet has no shared test-support
/// project yet). See that file's own remarks for the full rationale (object key-set-not-order
/// equality, array order DOES matter, numeric int/float-shape sensitivity).
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

        if (expectedKind == JsonValueKind.Number && actualKind == JsonValueKind.Number)
        {
            var expectedNumber = double.Parse(expected.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            var actualNumber = double.Parse(actual.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(
                expectedNumber == actualNumber,
                $"At {path}: number differs. expected {expectedNumber}, actual {actualNumber}.");

            Assert.True(
                IsFloatShaped(expected) == IsFloatShaped(actual),
                $"At {path}: number {expectedNumber} is written as an int in one JSON body and a "
                + "float in the other (one has a '.'/'e'/'E', the other doesn't) -- Azure AI "
                + "Search's REST API can treat these differently even though they're numerically equal.");
            return;
        }

        Assert.True(expectedKind == actualKind, $"At {path}: JSON value kind differs. expected {expectedKind}, actual {actualKind}.");
        switch (expectedKind)
        {
            case JsonValueKind.String:
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

    private static bool IsFloatShaped(JsonValue value)
    {
        if (value.TryGetValue(out JsonElement element))
        {
            return element.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0;
        }
        return value.TryGetValue(out double _);
    }
}
