using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backend.Realtime;

/// <summary>
/// PR #140 round-2 review (N2): the strict-parse step shared by both realtime relay loops
/// (client→server in <see cref="Backend.Sessions.RealtimeProcessor"/>'s browser→upstream loop,
/// server→client in its upstream→browser loop). Extracted so exactly one code path controls
/// <see cref="JsonDocumentOptions.AllowDuplicateProperties"/>, and one unit test
/// (<c>Backend.Tests/Realtime/RelayJsonTests.cs</c>) can pin that it stays strict, instead of the
/// per-frame `catch` masking the setting from any test that only observes the loop's outcome.
/// </summary>
internal static class RelayJson
{
    /// <summary>Parses a relay frame's payload as strict JSON: <c>AllowDuplicateProperties =
    /// false</c> makes <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
    /// throw <see cref="JsonException"/> immediately for a duplicate key at any depth (top-level
    /// as well as nested), instead of deferring to a later lazy-dictionary access.</summary>
    /// <exception cref="JsonException">The payload is not valid JSON, has a duplicate key at any
    /// depth, or does not parse to a JSON object.</exception>
    internal static JsonObject ParseRelayFrame(byte[] payload) =>
        JsonNode.Parse(
                Encoding.UTF8.GetString(payload),
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false })
            as JsonObject
        ?? throw new JsonException("frame was not a JSON object");
}
