using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace Conformance.Tests.Scenarios.Browser;

/// <summary>
/// Opt-in real-browser regression against the isolated Azure app and live realtime model.
/// Authentication stays inside the browser's normal MSAL cache; this test never reads or logs
/// tokens, WebSocket URLs, or raw audio frames.
/// </summary>
[Trait("Category", "LiveAzure")]
[Trait("Dotnet", "n/a-live-azure")] // Opt-in deployed-endpoint test; skipped unless LIVE_CSHARP_ORDER_ENDPOINT is set.
public sealed class LiveAzureOrderCompletionTests
{
    private const string CaptureEventsScript = """
        (() => {
          window.__liveOrderEvents = [];
          const OriginalWebSocket = window.WebSocket;
          window.WebSocket = new Proxy(OriginalWebSocket, {
            construct(target, args) {
              const socket = new target(...args);
              if (String(args[0] ?? '').includes('/realtime')) {
                socket.addEventListener('close', event => {
                  window.__liveOrderEvents.push({ type: 'socket.close', code: String(event.code), t: Math.round(performance.now()) });
                });
                socket.addEventListener('message', event => {
                  if (typeof event.data !== 'string') return;
                  let message;
                  try { message = JSON.parse(event.data); } catch { return; }
                  const record = { type: message.type, t: Math.round(performance.now()) };
                  if (message.type === 'error') {
                    record.code = message.error?.code ?? message.error?.type ?? '';
                  } else if (message.type === 'response.function_call_arguments.done') {
                    record.name = message.name ?? '';
                  } else if (message.type === 'conversation.item.input_audio_transcription.completed') {
                    record.transcript = message.transcript ?? '';
                  } else if (message.type === 'response.created') {
                    record.responseId = message.response?.id ?? '';
                  } else if (message.type === 'response.audio.delta') {
                    record.responseId = message.response_id ?? '';
                    record.audioDelta = true;
                  } else if (message.type === 'response.audio_transcript.delta') {
                    record.responseId = message.response_id ?? '';
                    record.delta = message.delta ?? '';
                  } else if (message.type === 'response.done') {
                    const response = message.response ?? {};
                    record.responseId = response.id ?? '';
                    record.status = response.status ?? '';
                    record.outputTypes = (response.output ?? []).map(item => item.type ?? '');
                    record.transcript = (response.output ?? [])
                      .flatMap(item => item.content ?? [])
                      .map(part => part.transcript ?? '')
                      .filter(Boolean)
                      .join(' ');
                  }
                  window.__liveOrderEvents.push(record);
                });
              }
              return socket;
            }
          });
        })();
        """;

    // Phases, in order: guest orders a burger; a tool round runs and the assistant speaks;
    // guest closes ("...be it"/"that's all"); a tool round (get_order) runs; the assistant
    // speaks a completed closing response with audio that reads back the total or the carhop.
    private const string OrderCompletedScript = """
        () => {
          const events = window.__liveOrderEvents ?? [];
          const after = (from, predicate) => {
            for (let i = from + 1; i < events.length; i++) { if (predicate(events[i])) return i; }
            return -1;
          };
          const hasAudio = id => events.some(e => e.type === 'response.audio.delta' && e.responseId === id);
          const spoken = e => e.type === 'response.done' && e.status === 'completed' && (e.transcript ?? '').trim() !== '' && hasAudio(e.responseId);
          const guest = re => e => e.type === 'conversation.item.input_audio_transcription.completed' && re.test(e.transcript ?? '');
          const tool = e => e.type === 'extension.middle_tier_tool_response';
          const order = after(-1, guest(/burger/i));
          if (order < 0) return false;
          const orderTool = after(order, tool);
          if (orderTool < 0) return false;
          const ask = after(orderTool, spoken);
          if (ask < 0) return false;
          const close = after(ask, guest(/be it|that'?s (it|all)/i));
          if (close < 0) return false;
          const closeTool = after(close, tool);
          if (closeTool < 0) return false;
          return after(closeTool, e => spoken(e) && /carhop|right out|total/i.test(e.transcript)) >= 0;
        }
        """;

    [Fact]
    public async Task Live_order_finishes_with_ticket_and_spoken_closing()
    {
        var endpoint = Environment.GetEnvironmentVariable("LIVE_CSHARP_ORDER_ENDPOINT");
        var audioPath = Environment.GetEnvironmentVariable("LIVE_CSHARP_ORDER_AUDIO");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(audioPath))
        {
            Assert.Skip("Set LIVE_CSHARP_ORDER_ENDPOINT and LIVE_CSHARP_ORDER_AUDIO to run the live Azure test.");
            return;
        }

