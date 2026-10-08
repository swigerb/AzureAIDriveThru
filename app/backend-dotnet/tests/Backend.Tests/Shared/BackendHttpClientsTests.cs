using System.Net;
using Backend.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Backend.Tests.Shared;

/// <summary>
/// Regression test for Rick's #335 review: <see cref="BackendHttpClients.AddBackendHttpClients"/>
/// must call <c>RemoveAllLoggers()</c> so its named clients don't log every request under
/// "System.Net.Http.HttpClient.{name}.*" (AddHttpClient's default LoggingHttpMessageHandler/
/// LoggingScopeHttpMessageHandler pipeline) -- the plain `new HttpClient()` instances these
/// replaced never logged anything, and matching that is required to keep log output unchanged.
///
/// Mutation-check: removing <c>.RemoveAllLoggers()</c> from <c>AddBackendHttpClients</c> makes
/// <see cref="AddBackendHttpClients_DoesNotLogRequestsUnderHttpClientCategory"/> fail (it then
/// captures "System.Net.Http.HttpClient.*" entries from the fake request below) -- verified by
/// hand for this revision.
/// </summary>
public sealed class BackendHttpClientsTests
{
    /// <summary>Captures every log entry across every category/logger instance a
    /// <see cref="ILoggerFactory"/> creates, so a test can assert on category name without a real
    /// console/file sink.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(string category, List<(string, LogLevel, string)> entries) : ILogger
        {
            IDisposable? ILogger.BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add((category, logLevel, formatter(state, exception)));
        }
    }

    /// <summary>Returns an immediate 200 OK without touching the network, so the test exercises
    /// the real handler pipeline (including any logging handlers) without real I/O.</summary>
    private sealed class ImmediateOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    [Fact]
    public async Task AddBackendHttpClients_DoesNotLogRequestsUnderHttpClientCategory()
    {
        const string clientName = "test-client";
        var provider = new RecordingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(provider));
        services.AddBackendHttpClients(TimeSpan.FromMinutes(10), clientName);
        // Overrides AddBackendHttpClients' SocketsHttpHandler with a fake one for this test --
        // IHttpClientFactory evaluates the last-registered primary-handler factory for a given
        // name, so this doesn't affect which RemoveAllLoggers() call is under test.
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => new ImmediateOkHandler());

        await using var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(clientName);

        using var response = await client.GetAsync(
            new Uri("http://backend-http-clients-test.invalid/"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(provider.Entries, e => e.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal));
    }
}
