using Backend.Configuration;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Shared;
using Backend.Tools;

namespace Backend.Sessions;

internal sealed record RealtimeProcessorOptions(
    string DefaultDeployment,
    string UpstreamEndpoint,
    string UpstreamApiKey,
    RealtimeSessionConfig SessionConfig,
    IReadOnlySet<string> AllowedVoices,
    double EchoCooldownSeconds,
    double GreetingTimeoutSeconds,
    RateLimitSettings RateLimitSettings,
    ConnectionConfig ConnectionConfig);

internal sealed record RealtimeProcessorDependencies(
    IReadOnlyDictionary<string, PromptLoader> PromptLoaders,
    IToolExecutor ToolExecutor,
    ILogger<RealtimeProcessor> Logger,
    ILogger<RateLimitRecovery> RateLimitLogger,
    ILogger<NudgeScheduler> NudgeLogger,
    IUpstreamBearerTokenProvider? BearerTokenProvider = null,
    Func<Persona, PromptLoader?, string?, IToolExecutor>? ToolExecutorFactory = null,
    TimeProvider? TimeProvider = null,
    SessionManager? SessionManager = null);
