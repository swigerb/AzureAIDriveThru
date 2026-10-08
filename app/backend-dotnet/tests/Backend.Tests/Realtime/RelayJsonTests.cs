using System.Text;
using System.Text.Json;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>PR #140 round-2 review (N2): pins <see cref="RelayJson.ParseRelayFrame"/>'s strict
/// parse directly, so "strict parsing is on" is provable independent of either relay loop's
/// per-frame catch. Mutation: dropping <c>AllowDuplicateProperties = false</c> from the helper
/// must fail <see cref="ParseRelayFrame_NestedDuplicateKey_ThrowsJsonException"/>.</summary>
public sealed class RelayJsonTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void ParseRelayFrame_NestedDuplicateKey_ThrowsJsonException()
    {
        var payload = Utf8("""{"type":"session.update","session":{"voice":"a","voice":"b"}}""");

        Assert.ThrowsAny<JsonException>(() => RelayJson.ParseRelayFrame(payload));
    }

    [Fact]
    public void ParseRelayFrame_TopLevelDuplicateKey_ThrowsJsonException()
    {
        var payload = Utf8("""{"type":"a","type":"b"}""");

        Assert.Throws<JsonException>(() => RelayJson.ParseRelayFrame(payload));
    }

    [Fact]
    public void ParseRelayFrame_NonObjectJson_ThrowsJsonException()
    {
        var payload = Utf8("[1]");

        Assert.Throws<JsonException>(() => RelayJson.ParseRelayFrame(payload));
    }

    [Fact]
    public void ParseRelayFrame_ValidObject_ReturnsJsonObject()
    {
        var payload = Utf8("""{"type":"input_audio_buffer.append","audio":"AAAA"}""");

        var result = RelayJson.ParseRelayFrame(payload);

        Assert.Equal("input_audio_buffer.append", result["type"]!.GetValue<string>());
        Assert.Equal("AAAA", result["audio"]!.GetValue<string>());
    }
}
