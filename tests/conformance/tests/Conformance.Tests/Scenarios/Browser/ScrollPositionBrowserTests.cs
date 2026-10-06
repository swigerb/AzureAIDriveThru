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
///
/// Round 2 (#176 review item 3): <see cref="Mobile_transcript_sheet_shows_the_newest_entry_without_scrolling_the_window"/>
/// covers the opposite case at a 390px mobile width -- the Radix <c>Sheet</c> that hosts
/// <c>TranscriptPanel</c> only mounts it when opened mid-conversation, so a round-1 "never scroll
/// on mount" guard left it showing the OLDEST entries instead of the newest. This proves the
/// panel's own container ends up scrolled to the bottom on mount (newest entry visible) while
/// <c>window.scrollY</c> is still never moved -- measured AFTER Playwright's own
/// scroll-trigger-into-view (which itself moves the window, since the trigger button sits well
/// below the fold at this viewport height), the one point in the flow that would surface a
/// regression back to <c>scrollIntoView()</c>-style ancestor scrolling.
///
/// Issue #21 (Browser-on-C#): both rows of both methods (every persona <see
/// cref="DiscoveredPersonaIds"/> discovers) pass unchanged against CONFORMANCE_BACKEND=dotnet --
/// this suite only loads the built frontend from static files and reads `window.scrollY`/DOM
/// layout, none of which touches the realtime relay or order pipeline. Verified 3x locally with
/// zero flakes.
/// </summary>
[Collection(BrowserConformanceCollection.Name)]
[Trait("Category", "Browser")]
[Trait("Dotnet", "ready")]
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

    [Theory]
    [MemberData(nameof(DiscoveredPersonaIds))]
    public Task Mobile_transcript_sheet_shows_the_newest_entry_without_scrolling_the_window(string personaId) =>
        fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        var context = await fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 }
        }).ConfigureAwait(false);
        await using var _ = context;
        // "mid-conversation" (per the review item) is simulated the same deterministic way the
        // frontend's own dummy-data toggle does (dummy-data-context.tsx reads this key from
        // localStorage at mount) -- an init script survives navigation, unlike a post-load
        // localStorage.setItem, which would race the very mount this test is proving out.
        await context.AddInitScriptAsync("window.localStorage.setItem('useDummyData', 'true');").ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);

        var url = new UriBuilder(fixture.Backend!.BaseUri) { Query = $"persona={personaId}" }.Uri;
        await page.GotoAsync(url.ToString()).ConfigureAwait(false);

        // Settle exactly like the sibling fresh-load test above, then confirm the premise: at
        // this width the mobile sheet hasn't been opened yet, so TranscriptPanel (a Radix Sheet
        // child) isn't mounted at all, and nothing has moved the window either.
        await Task.Delay(500, ct).ConfigureAwait(false);
        Assert.Equal(0, await page.EvaluateAsync<double>("window.scrollY").ConfigureAwait(false));

        var trigger = page.GetByRole(AriaRole.Button, new() { Name = "Transcript", Exact = true });
        // Playwright's own ClickAsync scrolls its target into view first if needed, and the
        // trigger sits well below the fold at this viewport height -- so DO that scroll-into-view
        // explicitly first and capture window.scrollY right after it, which per review item 3 is
        // the correct baseline (that scroll is Playwright's, not this component's, and is
        // expected). Anything the sheet OPENING then moves the window by further is what must
        // never happen -- the exact regression this suite is about.
        await trigger.ScrollIntoViewIfNeededAsync().ConfigureAwait(false);
        var scrollYAfterTriggerScroll = await page.EvaluateAsync<double>("window.scrollY").ConfigureAwait(false);

        await trigger.ClickAsync().ConfigureAwait(false);

        // The sheet slides in as a dialog; scope the lookup to it (rather than a bare
        // ".overflow-auto" class selector) so this can never accidentally match the OTHER,
        // permanently-present-but-"hidden" desktop TranscriptPanel instance in the same DOM.
        var dialog = page.GetByRole(AriaRole.Dialog);
        var scrollContainer = dialog.Locator(".overflow-auto");
        await scrollContainer.WaitForAsync().ConfigureAwait(false);

        // "Shows the newest entry" means scrolled to the bottom of its OWN container -- checked
        // generically (scrollTop at its max, scrollHeight minus clientHeight) rather than against
        // any persona's hardcoded transcript text, so this covers every discovered persona pack
        // without being coupled to any one pack's demo copy.
        var (scrollTop, maxScrollTop) = await scrollContainer.EvaluateAsync<double[]>(
            "el => [el.scrollTop, el.scrollHeight - el.clientHeight]").ConfigureAwait(false) is { Length: 2 } pair
                ? (pair[0], pair[1])
                : throw new InvalidOperationException("Expected a 2-element [scrollTop, maxScrollTop] array.");
        Assert.True(
            Math.Abs(scrollTop - maxScrollTop) <= 1,
            $"Expected the mobile transcript sheet's container to be scrolled to the bottom (scrollTop {scrollTop} ~= max {maxScrollTop}).");

        var scrollYAfterOpen = await page.EvaluateAsync<double>("window.scrollY").ConfigureAwait(false);
        Assert.Equal(scrollYAfterTriggerScroll, scrollYAfterOpen);
    });
}
