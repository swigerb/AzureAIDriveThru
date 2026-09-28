using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>Byte-for-byte port tests for rtmt.py's `_build_session` /
/// `build_bootstrap_session_update` / `build_fallback_session_update` -- mirrors
/// app/backend/tests/test_session_bootstrap.py's `ReasoningAndTranscriptionConfigTests` cases.</summary>
public sealed class RealtimeSessionBuilderTests
{
    private static readonly JsonObject UpdateOrderTool = new()
    {
        ["type"] = "function",
        ["name"] = "update_order",
    };

    private static RealtimeSessionConfig Config(
        string deployment = "gpt-realtime-2.1",
        string? reasoningEffort = null,
        bool? parallelToolCalls = null,
        bool? reasoningModel = null,
        bool reasoningRejected = false) => new()
        {
            Deployment = deployment,
            SystemMessage = "You are Sonic.",
            VoiceChoice = "marin",
            TranscriptionModel = "whisper-1",
            ReasoningEffort = reasoningEffort,
            ParallelToolCalls = parallelToolCalls,
            ReasoningModel = reasoningModel,
            ReasoningRejected = reasoningRejected,
        };

    private static JsonObject Bootstrap(RealtimeSessionConfig config) =>
        RealtimeSessionBuilder.BuildBootstrapSessionUpdate(config, [UpdateOrderTool])["session"]!.AsObject();

    [Fact]
    public void Bootstrap_ReasoningNotSentWhenUnconfigured()
    {
        var session = Bootstrap(Config());

        Assert.Null(session["reasoning"]);
        Assert.Null(session["parallel_tool_calls"]);
    }

    [Fact]
    public void Bootstrap_ReasoningSentOnAReasoningDeployment()
    {
        var session = Bootstrap(Config(reasoningEffort: "low", parallelToolCalls: false));

        Assert.Equal("low", session["reasoning"]!["effort"]!.GetValue<string>());
        Assert.False(session["parallel_tool_calls"]!.GetValue<bool>());

        var noneEffort = Bootstrap(Config(reasoningEffort: "none"));
        Assert.Equal("none", noneEffort["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("gpt-realtime-1.5")]
    [InlineData("gpt-realtime")]
    [InlineData("gpt-realtime-2025-08-28")]
    [InlineData("gpt-realtime-mini")]
    [InlineData("gpt-4o-realtime-preview")]
    public void Bootstrap_RollbackToNonReasoningDeploymentNeverSendsReasoning(string deployment)
    {
        var config = Config(deployment, reasoningEffort: "high", parallelToolCalls: true);

        var session = Bootstrap(config);

        Assert.Null(session["reasoning"]);
        Assert.Null(session["parallel_tool_calls"]);
        Assert.Equal("update_order", session["tools"]![0]!["name"]!.GetValue<string>());
        Assert.False(config.ReasoningEnabled(default));
    }

    [Fact]
    public void Bootstrap_ClientCannotInjectReasoning()
    {
        var config = Config("gpt-realtime-1.5");
        var clientSession = new JsonObject
        {
            ["reasoning"] = new JsonObject { ["effort"] = "high" },
            ["parallel_tool_calls"] = true,
        };

        var session = RealtimeSessionBuilder.BuildSession(config, clientSession, [UpdateOrderTool]);

        Assert.Null(session["reasoning"]);
        Assert.Null(session["parallel_tool_calls"]);
    }

    [Fact]
    public void Bootstrap_RuntimeRejectionStopsReasoning()
    {
        var config = Config(reasoningEffort: "low", reasoningRejected: true);
        Assert.Null(Bootstrap(config)["reasoning"]);

        // A live rejection beats even the explicit switch.
        config.ReasoningModel = true;
        Assert.Null(Bootstrap(config)["reasoning"]);
    }

    [Theory]
    // (deployment, config reasoning_model switch, expect reasoning sent?)
    [InlineData("gpt-realtime-2.1", null, true)]
    [InlineData("gpt-realtime-1.5", null, false)]
    [InlineData("gpt-realtime-2.1", false, false)]
    [InlineData("carhop-prod", null, true)] // unknown name: assumed reasoning (fallback guards it)
    [InlineData("gpt-4o-carhop", null, false)]
    [InlineData("gpt-4o-carhop", true, true)] // explicit switch wins over the name heuristic
    public void Bootstrap_ExplicitReasoningModelSwitchBeatsTheNameCheck(string deployment, bool? reasoningModel, bool sent)
    {
        var config = Config(deployment, reasoningEffort: "low", parallelToolCalls: false, reasoningModel: reasoningModel);

        var session = Bootstrap(config);

        Assert.Equal(sent, session["reasoning"] is not null);
        Assert.Equal(sent, session["parallel_tool_calls"] is not null);
        Assert.Equal(sent, config.ReasoningEnabled(default));
        Assert.Equal("update_order", session["tools"]![0]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("off")]
    [InlineData(null)]
    public void Bootstrap_EffortOffOrEmptyNeverSendsReasoningEvenWhenForced(string? effort)
    {
        var config = Config(reasoningEffort: effort, reasoningModel: true);

        var session = Bootstrap(config);

        Assert.Null(session["reasoning"]);
        Assert.Null(session["parallel_tool_calls"]);
    }

    [Fact]
    public void Bootstrap_ShapeMatchesTheClientSessionDefaultsPlusOverlay()
    {
        var config = Config();

        var update = RealtimeSessionBuilder.BuildBootstrapSessionUpdate(config, [UpdateOrderTool]);

        Assert.Equal("session.update", update["type"]!.GetValue<string>());
        Assert.StartsWith("sonic_bootstrap_", update["event_id"]!.GetValue<string>());
        var session = update["session"]!.AsObject();
        Assert.Equal("realtime", session["type"]!.GetValue<string>());
        Assert.Equal("You are Sonic.", session["instructions"]!.GetValue<string>());
        Assert.Equal("marin", session["audio"]!["output"]!["voice"]!.GetValue<string>());
        Assert.NotNull(session["audio"]!["input"]!["turn_detection"]);
        Assert.NotNull(session["audio"]!["input"]!["transcription"]);
        Assert.Equal("auto", session["tool_choice"]!.GetValue<string>());
        Assert.Single(session["tools"]!.AsArray());
    }

    [Fact]
    public void Fallback_OnlyCarriesTypeInstructionsToolsToolChoice()
    {
        var config = Config();

        var update = RealtimeSessionBuilder.BuildFallbackSessionUpdate(config, [UpdateOrderTool]);

        Assert.Equal("session.update", update["type"]!.GetValue<string>());
        Assert.StartsWith("sonic_fallback_", update["event_id"]!.GetValue<string>());
        var session = update["session"]!.AsObject();
        var keys = session.Select(kvp => kvp.Key).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "instructions", "tool_choice", "tools", "type" }, keys);
        Assert.Null(session["audio"]);
    }

    [Fact]
    public void Fallback_NeverCarriesAVoiceEvenWhenOneIsConfigured()
    {
        var config = Config();

        var update = RealtimeSessionBuilder.BuildFallbackSessionUpdate(config, [UpdateOrderTool]);

        Assert.Null(update["session"]!["audio"]);
    }
}
