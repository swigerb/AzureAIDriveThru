using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Rick's PR #106 review item 2: the two dedicated backend processes the model-selection
/// conformance rows need (design doc section 5.2/7.3, issue #75).
///
/// <see cref="ModelSelectionConformanceFixture"/> covers every NEGATIVE (404) row -- unknown,
/// disallowed, undeployed, wrong-pipeline -- which only need a persona whose own
/// `models.realtime.allowed` list is narrower than the full real catalog (the real `sonic` pack
/// allows BOTH real catalogued realtime models, so no persona in the shipped repo can ever
/// exercise "catalogued but not allowed for THIS persona" on its own). Reuses the same TEST-ONLY
/// fixture-pack convention <see cref="PersonaConformanceFixtures"/> already established
/// (test-alpha/test-beta) rather than touching a real pack under `personas/**` (explicitly out
/// of scope for #75's revision) -- adds a third pack, test-gamma, whose realtime `allowed` is
/// `["gpt-realtime-2.1-mini"]` only, deliberately excluding the real catalog's OTHER realtime model
/// (`gpt-realtime-2.1`) so requesting it against test-gamma is disallowed, not merely unknown.
/// No `AZURE_AI_MODEL_DEPLOYMENTS` override here -- the default (empty) deployment map is exactly
/// what the "undeployed" row needs.
///
/// <see cref="ModelDeploymentMapConformanceFixture"/> covers every POSITIVE row -- the model list,
/// `?model=` reaching the fake upstream as its OWN mapped deployment (not the
/// `AZURE_OPENAI_REALTIME_DEPLOYMENT` back-compat default), reasoning sent only for
/// catalog-reasoning models, the omitted-`?model=`-visible-in-metadata row, and `model_mismatch`
/// on resume in both directions -- all against the real, shipped `sonic` persona (no persona
/// override needed), with `AZURE_AI_MODEL_DEPLOYMENTS` populated for BOTH of sonic's allowed
/// realtime models so neither needs the default-only fallback branch.
///
/// Both env vars (`PERSONAS`/`PERSONAS_DIR` and `AZURE_AI_MODEL_DEPLOYMENTS`) are read once at
/// Python module-import time, so -- same reasoning as every other profile/persona override
/// fixture in this project (see <c>BackendProfileFixtures.cs</c>/<c>PersonaConformanceFixtures.cs</c>)
/// -- each needs its own dedicated collection/backend process.
/// </summary>
public sealed class ModelSelectionConformanceFixture : ConformanceFixture
{
    public const string PersonaAlpha = "test-alpha";
    public const string PersonaGamma = "test-gamma";

    protected override IReadOnlyList<string>? Personas => [PersonaAlpha, PersonaGamma];
    protected override string? Persona => PersonaAlpha;
    protected override string? PersonasDir => RepoPaths.FixturePersonasDirectory(RepoPaths.FindRepoRoot());
}

[CollectionDefinition(Name)]
public sealed class ModelSelectionConformanceCollection : ICollectionFixture<ModelSelectionConformanceFixture>
{
    public const string Name = "ConformanceModelSelection";
}

public sealed class ModelDeploymentMapConformanceFixture : ConformanceFixture
{
    public const string RealtimeDefaultDeployment = "gpt-realtime-2.1-mini-model-map-conformance";
    public const string RealtimeAltDeployment = "gpt-realtime-2.1-model-map-conformance";

    protected override BackendProfile Profile { get; } = new(
        "ModelDeploymentMap",
        new Dictionary<string, string>
        {
            ["AZURE_AI_MODEL_DEPLOYMENTS"] =
                $$"""{"gpt-realtime-2.1-mini":"{{RealtimeDefaultDeployment}}","gpt-realtime-2.1":"{{RealtimeAltDeployment}}"}""",
        });
}

[CollectionDefinition(Name)]
public sealed class ModelDeploymentMapConformanceCollection : ICollectionFixture<ModelDeploymentMapConformanceFixture>
{
    public const string Name = "ConformanceModelDeploymentMap";
}
