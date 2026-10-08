using Backend.Cascade;
using Backend.Personas;
using Backend.Realtime;
using Backend.Prompts;
using Backend.Tools;

namespace Backend.Sessions;

internal sealed record CascadeProcessorOptions(
    string FoundryEndpoint,
    string AudioEndpoint,
    CascadeRateLimitSettings RateLimitSettings,
    CascadeVadConfig VadConfig,
    IReadOnlySet<string> AllowedVoices,
    string DefaultVoice,
    double EchoCooldownSeconds);

internal sealed record CascadeProcessorDependencies(
    IReadOnlyDictionary<string, PromptLoader> PromptLoaders,
    IToolExecutor ToolExecutor,
    HttpClient HttpClient,
    ILogger<CascadeProcessor> Logger,
    IUpstreamBearerTokenProvider? BearerTokenProvider = null,
    Func<Persona, PromptLoader?, string?, IToolExecutor>? ToolExecutorFactory = null,
    TimeProvider? TimeProvider = null,
    SessionManager? SessionManager = null);
