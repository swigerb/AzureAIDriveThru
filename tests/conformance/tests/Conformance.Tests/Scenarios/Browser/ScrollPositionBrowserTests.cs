using Conformance.Harness;
using Microsoft.Playwright;
using Xunit;

namespace Conformance.Tests.Scenarios.Browser;

/// <summary>
/// Issue #176: on a fresh load at desktop widths (1280-1440px) the page scrolled 725-1010px past
/// the hero with no user input. Root cause: transcript-panel.tsx called
/// <c>scrollIntoView()</c> on mount (even with an empty transcript) -- which scrolls every
/// scrollable ancestor needed to bring its target into view, including the window itself, rather
/// than just its own transcript container. The fix makes the panel own its scrollable container
/// directly (<c>container.scrollTop = container.scrollHeight</c>) and only ever scroll it when
/// NEW transcript entries arrive, never on mount.
///
/// This proves the fix end to end against the real built frontend (served by the real backend
/// under test, <see cref="BrowserConformanceFixture"/>'s Development pass-through shape): a fresh
/// load at 1440x900 must never move <c>window.scrollY</c> away from 0, for every persona this
/// suite's backend actually has enabled. <see cref="DiscoveredPersonaIds"/> mirrors
/// <see cref="RealPackPersonaSmokeTests.DiscoveredPersonaIds"/>'s own
/// <see cref="ConformancePersonas.DiscoverFromDisk()"/> convention, rather than a hardcoded
/// persona list, so a future persona pack under personas/ is covered automatically.
/// </summary>
[Collection(BrowserConformanceCollection.Name)]
[Trait("Category", "Browser")]
public sealed class ScrollPositionBrowserTests(BrowserConformanceFixture fixture)
{
    public static TheoryData<string> DiscoveredPersonaIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in ConformancePersonas.DiscoverFromDisk())
        {
            data.Add(id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DiscoveredPersonaIds))]
    public Task Fresh_load_at_1440x900_never_scrolls_the_window(string personaId) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var context = await fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
        }).ConfigureAwait(false);
        await using var _ = context;
        var page = await context.NewPageAsync().ConfigureAwait(false);

        var url = new UriBuilder(fixture.Backend!.BaseUri) { Query = $"persona={personaId}" }.Uri;
        await page.GotoAsync(url.ToString()).ConfigureAwait(false);

        // No in-page signal tells a black-box caller "layout has fully settled" (same rationale
        // as this suite's other UntilAsync polls) -- but here we're proving a negative (nothing
        // EVER moves scrollY), so a short fixed settle window covers any lingering post-load
        // effect (e.g. a resize handler, async font/layout shift) before asserting.
        await Task.Delay(500, ct).ConfigureAwait(false);

        var scrollY = await page.EvaluateAsync<double>("window.scrollY").ConfigureAwait(false);
        Assert.Equal(0, scrollY);
    });
}
