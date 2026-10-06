using Microsoft.Extensions.DependencyInjection;

namespace Backend.Shared;

/// <summary>
/// Registers the backend's long-lived named <see cref="IHttpClientFactory"/> clients. Pulled out
/// of Program.cs's top-level statements into its own static method so the registration itself --
/// in particular, the <c>RemoveAllLoggers()</c> call below -- is directly testable with a plain
/// <see cref="IServiceCollection"/>, without needing a full ASP.NET Core host.
/// </summary>
internal static class BackendHttpClients
{
    /// <summary>Adds one named <see cref="HttpClient"/> registration per entry in
    /// <paramref name="clientNames"/>, each on its own <see cref="SocketsHttpHandler"/> with
    /// <paramref name="pooledConnectionLifetime"/>.</summary>
    public static void AddBackendHttpClients(
        this IServiceCollection services, TimeSpan pooledConnectionLifetime, params ReadOnlySpan<string> clientNames)
    {
        foreach (var clientName in clientNames)
        {
            services.AddHttpClient(clientName)
                .ConfigurePrimaryHttpMessageHandler(
                    () => new SocketsHttpHandler { PooledConnectionLifetime = pooledConnectionLifetime })
                // AddHttpClient's default handler pipeline inserts LoggingHttpMessageHandler/
                // LoggingScopeHttpMessageHandler, which log every request at Information (and the
                // full exception on failure) under "System.Net.Http.HttpClient.{name}.*". The
                // plain `new HttpClient()` instances these replaced never logged anything, so
                // RemoveAllLoggers() is required here to keep log output unchanged.
                .RemoveAllLoggers();
        }
    }
}
