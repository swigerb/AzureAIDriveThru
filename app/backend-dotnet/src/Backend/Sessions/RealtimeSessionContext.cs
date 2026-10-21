using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Shared;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Per-connection state shared by the realtime client and server relay handlers.
/// </summary>
internal sealed class RealtimeSessionContext
{
    public required RealtimeProcessor Processor { get; init; }
    public required WebSocket BrowserSocket { get; init; }
    public required WebSocket Upstream { get; init; }
    public required Persona Persona { get; init; }
    public required ResolvedModel ResolvedModel { get; init; }
    public required string? MenuMode { get; init; }
    public required string SessionId { get; init; }
    public required string? SystemMessage { get; init; }
    public required PromptLoader? PromptLoader { get; init; }
    public required IReadOnlyList<JsonObject> ToolSchemas { get; init; }
    public required Overridable<bool?> ReasoningOverride { get; init; }
    public required RealtimeProcessor.RealtimeSessionState State { get; init; }
    public required RealtimeSessionConfig SessionConfig { get; init; }
    public required SessionManager? SessionManager { get; init; }
    public required GreetingGate GreetingGate { get; init; }
    public required ToolCallDispatcher ToolCallDispatcher { get; init; }
    public required ResumeCoordinator ResumeCoordinator { get; init; }
    public required IReadOnlySet<string> AllowedVoices { get; init; }
    public required ILogger Logger { get; init; }
    public required TimeProvider TimeProvider { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public JsonObject BuildVoiceUpdateFrame(string newVoice) => new()
    {
        ["type"] = "session.update",
        ["event_id"] = EventIds.NewEventId("sonic_voice"),
        ["session"] = GaSessionTranslator.ToGaSession(new JsonObject { ["voice"] = newVoice }),
    };

    public double NowSeconds() => TimeProvider.GetTimestamp() / (double)TimeProvider.TimestampFrequency;
}
