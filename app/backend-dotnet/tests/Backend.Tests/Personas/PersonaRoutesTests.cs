using System.Reflection;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Tests.TestSupport;

namespace Backend.Tests.Personas;

public sealed class PersonaRoutesTests
{
    [Fact]
    public void Persona_asset_route_maps_mp3_to_audio_mpeg()
    {
        FieldInfo? field = typeof(PersonaRoutes).GetField("s_assetContentTypes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        IReadOnlyDictionary<string, string> contentTypes = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(field.GetValue(null));

        Assert.Equal("audio/mpeg", contentTypes[".mp3"]);
    }

    [Fact]
    public void Persona_detail_body_includes_machines_and_happy_hour_in_camel_case()
    {
        MethodInfo? method = typeof(PersonaRoutes).GetMethod("BuildPersonaDetailBody", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Persona alpha = DeltaFixture.Load("test-alpha");
        Persona beta = DeltaFixture.Load("test-beta");
        Persona delta = DeltaFixture.Load("test-delta");
        var models = ModelCatalog.FromConfig(AppConfig.Load());

        JsonObject alphaBody = Assert.IsType<System.Text.Json.Nodes.JsonObject>(method.Invoke(null, [alpha, models]));
        JsonObject betaBody = Assert.IsType<System.Text.Json.Nodes.JsonObject>(method.Invoke(null, [beta, models]));
        JsonObject deltaBody = Assert.IsType<System.Text.Json.Nodes.JsonObject>(method.Invoke(null, [delta, models]));

        JsonObject alphaMachines = Assert.IsType<System.Text.Json.Nodes.JsonObject>(alphaBody["machines"]);
        JsonObject alphaSoda = Assert.IsType<System.Text.Json.Nodes.JsonObject>(alphaMachines["soda_machine"]);
        Assert.Equal("down", alphaSoda["status"]?.GetValue<string>());
        Assert.Equal("Soda machine is down", alphaSoda["label"]?.GetValue<string>());

        JsonObject betaMachines = Assert.IsType<System.Text.Json.Nodes.JsonObject>(betaBody["machines"]);
        Assert.Equal("up", Assert.IsType<System.Text.Json.Nodes.JsonObject>(betaMachines["soda_machine"])["status"]?.GetValue<string>());

        JsonObject alphaHappyHour = Assert.IsType<System.Text.Json.Nodes.JsonObject>(alphaBody["happyHour"]);
        Assert.Equal(14, alphaHappyHour["startHour"]?.GetValue<int>());
        Assert.Equal(16, alphaHappyHour["endHour"]?.GetValue<int>());

        JsonObject deltaMachines = Assert.IsType<System.Text.Json.Nodes.JsonObject>(deltaBody["machines"]);
        Assert.Equal("down", Assert.IsType<System.Text.Json.Nodes.JsonObject>(deltaMachines["delta_machine"])["status"]?.GetValue<string>());
        Assert.True(deltaBody.ContainsKey("happyHour"));
        Assert.Null(deltaBody["happyHour"]);
    }
}
