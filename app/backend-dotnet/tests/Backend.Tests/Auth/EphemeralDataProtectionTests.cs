using Backend.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.Tests.Auth;

/// <summary>
/// <see cref="EphemeralDataProtection"/> against the real framework: a host with the app's own
/// Entra wiring (<see cref="EntraAuthentication.AddEntraAuthentication"/>, which is what pulls
/// Data Protection in) is started with and without the setup, and what the framework logs and
/// writes is compared. Every host points the key repository at a throw-away directory, so the
/// "writes nothing" assertions look at an observable instead of the user profile, and the
/// control host proves the log capture and the directory check can see a key ring being built.
/// </summary>
public sealed class EphemeralDataProtectionTests
{
    private const string DataProtectionCategoryPrefix = "Microsoft.AspNetCore.DataProtection";

    // Loopback instance: nothing here sends a request, and if one ever did it must fail fast
    // against a closed port instead of reaching the real Entra endpoint.
    private static readonly EntraSettings s_settings = new(
        EntraMode.Entra,
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222",
        "access_as_user",
        "DriveThru.User",
        "http://127.0.0.1:1/");

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _gate = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        private sealed class RecordingLogger(string category, RecordingLoggerProvider owner) : ILogger
        {
            IDisposable? ILogger.BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Add(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }

    private sealed class NoOpHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Starts a TestServer host. <paramref name="keyDirectory"/> is where a file-system key
    /// repository would write; it is always registered, as the "something earlier already set Data
    /// Protection up" case. <paramref name="withEntraAuthentication"/> is the production wiring
    /// (false mirrors Development mode, where Program.cs registers no authentication at all).
    /// <paramref name="applyEphemeralSetup"/> runs <see cref="EphemeralDataProtection.Configure"/>
    /// last, exactly as Program.cs calls it.
    /// </summary>
    private static async Task<IHost> StartHostAsync(
        RecordingLoggerProvider recorder,
        DirectoryInfo keyDirectory,
        bool withEntraAuthentication,
        bool applyEphemeralSetup,
        CancellationToken cancellationToken)
    {
        IHostBuilder hostBuilder = new HostBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(recorder))
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    if (withEntraAuthentication)
                    {
                        EntraAuthentication.AddEntraAuthentication(services, s_settings);
                    }

                    services.AddDataProtection().PersistKeysToFileSystem(keyDirectory);

                    if (applyEphemeralSetup)
                    {
                        EphemeralDataProtection.Configure(services);
                    }
                });
                webHost.Configure(static _ => { });
            });
        return await hostBuilder.StartAsync(cancellationToken);
    }

    private static List<LogEntry> DataProtectionEntries(RecordingLoggerProvider recorder) =>
        [.. recorder.Entries.Where(e => e.Category.StartsWith(DataProtectionCategoryPrefix, StringComparison.Ordinal))];

    private static string Describe(IEnumerable<LogEntry> entries) =>
        string.Join(Environment.NewLine, entries.Select(e => $"{e.Level} {e.Category}: {e.Message}"));

    private static int CountKeyRingHostedServices(IServiceCollection services) =>
        services.Count(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType?.FullName == EphemeralDataProtection.KeyRingHostedServiceTypeName);

    /// <summary>
    /// The control. Without the setup the framework builds a key ring on its own at startup --
    /// the unwanted behaviour -- and this proves the recorder and the directory check can see it,
    /// so the "silent" assertions below cannot pass because the capture is blind.
    /// </summary>
    [Fact]
    public async Task WithoutTheSetup_TheFrameworkBuildsAKeyRingAtStartup()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var recorder = new RecordingLoggerProvider();
        DirectoryInfo keys = Directory.CreateTempSubdirectory("dt-dataprotection-keys-");
        try
        {
            using IHost host = await StartHostAsync(
                recorder, keys, withEntraAuthentication: true, applyEphemeralSetup: false, ct);

            Assert.NotEmpty(DataProtectionEntries(recorder));
            Assert.NotEmpty(keys.GetFiles("key-*.xml"));
        }
        finally
        {
            keys.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithTheSetup_StartupLogsNothingFromDataProtectionAndWritesNoKey(bool withEntraAuthentication)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var recorder = new RecordingLoggerProvider();
        DirectoryInfo keys = Directory.CreateTempSubdirectory("dt-dataprotection-keys-");
        try
        {
            using IHost host = await StartHostAsync(
                recorder, keys, withEntraAuthentication, applyEphemeralSetup: true, ct);

            List<LogEntry> logged = DataProtectionEntries(recorder);
            Assert.True(logged.Count == 0, $"Expected no Data Protection log entries at startup, got:{Environment.NewLine}{Describe(logged)}");
            Assert.Empty(keys.GetFileSystemInfos());
        }
        finally
        {
            keys.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Removing the startup hosted service is only half of it: whatever resolves the provider
    /// first must get the in-memory one, not a file-system key ring created on demand.
    /// </summary>
    [Fact]
    public async Task WithTheSetup_TheProviderIsInMemoryAndRoundTripsWithoutTouchingTheKeyDirectory()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var recorder = new RecordingLoggerProvider();
        DirectoryInfo keys = Directory.CreateTempSubdirectory("dt-dataprotection-keys-");
        try
        {
            using IHost host = await StartHostAsync(
                recorder, keys, withEntraAuthentication: true, applyEphemeralSetup: true, ct);

            IDataProtectionProvider provider = host.Services.GetRequiredService<IDataProtectionProvider>();
            Assert.IsType<EphemeralDataProtectionProvider>(provider);

            IDataProtector protector = provider.CreateProtector("ephemeral-data-protection-tests");
            string protectedPayload = protector.Protect("payload");
            Assert.NotEqual("payload", protectedPayload);
            Assert.Equal("payload", protector.Unprotect(protectedPayload));

            Assert.Empty(keys.GetFileSystemInfos());
            List<LogEntry> keyRingEntries = [.. DataProtectionEntries(recorder).Where(e =>
                e.Category.EndsWith(".XmlKeyManager", StringComparison.Ordinal)
                || e.Category.EndsWith(".FileSystemXmlRepository", StringComparison.Ordinal))];
            Assert.True(keyRingEntries.Count == 0, $"Expected no key-ring activity, got:{Environment.NewLine}{Describe(keyRingEntries)}");
        }
        finally
        {
            keys.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The hosted service is internal to the framework, so it is matched by name. The "before"
    /// count pins that name to the real registration (a framework rename fails here instead of
    /// quietly turning the removal into a no-op), and nothing else may be removed with it.
    /// </summary>
    [Fact]
    public void Configure_RemovesTheFrameworkKeyRingHostedServiceAndOnlyThat()
    {
        var services = new ServiceCollection();
        EntraAuthentication.AddEntraAuthentication(services, s_settings);
        services.AddHostedService<NoOpHostedService>();
        int hostedServicesBefore = services.Count(d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(1, CountKeyRingHostedServices(services));

        EphemeralDataProtection.Configure(services);

        Assert.Equal(0, CountKeyRingHostedServices(services));
        Assert.Equal(hostedServicesBefore - 1, services.Count(d => d.ServiceType == typeof(IHostedService)));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(NoOpHostedService));
    }

    /// <summary>
    /// Keyed, instance and factory registrations have no implementation type to match (it is
    /// <c>null</c> for all three on net11.0), so none can be mistaken for the framework's service:
    /// they must survive untouched, and scanning them must not throw.
    /// </summary>
    [Fact]
    public void Configure_LeavesKeyedInstanceAndFactoryHostedServicesAlone()
    {
        var services = new ServiceCollection();
        EntraAuthentication.AddEntraAuthentication(services, s_settings);
        services.AddKeyedSingleton<IHostedService, NoOpHostedService>("keyed");
        services.AddSingleton<IHostedService>(new NoOpHostedService());
        services.AddSingleton<IHostedService>(static _ => new NoOpHostedService());
        int unrelatedBefore = services.Count(d => d.ServiceType == typeof(IHostedService)) - CountKeyRingHostedServices(services);

        EphemeralDataProtection.Configure(services);

        Assert.Equal(unrelatedBefore, services.Count(d => d.ServiceType == typeof(IHostedService)));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.IsKeyedService);
    }
}
