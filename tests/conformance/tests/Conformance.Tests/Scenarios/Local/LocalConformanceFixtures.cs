using System.Text.Json;
using System.Text.RegularExpressions;
using Conformance.Fakes;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests.Scenarios.Local;

/// <summary>
/// Issue #81 part 1: the dedicated backend process the local pipeline's conformance rows need,
/// with <c>LOCAL_RUNTIME_ENDPOINT</c> pointed at a <see cref="FakeLocalRuntimeServer"/> (the
/// companion-process seam of design doc section 7.4). No Azure fake is involved in a local turn
/// at all: transcription, chat (tool calling) and speech all go to <see cref="Runtime"/>.
///
/// The idle/grace/nudge widening is the same as <c>CascadeConformanceFixture</c>'s and for the
/// same reason: the idle clock is driven by guest activity only, so a turn still in flight on a
/// loaded machine must not race the idle checker.
/// </summary>
public sealed class LocalRuntimeConformanceFixture : ConformanceFixture
{
    public FakeLocalRuntimeServer Runtime { get; } = new();

    protected override async Task<IReadOnlyDictionary<string, string>> StartExtraFakesAsync()
    {
        await Runtime.StartAsync().ConfigureAwait(false);
        return new Dictionary<string, string>
        {
            ["LOCAL_RUNTIME_ENDPOINT"] = Runtime.BaseUri.ToString().TrimEnd('/'),
            ["CONFORMANCE_IDLE_TIMEOUT_SECONDS"] = "45",
            ["CONFORMANCE_GRACE_SECONDS"] = "45",
            ["CONFORMANCE_NUDGE_AFTER_SECONDS"] = "45",
        };
    }

    protected override async Task StopExtraFakesAsync() => await Runtime.DisposeAsync().ConfigureAwait(false);
}

[CollectionDefinition(Name)]
public sealed class LocalRuntimeConformanceCollection : ICollectionFixture<LocalRuntimeConformanceFixture>
{
    public const string Name = "ConformanceLocalRuntime";
}

/// <summary>
/// Issue #81 part 1, item 4: <c>LOCAL_RUNTIME_ENDPOINT</c> is configured (so local is
/// selectable) but nothing listens there: the "companion process not running" case. The port
/// is reserved and released by <see cref="NetworkUtils.GetFreeTcpPort"/>, so connects are
/// refused immediately instead of timing out.
/// </summary>
public sealed class LocalUnreachableRuntimeConformanceFixture : ConformanceFixture
{
    protected override Task<IReadOnlyDictionary<string, string>> StartExtraFakesAsync() =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
        {
            ["LOCAL_RUNTIME_ENDPOINT"] = $"http://127.0.0.1:{NetworkUtils.GetFreeTcpPort()}",
        });
}

[CollectionDefinition(Name)]
public sealed class LocalUnreachableRuntimeConformanceCollection : ICollectionFixture<LocalUnreachableRuntimeConformanceFixture>
{
    public const string Name = "ConformanceLocalUnreachableRuntime";
}

/// <summary>Reads what each real pack declares for the local pipeline straight from its own
/// files, so no row hardcodes a pack id, a model id or a message.</summary>
public static partial class LocalPackData
{
    /// <summary>Every real pack on disk, one Theory row each.</summary>
    public static TheoryData<string> PackIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in ConformancePersonas.DiscoverFromDisk())
        {
            data.Add(id);
        }
        return data;
    }

    /// <summary>The pack's own <c>models.local.default</c>. Design doc section 7: every shipped
    /// pack allows the local model (unselectable until a runtime is configured), so a pack
    /// without a <c>models.local</c> block fails here with the fix spelled out.</summary>
    public static string LocalDefaultModel(string personasDir, string personaId)
    {
        var path = Path.Combine(personasDir, personaId, "persona.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("models", out var models) ||
            !models.TryGetProperty("local", out var local) ||
            !local.TryGetProperty("default", out var defaultModel) ||
            string.IsNullOrWhiteSpace(defaultModel.GetString()))
        {
            throw new InvalidOperationException(
                $"{path} has no models.local.default. Every pack allows the local pipeline (design doc section 7): " +
                "add \"local\": { \"default\": \"<local catalog id>\", \"allowed\": [\"<local catalog id>\"] } under \"models\".");
        }
        return defaultModel.GetString()!;
    }

    /// <summary>The pack's own <c>generic_error</c> text from <c>prompts/error_messages.yaml</c>
    /// (required in every pack since #132). Plain-text values only, which is all any pack uses
    /// for this key.</summary>
    public static string GenericError(string personasDir, string personaId)
    {
        var path = Path.Combine(personasDir, personaId, "prompts", "error_messages.yaml");
        foreach (var line in File.ReadLines(path))
        {
            var match = GenericErrorLine().Match(line);
            if (match.Success)
            {
                return match.Groups["dq"].Success ? match.Groups["dq"].Value
                    : match.Groups["sq"].Success ? match.Groups["sq"].Value
                    : match.Groups["bare"].Value;
            }
        }
        throw new InvalidOperationException($"{path} has no generic_error message.");
    }

    [GeneratedRegex("""^\s+generic_error:\s*(?:"(?<dq>[^"]*)"|'(?<sq>[^']*)'|(?<bare>\S.*?))\s*$""")]
    private static partial Regex GenericErrorLine();
}
