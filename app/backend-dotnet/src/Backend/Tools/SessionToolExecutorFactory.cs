using Backend.Configuration;
using Backend.Ordering;
using Backend.Personas;
using Backend.Prompts;
using Backend.Search;
using Backend.Shared;

namespace Backend.Tools;

/// <summary>
/// Creates the session-bound tool stack (`OrderState` + `OrderToolExecutor` + `SearchTool`) while
/// reusing the process-wide configuration and shared HttpClient plumbing registered at startup.
/// </summary>
internal sealed class SessionToolExecutorFactory(
    BusinessRulesConfig businessRulesConfig,
    SearchConfig searchConfig,
    SearchEndpointConfig searchEndpointConfig,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory)
{
    public IToolExecutor Create(Persona sessionPersona, PromptLoader? sessionPromptLoader, string? sessionMenuMode)
    {
        var menu = PersonaOrderFactory.GetMenuCatalog(sessionPersona);
        var orderState = PersonaOrderFactory.CreateOrderState(sessionPersona);
        var orderTools = new OrderToolExecutor(
            orderState,
            menu,
            sessionPromptLoader,
            businessRulesConfig.MaxItemQuantity,
            businessRulesConfig.MaxOrderItems,
            sessionMenuMode);
        var searchTool = new SearchTool(
            httpClientFactory.CreateClient(BackendHttpClientNames.SearchEndpoint),
            searchEndpointConfig,
            searchConfig,
            menu,
            sessionPromptLoader,
            sessionPersona.Search.IndexName,
            sessionPersona.Id,
            bearerTokenProvider: null,
            logger: loggerFactory.CreateLogger(nameof(SearchTool)),
            menuMode: sessionMenuMode,
            effectiveMachineStatus: orderState.EffectiveMachineStatus);
        return new SessionToolExecutor(orderTools, searchTool);
    }
}
