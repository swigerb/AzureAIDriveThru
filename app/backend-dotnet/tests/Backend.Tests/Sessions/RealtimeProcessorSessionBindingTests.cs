using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Prompts;
using Backend.Realtime;
using Backend.Sessions;
using Backend.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Sessions;

/// <summary>
/// Issue #170 round 3, R5 (Rick's PR #175 round-2 review, Rick's mutation (f)): the Python side
/// had every session's tool list built from the deployment DEFAULT persona's own loader (fixed by
/// R4, see <c>PersonaSessionUpdateToolsConformanceTests.cs</c> in the conformance suite); Rick
/// asked for proof that the C# <see cref="RealtimeProcessor"/> never has (or regresses into) the
/// same defect for the <see cref="IToolExecutor"/> it hands to a session. <see
/// cref="RealtimeProcessor.RunSessionAsync"/> resolves <c>_promptLoaders.TryGetValue(persona.Id,
/// ...)</c> and threads THAT SAME loader into <c>_toolExecutorFactory.Invoke(persona, promptLoader,
/// menuMode)</c> -- correct today, but nothing proved it: a regression swapping in
/// <c>_promptLoaders.Values.First()</c> (the deployment default, or just whichever entry happens
/// to iterate first) would have passed every existing suite, since no test constructs two
/// differently-bound sessions and compares which loader the factory actually received.
///
/// This test exercises <see cref="RealtimeProcessor.ResolveSessionBinding"/> (internal, extracted
/// from <see cref="RealtimeProcessor.RunSessionAsync"/> for exactly this reason -- same idiom as
/// <see cref="RealtimeProcessor.ResolveUpstreamAuthHeaderAsync"/>) directly, with two distinct,
/// brand-agnostic temp packs (mirroring <see
/// cref="Tools.OrderToolExecutorToolExecutionFailedBoundPromptLoaderTests"/>'s own "widgetco"/
/// "northwind" idiom) and a capturing <c>toolExecutorFactory</c>, so the assertion only passes if
/// the factory is invoked with THIS SAME persona's own loader -- never the other persona's, never
/// a third, unrelated default.
/// </summary>
public sealed class RealtimeProcessorSessionBindingTests : IDisposable
{
    private readonly string _personasDir;

    public RealtimeProcessorSessionBindingTests()
    {
        _personasDir = Path.Combine(Path.GetTempPath(), "r170r3-session-binding-" + Guid.NewGuid().ToString("n"));
        WriteMinimalPromptPack("widgetco", "Widgetco's own tool failure text.");
        WriteMinimalPromptPack("northwind", "Northwind's own, different, tool failure text.");
    }

    [Fact]
    public void ResolveSessionBinding_passes_the_tool_executor_factory_THIS_personas_own_bound_loader()
    {
        var widgetcoLoader = new PromptLoader(_personasDir, "widgetco");
        var northwindLoader = new PromptLoader(_personasDir, "northwind");

        var capturedLoaders = new List<(string PersonaId, PromptLoader? Loader)>();
        IToolExecutor ToolExecutorFactory(Persona persona, PromptLoader? loader, string? menuMode)
        {
            capturedLoaders.Add((persona.Id, loader));
            return new StubToolExecutor([]);
        }

        var processor = new RealtimeProcessor(
            ModelCatalog.FromConfig(AppConfig.Load()),
            defaultDeployment: "gpt-realtime-2.1",
            upstreamEndpoint: "https://example-eastus2.openai.azure.com",
            upstreamApiKey: "sk-not-used",
            sessionConfig: new RealtimeSessionConfig(),
            promptLoaders: new Dictionary<string, PromptLoader>
            {
                ["widgetco"] = widgetcoLoader,
                ["northwind"] = northwindLoader,
            },
            toolExecutor: new StubToolExecutor([]),
            logger: NullLogger<RealtimeProcessor>.Instance,
            rateLimitLogger: NullLogger<RateLimitRecovery>.Instance,
            nudgeLogger: NullLogger<NudgeScheduler>.Instance,
            toolExecutorFactory: ToolExecutorFactory);

        var widgetcoPersona = PersonaCatalog.Load().Default with { Id = "widgetco" };
        var northwindPersona = PersonaCatalog.Load().Default with { Id = "northwind" };

        processor.ResolveSessionBinding(widgetcoPersona, menuMode: null);
        processor.ResolveSessionBinding(northwindPersona, menuMode: null);

        Assert.Equal(2, capturedLoaders.Count);
        var widgetcoCapture = capturedLoaders.Single(c => c.PersonaId == "widgetco");
        var northwindCapture = capturedLoaders.Single(c => c.PersonaId == "northwind");

        Assert.Same(widgetcoLoader, widgetcoCapture.Loader);
        Assert.Same(northwindLoader, northwindCapture.Loader);
        Assert.NotSame(widgetcoCapture.Loader, northwindCapture.Loader);

        // The live symptom R4/R5 both guard against: each persona's OWN rendered error text
        // reaches the loader the factory was actually given, never the other persona's.
        Assert.Equal("Widgetco's own tool failure text.", widgetcoCapture.Loader!.RenderError("tool_execution_failed"));
        Assert.Equal("Northwind's own, different, tool failure text.", northwindCapture.Loader!.RenderError("tool_execution_failed"));
    }

    [Fact]
    public void ResolveSessionBinding_also_threads_the_bound_loader_into_the_system_message_and_tool_schemas()
    {
        var widgetcoLoader = new PromptLoader(_personasDir, "widgetco");
        var processor = new RealtimeProcessor(
            ModelCatalog.FromConfig(AppConfig.Load()),
            defaultDeployment: "gpt-realtime-2.1",
            upstreamEndpoint: "https://example-eastus2.openai.azure.com",
            upstreamApiKey: "sk-not-used",
            sessionConfig: new RealtimeSessionConfig(),
            promptLoaders: new Dictionary<string, PromptLoader> { ["widgetco"] = widgetcoLoader },
            toolExecutor: new StubToolExecutor([]),
            logger: NullLogger<RealtimeProcessor>.Instance,
            rateLimitLogger: NullLogger<RateLimitRecovery>.Instance,
            nudgeLogger: NullLogger<NudgeScheduler>.Instance);

        var widgetcoPersona = PersonaCatalog.Load().Default with { Id = "widgetco" };

        var binding = processor.ResolveSessionBinding(widgetcoPersona, menuMode: null);

        Assert.Same(widgetcoLoader, binding.PromptLoader);
        Assert.Equal(widgetcoLoader.SystemPrompt, binding.SystemMessage);
        Assert.NotEmpty(binding.ToolSchemas);
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
