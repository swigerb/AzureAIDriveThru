using System.Collections.Concurrent;
using System.Text.Json;
using Conformance.Fakes;
using Conformance.Harness;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Conformance.Tests.Scenarios.Browser;

/// <summary>
/// A Content-Security-Policy that blocks something the single-page app needs is the usual way these
/// headers break a release, and nothing in <c>Backend.Tests</c> can see it: that suite pins the policy
/// text, not what a browser does with it. This runs the real built frontend, served by the real
/// backend under test (<see cref="BrowserConformanceFixture"/>'s Development shape: the policy is on,
/// HSTS is not), in a real browser and fails on any CSP violation.
///
/// What each row proves:
/// <list type="bullet">
/// <item><description><see cref="Every_persona_loads_under_the_policy_with_no_violation_and_reaches_the_realtime_relay"/>:
/// the shell, the persona theme (it injects a Google Fonts <c>&lt;link&gt;</c> for a persona that
/// declares a web font) and the same-origin <c>ws://</c> relay socket, for every persona on disk. A
/// persona that brings a new external origin fails here as well as in the <c>Backend.Tests</c>
/// persona-pack guard.</description></item>
/// <item><description><see cref="Recording_a_turn_on_the_default_persona_raises_no_violation"/>: the
/// microphone, both audio worklets and mic frames reaching the relay.</description></item>
/// <item><description><see cref="Settings_and_the_dark_mode_switch_raise_no_violation"/>: the
/// <c>&lt;style&gt;</c> that Radix's scroll lock injects as the dialog opens (one of the reasons
/// <c>style-src</c> carries <c>'unsafe-inline'</c>; the persona theme's <c>.dark</c> rules are
/// another) and the dark-mode path, where the bundled-but-unused Dark Reader would start to violate
/// the policy if it were ever wired up.</description></item>
/// <item><description><see cref="The_browser_enforces_the_policy_and_the_recorder_sees_what_it_blocks"/>:
/// the negative control. A clean run proves nothing unless the browser really enforces the policy and
/// the recorder really sees a block, so this provokes one of each kind on purpose.</description></item>
/// <item><description><see cref="The_app_can_frame_its_own_origin_but_a_foreign_page_cannot_frame_it"/>:
/// <c>frame-ancestors 'self'</c> and <c>X-Frame-Options: SAMEORIGIN</c> block a foreign embedder yet
/// still let the app load inside itself, which is the shape of MSAL's silent-renewal iframe. The
/// foreign embedder is a real second origin on loopback with a control frame of its own. An earlier
/// version framed from a routed, made-up host instead and kept passing with the policy opened to
/// <c>frame-ancestors *</c>: the child never loaded from there (probably Chromium's private-network
/// rules), so "refused" proved nothing.</description></item>
/// </list>
///
/// The Development fixture has no MSAL (the SPA is built <c>VITE_AUTH_MODE=Development</c>), so the
/// Entra sign-in and silent-renewal paths are verified against production, not here.
///
/// C#-only: the python backend sends no Content-Security-Policy, so every row skips on that leg.
/// </summary>
[Collection(BrowserConformanceCollection.Name)]
[Trait("Category", "Browser")]
[Trait("Dotnet", "ready")]
public sealed class SecurityHeadersBrowserTests(BrowserConformanceFixture fixture)
{
    private const string CSharpOnly =
        "C#-only hardening (Backend/Http/SecurityHeaders.cs); the python backend sends no Content-Security-Policy.";

    private const string StartRecording = "Start recording";

    // Runs in every frame before any page script. A violation is reported on a task of its own, so
    // the array can be read at any later point.
    private const string RecordViolations = """
        (() => {
          window.__csp = [];
          document.addEventListener('securitypolicyviolation', e =>
            window.__csp.push(`${e.effectiveDirective} <- ${e.blockedURI}`), true);
        })();
        """;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    public static TheoryData<string> DiscoveredPersonaIds()
    {
        var data = new TheoryData<string>();
        foreach (string id in ConformancePersonas.DiscoverFromDisk())
        {
            data.Add(id);
        }

        return data;
    }

