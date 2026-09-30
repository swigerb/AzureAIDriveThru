namespace Backend.Sessions;

/// <summary>
/// Port of processors.py's ProcessorRegistry (issue #75, design doc section 7.4): maps a pipeline
/// name ("realtime" | "cascade") to the single <see cref="IPipelineProcessor"/> that
/// owns every session bound to it. Registration happens once at startup (Program.cs); lookup
/// happens once per `/realtime` connection, in Models/ModelDispatch.cs's `DispatchProcessor`,
/// before the WebSocket upgrade.
/// </summary>
public sealed class ProcessorRegistry
{
    private readonly Dictionary<string, IPipelineProcessor> _processors = new(StringComparer.Ordinal);

    /// <exception cref="InvalidOperationException">A processor is already registered for this
    /// pipeline -- mirrors model_catalog.py's fail-fast on ambiguous configuration, since two
    /// processors racing to own the same pipeline's sessions is a startup bug, not a runtime
    /// condition to tolerate.</exception>
    public void Register(IPipelineProcessor processor)
    {
        if (!_processors.TryAdd(processor.PipelineName, processor))
        {
            throw new InvalidOperationException(
                $"A processor is already registered for pipeline '{processor.PipelineName}'.");
        }
    }

    /// <summary>Null if no processor has been registered for <paramref name="pipelineName"/> --
    /// callers turn this into the same 404 an unknown persona/model already gets (never a silent
    /// fallback to some other pipeline).</summary>
    public IPipelineProcessor? Get(string pipelineName) => _processors.GetValueOrDefault(pipelineName);
}
