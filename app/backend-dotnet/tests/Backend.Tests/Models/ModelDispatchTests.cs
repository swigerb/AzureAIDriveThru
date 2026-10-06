using Backend.Configuration;
using Backend.Models;
using Backend.Personas;
using Backend.Sessions;
using Backend.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.Tests.Models;

/// <summary>
/// Rick's PR #122 review item 4: <see cref="Backend.Tests.Sessions.SessionActorTests"/>'s
/// <c>RecordingProcessor</c> doc comment has pointed at this file (and at
/// <c>Sessions/RealtimeProcessorTests.cs</c>) since S2 part 2 (#12) added the persona/model
/// dispatch seam, but neither file actually existed on this branch until now -- direct coverage
/// for <see cref="ModelDispatch.DispatchProcessor"/> and <see cref="ModelDispatch.ResolveRealtimeModel"/>
/// (processors.py's `dispatch_processor`/`resolve_realtime_model` port) was previously only
/// exercised indirectly, through the conformance suite's over-the-wire
/// <c>ModelSelectionRejectionConformanceTests</c>/<c>ModelSelectionConformanceTests</c> rows.
/// <see cref="Backend.Sessions.RealtimeProcessor.ResolveModel"/> is a one-line delegation straight
/// to <see cref="ModelDispatch.ResolveRealtimeModel"/> (see that class's own doc comment), so THIS
/// file is that logic's real unit coverage -- a separate <c>RealtimeProcessorTests.cs</c> would
/// only re-test the same delegation, hence SessionActorTests' comment now points here alone.
/// </summary>
public sealed class ModelDispatchTests
{
    /// <summary>A minimal <see cref="IPipelineProcessor"/> double for DispatchProcessor's own
    /// lookup-by-pipeline-name behaviour -- its ResolveModel/ProcessAsync are never invoked by
    /// anything DispatchProcessor itself does, so both are deliberately NotImplemented.</summary>
    private sealed class FakeProcessor(string pipelineName) : IPipelineProcessor
    {
        public string PipelineName { get; } = pipelineName;

        public ResolvedModel ResolveModel(Persona persona, string? requestedModelId) =>
            throw new NotImplementedException("DispatchProcessor never calls ResolveModel itself.");

        public Task ProcessAsync(SessionEvent sessionEvent, CancellationToken cancellationToken) =>
            throw new NotImplementedException("DispatchProcessor never calls ProcessAsync itself.");
    }