    [Theory(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    [MemberData(nameof(DiscoveredPersonaIds))]
    public Task Every_persona_loads_under_the_policy_with_no_violation_and_reaches_the_realtime_relay(string personaId) =>
        fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IBrowserContext context = await NewContextAsync();
        (WatchedPage watched, _) = await OpenPersonaAsync(context, personaId, ct);
        IPage page = watched.Page;

        // The one external request a persona theme makes, so the one a policy most easily blocks. It is
        // attached in the same call that set --brand-primary, which OpenPersonaAsync already waited for.
        string? webFontUrl = DeclaredWebFontUrl(personaId);
        if (webFontUrl is not null)
        {
            ILocator fontLink = page.Locator("#persona-font-link");
            Assert.True(await fontLink.CountAsync() == 1, $"Persona '{personaId}' declares the web font {webFontUrl} but the page has no #persona-font-link.");
            Assert.Equal(webFontUrl, await fontLink.GetAttributeAsync("href"));
        }

        await Task.Delay(500, ct);
        await watched.AssertNoViolationsAsync();
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task Recording_a_turn_on_the_default_persona_raises_no_violation() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IBrowserContext context = await NewContextAsync();
        (WatchedPage watched, FakeRealtimeConnection connection) = await OpenPersonaAsync(context, ConformancePersonas.DefaultPersonaId, ct);
        IPage page = watched.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = StartRecording, Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Stop recording", Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = (float)s_timeout.TotalMilliseconds });

        // Fake microphone -> capture worklet -> same-origin socket -> relay -> upstream.
        RecordedFrame? audio = await connection.ReceivedFrames.WaitForAsync(f => f.Type == "input_audio_buffer.append", s_timeout, ct);
        Assert.True(audio is not null, "No microphone audio reached the upstream connection.");

        await Task.Delay(500, ct);
        await watched.AssertNoViolationsAsync();
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task Settings_and_the_dark_mode_switch_raise_no_violation() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IBrowserContext context = await NewContextAsync();
        (WatchedPage watched, _) = await OpenPersonaAsync(context, ConformancePersonas.DefaultPersonaId, ct);
        IPage page = watched.Page;

        await page.GetByRole(AriaRole.Button, new() { Name = "Open settings", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync(new() { Timeout = (float)s_timeout.TotalMilliseconds });

        // The app's Switch is a <label> around a visually hidden checkbox and a visible track. The checkbox
        // is clipped to nothing, so a user (and this test) toggles it by clicking the label.
        ILocator darkMode = page.GetByRole(AriaRole.Checkbox, new() { Name = "Toggle dark mode", Exact = true });
        await page.Locator("label").Filter(new() { Has = darkMode }).ClickAsync();
        await Expect(darkMode).ToBeCheckedAsync();
        await page.WaitForFunctionAsync("() => document.documentElement.classList.contains('dark')");

        await Task.Delay(500, ct);
        await watched.AssertNoViolationsAsync();
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task The_browser_enforces_the_policy_and_the_recorder_sees_what_it_blocks() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IBrowserContext context = await NewContextAsync();
        WatchedPage watched = await WatchedPage.OpenAsync(context);
        IPage page = watched.Page;

        await page.GotoAsync(fixture.Backend!.BaseUri.ToString());

        // One of each thing the policy exists to stop: an inline script, eval, and a request to an origin
        // that is not on the allow-list (a reserved TLD, so nothing is ever resolved even if it got through).
        // DevTools-driven evaluation is exempt from the page's eval ban, so eval has to run from a timer.
        await page.EvaluateAsync("""
            () => {
              const inline = document.createElement('script');
              inline.textContent = 'window.__inlineRan = true';
              document.head.appendChild(inline);
              setTimeout(() => { try { eval('window.__evalRan = true'); } catch { /* blocked: expected */ } }, 0);
              fetch('https://csp-canary.invalid/probe').catch(() => { /* blocked: expected */ });
            }
            """);

        IReadOnlyList<string> events = await watched.WaitForEventsAsync(minimum: 3, TimeSpan.FromSeconds(10), ct);
        Assert.True(events.Count >= 3, $"Expected three CSP violations; the recorder saw: [{string.Join(", ", events)}].");
        Assert.Contains(events, e => e.StartsWith("script-src", StringComparison.Ordinal) && e.EndsWith("<- inline", StringComparison.Ordinal));
        Assert.Contains(events, e => e.StartsWith("script-src", StringComparison.Ordinal) && e.EndsWith("<- eval", StringComparison.Ordinal));
        Assert.Contains(events, e => e.StartsWith("connect-src <- https://csp-canary.invalid", StringComparison.Ordinal));

        Assert.False(await page.EvaluateAsync<bool>("'__inlineRan' in window"), "The inline script ran: script-src is not enforced.");
        Assert.False(await page.EvaluateAsync<bool>("'__evalRan' in window"), "eval ran: script-src is not enforced.");
    });

    [Fact(Skip = CSharpOnly, SkipWhen = nameof(BackendUnderTest.IsPython), SkipType = typeof(BackendUnderTest))]
    public Task The_app_can_frame_its_own_origin_but_a_foreign_page_cannot_frame_it() => fixture.RunAsync(async () =>
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using IBrowserContext context = await NewContextAsync();
        IPage page = await context.NewPageAsync();

        Uri target = new(fixture.Backend!.BaseUri, "/health");

        // Playwright answers the same-origin embedding page itself, so it carries no policy of its own,
        // while the framed /health request goes to the real backend.
        Uri sameOriginParent = new(fixture.Backend!.BaseUri, "/csp-same-origin-parent");
        string embedder = $"<!doctype html><title>embedder</title><iframe name=\"child\" src=\"{target}\"></iframe>";
        await page.RouteAsync(sameOriginParent.ToString(), route => route.FulfillAsync(new() { ContentType = "text/html", Body = embedder }));

        await page.GotoAsync(sameOriginParent.ToString());
        Assert.Equal(target.ToString(), await FrameUrlAsync(page, "child", ct));

        await using ForeignOrigin foreign = await ForeignOrigin.StartAsync(target, ct);
        await page.GotoAsync(foreign.BaseUri.ToString());

        // The control frame is the foreign origin's own document: if it loads, framing works in this
        // browser and the refusal below is the app's policy, not the environment.
        Assert.Equal(foreign.ControlUri.ToString(), await FrameUrlAsync(page, "control", ct));
        string blocked = await FrameUrlAsync(page, "child", ct);
        Assert.StartsWith("chrome-error://", blocked, StringComparison.Ordinal);
    });

    private async Task<IBrowserContext> NewContextAsync()
    {
        IBrowserContext context = await fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            Permissions = ["microphone"],
        });
        await context.AddInitScriptAsync(RecordViolations);
        return context;
    }

    private Uri PersonaUrl(string personaId) => new UriBuilder(fixture.Backend!.BaseUri) { Query = $"persona={personaId}" }.Uri;

    /// <summary>
    /// Opens a persona and returns once the app is really up: the policy header was served, the
    /// <c>/realtime</c> socket reached the upstream fake, the persona theme was applied and the main
    /// button is on screen. Settling first matters because acting on a half-rendered page races React's
    /// re-render, which discards an open dialog.
    /// </summary>
    private async Task<(WatchedPage Watched, FakeRealtimeConnection Connection)> OpenPersonaAsync(
        IBrowserContext context, string personaId, CancellationToken ct)
    {
        WatchedPage watched = await WatchedPage.OpenAsync(context);
        IPage page = watched.Page;

        // Armed before the navigation: the SPA opens its /realtime socket on load, and a connect-src
        // block would leave this null.
        Task<FakeRealtimeConnection?> connectionTask = fixture.Realtime.WaitForNextConnectionAsync(s_timeout, ct);
        IResponse? navigation = await page.GotoAsync(PersonaUrl(personaId).ToString());
        Assert.NotNull(navigation);
        Dictionary<string, string> headers = await navigation.AllHeadersAsync();
        Assert.True(headers.ContainsKey("content-security-policy"), "The page was served without a Content-Security-Policy header.");

        FakeRealtimeConnection? connection = await connectionTask;
        Assert.True(connection is not null, $"No upstream connection within {s_timeout}: the page's /realtime socket never opened.");

        // applyTheme writes --brand-primary on <html> only once the persona has loaded, and attaches the
        // web-font <link> in that same call.
        await page.WaitForFunctionAsync(
            "() => document.documentElement.style.getPropertyValue('--brand-primary') !== ''",
            null,
            new() { Timeout = (float)s_timeout.TotalMilliseconds });
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = StartRecording, Exact = true }))
            .ToBeVisibleAsync(new() { Timeout = (float)s_timeout.TotalMilliseconds });

        return (watched, connection);
    }

    /// <summary>The web-font stylesheet the persona's own pack declares (<c>ui.theme.font.importUrl</c>), if any.</summary>
    private static string? DeclaredWebFontUrl(string personaId)
    {
        string file = Path.Combine(RepoPaths.PersonasDirectory(RepoPaths.FindRepoRoot()), personaId, "persona.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(file));
        return document.RootElement.TryGetProperty("ui", out JsonElement ui)
            && ui.TryGetProperty("theme", out JsonElement theme)
            && theme.TryGetProperty("font", out JsonElement font)
            && font.TryGetProperty("importUrl", out JsonElement url)
                ? url.GetString()
                : null;
    }

    /// <summary>The address the named iframe settled on once its navigation was decided (it starts as about:blank).</summary>
    private static async Task<string> FrameUrlAsync(IPage page, string frameName, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + s_timeout;
        while (true)
        {
            IFrame? frame = page.Frame(frameName);
            if (frame is not null && frame.Url != "about:blank")
            {
                return frame.Url;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"The '{frameName}' iframe never navigated within {s_timeout}.");
            }

            await Task.Delay(100, ct);
        }
    }

    /// <summary>
    /// A second origin on loopback (its own port, so cross-origin to the backend), sending no policy
    /// headers. It serves a page that frames <c>target</c> as "child" and its own <c>/control</c> as "control".
    /// </summary>
    private sealed class ForeignOrigin : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ForeignOrigin(WebApplication app, Uri baseUri)
        {
            _app = app;
            BaseUri = baseUri;
        }

        public Uri BaseUri { get; }

        public Uri ControlUri => new(BaseUri, "/control");

        public static async Task<ForeignOrigin> StartAsync(Uri target, CancellationToken ct)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            WebApplication app = builder.Build();

            string page = $"<!doctype html><title>foreign embedder</title><iframe name=\"child\" src=\"{target}\"></iframe><iframe name=\"control\" src=\"/control\"></iframe>";
            app.MapGet("/", () => Results.Content(page, "text/html"));
            app.MapGet("/control", () => Results.Text("control", "text/plain"));

            await app.StartAsync(ct);
            return new ForeignOrigin(app, new Uri(app.Urls.First()));
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>A page plus every policy violation it reported: events from the in-page recorder and CSP console errors.</summary>
    private sealed class WatchedPage
    {
        private readonly ConcurrentQueue<string> _consoleReports = new();

        private WatchedPage(IPage page) => Page = page;

        public IPage Page { get; }

        public static async Task<WatchedPage> OpenAsync(IBrowserContext context)
        {
            var watched = new WatchedPage(await context.NewPageAsync());
            watched.Page.Console += (_, message) =>
            {
                if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase))
                {
                    watched._consoleReports.Enqueue($"console {message.Type}: {message.Text}");
                }
            };
            return watched;
        }

        public async Task<IReadOnlyList<string>> RecordedEventsAsync() =>
            await Page.EvaluateAsync<string[]>("window.__csp ?? []");

        /// <summary>Polls the in-page recorder until it holds <paramref name="minimum"/> events or the timeout passes, then returns what it has.</summary>
        public async Task<IReadOnlyList<string>> WaitForEventsAsync(int minimum, TimeSpan timeout, CancellationToken ct)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (true)
            {
                IReadOnlyList<string> events = await RecordedEventsAsync();
                if (events.Count >= minimum || DateTimeOffset.UtcNow >= deadline)
                {
                    return events;
                }

                await Task.Delay(100, ct);
            }
        }

        public async Task AssertNoViolationsAsync()
        {
            IReadOnlyList<string> events = await RecordedEventsAsync();
            string[] all = [.. events, .. _consoleReports];
            Assert.True(all.Length == 0, $"The page violated its Content-Security-Policy: {string.Join(" | ", all)}");
        }
    }
}
