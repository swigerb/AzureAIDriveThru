using System.Text;
using SearchIndexRequestBuilder;

// Issue #16: implements the "client-seam + recorded-fixture" design note in
// docs/dotnet_tooling.md for app/backend/setup_search_index.py. With no arguments, discovers every
// enabled persona pack (personas/*/persona.json, via EnabledPersonaDiscovery) and prints each one's
// request-building plan: the exact Azure AI Search index-definition body and document-upload-batch
// count that setup_search_index.py would send for that persona -- but this tool NEVER opens a
// socket, under any flag. See tools/dotnet/tests/SearchIndexRequestBuilder.Tests for how this is
// proven to match the real Python script's own on-wire request bodies.
Console.OutputEncoding = Encoding.UTF8;

return CliRunner.Run(args, Console.Out, Console.Error);
