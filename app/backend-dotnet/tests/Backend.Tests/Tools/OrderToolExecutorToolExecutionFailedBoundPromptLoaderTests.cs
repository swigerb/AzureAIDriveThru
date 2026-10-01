using System.Text.Json;
using Backend.Ordering;
using Backend.Personas;
using Backend.Prompts;
using Backend.Tests.TestSupport;
using Backend.Tools;

namespace Backend.Tests.Tools;

/// <summary>
/// Issue #170 round 2 (Rick's PR #175 review, item R2): the Python side had a defect where the
/// `tool_execution_failed`/tool-failure-cap-notice error text was always rendered from the
/// deployment DEFAULT persona's prompt loader (<c>self._prompt_loader</c>), not the connection's
/// own BOUND persona's loader -- see rtmt.py's <c>ProcessMessageToClientTests</c> regression
/// tests added alongside this file. Rick's review asked that the C# equivalent be checked too.
///
/// <see cref="OrderToolExecutor"/>'s own `tool_execution_failed` site (missing required
/// `update_order` arguments) already renders from its OWN constructor-injected
/// <see cref="PromptLoader"/> field (<c>_promptLoader</c>), which <see
/// cref="Sessions.RealtimeProcessor.RunSessionAsync"/> constructs per-session from THIS
/// connection's own bound persona (<c>_promptLoaders.TryGetValue(persona.Id, ...)</c>) -- so the
/// production code was already correct here, unlike Python's. This test exists because, before
/// it, nothing proved that: a regression that swapped in a shared/default loader instead of the
/// per-executor one would have passed every existing test (none of which construct two
/// differently-bound executors and compare their error text).
///
/// Two synthetic, brand-agnostic temp packs (mirroring <c>PromptLoaderTests.cs</c>'s own
/// "acme" temp-pack idiom -- never real personas/, never a shared fixture under
/// app/backend/tests/fixtures/personas/, which carry no `tool_execution_failed` key of their own
/// and would render byte-identically via <see cref="PromptLoader.RenderError"/>'s unknown-key
/// fallback either way) each define their OWN distinct `tool_execution_failed` text, so the
/// assertion below only passes if <see cref="OrderToolExecutor"/> is reading through the loader
/// it was actually constructed with.
/// </summary>
public sealed class OrderToolExecutorToolExecutionFailedBoundPromptLoaderTests : IDisposable
{
    private const int MaxItemQuantity = 10;
    private const int MaxOrderItems = 25;

    private readonly string _personasDir;

    public OrderToolExecutorToolExecutionFailedBoundPromptLoaderTests()
    {
        _personasDir = Path.Combine(Path.GetTempPath(), "r170r2-tool-exec-failed-" + Guid.NewGuid().ToString("n"));
        WriteMinimalPromptPack("widgetco", "Widgetco's own tool failure text.");
        WriteMinimalPromptPack("northwind", "Northwind's own, different, tool failure text.");
    }

    [Fact]
    public async Task UpdateOrder_missing_required_args_renders_tool_execution_failed_from_THIS_executors_own_bound_loader()
    {
        var ct = TestContext.Current.CancellationToken;
        var persona = DeltaFixture.Load();
        var menu = PersonaOrderFactory.GetMenuCatalog(persona);

        var widgetcoLoader = new PromptLoader(_personasDir, "widgetco");
        var widgetcoExecutor = new OrderToolExecutor(
            PersonaOrderFactory.CreateOrderState(persona), menu, widgetcoLoader, MaxItemQuantity, MaxOrderItems);

        var northwindLoader = new PromptLoader(_personasDir, "northwind");
        var northwindExecutor = new OrderToolExecutor(
            PersonaOrderFactory.CreateOrderState(persona), menu, northwindLoader, MaxItemQuantity, MaxOrderItems);

        var missingArgs = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["action"] = "add" });

        var widgetcoResult = await widgetcoExecutor.ExecuteAsync("update_order", missingArgs, ct);
        var northwindResult = await northwindExecutor.ExecuteAsync("update_order", missingArgs, ct);

        Assert.Equal("Widgetco's own tool failure text.", widgetcoResult.ToText());
        Assert.Equal("Northwind's own, different, tool failure text.", northwindResult.ToText());
        Assert.NotEqual(widgetcoResult.ToText(), northwindResult.ToText());
    }

    private void WriteMinimalPromptPack(string personaId, string toolExecutionFailedText)
    {
        var promptsDir = Path.Combine(_personasDir, personaId, "prompts");
        Directory.CreateDirectory(promptsDir);

        File.WriteAllText(Path.Combine(promptsDir, "system_prompt.yaml"), """
            sections:
              - priority: 1
                content: "Test persona."
            """);
        File.WriteAllText(Path.Combine(promptsDir, "greeting.yaml"), """
            greeting:
              type: "text"
              text: "Welcome!"
            """);
        File.WriteAllText(Path.Combine(promptsDir, "tool_schemas.yaml"), """
            tools:
              - name: "update_order"
                type: "function"
            """);
        File.WriteAllText(Path.Combine(promptsDir, "error_messages.yaml"), $"""
            messages:
              generic_error: "Something went wrong."
              item_not_on_menu: "Sorry, that isn't on our menu."
              size_not_available: "Sorry, that size isn't available."
              item_not_in_order: "That isn't in the order."
              machine_unavailable: "Sorry, that isn't available right now."
              extras_blocked_category: "Extras can't be added to that category right now."
              extras_no_base_item: "Extras need a base item in the order first."
              item_out_of_mode: "Sorry, that isn't on the menu right now."
              tool_execution_failed: "{toolExecutionFailedText}"
            """);
        File.WriteAllText(Path.Combine(promptsDir, "hints.yaml"), """
            hints:
              upsell: "Would you like fries with that?"
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_personasDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp-dir cleanup -- matches PromptLoaderTests.cs's own Dispose.
        }
    }
}
