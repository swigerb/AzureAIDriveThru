using System.Reflection;
using Backend.Personas;

namespace Backend.Tests.Personas;

public sealed class PersonaRoutesTests
{
    [Fact]
    public void Persona_asset_route_maps_mp3_to_audio_mpeg()
    {
        var field = typeof(PersonaRoutes).GetField("AssetContentTypes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var contentTypes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(field.GetValue(null));

        Assert.Equal("audio/mpeg", contentTypes[".mp3"]);
    }
}
