using Backend;
using Backend.Prompts;

namespace Backend.Tests.Prompts;

/// <summary>Fail-fast prompt-loading tests, mirroring app/backend/prompt_loader.py's contract
/// (minus DEV_MODE hot-reload and Jinja2 rendering -- deliberately out of scope this wave, see
/// docs/dotnet_mapping.md).</summary>
public sealed class PromptLoaderTests : IDisposable
{
    private readonly string _personasDir;

    public PromptLoaderTests()
    {
        _personasDir = Path.Combine(Path.GetTempPath(), "beth-prompt-tests-" + Guid.NewGuid().ToString("n"));
        WriteValidPromptPack("acme");
    }

    [Fact]
    public void LoadsValidPromptPack()
    {
        var loader = new PromptLoader(_personasDir, "acme");

        Assert.Contains("Welcome to Acme", loader.SystemPrompt);
        Assert.Contains("First section.", loader.SystemPrompt);
        Assert.True(loader.Greeting.ContainsKey("type"));
        Assert.Single(loader.ToolSchemas);
        Assert.NotEmpty(loader.ErrorMessages);
        Assert.NotEmpty(loader.Hints);
    }

    [Fact]
    public void SystemPromptSections_AssembledInPriorityOrder()
    {
        var loader = new PromptLoader(_personasDir, "acme");

        var firstIndex = loader.SystemPrompt.IndexOf("First section.", StringComparison.Ordinal);
        var secondIndex = loader.SystemPrompt.IndexOf("Second section.", StringComparison.Ordinal);

        Assert.True(firstIndex >= 0 && secondIndex >= 0 && firstIndex < secondIndex);
    }

