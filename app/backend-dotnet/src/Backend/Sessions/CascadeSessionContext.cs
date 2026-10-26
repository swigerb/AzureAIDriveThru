using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Backend.Cascade;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Tools;

namespace Backend.Sessions;

/// <summary>
/// Per-connection state shared by Cascade's resume and receive coordinators.
/// </summary>
internal sealed class CascadeSessionContext
{
    public required WebSocket BrowserSocket { get; init; }
    public required Persona Persona { get; init; }
    public required ResolvedModel ResolvedModel { get; init; }
    public required string? MenuMode { get; init; }
    public required CancellationTokenSource LinkedCts { get; init; }
    public required CancellationToken CancellationToken { get; init; }
    public required SessionManager? SessionManager { get; init; }
    public required CascadeProcessor.CascadeSessionState State { get; init; }
    public required PromptLoader? PromptLoader { get; init; }
    public required TurnDetector Detector { get; init; }
    public CascadeToolCallDispatcher ToolCallDispatcher { get; set; } = null!;
    public CascadeAudioTurnPipeline AudioTurnPipeline { get; set; } = null!;
    public required IReadOnlySet<string> AllowedVoices { get; init; }
    public required ILogger Logger { get; init; }
    public required TimeProvider TimeProvider { get; init; }
    public required object TurnRegistryLock { get; init; }
    public CascadeTurnCoordinator TurnCoordinator { get; set; } = null!;
    public double NowSeconds() => TimeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
    public Task NotifyClientAsync(JsonObject frame, CancellationToken turnCt) =>
        CascadeProcessor.SendTextAsync(BrowserSocket, frame.ToJsonString(), turnCt, CancellationToken);
    public Task SendGreetingAsync(CancellationToken ct) => TurnCoordinator.SendGreetingAsync(ct);
    public Task ProcessTurnAsync(byte[] audio, CancellationToken ct) => TurnCoordinator.ProcessTurnAsync(audio, ct);
    public required string SessionId { get; set; }
    public required IToolExecutor ToolExecutor { get; set; }
    public required SessionIdentifiers Identifiers { get; set; }
    public Task<WebSocketFrame?>? PendingFirstFrameTask { get; set; }
    public CascadeDiagnosticCapture? Diagnostic { get; set; }
}