        Assert.True(Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            && endpointUri.Scheme == Uri.UriSchemeHttps,
            "The live test endpoint must be an absolute HTTPS URL.");
        Assert.True(File.Exists(audioPath), "The prerecorded fake-microphone WAV file must exist.");

        var edgePath = Environment.GetEnvironmentVariable("LIVE_CSHARP_EDGE_PATH")
            ?? @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        Assert.True(File.Exists(edgePath), "The explicitly configured Microsoft Edge executable must exist.");

        // A persistent profile keeps the MSAL cache in the browser's own storage after an
        // interactive sign-in (LIVE_CSHARP_HEADED=1); tokens are never read by the test.
        var profileDir = Environment.GetEnvironmentVariable("LIVE_CSHARP_PROFILE_DIR");
        var headed = Environment.GetEnvironmentVariable("LIVE_CSHARP_HEADED") == "1";
        string[] mediaArgs =
        [
            "--use-fake-ui-for-media-stream",
            "--use-fake-device-for-media-stream",
            $"--use-file-for-fake-audio-capture={Path.GetFullPath(audioPath)}",
        ];

        using var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        IBrowserContext context;
        if (!string.IsNullOrWhiteSpace(profileDir))
        {
            Directory.CreateDirectory(profileDir);
            context = await playwright.Chromium.LaunchPersistentContextAsync(profileDir, new BrowserTypeLaunchPersistentContextOptions
            {
                ExecutablePath = edgePath,
                Headless = !headed,
                Args = mediaArgs,
                Permissions = ["microphone"],
            });
        }
        else
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                ExecutablePath = edgePath,
                Headless = !headed,
                Args = mediaArgs,
            });
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Permissions = ["microphone"],
            });
        }

        try
        {
            try
            {
                await context.AddInitScriptAsync(CaptureEventsScript);
                var page = await context.NewPageAsync();
                await page.GotoAsync(endpoint, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                var controls = page.Locator("[data-testid='conversation-controls']");
                var accountUpn = Environment.GetEnvironmentVariable("LIVE_CSHARP_ACCOUNT_UPN");
                if (!string.IsNullOrWhiteSpace(accountUpn))
                {
                    // Selects an account the dedicated profile is already signed in to on the
                    // Entra account picker; never types credentials.
                    var tile = page.GetByRole(AriaRole.Button, new() { Name = $"Sign in with {accountUpn}" }).First;
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline && !await controls.IsVisibleAsync())
                    {
                        if (await tile.IsVisibleAsync())
                        {
                            await tile.ClickAsync();
                            break;
                        }
                        await page.WaitForTimeoutAsync(1_000);
                    }
                }
                await controls
                    .WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = headed ? 1_800_000 : 120_000,
                    });

                await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync();
                try
                {
                    // The model's wording and turn segmentation vary between runs (for example
                    // it may offer a combo instead of asking "anything else", and "No, that'll be
                    // it" may arrive as two transcripts), so assert the turn structure instead.
                    await page.WaitForFunctionAsync(OrderCompletedScript, null,
                        new PageWaitForFunctionOptions { Timeout = 180_000 });
                    await page.WaitForFunctionAsync(
                        """
                        () => {
                          const ticket = document.querySelector('[data-testid="order-ticket"]')?.innerText ?? '';
                          return /Double Cheeseburger/i.test(ticket) && ticket.includes('$7.12');
                        }
                        """,
                        null,
                        new PageWaitForFunctionOptions { Timeout = 30_000 });
                }
                finally
                {
                    await WriteDiagnosticsAsync(page, endpointUri!.Host);
                }
            }
            finally
            {
                await context.CloseAsync();
            }
        }
        finally
        {
            if (browser is not null)
            {
                await browser.CloseAsync();
            }
        }
    }

    private static async Task WriteDiagnosticsAsync(IPage page, string host)
    {
        var diagnosticsPath = Environment.GetEnvironmentVariable("LIVE_CSHARP_ORDER_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(diagnosticsPath))
        {
            return;
        }

        var events = await page.EvaluateAsync<JsonElement>("() => window.__liveOrderEvents ?? []");
        var ticket = await page.EvaluateAsync<string>(
            "() => document.querySelector('[data-testid=\"order-ticket\"]')?.innerText ?? ''");
        var status = await page.EvaluateAsync<string>("() => document.body?.innerText?.slice(-2000) ?? ''");
        await File.WriteAllTextAsync(diagnosticsPath, JsonSerializer.Serialize(new
        {
            endpointHost = host,
            observedAtUtc = DateTimeOffset.UtcNow,
            events,
            ticket,
            pageTail = status,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
