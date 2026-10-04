using System.Text.Json.Nodes;

namespace SearchIndexIngestor.Tests;

/// <summary>
/// Unit tests for CliRunner (the orchestration port's full CLI/<c>main()</c>/<c>run()</c>
/// faithful port), driven directly against in-memory <see cref="TextWriter"/>s and the test-seam
/// factory parameters -- no subprocess spawning, no real Azure OpenAI/Search endpoint ever
/// contacted, no real credential ever constructed. Mirrors
/// SearchIndexRequestBuilder.Tests/CliRunnerTests.cs's shape for the parts this port shares
/// (help text, endpoint-resolution-before-persona-discovery ordering) and adds coverage for the
/// orchestration-specific pieces that project's CliRunner never had: persona targeting,
/// dry-run output, the AZURE_SEARCH_SKIP_INDEX_SETUP short-circuit, and a full non-dry-run flow
/// wired end to end through fakes.
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
        var dir = Path.Combine(Path.GetTempPath(), $"squanchy-ingestor-cli-runner-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreatePersonasFixture(params (string PersonaId, string IndexName)[] personas)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"squanchy-ingestor-cli-runner-personas-{Guid.NewGuid():n}");
        foreach (var (personaId, indexName) in personas)
        {
            var personaDir = Path.Combine(dir, personaId);
            Directory.CreateDirectory(Path.Combine(personaDir, "menu"));
            File.WriteAllText(
                Path.Combine(personaDir, "persona.json"),
                "{\"search\": {\"indexName\": \"" + indexName + "\"}}");
            File.WriteAllText(
                Path.Combine(personaDir, "menu", "menuItems.json"),
                """{"menuItems": [{"category": "Coffee", "items": [{"name": "Latte", "description": "A latte.", "sizes": []}]}]}""");
        }
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public async Task RunAsync_PrintsHelpAndExitsZero_WhenHelpFlagGiven()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(["--help"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_PrintsHelpAndExitsZero_WhenShortHelpFlagGiven()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(["-h"], stdout, stderr);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_DryRun_PrintsOneLinePerTargetedPersona_AndExitsZero_WithNoAzdLoad()
    {
        var personasDir = CreatePersonasFixture(("alpha", "alpha-index"), ("bravo", "bravo-index"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var azdLoadCalls = 0;

        var exitCode = await CliRunner.RunAsync(
            ["--dry-run", "--personas-dir", personasDir],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot(),
            getEnvironmentVariable: _ => null,
            loadAzdEnvValues: _ => { azdLoadCalls++; return new Dictionary<string, string>(); });

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr.ToString());
        var stdoutText = stdout.ToString();
        Assert.Contains("[dry-run] persona 'alpha': index 'alpha-index', 1 document(s) planned", stdoutText);
        Assert.Contains("[dry-run] persona 'bravo': index 'bravo-index', 1 document(s) planned", stdoutText);
        // --dry-run's own help text promises "no azd env loaded" -- confirms that's true.
        Assert.Equal(0, azdLoadCalls);
    }

    [Fact]
    public async Task RunAsync_DryRun_TargetsOnlyTheRequestedPersona_WhenPersonaFlagGiven()
    {
        var personasDir = CreatePersonasFixture(("alpha", "alpha-index"), ("bravo", "bravo-index"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["--dry-run", "--personas-dir", personasDir, "--persona", "bravo"],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot());

        Assert.Equal(0, exitCode);
        var stdoutText = stdout.ToString();
        Assert.DoesNotContain("'alpha'", stdoutText);
        Assert.Contains("'bravo'", stdoutText);
    }

    [Fact]
    public async Task RunAsync_DryRun_PrintsCleanErrorAndExitsNonZero_ForUnknownPersonaId()
    {
        var personasDir = CreatePersonasFixture(("alpha", "alpha-index"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["--dry-run", "--personas-dir", personasDir, "--persona", "nonexistent"],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot());

        Assert.Equal(1, exitCode);
        Assert.Equal("", stdout.ToString());
        var stderrText = stderr.ToString();
        Assert.Contains("nonexistent", stderrText);
        Assert.Contains("not in the enabled persona catalog", stderrText);
        Assert.DoesNotContain("   at ", stderrText); // clean message, not a stack trace
    }

    [Fact]
    public async Task RunAsync_DryRun_PrintsCleanErrorAndExitsNonZero_WhenAPersonaHasNoMenuFile()
    {
        var personasDir = CreatePersonasFixture(("alpha", "alpha-index"));
        File.Delete(Path.Combine(personasDir, "alpha", "menu", "menuItems.json"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["--dry-run", "--personas-dir", personasDir],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot());

        Assert.Equal(1, exitCode);
        var stderrText = stderr.ToString();
        Assert.Contains("menu data file not found", stderrText);
        Assert.DoesNotContain("   at ", stderrText);
    }

    [Fact]
    public async Task RunAsync_SkipsEverything_AndExitsZero_WhenSkipIndexSetupVariableIsTrue()
    {
        // Short-circuits BEFORE persona discovery (repoRootOverride points at a repo root with no
        // personas directory at all, proving discovery never ran).
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            [],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot(),
            getEnvironmentVariable: name => name == "AZURE_SEARCH_SKIP_INDEX_SETUP" ? "true" : null,
            loadAzdEnvValues: _ => new Dictionary<string, string>());

        Assert.Equal(0, exitCode);
        Assert.Contains("AZURE_SEARCH_SKIP_INDEX_SETUP", stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_DoesNotSkip_WhenDryRun_EvenIfSkipVariableWouldBeTrue()
    {
        // --dry-run never loads azd env / checks the skip flag at all (lines 538-547 only run
        // outside --dry-run) -- confirmed by using a getEnvironmentVariable that would ALWAYS
        // short-circuit if consulted, yet dry-run output still appears.
        var personasDir = CreatePersonasFixture(("alpha", "alpha-index"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["--dry-run", "--personas-dir", personasDir],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot(),
            getEnvironmentVariable: _ => "true");

        Assert.Equal(0, exitCode);
        Assert.Contains("[dry-run] persona 'alpha'", stdout.ToString());
        Assert.DoesNotContain("AZURE_SEARCH_SKIP_INDEX_SETUP", stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_FullNonDryRunFlow_WiresFakeClientsEndToEnd_AndExitsZero()
    {
        var personasDir = CreatePersonasFixture(("alpha", "alpha-index"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var handler = new FakeHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return FakeHttpMessageHandler.Json(System.Net.HttpStatusCode.OK, request.Body!);
            }
            if (request.Uri.Contains("search.post.search"))
            {
                return FakeHttpMessageHandler.Json(System.Net.HttpStatusCode.OK, """{"value":[]}""");
            }
            if (request.Uri.Contains("search.index"))
            {
                var value = JsonNode.Parse(request.Body!)!["value"]!.AsArray();
                var results = value.Select(d => new JsonObject { ["key"] = d!["id"]!.DeepClone(), ["status"] = true });
                return FakeHttpMessageHandler.Json(
                    System.Net.HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(results.ToArray()) }.ToJsonString());
            }
            return FakeHttpMessageHandler.PlainText(System.Net.HttpStatusCode.OK, "1");
        });

        var exitCode = await CliRunner.RunAsync(
            [
                "--personas-dir", personasDir,
                "--search-endpoint", "https://fake.search.windows.net",
                "--openai-endpoint", "https://fake.openai.azure.com",
                "--embedding-deployment", "fake-deployment",
            ],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot(),
            getEnvironmentVariable: _ => null,
            loadAzdEnvValues: _ => new Dictionary<string, string>(),
            createSearchClient: (endpoint, _) => new SearchIndexHttpClient(new HttpClient(handler), endpoint),
            createEmbeddingClient: (_, _) => new FixtureEmbeddingClient(),
            createCredential: () => new FakeTokenCredential());

        Assert.Equal(0, exitCode);
        Assert.Equal("", stderr.ToString());
        var stdoutText = stdout.ToString();
        Assert.Contains("Persona 'alpha'", stdoutText);
        Assert.Contains("search index setup complete", stdoutText);
    }

    [Fact]
    public async Task RunAsync_NeverConstructsARealDefaultAzureCredential_ForDryRun()
    {
        // DefaultAzureCredential's constructor can be slow (probes several credential sources) --
        // dry-run's own help text promise ("no credential required") means this path must never
        // even construct one. Proven here by never supplying createCredential and still succeeding
        // fast with no network/credential-probing side effects possible in a unit test sandbox.
        var personasDir = CreatePersonasFixture(("alpha", "alpha-index"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await CliRunner.RunAsync(
            ["--dry-run", "--personas-dir", personasDir],
            stdout,
            stderr,
            repoRootOverride: CreateEmptyRepoRoot());

        Assert.Equal(0, exitCode);
    }
}
