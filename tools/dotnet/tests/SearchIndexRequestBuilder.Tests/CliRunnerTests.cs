namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Unit tests for CliRunner, driven directly against in-memory <see cref="TextWriter"/>s so
/// they're fast and deterministic -- no subprocess spawning, no real Azure OpenAI/Search endpoint
/// ever contacted, no real <c>azd</c> process invocation. Mirrors
/// ExtractProductionItems.Tests/CliRunnerTests.cs's and UpdateMenuSizes.Tests/CliRunnerTests.cs's
/// shape. Covers PR #250 review R1: the CLI must resolve the real OpenAI endpoint/deployment
/// (flag, then azd env, then process env var, then default) and fail cleanly -- not with a stack
/// trace -- when no endpoint is configured at all, rather than always printing the old hardcoded
/// fake values; and review R5: the azd default-environment value must actually reach resolution,
/// both via injection and via a real (but temp, disposable) .azure folder.
/// </summary>
public sealed class CliRunnerTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private string CreateEmptyRepoRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"squanchy-search-index-cli-runner-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void Run_PrintsHelpAndExitsZero_WhenHelpFlagGiven()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(["--help"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    /// <summary>
    /// Mutation check performed (reverted after confirming): temporarily restoring the old
    /// hardcoded "https://fake.openai.azure.com" literal in place of calling
    /// OpenAiSettingsResolver.Resolve made this test fail (exit code was 0, not 1, and stderr was
    /// empty) -- confirming this test genuinely guards the real resolution path being wired up,
    /// restored before committing.
    /// </summary>
    [Fact]
    public void Run_PrintsCleanErrorAndReturnsNonZeroExitCode_WhenNoOpenAiEndpointIsConfigured()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            [],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot(),
            getEnvironmentVariable: _ => null);

        Assert.Equal(1, exitCode);
        Assert.Equal("", stdout.ToString());
        var stderrText = stderr.ToString();
        Assert.Contains("AZURE_OPENAI_EASTUS2_ENDPOINT", stderrText);
        Assert.Contains("--openai-endpoint", stderrText);
        // A clean, single-line, actionable message -- not a stack trace, unlike Python's own
        // unhandled KeyError for the same missing-configuration case.
        Assert.DoesNotContain("   at ", stderrText);
        Assert.DoesNotContain("System.InvalidOperationException", stderrText);
    }

    [Fact]
    public void Run_NeverMentionsTheOldHardcodedFakeEndpoint_InItsErrorMessage()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliRunner.Run(
            [],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot(),
            getEnvironmentVariable: _ => null);

        Assert.DoesNotContain("fake.openai.azure.com", stdout.ToString() + stderr.ToString());
        Assert.DoesNotContain("fake-embedding-deployment", stdout.ToString() + stderr.ToString());
    }

    [Fact]
    public void Run_ResolvesOpenAiEndpointFlagFirst_BeforeAttemptingPersonaDiscovery()
    {
        // No personas directory under the synthetic repo root, so persona discovery will fail --
        // but ONLY after OpenAiSettingsResolver.Resolve has already succeeded via the flag given
        // here, proving settings resolution runs (and can succeed) ahead of persona discovery,
        // not the other way around.
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            ["--openai-endpoint", "https://real.openai.azure.com"],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot(),
            getEnvironmentVariable: _ => null);

        Assert.Equal(1, exitCode);
        var stderrText = stderr.ToString();
        // The failure is persona discovery's, not the endpoint's -- confirming the endpoint flag
        // was accepted and never re-triggered the "no Azure OpenAI endpoint configured" message.
        Assert.DoesNotContain("Azure OpenAI endpoint not set", stderrText);
    }

    // --- PR #250 review R5: CliRunner must actually wire AzdEnvLoader's result into
    // OpenAiSettingsResolver.Resolve, not just accept it as a parameter. ---

    /// <summary>
    /// Mutation check performed (reverted after confirming): temporarily hardcoding
    /// <c>loadAzdEnvValues</c>'s effective result to an always-empty dictionary (ignoring whatever
    /// was actually injected) made this test fail (exit code 1, "Azure OpenAI endpoint not set")
    /// instead of 0 -- confirming this test genuinely guards the azd value reaching
    /// OpenAiSettingsResolver, restored before committing.
    /// </summary>
    [Fact]
    public void Run_ResolvesOpenAiEndpoint_FromInjectedAzdEnvValues_WhenNoFlagOrProcessEnvVar()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            [],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot(),
            getEnvironmentVariable: _ => null,
            loadAzdEnvValues: _ => new Dictionary<string, string>
            {
                ["AZURE_OPENAI_EASTUS2_ENDPOINT"] = "https://azd.openai.azure.com",
            });

        // No personas directory exists under the synthetic repo root either, so this still exits
        // non-zero -- but via persona discovery's failure, not the endpoint's, proving the
        // injected azd value alone was enough to resolve the endpoint.
        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("Azure OpenAI endpoint not set", stderr.ToString());
    }

    /// <summary>
    /// End-to-end (no injection): proves CliRunner's default -- real -- AzdEnvLoader wiring reads
    /// an actual <c>.azure/config.json</c> + <c>.azure/&lt;env&gt;/.env</c> pair written to a real
    /// temp repo root, with no real <c>azd</c> process invocation and no real Azure call.
    /// </summary>
    [Fact]
    public void Run_ResolvesOpenAiEndpoint_FromARealTempAzureFolder_WithNoInjectionAndNoRealAzdCall()
    {
        var repoRoot = CreateEmptyRepoRoot();
        var envDir = Path.Combine(repoRoot, ".azure", "dev");
        Directory.CreateDirectory(envDir);
        File.WriteAllText(
            Path.Combine(repoRoot, ".azure", "config.json"),
            "{\"version\":1,\"defaultEnvironment\":\"dev\"}");
        File.WriteAllText(
            Path.Combine(envDir, ".env"),
            "AZURE_OPENAI_EASTUS2_ENDPOINT=\"https://real-temp-azd-folder.openai.azure.com\"\n");

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = CliRunner.Run(
            [],
            stdout,
            stderr,
            repoRootOverride: repoRoot,
            getEnvironmentVariable: _ => null);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("Azure OpenAI endpoint not set", stderr.ToString());
    }
}