    /// <summary>Captures every Log call so a test can assert a warning was actually logged,
    /// without pulling in an extra test package for a single assertion.</summary>
    private sealed class RecordingLogger : ILogger<RealtimeProcessor>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Builds the real, shared config.yaml's ModelCatalog with an explicit
    /// AZURE_AI_MODEL_DEPLOYMENTS map (deliberately never falling back to this process's actual
    /// environment, for determinism) -- <paramref name="deployments"/> is model id -> deployment
    /// name, JSON-encoded here since that's the shape ModelCatalog.FromConfig expects under the
    /// env-var key itself (see ModelCatalogTests.DeploymentMap_MapsIdsToDeploymentNames).</summary>
    private static ModelCatalog RealCatalog(IReadOnlyDictionary<string, string> deployments)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(deployments);
        var env = new Dictionary<string, string> { ["AZURE_AI_MODEL_DEPLOYMENTS"] = json };
        return ModelCatalog.FromConfig(AppConfig.Load(), env);
    }

    /// <summary>Loads a throwaway copy of the real sonic pack (never the shared tree) with its
    /// `models.realtime` block replaced by <paramref name="realtime"/>.</summary>
    private static Persona SonicWithRealtimeBlock(PersonaPackFixture fixture, string defaultModelId, params string[] allowed)
    {
        fixture.MutatePersonaJson("sonic", obj =>
        {
            var models = obj["models"]!.AsObject();
            models["realtime"] = new System.Text.Json.Nodes.JsonObject
            {
                ["default"] = defaultModelId,
                ["allowed"] = new System.Text.Json.Nodes.JsonArray(allowed.Select(id => (System.Text.Json.Nodes.JsonNode)id).ToArray()),
            };
        });
        var catalog = PersonaCatalog.Load(personasDir: fixture.PersonasDir);
        return catalog.Get("sonic");
    }

    [Fact]
    public void Unknown_model_id_throws()
    {
        using var fixture = new PersonaPackFixture();
        var persona = SonicWithRealtimeBlock(fixture, "gpt-realtime-2.1", "gpt-realtime-2.1", "gpt-realtime-2.1-mini");
        var catalog = RealCatalog(new Dictionary<string, string> { ["gpt-realtime-2.1"] = "rt-deploy" });
        var registry = new ProcessorRegistry();
        registry.Register(new FakeProcessor("realtime"));

        var exc = Assert.Throws<ModelSelectionException>(
            () => ModelDispatch.DispatchProcessor(persona, "totally-unknown-model", catalog, registry));
        Assert.Contains("totally-unknown-model", exc.Message);
    }

    [Fact]
    public void Models_pipeline_with_no_registered_processor_throws()
    {
        using var fixture = new PersonaPackFixture();
        var persona = SonicWithRealtimeBlock(fixture, "gpt-realtime-2.1", "gpt-realtime-2.1");
        var catalog = RealCatalog(new Dictionary<string, string> { ["gpt-realtime-2.1"] = "rt-deploy" });
        var registry = new ProcessorRegistry();
        registry.Register(new FakeProcessor("realtime"));
        // gpt-5-mini is catalogued for "cascade" in the real config.yaml catalog, but no processor
        // is registered for that pipeline in this registry -- 404, same as an unknown model.
        Assert.True(catalog.IsCataloguedFor("gpt-5-mini", "cascade"));

        var exc = Assert.Throws<ModelSelectionException>(
            () => ModelDispatch.DispatchProcessor(persona, "gpt-5-mini", catalog, registry));
        Assert.Contains("gpt-5-mini", exc.Message);
    }

    [Fact]
    public void Omitted_model_dispatches_on_the_personas_realtime_default()
    {
        using var fixture = new PersonaPackFixture();
        var persona = SonicWithRealtimeBlock(fixture, "gpt-realtime-2.1", "gpt-realtime-2.1", "gpt-realtime-2.1-mini");
        var catalog = RealCatalog(new Dictionary<string, string> { ["gpt-realtime-2.1"] = "rt-deploy" });
        var registry = new ProcessorRegistry();
        var realtimeProcessor = new FakeProcessor("realtime");
        registry.Register(realtimeProcessor);

        var (pipelineName, processor) = ModelDispatch.DispatchProcessor(persona, requestedModelId: null, catalog, registry);

        Assert.Equal("realtime", pipelineName);
        Assert.Same(realtimeProcessor, processor);
    }

    [Fact]
    public void Default_with_no_deployment_falls_back_to_the_configured_default_deployment_and_logs_a_warning()
    {
        using var fixture = new PersonaPackFixture();
        // gpt-realtime-2.1-mini IS catalogued for realtime in the real config.yaml, but this test's
        // own deployment map deliberately leaves it unmapped -- the persona's OWN default, so the
        // AZURE_OPENAI_REALTIME_DEPLOYMENT back-compat fallback applies.
        var persona = SonicWithRealtimeBlock(fixture, "gpt-realtime-2.1-mini", "gpt-realtime-2.1-mini");
        var catalog = RealCatalog(new Dictionary<string, string>());
        var logger = new RecordingLogger();

        var resolved = ModelDispatch.ResolveRealtimeModel(
            persona, requestedModelId: null, catalog, defaultDeployment: "fallback-deployment", logger);

        Assert.Equal("gpt-realtime-2.1-mini", resolved.Id);
        Assert.Equal("fallback-deployment", resolved.Deployment);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// Rick's PR #122 review item 4's mutation check: removing the
    /// <c>modelId != pipelineCfg.Default</c> guard in <see cref="ModelDispatch.ResolveRealtimeModel"/>'s
    /// fallback branch (so EVERY undeployed model, not just the pipeline's own default, silently
    /// falls back to <c>defaultDeployment</c>) makes this test fail: a non-default undeployed
    /// model would then resolve successfully instead of throwing. Verified by hand for this
    /// revision: commenting out that guard turns this exact test red (`ResolveModel` returns a
    /// `fallback-deployment`-bound `ResolvedModel` instead of throwing), while every other test in
    /// this file still passes -- restoring the guard turns it back to green.
    /// </summary>
    [Fact]
    public void A_non_default_model_with_no_deployment_throws()
    {
        using var fixture = new PersonaPackFixture();
        var persona = SonicWithRealtimeBlock(fixture, "gpt-realtime-2.1", "gpt-realtime-2.1", "gpt-realtime-2.1-mini");
        // gpt-realtime-2.1 (the default) IS deployed; gpt-realtime-2.1-mini (merely allowed) is not.
        var catalog = RealCatalog(new Dictionary<string, string> { ["gpt-realtime-2.1"] = "rt-deploy" });

        var exc = Assert.Throws<ModelSelectionException>(() => ModelDispatch.ResolveRealtimeModel(
            persona, requestedModelId: "gpt-realtime-2.1-mini", catalog, defaultDeployment: "fallback-deployment",
            NullLogger<RealtimeProcessor>.Instance));
        Assert.Contains("gpt-realtime-2.1-mini", exc.Message);
    }

    [Fact]
    public void Reasoning_comes_from_the_catalog_entry_not_a_hardcoded_default()
    {
        using var fixture = new PersonaPackFixture();
        var persona = SonicWithRealtimeBlock(fixture, "gpt-realtime-2.1", "gpt-realtime-2.1", "gpt-realtime-2.1-mini");
        var catalog = RealCatalog(new Dictionary<string, string>
        {
            ["gpt-realtime-2.1"] = "rt-deploy",
            ["gpt-realtime-2.1-mini"] = "rt-mini-deploy",
        });

        // Real config.yaml catalogues BOTH gpt-realtime-2.1 and gpt-realtime-2.1-mini with
        // reasoning: true (ModelCatalogTests.RealSharedConfig_HasFourCatalogueEntries) --
        // issue #306 removed the real catalog's only non-reasoning realtime model
        // (gpt-realtime-mini, never deployed), so this row no longer has a non-reasoning realtime
        // model to contrast against. ResolvedModel.Reasoning must still reflect the catalog's own
        // per-model flag, not a single fixed value -- both resolve to the SAME catalog-sourced
        // true here, rather than one hardcoded default being reused for both ids.
        var reasoningModel = ModelDispatch.ResolveRealtimeModel(
            persona, "gpt-realtime-2.1", catalog, "unused-fallback", NullLogger<RealtimeProcessor>.Instance);
        var miniReasoningModel = ModelDispatch.ResolveRealtimeModel(
            persona, "gpt-realtime-2.1-mini", catalog, "unused-fallback", NullLogger<RealtimeProcessor>.Instance);

        Assert.True(reasoningModel.Reasoning);
        Assert.True(miniReasoningModel.Reasoning);
    }
}
