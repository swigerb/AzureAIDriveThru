namespace SearchIndexIngestor;

/// <summary>
/// Raised when an index's final document count still differs from its ingest plan after every
/// verification retry -- this port's equivalent of setup_search_index.py's
/// <c>verify_document_count</c> <c>RuntimeError</c>. Deliberately derives directly from
/// <see cref="Exception"/>, not <see cref="InvalidOperationException"/>, so <see cref="CliRunner"/>'s
/// clean configuration-error catch does not swallow it (see
/// <see cref="SearchIndexOrchestrator"/>'s remarks).
/// </summary>
public sealed class DocumentCountMismatchException : Exception
{
    public DocumentCountMismatchException()
    {
    }

    public DocumentCountMismatchException(string message)
        : base(message)
    {
    }

    public DocumentCountMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
