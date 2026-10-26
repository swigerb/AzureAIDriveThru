using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// End-to-end proof that the two Kestrel fakes actually honour a fixed port when one is
/// requested (PR #22 review item 16), rather than always letting the OS pick one. This is what
/// makes <see cref="ExternalModePortPolicy"/>'s resolved ports meaningful in practice: without
/// this, external mode could resolve valid ports and still silently bind to something else.
/// </summary>
[Trait("Category", "Harness")]
[Trait("Dotnet", "n/a-harness")] // Issue #21: backend-agnostic harness self-test, never exercises app/backend or app/backend-dotnet.
public sealed class FakeFixedPortBindingTests
{
    [Fact]
    public async Task FakeRealtimeUpstreamServer_binds_to_the_requested_fixed_port()
    {
        int requestedPort = NetworkUtils.GetFreeTcpPort();
        await using var realtime = new FakeRealtimeUpstreamServer();

        await realtime.StartAsync(requestedPort, TestContext.Current.CancellationToken);

        Assert.Equal(requestedPort, realtime.BaseUri.Port);
    }

    [Fact]
    public async Task FakeSearchServer_binds_to_the_requested_fixed_port()
    {
        int requestedPort = NetworkUtils.GetFreeTcpPort();
        string repoRoot = RepoPaths.FindRepoRoot();
        IReadOnlyDictionary<string, string> indexPaths = MenuIndex.ResolveIndexPaths(
            RepoPaths.PersonasDirectory(repoRoot), [ConformancePersonas.DefaultPersonaId]);
        await using var search = new FakeSearchServer(indexPaths);

        await search.StartAsync(requestedPort, TestContext.Current.CancellationToken);

        Assert.Equal(requestedPort, search.BaseUri.Port);
    }

    [Fact]
    public async Task FakeRealtimeUpstreamServer_still_lets_the_OS_pick_a_port_when_none_requested()
    {
        await using var realtime = new FakeRealtimeUpstreamServer();

        await realtime.StartAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(0, realtime.BaseUri.Port);
    }
}
