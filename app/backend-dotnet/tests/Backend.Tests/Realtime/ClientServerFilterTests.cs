using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>
/// Rick's #230 review item 3: rtmt.py validates `event_id`/`response_id`/`audio` with
/// `_CLIENT_EVENT_ID_RE.fullmatch(...)`/`_CLIENT_BASE64_RE.fullmatch(...)` -- Python's
/// `fullmatch` requires the ENTIRE string to be consumed, with no exception for a trailing
/// newline. <see cref="ClientServerFilter.Filter"/> used `$`-anchored patterns with plain
/// <c>Regex.IsMatch</c>, which (absent <c>RegexOptions.Multiline</c>/<c>Singleline</c>) also
/// matches the position immediately before a single trailing "\n" -- so a forged value ending in
/// a newline wrongly passed validation here while Python correctly rejects it. These pin the
/// `\z`-anchored fix directly against the three call sites that use these patterns, independent
/// of (and faster/more deterministic than) the equivalent over-the-wire conformance row in
/// <c>Scenarios/Security/ClientToServerAllowListTests.cs</c>.
/// </summary>
public sealed class ClientServerFilterTests
{
    private static JsonObject InputAudioBufferClear(JsonNode? eventId) => new()
    {
        ["type"] = "input_audio_buffer.clear",
        ["event_id"] = eventId,
    };

    [Fact]
    public void EventIdWithTrailingNewline_IsStrippedNotTheWholeFrame()
    {
        var message = InputAudioBufferClear("abc123\n");

        var filtered = ClientServerFilter.Filter(message, hooksEnabled: false);

        Assert.NotNull(filtered);
        Assert.False(filtered!.TryGetPropertyValue("event_id", out _),
            "A trailing-newline event_id must be stripped, exactly like any other malformed shape -- not forwarded, and not treated as valid just because '$' alone would match before the newline.");
    }

    [Fact]
    public void EventIdWithNoTrailingNewline_StillSurvives()
    {
        // Sanity check on the other side of the fix: a genuinely valid 1-64 char id (no
        // newline) must still pass -- the fix must not become overly strict.
        var message = InputAudioBufferClear("abc123");

        var filtered = ClientServerFilter.Filter(message, hooksEnabled: false);

        Assert.NotNull(filtered);
        Assert.True(filtered!.TryGetPropertyValue("event_id", out var eventId));
        Assert.Equal("abc123", eventId!.GetValue<string>());
    }

    [Fact]
    public void ResponseIdWithTrailingNewline_DropsTheWholeFrame()
    {
        var message = new JsonObject
        {
            ["type"] = "response.cancel",
            ["response_id"] = "resp_abc123\n",
        };

        var filtered = ClientServerFilter.Filter(message, hooksEnabled: false);

        Assert.Null(filtered);
    }

    [Fact]
    public void AudioWithTrailingNewline_DropsTheWholeFrame()
    {
        // "AAA=" alone is valid base64 -- only the trailing newline should make this invalid.
        var message = new JsonObject
        {
            ["type"] = "input_audio_buffer.append",
            ["audio"] = "AAA=\n",
        };

        var filtered = ClientServerFilter.Filter(message, hooksEnabled: false);

        Assert.Null(filtered);
    }
}
