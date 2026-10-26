using System.Text.Json.Nodes;
using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>Byte-for-byte port tests for rtmt.py's `_build_session` /
/// `build_bootstrap_session_update` / `build_fallback_session_update` -- mirrors
/// app/backend/tests/test_session_bootstrap.py's `ReasoningAndTranscriptionConfigTests` cases.</summary>
public sealed class RealtimeSessionBuilderTests
{
    private static readonly JsonObject s_updateOrderTool = new()
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
            SystemMessage = "You are a drive-thru assistant.",
            VoiceChoice = "marin",
            TranscriptionModel = "whisper-1",
            ReasoningEffort = reasoningEffort,
            ParallelToolCalls = parallelToolCalls,
            ReasoningModel = reasoningModel,
            ReasoningRejected = reasoningRejected,
        };

    private static JsonObject Bootstrap(RealtimeSessionConfig config) =>
        RealtimeSessionBuilder.BuildBootstrapSessionUpdate(config, [s_updateOrderTool])["session"]!.AsObject();

    [Fact]
    public void Bootstrap_ReasoningNotSentWhenUnconfigured()
    {
        JsonObject session = Bootstrap(Config());

        Assert.Null(session["reasoning"]);
        Assert.Null(session["parallel_tool_calls"]);
    }

    [Fact]
    public void Bootstrap_ReasoningSentOnAReasoningDeployment()
    {
        JsonObject session = Bootstrap(Config(reasoningEffort: "low", parallelToolCalls: false));

        Assert.Equal("low", session["reasoning"]!["effort"]!.GetValue<string>());
        Assert.False(session["parallel_tool_calls"]!.GetValue<bool>());

        JsonObject noneEffort = Bootstrap(Config(reasoningEffort: "none"));
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
        RealtimeSessionConfig config = Config(deployment, reasoningEffort: "high", parallelToolCalls: true);

        JsonObject session = Bootstrap(config);

        Assert.Null(session["reasoning"]);
        Assert.Null(session["parallel_tool_calls"]);
        Assert.Equal("update_order", session["tools"]![0]!["name"]!.GetValue<string>());
        Assert.False(config.ReasoningEnabled(default));
    }

    [Fact]
    public void Bootstrap_ClientCannotInjectReasoning()
    {
        RealtimeSessionConfig config = Config("gpt-realtime-1.5");
        var clientSession = new JsonObject
        {
            ["reasoning"] = new JsonObject { ["effort"] = "high" },
            ["parallel_tool_calls"] = true,
        };

        JsonObject session = RealtimeSessionBuilder.BuildSession(config, clientSession, [s_updateOrderTool]);

        Assert.Null(session["reasoning"]);
        Assert.Null(session["parallel_tool_calls"]);
    }

    [Fact]
    public void Bootstrap_RuntimeRejectionStopsReasoning()
    {
        RealtimeSessionConfig config = Config(reasoningEffort: "low", reasoningRejected: true);
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
        RealtimeSessionConfig config = Config(deployment, reasoningEffort: "low", parallelToolCalls: false, reasoningModel: reasoningModel);

        JsonObject session = Bootstrap(config);

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
        RealtimeSessionConfig config = Config(reasoningEffort: effort, reasoningModel: true);

        JsonObject session = Bootstrap(config);

        Assert.Null(session["reasoning"]);
        Assert.Null(session["parallel_tool_calls"]);
    }

    [Fact]
    public void Bootstrap_ShapeMatchesTheClientSessionDefaultsPlusOverlay()
    {
        RealtimeSessionConfig config = Config();

        JsonObject update = RealtimeSessionBuilder.BuildBootstrapSessionUpdate(config, [s_updateOrderTool]);

        Assert.Equal("session.update", update["type"]!.GetValue<string>());
        Assert.StartsWith("sonic_bootstrap_", update["event_id"]!.GetValue<string>());
        JsonObject session = update["session"]!.AsObject();
        Assert.Equal("realtime", session["type"]!.GetValue<string>());
        Assert.Equal("You are a drive-thru assistant.", session["instructions"]!.GetValue<string>());
        Assert.Equal("marin", session["audio"]!["output"]!["voice"]!.GetValue<string>());
        Assert.NotNull(session["audio"]!["input"]!["turn_detection"]);
        Assert.NotNull(session["audio"]!["input"]!["transcription"]);
        Assert.Equal("auto", session["tool_choice"]!.GetValue<string>());
        Assert.Single(session["tools"]!.AsArray());
    }

    /// <summary>Issue #170 (Python equivalent: ProcessMessageToServerTests'
    /// `test_session_update_uses_the_bound_sessions_system_message_not_the_deployment_default`).
    /// `BuildSession` is the client-update build path's own session builder (called directly
    /// from `RealtimeProcessor.ProcessClientMessage`). Unlike the Python side, this parameter was
    /// never a problem on this backend: `ProcessClientMessage` is a local function closing over
    /// its enclosing method's own `systemMessage` local (this bound session's own persona prompt,
    /// resolved once per connection) -- there is no separate caller that could "forget" to pass
    /// it the way Python's top-level `_process_message_to_server` could. This test pins that an
    /// explicit `systemMessage` override always wins over `config.SystemMessage` (the deployment
    /// default), so a future refactor that collapses the closure into a parameterised method
    /// cannot silently drop this threading and still pass.</summary>
    [Fact]
    public void Session_ExplicitSystemMessageOverridesTheDeploymentDefault()
    {
        RealtimeSessionConfig config = Config();
        string boundPersonaSystemMessage = "You are a different, non-default persona's crew associate.";

        JsonObject session = RealtimeSessionBuilder.BuildSession(
            config, RealtimeSessionBuilder.BootstrapClientSession(), [s_updateOrderTool],
            systemMessage: Overridable.Of<string?>(boundPersonaSystemMessage));

        Assert.Equal(boundPersonaSystemMessage, session["instructions"]!.GetValue<string>());
        Assert.NotEqual(config.SystemMessage, session["instructions"]!.GetValue<string>());
    }

    [Fact]
    public void Fallback_OnlyCarriesTypeInstructionsToolsToolChoice()
    {
        RealtimeSessionConfig config = Config();

        JsonObject update = RealtimeSessionBuilder.BuildFallbackSessionUpdate(config, [s_updateOrderTool]);

        Assert.Equal("session.update", update["type"]!.GetValue<string>());
        Assert.StartsWith("sonic_fallback_", update["event_id"]!.GetValue<string>());
        JsonObject session = update["session"]!.AsObject();
        string[] keys = [.. session.Select(kvp => kvp.Key).OrderBy(k => k)];
        Assert.Equal(new[] { "instructions", "tool_choice", "tools", "type" }, keys);
        Assert.Null(session["audio"]);
    }

    [Fact]
    public void Fallback_NeverCarriesAVoiceEvenWhenOneIsConfigured()
    {
        RealtimeSessionConfig config = Config();

        JsonObject update = RealtimeSessionBuilder.BuildFallbackSessionUpdate(config, [s_updateOrderTool]);

        Assert.Null(update["session"]!["audio"]);
    }
}
