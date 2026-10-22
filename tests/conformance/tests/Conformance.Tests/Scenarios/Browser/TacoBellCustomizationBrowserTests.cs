using System.Text.Json;
using System.Text.Json.Nodes;
using Conformance.Fakes;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Conformance.Tests.Scenarios.Browser;

[Collection(BrowserConformanceCollection.Name)]
[Trait("Category", "Browser")]
[Trait("Dotnet", "ready")]
public sealed class TacoBellCustomizationBrowserTests(BrowserConformanceFixture fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string Capture = """
        (() => {
          window.__customFrames = [];
          const Original = window.WebSocket;
          window.WebSocket = new Proxy(Original, {
            construct(target, args) {
              const ws = new target(...args);
              if (String(args[0]).includes('/realtime')) {
                window.__customSocket = ws;
                ws.addEventListener('message', e => window.__customFrames.push(JSON.parse(e.data)));
              }
              return ws;
            }
          });
        })();
        """;

    [Fact]
    public Task Shell_change_omissions_meal_drink_late_sweet_and_free_reward_render_exactly() => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await fixture.Browser!.NewContextAsync(new()
        {
            ViewportSize = new() { Width = 1440, Height = 1100 }, Permissions = ["microphone"],
        });
        await context.AddInitScriptAsync(Capture);
        var page = await context.NewPageAsync();
        var next = fixture.Realtime.WaitForNextConnectionAsync(Timeout, ct);
        await page.GotoAsync(new Uri(fixture.Backend!.BaseUri, "?persona=tacobell").ToString());
        var upstream = await next;
        Assert.NotNull(upstream);
        upstream.Script.RemoveVadSpeechDefaultRule();
        upstream.Script.Enqueue(new ResponseScript([new DoneEvent()]));
        await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync();
        await Expect(page.GetByLabel("Demo rewards / check-in code", new() { Exact = true })).ToBeEnabledAsync();
        var evidence = new List<object>();

        async Task Guest(string text)
        {
            evidence.Add(new { guest = text });
            await upstream.SendAsync(new JsonObject
            {
                ["type"] = "conversation.item.input_audio_transcription.completed",
                ["item_id"] = $"guest_{Guid.NewGuid():N}", ["transcript"] = text,
            }, ct);
        }

        async Task<JsonElement?> Tool(string name, object arguments)
        {
            var id = $"custom_{Guid.NewGuid():N}";
            var watermark = await page.EvaluateAsync<int>("window.__customFrames.length");
            upstream.Script.Enqueue(new ResponseScript([
                new FunctionCallEvent(name, JsonSerializer.Serialize(arguments), id), new DoneEvent(),
            ]));
            // This is a synthetic model turn, not a direct mutation of the ticket or final state.
            await page.EvaluateAsync("window.__customSocket.send(JSON.stringify({type:'response.create'}))");
            var output = await upstream.ReceivedFrames.WaitForAsync(f =>
                f.Type == "conversation.item.create" && f.Json.TryGetProperty("item", out var item) &&
                item.TryGetProperty("call_id", out var call) && call.GetString() == id, Timeout, ct);
            Assert.NotNull(output);
            var text = output.Json.GetProperty("item").GetProperty("output").GetString()!;
            Assert.DoesNotContain("\"status\": \"rejected\"", text);
            Assert.DoesNotContain("\"status\":\"rejected\"", text);
            await page.WaitForFunctionAsync(
                "n => window.__customFrames.slice(n).some(f=>f.type==='extension.round_trip_token')", watermark);
            evidence.Add(new { tool = name, arguments, output = text });
            if (name == "search") return JsonSerializer.SerializeToElement(text);
            return await page.EvaluateAsync<JsonElement>(
                "n => JSON.parse(window.__customFrames.slice(n).filter(f=>f.type==='extension.middle_tier_tool_response').at(-1).tool_result)",
                watermark);
        }

        async Task Update(string action, string item, string size = "standard")
        {
            if (action == "add")
            {
                var query = item.Contains("Combo") ? "3" :
                    item.Contains("Baja Blast") ? "Baja" : item.Split(' ')[0];
                var search = await Tool("search", new { query });
                Assert.Contains(item.Split('(')[0].Trim(), search!.Value.GetString());
            }
            await Tool("update_order", new { action, item_name = item, size, quantity = 1 });
        }

        async Task<JsonElement> ReadBack()
        {
            var ticket = (await Tool("get_order", new { }))!.Value;
            var spoken = ticket.GetProperty("spokenReadBack").GetString()!;
            evidence.Add(new { assistant = spoken, ticket });
            await upstream.SendAsync(new JsonObject
            {
                ["type"] = "response.done",
                ["response"] = new JsonObject
                {
                    ["id"] = $"readback_{Guid.NewGuid():N}", ["status"] = "completed",
                    ["output"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "message", ["role"] = "assistant",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "audio", ["transcript"] = spoken }),
                    }),
                },
            }, ct);
            return ticket;
        }

        var code = page.GetByLabel("Demo rewards / check-in code", new() { Exact = true });
        await code.FillAsync("DEMO-PLUS");
        await page.GetByRole(AriaRole.Button, new() { Name = "Check in to demo order" }).ClickAsync();
        await Expect(page.GetByText("Demo check-in linked to this order", new() { Exact = true })).ToBeVisibleAsync();

        await Guest("I'd like the three taco meal with crunchy shells.");
        await Update("add", "3 Crunchy Tacos Combo");
        await ReadBack();
        await Guest("Actually make all three tacos soft, not hard shell.");
        await Update("remove", "3 Crunchy Tacos Combo");
        await Update("add", "3 Soft Tacos Combo");
        await ReadBack();
        await Guest("Remove tomatoes from all three tacos.");
        await Update("remove", "3 Soft Tacos Combo");
        await Update("add", "3 Soft Tacos Combo (No Tomatoes)");
        await ReadBack();
        await Guest("Add nachos and cheese, and a large Mountain Dew Baja Blast with the meal.");
        await Update("add", "Chips and Nacho Cheese Sauce");
        await Update("add", "Mountain Dew Baja Blast", "large");
        var initial = await ReadBack();
        var items = initial.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        var meal = items.Single(i => i.GetProperty("item").GetString() == "3 Soft Tacos Combo (No Tomatoes)");
        Assert.Equal(1, meal.GetProperty("quantity").GetInt32());
        Assert.Equal(9.49m, meal.GetProperty("price").GetDecimal());
        Assert.Equal(["Large Mountain Dew Baja Blast"], meal.GetProperty("components").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.Equal(0.50m, meal.GetProperty("componentUpcharges")[0].GetDecimal());
        Assert.Equal("$11.98", initial.GetProperty("totalDisplay").GetString());
        Assert.Equal("$0.96", initial.GetProperty("taxDisplay").GetString());
        Assert.Equal("$12.94", initial.GetProperty("finalTotalDisplay").GetString());
        Assert.False(initial.GetProperty("rewardsDemo").GetProperty("completed").GetBoolean());
        var ticketUi = page.GetByTestId("order-ticket");
        await Expect(ticketUi).ToContainTextAsync("No Tomatoes");
        await Expect(ticketUi).ToContainTextAsync("Large Mountain Dew Baja Blast");
        await Expect(ticketUi).ToContainTextAsync("+$0.50");
        await Expect(ticketUi).Not.ToContainTextAsync("Crunchy Tacos Combo");

        await Guest("I forgot Cinnamon Twists. Add those after that total, please.");
        await Update("add", "Cinnamon Twists");
        var amended = await ReadBack();
        Assert.Equal(3, amended.GetProperty("items").GetArrayLength());
        Assert.Equal("$13.47", amended.GetProperty("totalDisplay").GetString());
        Assert.Equal("$1.08", amended.GetProperty("taxDisplay").GetString());
        Assert.Equal("$14.55", amended.GetProperty("finalTotalDisplay").GetString());
        Assert.Equal("Cinnamon Twists", amended.GetProperty("rewardsDemo").GetProperty("rewardItem").GetString());
        await Expect(page.GetByText("You have an eligible reward!", new() { Exact = false })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Simulated rewards" })).ToContainTextAsync("Cinnamon Twists");

        await Guest("Use my demo reward to make the Cinnamon Twists free.");
        await page.GetByRole(AriaRole.Button, new() { Name = "Redeem demo reward", Exact = true }).ClickAsync();
        await Expect(ticketUi).ToContainTextAsync("-$1.49");
        var free = await ReadBack();
        Assert.Equal("$12.94", free.GetProperty("finalTotalDisplay").GetString());
        Assert.Equal(1.49m, free.GetProperty("rewardsDemo").GetProperty("discount").GetDecimal());
        Assert.Equal(500, free.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        await page.GetByRole(AriaRole.Button, new() { Name = "Complete demo order", Exact = true }).ClickAsync();
        await Expect(page.GetByText("Completed · 119 fictional points earned.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Region, new() { Name = "Simulated rewards" })).ToContainTextAsync("369");
        await page.EvaluateAsync("window.__customSocket.send(JSON.stringify({type:'extension.demo_rewards',action:'complete'}))");
        var final = await Tool("get_order", new { });
        Assert.Equal(369, final!.Value.GetProperty("rewardsDemo").GetProperty("balance").GetInt32());
        var conversation = page.GetByTestId("transcript-panel-card");
        await Expect(conversation).ToContainTextAsync("Actually make all three tacos soft, not hard shell.");
        await Expect(conversation).ToContainTextAsync("Remove tomatoes from all three tacos.");
        await Expect(conversation).ToContainTextAsync("I forgot Cinnamon Twists. Add those after that total, please.");
        await Expect(conversation).ToContainTextAsync("fourteen dollars and fifty-five cents");
        await Expect(conversation).ToContainTextAsync("twelve dollars and ninety-four cents");
        evidence.Add(new { finalTicket = final.Value });
        var folder = Environment.GetEnvironmentVariable("TACO_BELL_EVIDENCE_DIR");
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "customization-transcript.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), ct);
            await page.ScreenshotAsync(new() { Path = Path.Combine(folder, "customization-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            Assert.False(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(folder, "customization-mobile.png"), FullPage = true });
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Expired_rewards_disable_checkin_and_reconnect_to_a_fresh_ledger(bool checkedIn) => fixture.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var context = await fixture.Browser!.NewContextAsync(new() { Permissions = ["microphone"] });
        await context.AddInitScriptAsync(Capture);
        var page = await context.NewPageAsync();
        var next = fixture.Realtime.WaitForNextConnectionAsync(Timeout, ct);
        await page.GotoAsync(new Uri(fixture.Backend!.BaseUri, "?persona=tacobell").ToString());
        var upstream = await next;
        Assert.NotNull(upstream);
        upstream.Script.RemoveVadSpeechDefaultRule();
        await Expect(page.GetByLabel("Demo rewards / check-in code", new() { Exact = true })).ToBeEnabledAsync();
        await page.GetByLabel("Demo rewards / check-in code", new() { Exact = true }).FillAsync("DEMO-PLUS");
        if (checkedIn)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Check in to demo order" }).ClickAsync();
            await Expect(page.GetByText("Demo check-in linked to this order", new() { Exact = true })).ToBeVisibleAsync();
        }
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Reconnect demo rewards" })).ToBeVisibleAsync(new() { Timeout = 20000 });
        if (checkedIn)
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Complete demo order", Exact = true })).ToBeDisabledAsync();
        if (!checkedIn)
        {
            await Expect(page.GetByLabel("Demo rewards / check-in code", new() { Exact = true })).ToBeDisabledAsync();
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Check in to demo order" })).ToBeDisabledAsync();
        }
        var fresh = fixture.Realtime.WaitForNextConnectionAsync(Timeout, ct);
        await page.GetByRole(AriaRole.Button, new() { Name = "Reconnect demo rewards" }).ClickAsync();
        Assert.NotNull(await fresh);
        await Expect(page.GetByLabel("Demo rewards / check-in code", new() { Exact = true })).ToBeEnabledAsync();
        await page.GetByLabel("Demo rewards / check-in code", new() { Exact = true }).FillAsync("DEMO-NEW");
        await page.GetByRole(AriaRole.Button, new() { Name = "Check in to demo order" }).ClickAsync();
        await Expect(page.GetByText("Demo check-in linked to this order", new() { Exact = true })).ToBeVisibleAsync();
        var reward = page.GetByRole(AriaRole.Region, new() { Name = "Simulated rewards" });
        await Expect(reward).ToContainTextAsync("0 demo points");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Redeem demo reward", Exact = true })).ToBeDisabledAsync();
        await Expect(page.GetByTestId("order-ticket")).ToContainTextAsync("$0.00");
    });
}
