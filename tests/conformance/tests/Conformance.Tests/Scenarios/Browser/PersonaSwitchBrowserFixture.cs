using Conformance.Fakes;
using Conformance.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Conformance.Tests.Scenarios.Browser;

/// <summary>
/// Issue #171 round 2 (Rick's PR #173 round-1 review item 4 -- REQUIRED, not optional): the bug
/// is a timing race across real browser WebSocket semantics, React's async persona-detail fetch,
/// and the server's close -- the hand-built-FakeWebSocket unit tests in
/// useRealtime.socketRace.test.tsx prove the hook's own guards hold against a controlled clock,
/// but only a REAL browser driving the REAL built frontend against a REAL backend can prove the
/// fix survives actual network/event-loop timing. Dedicated fixture/collection (not reusing
/// <see cref="BrowserConformanceCollection"/>, which is already pinned to ONE shared
/// <see cref="BrowserConformanceFixture"/> instance per collection, and that instance's inner
/// <see cref="BrowserConformanceFixture.BrowserTimersBackendFixture"/> never overrides
/// Personas/Persona/PersonasDir) so this suite can point at the test-alpha/test-beta fixture pack
/// exactly like <see cref="TwoPersonaConformanceFixture"/>, while still getting the Browser
/// category's Development-mode pass-through profile and headless-browser-launch shape from
/// <see cref="BrowserConformanceFixture"/>'s own established pattern -- same reasoning as every
/// other profile/persona override fixture in this project (PERSONAS/DEFAULT_PERSONA/PERSONAS_DIR
/// are read once at Python module-import time, so a profile+persona combination this specific
/// needs its own dedicated process).
/// </summary>
internal sealed class PersonaSwitchBackendFixture : ConformanceFixture
{
    public const string PersonaA = TwoPersonaConformanceFixture.PersonaA; // "test-alpha"
    public const string PersonaB = TwoPersonaConformanceFixture.PersonaB; // "test-beta"

    protected override BackendProfile Profile => BackendProfiles.BrowserTimersDevelopment;
    protected override bool UseEntraMode => false;
    protected override IReadOnlyList<string>? Personas => [PersonaA, PersonaB];
    protected override string? Persona => PersonaA;
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
}

/// <summary>
/// Thin re-derivation of <see cref="BrowserConformanceFixture"/>'s own browser-launch/skip/
/// benign-proactor-filter wrapper (see that type's doc comment for the full reasoning behind each
/// piece -- composition over inheritance for the same <see cref="IAsyncLifetime"/> reason, the
/// skip-vs-throw CI/local split, and the Windows ProactorEventLoop filter), just over
/// <see cref="PersonaSwitchBackendFixture"/> instead of the default single-persona backend.
/// Reuses <see cref="BrowserConformanceFixture.IsBenignProactorTeardownIncident(IReadOnlyList{string})"/>
/// directly (internal, same assembly) rather than copying that method's ~100 lines of CPython
/// root-cause documentation a second time.
/// </summary>
public sealed class PersonaSwitchBrowserFixture : IAsyncLifetime
{
    private readonly ConformanceFixture _inner = new PersonaSwitchBackendFixture();
    private string? _browserSkipReason;

    public FakeRealtimeUpstreamServer Realtime => _inner.Realtime;
    public IBackendUnderTest? Backend => _inner.Backend;

    public string? BrowserChannel { get; private set; }
    public IPlaywright? Playwright { get; private set; }
    public IBrowser? Browser { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await _inner.InitializeAsync().ConfigureAwait(false);
        if (_inner.SkipReason is not null)
        {
            return; // The backend itself is skipping -- nothing more to do.
        }

        BrowserChannel = BrowserChannelPolicy.DetectInstalledChannel();
        if (BrowserChannelPolicy.ShouldSkip(BrowserChannel, CiEnvironment.IsCi))
        {
            _browserSkipReason = BrowserChannelPolicy.BuildMessage(BrowserChannel, CiEnvironment.IsCi);
            return;
        }
        if (BrowserChannel is null)
        {
            // Not skip-eligible (this looks like CI) -- fail every test in the collection loudly.
            throw new InvalidOperationException(BrowserChannelPolicy.BuildMessage(BrowserChannel, CiEnvironment.IsCi));
        }

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = BrowserChannel,
            Headless = true,
            Args = ["--use-fake-ui-for-media-stream", "--use-fake-device-for-media-stream"],
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync().ConfigureAwait(false);
        }
        Playwright?.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    public async Task RunAsync(Func<Task> body)
    {
        if (_browserSkipReason is not null)
        {
            Assert.Skip(_browserSkipReason);
            return;
        }

        var filteredBefore = Backend?.UnhandledErrorCount(BrowserConformanceFixture.IsBenignProactorTeardownIncident) ?? 0;
        try
        {
            await _inner.RunAsync(body).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (
            Backend is not null &&
            ex.Message.Contains("new backend error(s) above the baseline", StringComparison.Ordinal) &&
            Backend.UnhandledErrorCount(BrowserConformanceFixture.IsBenignProactorTeardownIncident) == filteredBefore)
        {
            // See BrowserConformanceFixture.RunAsync's identical catch clause: the unfiltered
            // check just failed on *something* new, but re-counting with the benign-aware filter
            // shows the filtered count never moved -- every "new" incident was positively
            // identified as the known-benign Windows ProactorEventLoop teardown race and nothing
            // else. A genuine new backend defect always moves the filtered count too.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PersonaSwitchBrowserCollection : ICollectionFixture<PersonaSwitchBrowserFixture>
{
    public const string Name = "ConformancePersonaSwitchBrowser";
}
