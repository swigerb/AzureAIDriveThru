using Backend.Configuration;
using Backend.Prompts;

namespace Backend.Search;

internal sealed record SearchToolOptions(
    SearchEndpointConfig EndpointConfig,
    SearchConfig SearchConfig,
    PromptLoader? PromptLoader,
    string IndexName,
    string? PersonaId,
    ISearchBearerTokenProvider? BearerTokenProvider = null,
    string? MenuMode = null,
    Func<string, string?>? EffectiveMachineStatus = null);
