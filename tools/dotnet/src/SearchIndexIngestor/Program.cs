using System.Text;
using SearchIndexIngestor;

// Issue #16: the REST of app/backend/setup_search_index.py -- the run()/main() orchestration that
// Batch 2 (#250/#258, SearchIndexRequestBuilder) deliberately stopped short of. With no arguments,
// targets every enabled persona pack and creates/updates its Azure AI Search index, generates
// embeddings, uploads its documents, deletes anything stale, and verifies the final document count
// -- a genuine, live Azure run, unlike SearchIndexRequestBuilder. See docs/dotnet_tooling.md's
// "This PR's port: orchestration" section for the full design and its known, accepted divergences
// from the Python twin (stdout formatting, error-message wording).
Console.OutputEncoding = Encoding.UTF8;

return await CliRunner.RunAsync(args, Console.Out, Console.Error);