    [Fact]
    public void MissingPromptsDirectory_Throws()
    {
        var exc = Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "no-such-persona"));
        Assert.Contains("Prompt directory not found", exc.Message);
    }

    [Fact]
    public void MissingSystemPromptFile_Throws()
    {
        File.Delete(Path.Combine(_personasDir, "acme", "prompts", "system_prompt.yaml"));

        var exc = Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "acme"));
        Assert.Contains("system_prompt.yaml", exc.Message);
    }

    [Fact]
    public void MalformedYaml_Throws()
    {
        File.WriteAllText(Path.Combine(_personasDir, "acme", "prompts", "system_prompt.yaml"), "sections: [unterminated");

        Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "acme"));
    }

    [Fact]
    public void EmptySystemPromptSections_Throws()
    {
        File.WriteAllText(
            Path.Combine(_personasDir, "acme", "prompts", "system_prompt.yaml"),
            "sections:\n  - priority: 1\n    content: \"\"\n");

        var exc = Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "acme"));
        Assert.Contains("empty prompt", exc.Message);
    }

    [Fact]
    public void GreetingMissingTypeField_Throws()
    {
        File.WriteAllText(Path.Combine(_personasDir, "acme", "prompts", "greeting.yaml"), "greeting:\n  text: hi\n");

        var exc = Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "acme"));
        Assert.Contains("type", exc.Message);
    }

    [Fact]
    public void ToolSchemasEmptyList_Throws()
    {
        File.WriteAllText(Path.Combine(_personasDir, "acme", "prompts", "tool_schemas.yaml"), "tools: []\n");

        var exc = Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "acme"));
        Assert.Contains("non-empty 'tools' list", exc.Message);
    }

    [Fact]
    public void ToolSchemaMissingName_Throws()
    {
        File.WriteAllText(
            Path.Combine(_personasDir, "acme", "prompts", "tool_schemas.yaml"),
            "tools:\n  - type: function\n");

        var exc = Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "acme"));
        Assert.Contains("missing 'name'", exc.Message);
    }

    // ── #125 (fail-fast follow-up to #116): a pack whose error_messages.yaml lacks one or more of
    // RequiredErrorMessageKeys must fail startup the same way a missing greeting/tool schema does,
    // naming both the pack and the missing key(s) -- never a silent runtime fallback to a bare
    // "An error occurred" placeholder. ──────────────────────────────────────────────────────────

    [Fact]
    public void ErrorMessagesMissingOneRequiredKey_Throws()
    {
        File.WriteAllText(Path.Combine(_personasDir, "acme", "prompts", "error_messages.yaml"), """
            messages:
              generic_error: "Something went wrong."
              item_not_on_menu: "Sorry, that isn't on our menu."
              size_not_available: "Sorry, that size isn't available."
              item_not_in_order: "That isn't in the order."
              machine_unavailable: "Sorry, that isn't available right now."
              extras_blocked_category: "Extras can't be added to that category right now."
            """);

        var exc = Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "acme"));
        Assert.Contains("acme", exc.Message);
        Assert.Contains("extras_no_base_item", exc.Message);
        Assert.Contains("missing required rejection-message key", exc.Message);
    }

    [Fact]
    public void ErrorMessagesMissingAllRequiredKeys_ThrowsNamingEachOne()
    {
        File.WriteAllText(Path.Combine(_personasDir, "acme", "prompts", "error_messages.yaml"), """
            messages:
              generic: "Something went wrong."
            """);

        var exc = Assert.Throws<PromptLoadException>(() => new PromptLoader(_personasDir, "acme"));
        foreach (var key in PromptLoader.RequiredErrorMessageKeys)
        {
            Assert.Contains(key, exc.Message);
        }
    }

    [Fact]
    public void ErrorMessagesWithAllRequiredKeys_DoesNotThrow()
    {
        // Baseline proof the fixture pack (and the validation itself) isn't accidentally
        // over-strict: WriteValidPromptPack's error_messages.yaml already has every required key.
        var exc = Record.Exception(() => new PromptLoader(_personasDir, "acme"));
        Assert.Null(exc);
    }

    /// <summary>Mirrors app/backend/prompt_loader.py's REQUIRED_ERROR_MESSAGE_KEYS constant
    /// byte-for-byte (design doc section 6). Parses the real Python source file directly (never a
    /// second hardcoded literal copy in this test) so the two lists can never silently drift
    /// apart -- if either file's list changes without the other, this test fails.</summary>
    [Fact]
    public void RequiredErrorMessageKeys_MatchPython()
    {
        var pythonKeys = ParsePythonRequiredErrorMessageKeys();

        Assert.Equal(pythonKeys, PromptLoader.RequiredErrorMessageKeys);
    }

    private static List<string> ParsePythonRequiredErrorMessageKeys()
    {
        var repoRoot = RepoRootLocator.Find();
        var pythonFile = Path.Combine(repoRoot, "app", "backend", "prompt_loader.py");
        var source = File.ReadAllText(pythonFile);

        var marker = "REQUIRED_ERROR_MESSAGE_KEYS: tuple[str, ...] = (";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find REQUIRED_ERROR_MESSAGE_KEYS in {pythonFile}");
        var end = source.IndexOf(")", start, StringComparison.Ordinal);
        var body = source[(start + marker.Length)..end];

        var keys = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('"'))
            {
                continue;
            }
            var closingQuote = line.IndexOf('"', 1);
            keys.Add(line[1..closingQuote]);
        }
        return keys;
    }

    private void WriteValidPromptPack(string personaId)
    {
        var promptsDir = Path.Combine(_personasDir, personaId, "prompts");
        Directory.CreateDirectory(promptsDir);

        File.WriteAllText(Path.Combine(promptsDir, "system_prompt.yaml"), """
            sections:
              - priority: 2
                content: "Second section."
              - priority: 1
                content: "Welcome to Acme. First section."
            """);
        File.WriteAllText(Path.Combine(promptsDir, "greeting.yaml"), """
            greeting:
              type: "text"
              text: "Welcome!"
            """);
        File.WriteAllText(Path.Combine(promptsDir, "tool_schemas.yaml"), """
            tools:
              - name: "add_item"
                type: "function"
            """);
        File.WriteAllText(Path.Combine(promptsDir, "error_messages.yaml"), """
            messages:
              generic: "Something went wrong."
              generic_error: "Something went wrong."
              item_not_on_menu: "Sorry, that isn't on our menu."
              size_not_available: "Sorry, that size isn't available."
              item_not_in_order: "That isn't in the order."
              machine_unavailable: "Sorry, that isn't available right now."
              extras_blocked_category: "Extras can't be added to that category right now."
              extras_no_base_item: "Extras need a base item in the order first."
              item_out_of_mode: "Sorry, that isn't on the menu right now."
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
            // Best-effort cleanup.
        }
    }
}
