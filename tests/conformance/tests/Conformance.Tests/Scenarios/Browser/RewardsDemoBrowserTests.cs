using Conformance.Fakes;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Conformance.Tests.Scenarios.Browser;

[Collection(BrowserConformanceCollection.Name)]
[Trait("Category", "Browser")]
[Trait("Dotnet", "ready")]
public sealed class RewardsDemoBrowserTests(BrowserConformanceFixture fixture)
{
    [Fact]
    public Task Taco_Bell_checkin_rewards_resume_and_mobile_dark_theme_work_in_Edge() => fixture.RunAsync(async () =>
    {
        await using IBrowserContext context = await fixture.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1080 },
            Permissions = ["microphone"],
        });
        await context.AddInitScriptAsync("""
            (() => {
              window.__rewardFrames = [];
              const Original = window.WebSocket;
              window.WebSocket = new Proxy(Original, {
                construct(target, args) {
                  const ws = new target(...args);
                  if (String(args[0]).includes('/realtime')) {
                    window.__rewardSocket = ws;
                    const send = ws.send.bind(ws);
                    ws.send = data => { window.__rewardFrames.push(JSON.parse(data)); return send(data); };
                  }
                  return ws;
                }
              });
            })();
            """);
        CancellationToken ct = TestContext.Current.CancellationToken;
        IPage page = await context.NewPageAsync();
        Task<FakeRealtimeConnection?> connectionTask = fixture.Realtime.WaitForNextConnectionAsync(TimeSpan.FromSeconds(30), ct);
        await page.GotoAsync(new Uri(fixture.Backend!.BaseUri, "?persona=tacobell").ToString());
        FakeRealtimeConnection? connection = await connectionTask;
        Assert.NotNull(connection);
        ILocator logo = page.GetByRole(AriaRole.Img, new() { Name = "Taco Bell Voice Ordering Demo logo", Exact = true });
        await Expect(logo).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex(@"/personas/tacobell/assets/logo\.svg"));
        Assert.True(await logo.EvaluateAsync<bool>("e => e.complete && e.naturalWidth > 0"));
        ILocator tacos = page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Tacos") });
        await Expect(tacos).ToBeVisibleAsync();
        if (await tacos.GetAttributeAsync("aria-expanded") != "true")
        {
            await tacos.ClickAsync();
        }

        await Expect(tacos.Locator("..").GetByText("Crunchy Taco", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(tacos.Locator("..").GetByText("Crunchy shell taco. Illustrative demo price, not a store quote.", new() { Exact = true }))
            .ToBeVisibleAsync();
        ILocator code = page.GetByLabel("Demo rewards / check-in code", new() { Exact = true });
        await Expect(code).ToBeEnabledAsync();
        await code.FillAsync("5551234567");
        await page.GetByRole(AriaRole.Button, new() { Name = "Check in", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("No value was sent");
        Assert.False(await page.EvaluateAsync<bool>("window.__rewardFrames.some(f => f.code === '5551234567')"));
        await code.FillAsync("DEMO-PLUS");
        await page.GetByRole(AriaRole.Button, new() { Name = "Check in", Exact = true }).ClickAsync();
        await Expect(page.GetByText("500", new() { Exact = false }).Filter(new() { HasText = "demo points" })).ToBeVisibleAsync();

        connection.Script.Enqueue(new ResponseScript([
            new FunctionCallEvent("update_order", """{"action":"add","item_name":"Crunchy Taco","size":"standard","quantity":2}""", "browser_taco"),
            new DoneEvent(),
        ]));
        await page.EvaluateAsync("window.__rewardSocket.send(JSON.stringify({ type: 'response.create' }))");
        await Expect(page.GetByText("You have an eligible reward!", new() { Exact = false })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Use reward", Exact = true }).ClickAsync();
        await Expect(page.GetByText("One item covered.", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(page.GetByText("-$1.99", new() { Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Complete order", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Earned 19 fictional points. No purchase was placed.", new() { Exact = true })).ToBeVisibleAsync();
        ILocator complete = page.GetByRole(AriaRole.Button, new() { Name = "Complete order", Exact = true });
        await Expect(complete).ToHaveCountAsync(0);
        string? screenshot = Environment.GetEnvironmentVariable("TACO_BELL_SCREENSHOT");
        if (!string.IsNullOrEmpty(screenshot))
        {
            await page.ScreenshotAsync(new() { Path = Path.ChangeExtension(screenshot, "desktop.png"), FullPage = true });
        }

        await page.ReloadAsync();
        await Expect(page.GetByText("Earned 19 fictional points. No purchase was placed.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("269", new() { Exact = false }).Filter(new() { HasText = "demo points" })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Next order", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Demo check-in linked to this order", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("-$1.99", new() { Exact = true })).ToHaveCountAsync(0);

        await page.SetViewportSizeAsync(390, 844);
        await page.EvaluateAsync("document.documentElement.classList.add('dark')");
        Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth"));
        ILocator panel = page.GetByRole(AriaRole.Region, new() { Name = "Simulated rewards" });
        await Expect(panel).ToBeVisibleAsync();
        Assert.Equal("rgb(59, 11, 113)", await panel.EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));
        Assert.Equal("rgb(255, 255, 255)", await panel.EvaluateAsync<string>("e => getComputedStyle(e).color"));
        Assert.Equal("rgb(59, 11, 113)", await panel.GetByRole(AriaRole.Button, new() { Name = "Complete order", Exact = true })
            .EvaluateAsync<string>("e => getComputedStyle(e).color"));
        await Expect(logo).ToBeVisibleAsync();
        if (!string.IsNullOrEmpty(screenshot))
        {
            await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
        }
    });
}
