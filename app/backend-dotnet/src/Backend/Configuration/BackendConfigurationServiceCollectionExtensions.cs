using Backend.Search;
using Microsoft.Extensions.Options;

namespace Backend.Configuration;

internal static class BackendConfigurationServiceCollectionExtensions
{
    public static IServiceCollection AddBackendConfigurationOptions(this IServiceCollection services)
    {
        services.AddOptions<RequiredBackendOptions>()
            .Configure(RequiredBackendOptions.ConfigureFromEnvironment)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RequiredBackendOptions>, RequiredBackendOptionsValidator>();

        services.AddOptions<SearchEndpointConfig>()
            .Configure(SearchEndpointConfig.ConfigureFromEnvironment);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<SearchEndpointConfig>>().Value);

        AddAppConfigOptions(services, BusinessRulesConfig.FromAppConfig);
        AddAppConfigOptions(services, SearchConfig.FromAppConfig);
        AddAppConfigOptions(services, SessionsConfig.FromConfig);
        AddAppConfigOptions(services, ConnectionConfig.FromConfig);
        AddAppConfigOptions(services, SecurityConfig.FromConfig);
        AddAppConfigOptions(services, AssetCacheConfig.FromConfig);

        return services;
    }

    private static void AddAppConfigOptions<TOptions>(
        IServiceCollection services,
        Func<AppConfig, TOptions> factory)
        where TOptions : class
    {
        services.AddSingleton<IOptions<TOptions>>(sp => Options.Create(factory(sp.GetRequiredService<AppConfig>())));
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<TOptions>>().Value);
    }
}
