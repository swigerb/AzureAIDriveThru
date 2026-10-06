using System.Text.Json;
using System.Text.Json.Nodes;
using Backend;
using Backend.Prompts;

namespace Backend.Tests.Prompts;

/// <summary>
/// Regression for the live 2026-10-05 C# outage: YamlDotNet's untyped deserializer returned every
/// scalar as a string, so tool_schemas.yaml's <c>additionalProperties: false</c> reached the
/// realtime API as the STRING "false". The API rejected every session.update
/// (invalid_function_parameters), so C# sessions ran with no tools and no instructions and the
/// order ticket never updated. These tests assert the JSON value kinds the wire actually carries,
/// for every shipped persona pack, the same way PyYAML's safe_load types them for Python.
/// </summary>
public sealed class PromptLoaderScalarTypingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "prompt-typing-" + Guid.NewGuid().ToString("n"));

    public static TheoryData<string> ShippedPersonas()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.GetDirectories(Path.Combine(RepoRootLocator.Find(), "personas")).Order())
        {
            if (File.Exists(Path.Combine(dir, "prompts", "tool_schemas.yaml")))
            {
                data.Add(Path.GetFileName(dir));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ShippedPersonas))]
    public void ShippedToolSchemas_SerializeBooleansAsJsonBooleans(string personaId)
    {
        var loader = new PromptLoader(Path.Combine(RepoRootLocator.Find(), "personas"), personaId);
        Assert.NotEmpty(loader.ToolSchemas);

        var sawAdditionalProperties = false;
        foreach (var schema in loader.ToolSchemas)
        {
            var node = YamlJson.ToJsonNode(schema);
            Assert.NotNull(node);
            foreach (var (path, value) in Walk(node!, "$"))
            {
                if (path.EndsWith(".additionalProperties", StringComparison.Ordinal))
                {
                    sawAdditionalProperties = true;
                    Assert.True(value.GetValueKind() is JsonValueKind.False or JsonValueKind.True or JsonValueKind.Object,
                        $"{personaId}: {path} must be a JSON boolean or object, got {value.GetValueKind()} ({value.ToJsonString()})");
                }
                if (value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                {
                    var text = v.GetValue<string>();
                    Assert.False(text is "true" or "false" && (path.EndsWith(".additionalProperties", StringComparison.Ordinal) || path.EndsWith(".strict", StringComparison.Ordinal)),
                        $"{personaId}: {path} is the string \"{text}\" -- the realtime API rejects this");
                }
            }
        }
        Assert.True(sawAdditionalProperties, $"{personaId}: expected at least one additionalProperties in tool schemas");
    }

    [Fact]
    public void PlainScalarsAreTyped_QuotedScalarsStayStrings()
    {
        var prompts = Path.Combine(_tempDir, "acme", "prompts");
        Directory.CreateDirectory(prompts);
        File.WriteAllText(Path.Combine(prompts, "system_prompt.yaml"), "sections:\n  - name: a\n    priority: 1\n    content: \"Welcome to Acme.\"\n");
        File.WriteAllText(Path.Combine(prompts, "greeting.yaml"), "greeting:\n  type: response.create\n");
        File.WriteAllText(Path.Combine(prompts, "error_messages.yaml"),
            "messages:\n  generic: \"Sorry.\"\n  generic_error: \"Sorry.\"\n  item_not_on_menu: \"x\"\n  size_not_available: \"x\"\n  item_not_in_order: \"x\"\n  machine_unavailable: \"x\"\n  extras_blocked_category: \"x\"\n  extras_no_base_item: \"x\"\n  item_out_of_mode: \"x\"\n");
        File.WriteAllText(Path.Combine(prompts, "hints.yaml"), "upsell_hints: {}\n");
        File.WriteAllText(Path.Combine(prompts, "tool_schemas.yaml"),
            "tools:\n  - type: function\n    name: search\n    parameters:\n      type: object\n      additionalProperties: false\n      properties:\n        q:\n          type: string\n          default: 'false'\n        n:\n          type: integer\n          default: 3\n          note: \"10\"\n");

        var loader = new PromptLoader(_tempDir, "acme");
        var json = YamlJson.ToJsonNode(loader.ToolSchemas[0])!.AsObject();
        var parameters = json["parameters"]!.AsObject();

        Assert.Equal(JsonValueKind.False, parameters["additionalProperties"]!.GetValueKind());
        Assert.Equal(JsonValueKind.String, parameters["properties"]!["q"]!["default"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, parameters["properties"]!["n"]!["default"]!.GetValueKind());
        Assert.Equal(JsonValueKind.String, parameters["properties"]!["n"]!["note"]!.GetValueKind());
        Assert.Contains("Welcome to Acme.", loader.SystemPrompt);
    }

    private static IEnumerable<(string Path, JsonNode Value)> Walk(JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (child is null) continue;
                    var childPath = $"{path}.{key}";
                    yield return (childPath, child);
                    foreach (var inner in Walk(child, childPath)) yield return inner;
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is null) continue;
                    foreach (var inner in Walk(arr[i]!, $"{path}[{i}]")) yield return inner;
                }
                break;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
