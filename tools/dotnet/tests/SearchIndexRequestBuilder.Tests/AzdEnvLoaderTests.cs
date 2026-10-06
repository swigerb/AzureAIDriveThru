using System.Text.Json;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Unit tests for AzdEnvLoader (PR #250 review R5): builds a real, disposable temp
/// <c>.azure/config.json</c> + <c>.azure/&lt;env&gt;/.env</c> pair per test -- real file I/O, but
/// never a real <c>azd</c> process invocation and never a real Azure call, per Rick's explicit
/// request ("tests using a temp .azure folder (no real azd calls in tests)").
/// </summary>
public sealed class AzdEnvLoaderTests : IDisposable
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

    private string CreateTempRepoRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"squanchy-azd-env-loader-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void WriteAzdEnv(string repoRoot, string environmentName, string dotEnvContents)
    {
        var envDir = Path.Combine(repoRoot, ".azure", environmentName);
        Directory.CreateDirectory(envDir);
        File.WriteAllText(
            Path.Combine(repoRoot, ".azure", "config.json"),
            JsonSerializer.Serialize(new { version = 1, defaultEnvironment = environmentName }));
        File.WriteAllText(Path.Combine(envDir, ".env"), dotEnvContents);
    }

    [Fact]
    public void LoadDefaultEnvValues_ReturnsEmpty_WhenNoAzureFolderExists()
    {
        var repoRoot = CreateTempRepoRoot();

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Empty(values);
    }

    [Fact]
    public void LoadDefaultEnvValues_ReturnsEmpty_WhenConfigJsonHasNoDefaultEnvironment()
    {
        var repoRoot = CreateTempRepoRoot();
        Directory.CreateDirectory(Path.Combine(repoRoot, ".azure"));
        File.WriteAllText(Path.Combine(repoRoot, ".azure", "config.json"), "{\"version\":1}");

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Empty(values);
    }

    [Fact]
    public void LoadDefaultEnvValues_ReturnsEmpty_WhenDefaultEnvironmentHasNoEnvFile()
    {
        var repoRoot = CreateTempRepoRoot();
        Directory.CreateDirectory(Path.Combine(repoRoot, ".azure", "dev"));
        File.WriteAllText(
            Path.Combine(repoRoot, ".azure", "config.json"),
            "{\"version\":1,\"defaultEnvironment\":\"dev\"}");

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Empty(values);
    }

    [Fact]
    public void LoadDefaultEnvValues_ReturnsEmpty_WhenConfigJsonIsMalformed()
    {
        var repoRoot = CreateTempRepoRoot();
        Directory.CreateDirectory(Path.Combine(repoRoot, ".azure"));
        File.WriteAllText(Path.Combine(repoRoot, ".azure", "config.json"), "{not valid json");

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Empty(values);
    }

    [Fact]
    public void LoadDefaultEnvValues_ParsesQuotedValues_FromTheDefaultEnvironmentsDotEnvFile()
    {
        var repoRoot = CreateTempRepoRoot();
        WriteAzdEnv(
            repoRoot,
            "dev",
            "AZURE_ENV_NAME=\"dev\"\n" +
            "AZURE_OPENAI_EASTUS2_ENDPOINT=\"https://azd.openai.azure.com\"\n" +
            "AZURE_OPENAI_EMBEDDING_DEPLOYMENT=\"azd-deployment\"\n");

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Equal("https://azd.openai.azure.com", values["AZURE_OPENAI_EASTUS2_ENDPOINT"]);
        Assert.Equal("azd-deployment", values["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"]);
    }

    /// <summary>
    /// azd itself writes a backslash-escaped embedded quote exactly this way -- confirmed
    /// empirically via <c>azd env set TEST_WITH_QUOTE 'a"b'</c> against a disposable local
    /// environment (see AzdEnvLoader.cs's own doc comment).
    /// </summary>
    [Fact]
    public void LoadDefaultEnvValues_UnescapesBackslashEscapedQuotes()
    {
        var repoRoot = CreateTempRepoRoot();
        WriteAzdEnv(repoRoot, "dev", "TEST_WITH_QUOTE=\"a\\\"b\"\n");

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Equal("a\"b", values["TEST_WITH_QUOTE"]);
    }

    [Fact]
    public void LoadDefaultEnvValues_SkipsBlankLinesAndComments()
    {
        var repoRoot = CreateTempRepoRoot();
        WriteAzdEnv(
            repoRoot,
            "dev",
            "# a comment\n\nAZURE_ENV_NAME=\"dev\"\n   \n# AZURE_OPENAI_EASTUS2_ENDPOINT=\"commented-out\"\n");

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Single(values);
        Assert.Equal("dev", values["AZURE_ENV_NAME"]);
    }

    /// <summary>
    /// Mutation check performed (reverted after confirming): temporarily making
    /// UnquoteDotEnvValue a no-op (returning the raw, still-quoted text unchanged) made this test
    /// fail (the value included the surrounding quote characters) -- confirmed, then restored.
    /// </summary>
    [Fact]
    public void LoadDefaultEnvValues_StripsSurroundingQuotes_NotJustLeavesThemIn()
    {
        var repoRoot = CreateTempRepoRoot();
        WriteAzdEnv(repoRoot, "dev", "AZURE_ENV_NAME=\"dev\"\n");

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Equal("dev", values["AZURE_ENV_NAME"]);
        Assert.DoesNotContain('"', values["AZURE_ENV_NAME"]);
    }

    /// <summary>
    /// Confirmed against real azd 1.34.2 (Rick's re-review): azd backslash-escapes <c>$</c>,
    /// <c>!</c>, and the backtick in addition to the embedded-quote case above (shell-safety for a
    /// value that will later be sourced), and this parser's catch-all <c>_ =&gt; inner[i]</c> case
    /// happens to decode all three back to the plain character -- matching azd's own intent, but
    /// NOT matching python-dotenv, which only recognizes a small fixed set of escape sequences and
    /// leaves an unrecognized <c>\$</c>/<c>\!</c>/<c>\`</c> as a literal backslash followed by the
    /// character. This is a real, documented divergence (see docs/dotnet_tooling.md), but one that
    /// cannot occur for either of the two keys this tool ever reads: a URL
    /// (AZURE_OPENAI_EASTUS2_ENDPOINT) and an Azure OpenAI deployment name
    /// (AZURE_OPENAI_EMBEDDING_DEPLOYMENT) can never contain <c>$</c>, <c>!</c>, or a backtick in
    /// practice, per Azure's own naming rules: the endpoint is always
    /// <c>https://&lt;resource&gt;.openai.azure.com/</c>, where resource names allow only letters,
    /// digits, and hyphens, and deployment names allow only letters, digits, <c>-</c>, <c>_</c>,
    /// and <c>.</c> -- not because URL syntax itself forbids those characters (RFC 3986 allows
    /// both <c>$</c> and <c>!</c> as sub-delims; only a literal backtick is disallowed there). So
    /// this parser's extra-permissive unescaping is never actually exercised by real data -- this
    /// test exists only to pin the behavior deliberately.
    /// </summary>
    [Fact]
    public void LoadDefaultEnvValues_UnescapesBackslashEscapedDollarBangAndBacktick()
    {
        var repoRoot = CreateTempRepoRoot();
        WriteAzdEnv(repoRoot, "dev", "TEST_WITH_SPECIAL_CHARS=\"a\\$b\\!c\\`d\"\n");

        var values = AzdEnvLoader.LoadDefaultEnvValues(repoRoot);

        Assert.Equal("a$b!c`d", values["TEST_WITH_SPECIAL_CHARS"]);
    }
}
