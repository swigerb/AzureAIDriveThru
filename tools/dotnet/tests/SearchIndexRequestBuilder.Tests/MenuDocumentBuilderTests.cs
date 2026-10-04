using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Unit tests for MenuDocumentBuilder.Build against a small synthetic menuItems.json, covering
/// field defaults, doc-id sanitization, and the "sizes" field's PythonJsonDumps-encoded string
/// value -- independent of whatever real persona data happens to exist today.
/// </summary>
public sealed class MenuDocumentBuilderTests
{
    private const string MenuJson = """
        {
          "menuItems": [
            {
              "category": "Burgers & Fries!",
              "items": [
                {
                  "name": "Classic Cheeseburger",
                  "description": "A juicy burger",
                  "longDescription": "With cheese and all the fixings.",
                  "origin": "USA",
                  "caffeineContent": "0mg",
                  "brewingMethod": "n/a",
                  "popularity": "high",
                  "menuPeriod": "all-day",
                  "sizes": [{"size": "regular", "price": 5.5}]
                },
                {
                  "name": "Fries",
                  "description": "Crispy fries",
                  "sizes": [{"size": "small", "price": 2}, {"size": "large", "price": 3.5}]
                }
              ]
            }
          ]
        }
        """;

    private static List<MenuDocumentBuilder.PreparedDocument> BuildFromSyntheticMenu() =>
        MenuDocumentBuilder.Build(JsonNode.Parse(MenuJson)!);

    [Fact]
    public void Build_PreservesFileOrder_NeverSorts()
    {
        var documents = BuildFromSyntheticMenu();
        Assert.Equal(["Classic Cheeseburger", "Fries"], documents.Select(d => d.Fields["name"]!.GetValue<string>()));
    }

    [Fact]
    public void Build_SanitizesDocId_LowercasingAndReplacingSpacesAndPunctuation()
    {
        var documents = BuildFromSyntheticMenu();

        // "Burgers & Fries!" + "_" + "Classic_Cheeseburger", lowercased, then every character
        // outside [a-zA-Z0-9_-] (here: '&', '!', and the spaces already turned into '_' by the
        // name.Replace(' ', '_') step) replaced with '_' -- mirrors setup_search_index.py's
        // sanitize_key regex exactly (see MenuDocumentBuilder.cs's SanitizeKey remarks).
        Assert.Equal("burgers_&_fries!_classic_cheeseburger".Replace("&", "_").Replace("!", "_"),
            documents[0].Fields["id"]!.GetValue<string>());
    }

    [Fact]
    public void Build_DefaultsMissingOptionalFields_ToEmptyString_NotOmitted()
    {
        var documents = BuildFromSyntheticMenu();
        var fries = documents[1];

        // Fries omits longDescription/origin/caffeineContent/brewingMethod/popularity/menuPeriod
        // entirely -- setup_search_index.py's own prepare_documents (and the #165 comment
        // preserved in MenuDocumentBuilder.cs) requires every field to be PRESENT as an empty
        // string, never omitted, so a later merge/upload never 400s with a missing-required-field
        // error.
        Assert.Equal("", fries.Fields["longDescription"]!.GetValue<string>());
        Assert.Equal("", fries.Fields["origin"]!.GetValue<string>());
        Assert.Equal("", fries.Fields["caffeineContent"]!.GetValue<string>());
        Assert.Equal("", fries.Fields["brewingMethod"]!.GetValue<string>());
        Assert.Equal("", fries.Fields["popularity"]!.GetValue<string>());
        Assert.Equal("", fries.Fields["menuPeriod"]!.GetValue<string>());
        Assert.True(fries.Fields.ContainsKey("menuPeriod"));
    }

    [Fact]
    public void Build_CombinesTextForEmbedding_AsCategoryNameDescriptionLongDescription()
    {
        var documents = BuildFromSyntheticMenu();
        Assert.Equal(
            "Burgers & Fries! Classic Cheeseburger A juicy burger With cheese and all the fixings.",
            documents[0].CombinedTextForEmbedding);

        // Fries has no longDescription -- the combined text still has a trailing space before the
        // (empty) longDescription slot, matching setup_search_index.py's f-string exactly.
        Assert.Equal("Burgers & Fries! Fries Crispy fries ", documents[1].CombinedTextForEmbedding);
    }

    [Fact]
    public void Build_SerializesSizes_ViaPythonJsonDumps_KeepingWholeNumbersWhole()
    {
        var documents = BuildFromSyntheticMenu();

        // "price": 2 (a Python int) must stay "2", not become "2.0" -- see PythonJsonDumpsTests for
        // the dedicated, Python-oracle-backed coverage of this encoder in isolation; this test only
        // confirms MenuDocumentBuilder actually routes the "sizes" field through it.
        Assert.Equal(
            """[{"size": "small", "price": 2}, {"size": "large", "price": 3.5}]""",
            documents[1].Fields["sizes"]!.GetValue<string>());
    }
}
