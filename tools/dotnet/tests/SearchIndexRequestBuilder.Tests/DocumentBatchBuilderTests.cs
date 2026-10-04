using System.Text.Json.Nodes;

namespace SearchIndexRequestBuilder.Tests;

/// <summary>
/// Unit tests for DocumentBatchBuilder's 100-document batching boundary and
/// "@search.action": "mergeOrUpload" tagging, against synthetic documents -- independent of
/// whatever real persona document counts happen to exist today (today's real counts, 60/71/180,
/// happen to already exercise a 100-boundary split for one persona, but that's incidental; these
/// tests pin the boundary behaviour explicitly and directly, including an exact multiple of 100
/// and one document under/over the boundary).
///
/// Mutation check performed while implementing this test (reverted immediately after confirming):
/// changing BatchSize from 100 to 99 in DocumentBatchBuilder.cs made
/// BuildBatches_SplitsExactlyAtTheHundredDocumentBoundary below FAIL (3 batches of
/// 99/99/2 instead of the expected 100/100). Also confirmed PythonParityTests'
/// real-data assertion on the largest real persona's two real batches (100 then 80) would likewise
/// fail with that mutation -- restored before committing.
/// </summary>
public sealed class DocumentBatchBuilderTests
{
    private static JsonObject MakeDocument(int index) => new() { ["id"] = $"doc-{index}" };

    [Fact]
    public void BuildBatches_TagsEveryDocument_WithMergeOrUploadAction()
    {
        var batches = DocumentBatchBuilder.BuildBatches([MakeDocument(1)]);
        var batch = Assert.Single(batches);
        var value = batch["value"]!.AsArray();
        var document = Assert.Single(value)!.AsObject();

        Assert.Equal("mergeOrUpload", document["@search.action"]!.GetValue<string>());
        Assert.Equal("doc-1", document["id"]!.GetValue<string>());
    }

    [Fact]
    public void BuildBatches_PreservesDocumentOrder_WithinABatch()
    {
        var documents = Enumerable.Range(0, 5).Select(MakeDocument).ToList();
        var batches = DocumentBatchBuilder.BuildBatches(documents);
        var batch = Assert.Single(batches);
        var ids = batch["value"]!.AsArray().Select(d => d!["id"]!.GetValue<string>());

        Assert.Equal(["doc-0", "doc-1", "doc-2", "doc-3", "doc-4"], ids);
    }

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(100, new[] { 100 })]
    [InlineData(101, new[] { 100, 1 })]
    [InlineData(180, new[] { 100, 80 })]
    [InlineData(200, new[] { 100, 100 })]
    public void BuildBatches_SplitsExactlyAtTheHundredDocumentBoundary(int documentCount, int[] expectedBatchSizes)
    {
        var documents = Enumerable.Range(0, documentCount).Select(MakeDocument).ToList();
        var batches = DocumentBatchBuilder.BuildBatches(documents);

        Assert.Equal(expectedBatchSizes, batches.Select(b => b["value"]!.AsArray().Count));
    }

    [Fact]
    public void BuildBatches_ReturnsEmptyList_ForNoDocuments()
    {
        var batches = DocumentBatchBuilder.BuildBatches([]);
        Assert.Empty(batches);
    }

    [Fact]
    public void BuildBatches_DoesNotMutateTheInputDocuments()
    {
        var original = MakeDocument(1);
        DocumentBatchBuilder.BuildBatches([original]);

        Assert.False(original.ContainsKey("@search.action"));
    }
}
