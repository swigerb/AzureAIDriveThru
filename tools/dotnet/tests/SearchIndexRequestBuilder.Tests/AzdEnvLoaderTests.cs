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
}
