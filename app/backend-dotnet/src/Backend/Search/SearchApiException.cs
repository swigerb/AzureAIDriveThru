namespace Backend.Search;

/// <summary>Thrown by <see cref="SearchTool"/>'s REST call helper for any non-2xx Azure AI Search
/// response -- mirrors Python's <c>azure.core.exceptions.HttpResponseError</c> (issue #23: this
/// backend talks to the Search REST API directly via <see cref="System.Net.Http.HttpClient"/>
/// rather than the <c>Azure.Search.Documents</c> SDK, so there is no SDK exception type to
/// reuse). <see cref="Exception.Message"/> is the exact error message text the response body's own
/// <c>error.message</c> field carries (or a generic fallback) -- <see cref="SearchTool"/>'s own
/// retry branches pattern-match on this text (e.g. "Could not find a property named") exactly
/// like Python's own `"Could not find a property named" in str(exc)` check.</summary>
public sealed class SearchApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
