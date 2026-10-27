using System.Text.Json;
using Backend.Http;

namespace Backend.Tests.Http;

/// <summary>
/// Keeps the shipped persona packs and the Content-Security-Policy in step. A pack can point the SPA
/// at an external host (a font stylesheet, an image CDN) and the browser enforces whatever
/// <see cref="SecurityHeaders"/> allows, so a pack that names an origin the policy omits fails only in
/// the page: a console violation, a fallback font, a missing image. This turns that into a test
/// failure that says what to decide.
/// </summary>
public sealed class PersonaPackContentSecurityPolicyTests
{
    // Origins that persona data carries but the SPA never loads. Both entries are menu-item image
    // URLs: the menu panel renders names, sizes, prices and calories, never the image. A UI change
    // that starts to load one has to add the origin to img-src in SecurityHeaders and then delete the
    // entry here; Data_only_origins_are_not_allowed_by_the_policy fails until it does.
    private static readonly (string Key, string Origin)[] s_dataOnly =
    [
        ("image", "https://imagedelivery.net"),
        ("imageUrl", "https://imagedelivery.net"),
    ];

    private static List<ExternalReference> ShippedReferences(out List<string> files)
    {
        string personasDir = Path.Combine(RepoRootLocator.Find(), "personas");
        files = [];
        List<ExternalReference> references = [];
        foreach (string file in Directory.EnumerateFiles(personasDir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(personasDir, file).Replace('\\', '/');
            foreach (ExternalReference reference in FindExternalReferences(File.ReadAllText(file)))
            {
                files.Add(relative);
                references.Add(reference);
            }
        }

        return references;
    }

    private static List<ExternalReference> FindExternalReferences(string json)
    {
        List<ExternalReference> found = [];
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, string.Empty, found);
        return found;
    }

    private static void Walk(JsonElement element, string key, List<ExternalReference> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Walk(property.Value, property.Name, found);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, key, found);
                }

                break;

            // $schema and $id name a JSON Schema document; nothing ever fetches them.
            case JsonValueKind.String when key is not ("$schema" or "$id"):
                string? value = element.GetString();
                if (value is not null &&
                    value.Contains("://", StringComparison.Ordinal) &&
                    Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                {
                    found.Add(new ExternalReference(key, uri.GetLeftPart(UriPartial.Authority), uri.Scheme));
                }

                break;
        }
    }

    private static string[] DirectiveSources(string policy, string directive)
    {
        string entry = policy.Split("; ").Single(d => d.StartsWith(directive + " ", StringComparison.Ordinal));
        return entry[(directive.Length + 1)..].Split(' ');
    }

    private static string? Judge(ExternalReference reference, string[] styleSources)
    {
        if (reference.Scheme != Uri.UriSchemeHttps)
        {
            return $"uses {reference.Scheme}://, persona data may only reference https URLs";
        }

        if (reference.Key == "importUrl")
        {
            return styleSources.Contains(reference.Origin) ? null : "a font stylesheet origin that style-src does not allow";
        }

        return s_dataOnly.Contains((reference.Key, reference.Origin)) ? null : "an external reference the policy does not know";
    }

    // ── The shipped packs ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_external_url_in_the_shipped_persona_packs_is_allowed_by_the_policy_or_declared_data_only()
    {
        string[] styleSources = DirectiveSources(SecurityHeaders.FallbackContentSecurityPolicy, "style-src");
        List<ExternalReference> references = ShippedReferences(out List<string> files);

        var problems = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < references.Count; i++)
        {
            string? problem = Judge(references[i], styleSources);
            if (problem is not null)
            {
                problems.Add($"{files[i]}: \"{references[i].Key}\" -> {references[i].Origin}: {problem}");
            }
        }

        if (problems.Count > 0)
        {
            Assert.Fail(
                "Persona data references origins the Content-Security-Policy does not cover. For each, either " +
                "the SPA loads it (a font stylesheet, an image it renders), so add the origin to SecurityHeaders " +
                "and to ExpectedFallbackPolicy in SecurityHeadersTests; or the SPA never loads it, so declare it " +
                "in s_dataOnly with the reason." + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }

    [Fact]
    public void The_scan_sees_the_font_stylesheets_the_policy_exists_to_allow()
    {
        List<ExternalReference> references = ShippedReferences(out _);

        Assert.NotEmpty(references);
        Assert.Contains(references, r => r is { Key: "importUrl", Origin: "https://fonts.googleapis.com" });
    }

    [Fact]
    public void Data_only_origins_are_not_allowed_by_the_policy()
    {
        foreach ((string key, string origin) in s_dataOnly)
        {
            Assert.False(
                SecurityHeaders.FallbackContentSecurityPolicy.Contains(origin, StringComparison.Ordinal),
                $"{origin} is declared data-only for \"{key}\" but the policy now allows it; remove the declaration.");
        }
    }

    // ── The scanner itself ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scanner_finds_urls_in_nested_objects_and_arrays_and_reports_key_origin_and_scheme()
    {
        const string Json = """
            {
              "menuItems": [ { "items": [ { "image": "https://cdn.example.com/a/b.png?x=1" } ] } ],
              "theme": { "importUrl": "https://fonts.example.com:8443/css?family=X:wght@400" }
            }
            """;

        Assert.Equal(
            [
                new ExternalReference("image", "https://cdn.example.com", "https"),
                new ExternalReference("importUrl", "https://fonts.example.com:8443", "https"),
            ],
            FindExternalReferences(Json));
    }

    [Fact]
    public void Scanner_ignores_schema_identifiers_free_text_property_names_and_relative_paths()
    {
        const string Json = """
            {
              "$schema": "http://json-schema.org/draft-07/schema#",
              "$id": "https://github.com/org/repo/schema.json",
              "description": "See https://docs.example.com for details",
              "https://named.example.com": "plain value",
              "logo": "/personas/acme/assets/logo.svg"
            }
            """;

        Assert.Empty(FindExternalReferences(Json));
    }

    [Fact]
    public void Scanner_reports_the_scheme_so_plain_http_and_websocket_urls_fail_the_guard()
    {
        const string Json = """{ "a": "http://insecure.example.com/x", "b": "wss://socket.example.com/y" }""";

        string[] styleSources = DirectiveSources(SecurityHeaders.FallbackContentSecurityPolicy, "style-src");
        List<ExternalReference> found = FindExternalReferences(Json);

        Assert.Equal(["http", "wss"], found.Select(r => r.Scheme));
        Assert.All(found, r => Assert.NotNull(Judge(r, styleSources)));
    }

    [Fact]
    public void An_unknown_font_origin_and_an_unknown_image_origin_are_both_judged_a_problem()
    {
        string[] styleSources = DirectiveSources(SecurityHeaders.FallbackContentSecurityPolicy, "style-src");

        Assert.Null(Judge(new ExternalReference("importUrl", "https://fonts.googleapis.com", "https"), styleSources));
        Assert.NotNull(Judge(new ExternalReference("importUrl", "https://fonts.bunny.net", "https"), styleSources));
        Assert.Null(Judge(new ExternalReference("image", "https://imagedelivery.net", "https"), styleSources));
        Assert.NotNull(Judge(new ExternalReference("image", "https://cdn.example.com", "https"), styleSources));
        Assert.NotNull(Judge(new ExternalReference("logoUrl", "https://imagedelivery.net", "https"), styleSources));
    }

    private readonly record struct ExternalReference(string Key, string Origin, string Scheme);
}
