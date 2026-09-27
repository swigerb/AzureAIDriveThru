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
