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
                socket.addEventListener('message', event => {
                  if (typeof event.data !== 'string') return;
                  let message;
                  try { message = JSON.parse(event.data); } catch { return; }
                  const record = { type: message.type };
                  if (message.type === 'conversation.item.input_audio_transcription.completed') {
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
                await page.Locator("[data-testid='conversation-controls']")
                    .WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = headed ? 1_800_000 : 120_000,
                    });

                await page.GetByRole(AriaRole.Button, new() { Name = "Start recording", Exact = true }).ClickAsync();
                await page.WaitForFunctionAsync(
                    """() => (window.__liveOrderEvents ?? []).some(event => event.type === 'conversation.item.input_audio_transcription.completed' && /burger/i.test(event.transcript ?? ''))""",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 90_000 });
                await page.WaitForFunctionAsync(
                    """() => (window.__liveOrderEvents ?? []).some(event => event.type === 'response.done' && event.status === 'completed' && /anything else/i.test(event.transcript ?? ''))""",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 90_000 });
                await page.WaitForFunctionAsync(
                    """() => (window.__liveOrderEvents ?? []).some(event => event.type === 'conversation.item.input_audio_transcription.completed' && /no\b.*\bit\b/i.test(event.transcript ?? ''))""",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 90_000 });
                await page.WaitForFunctionAsync(
                    """
                    () => {
                      const events = window.__liveOrderEvents ?? [];
                      const close = events.find(event => event.type === 'response.done' && event.status === 'completed' && /carhop/i.test(event.transcript ?? ''));
                      return close && events.some(event => event.type === 'response.audio.delta' && event.responseId === close.responseId);
                    }
                    """,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 90_000 });
                await page.WaitForFunctionAsync(
                    """
                    () => {
                      const ticket = document.querySelector('[data-testid="order-ticket"]')?.innerText ?? '';
                      return /Sonic Double Cheeseburger/i.test(ticket) && ticket.includes('$7.12');
                    }
                    """,
                    null,
                    new PageWaitForFunctionOptions { Timeout = 30_000 });

                var events = await page.EvaluateAsync<JsonElement>("() => window.__liveOrderEvents");
                var eventSummary = events.EnumerateArray().Select((entry, index) => new
                {
                    Index = index,
                    Type = entry.TryGetProperty("type", out var type) ? type.GetString() : null,
                    Status = entry.TryGetProperty("status", out var status) ? status.GetString() : null,
                    ResponseId = entry.TryGetProperty("responseId", out var responseId) ? responseId.GetString() : null,
                    Transcript = entry.TryGetProperty("transcript", out var transcript) ? transcript.GetString() : null,
                    AudioDelta = entry.TryGetProperty("audioDelta", out var audioDelta) && audioDelta.GetBoolean(),
                }).ToArray();

                var firstGuest = Array.FindIndex(eventSummary,
                    item => item.Type == "conversation.item.input_audio_transcription.completed"
                        && item.Transcript?.Contains("burger", StringComparison.OrdinalIgnoreCase) == true);
                var firstAssistant = Array.FindIndex(eventSummary,
                    item => item.Type == "response.done"
                        && item.Transcript?.Contains("Anything else", StringComparison.OrdinalIgnoreCase) == true);
                var closingGuest = Array.FindIndex(eventSummary,
                    item => item.Type == "conversation.item.input_audio_transcription.completed"
                        && item.Transcript?.Contains("No", StringComparison.OrdinalIgnoreCase) == true
                        && item.Transcript?.Contains("it", StringComparison.OrdinalIgnoreCase) == true);
                var closingAssistant = Array.FindIndex(eventSummary,
                    item => item.Type == "response.done"
                        && item.Transcript?.Contains("carhop", StringComparison.OrdinalIgnoreCase) == true);
                Assert.True(firstGuest >= 0 && firstGuest < firstAssistant
                    && firstAssistant < closingGuest && closingGuest < closingAssistant,
                    "Expected order speech -> assistant 'anything else' -> guest close -> assistant carhop farewell.");

                var diagnosticsPath = Environment.GetEnvironmentVariable("LIVE_CSHARP_ORDER_DIAGNOSTICS");
                if (!string.IsNullOrWhiteSpace(diagnosticsPath))
                {
                    await File.WriteAllTextAsync(diagnosticsPath, JsonSerializer.Serialize(new
                    {
                        endpointHost = endpointUri!.Host,
                        observedAtUtc = DateTimeOffset.UtcNow,
                        events = eventSummary,
                        ticket = await page.Locator("[data-testid='order-ticket']").InnerTextAsync(),
                    }, new JsonSerializerOptions { WriteIndented = true }),
                        TestContext.Current.CancellationToken);
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
}
