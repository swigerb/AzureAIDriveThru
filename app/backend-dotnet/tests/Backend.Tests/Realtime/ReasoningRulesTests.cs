using Backend.Realtime;

namespace Backend.Tests.Realtime;

/// <summary>Byte-for-byte port tests for rtmt.py's `deployment_supports_reasoning`,
/// `parse_reasoning_model`, `normalize_reasoning_effort` -- mirrors
/// app/backend/tests/test_session_bootstrap.py's `test_deployment_name_check` and
/// `test_data_zone_deployment_name_is_a_reasoning_deployment` cases exactly.</summary>
public sealed class ReasoningRulesTests
{
    [Theory]
    [InlineData("gpt-realtime-2")]
    [InlineData("gpt-realtime-2.1")]
    [InlineData("GPT-Realtime-2.1")]
    [InlineData("gpt-realtime-2.1-dz")]
    [InlineData("GPT-Realtime-2.1-DZ")]
    [InlineData("gpt-realtime-2-dz")]
    [InlineData("my-custom-carhop")]
    [InlineData("")]
    [InlineData(null)]
    public void DeploymentSupportsReasoning_TrueForReasoningModels(string? deployment)
    {
        Assert.True(ReasoningRules.DeploymentSupportsReasoning(deployment));
    }

    [Theory]
    [InlineData("gpt-realtime-1.5")]
    [InlineData("gpt-realtime-1")]
    [InlineData("gpt-realtime")]
    [InlineData("gpt-realtime-2025-08-28")]
    [InlineData("gpt-realtime-mini-2025-10-06")]
    [InlineData("gpt-4o-realtime-preview")]
    [InlineData("gpt-realtime-1.5-dz")]
    [InlineData("gpt-realtime-mini-dz")]
    public void DeploymentSupportsReasoning_FalseForNonReasoningModels(string deployment)
    {
        Assert.False(ReasoningRules.DeploymentSupportsReasoning(deployment));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    [InlineData("auto", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("bogus", null)]
    public void ParseReasoningModel_ReturnsExpectedTriState(string? value, bool? expected)
    {
        Assert.Equal(expected, ReasoningRules.ParseReasoningModel(value));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("auto", true)]
    [InlineData("null", true)]
    [InlineData("none", true)]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("bogus", false)]
    public void IsRecognisedReasoningModelValue_FlagsUnknownValues(string? value, bool expected)
    {
        Assert.Equal(expected, ReasoningRules.IsRecognisedReasoningModelValue(value));
    }

    [Theory]
    [InlineData("none", "none")]
    [InlineData("minimal", "minimal")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("HIGH", "high")]
    [InlineData("", null)]
    [InlineData("off", null)]
    [InlineData("disabled", null)]
    [InlineData("false", null)]
    [InlineData("null", null)]
    [InlineData(null, null)]
    [InlineData("bogus", null)]
    public void NormalizeReasoningEffort_MapsToWireValueOrNull(string? value, string? expected)
    {
        Assert.Equal(expected, ReasoningRules.NormalizeReasoningEffort(value));
    }
}
