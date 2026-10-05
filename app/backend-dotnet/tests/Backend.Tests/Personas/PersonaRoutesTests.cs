using System.Reflection;
using Backend.Configuration;
using Backend.Personas;
using Backend.Tests.TestSupport;

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

    [Fact]
    public void Persona_detail_body_includes_machines_and_happy_hour_in_camel_case()
    {
        var method = typeof(PersonaRoutes).GetMethod("BuildPersonaDetailBody", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var alpha = DeltaFixture.Load("test-alpha");
        var beta = DeltaFixture.Load("test-beta");
        var delta = DeltaFixture.Load("test-delta");
        var models = ModelCatalog.FromConfig(AppConfig.Load());

        var alphaBody = Assert.IsType<System.Text.Json.Nodes.JsonObject>(method.Invoke(null, new object?[] { alpha, models }));
        var betaBody = Assert.IsType<System.Text.Json.Nodes.JsonObject>(method.Invoke(null, new object?[] { beta, models }));
        var deltaBody = Assert.IsType<System.Text.Json.Nodes.JsonObject>(method.Invoke(null, new object?[] { delta, models }));

        var alphaMachines = Assert.IsType<System.Text.Json.Nodes.JsonObject>(alphaBody["machines"]);
        var alphaSoda = Assert.IsType<System.Text.Json.Nodes.JsonObject>(alphaMachines["soda_machine"]);
        Assert.Equal("down", alphaSoda["status"]?.GetValue<string>());
        Assert.Equal("Soda machine is down", alphaSoda["label"]?.GetValue<string>());

        var betaMachines = Assert.IsType<System.Text.Json.Nodes.JsonObject>(betaBody["machines"]);
        Assert.Equal("up", Assert.IsType<System.Text.Json.Nodes.JsonObject>(betaMachines["soda_machine"])["status"]?.GetValue<string>());

        var alphaHappyHour = Assert.IsType<System.Text.Json.Nodes.JsonObject>(alphaBody["happyHour"]);
        Assert.Equal(14, alphaHappyHour["startHour"]?.GetValue<int>());
        Assert.Equal(16, alphaHappyHour["endHour"]?.GetValue<int>());

        var deltaMachines = Assert.IsType<System.Text.Json.Nodes.JsonObject>(deltaBody["machines"]);
        Assert.Equal("down", Assert.IsType<System.Text.Json.Nodes.JsonObject>(deltaMachines["delta_machine"])["status"]?.GetValue<string>());
        Assert.True(deltaBody.ContainsKey("happyHour"));
        Assert.Null(deltaBody["happyHour"]);
    }
}
