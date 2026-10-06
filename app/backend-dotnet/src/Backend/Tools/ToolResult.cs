using System.Text.Json;

namespace Backend.Tools;

/// <summary>Where a tool result is routed -- direct C# port of rtmt.py's ToolResultDirection enum
/// (same three members, same meaning): a structured rejection is always ToServer-only (the model
/// gets corrective text, the guest-facing client display is untouched); a successful order update
/// is ToBoth (the model gets spoken confirmation text, the client also gets a fresh order-summary
/// JSON payload).</summary>
internal enum ToolResultDirection
{
    ToServer,
    ToClient,
    ToBoth,
}

/// <summary>Direct C# port of rtmt.py's ToolResult: the outcome of one tool invocation, addressed
/// to the model (<see cref="_text"/>/<see cref="ToText"/>) and optionally to the connected client
/// separately (<see cref="_clientText"/>/<see cref="ToClientText"/>). <see cref="_payload"/> carries
/// a structured (non-string) result -- e.g. a rejection object or an order summary -- exactly like
/// Python's ToolResult.text may be a dict there; <see cref="ToText"/> serializes it the same way
/// Python's <c>to_text()</c> calls <c>json.dumps()</c> on a non-str payload.</summary>
internal sealed class ToolResult
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new();

    private readonly string? _text;
    private readonly object? _payload;
    private readonly string? _clientText;

    public ToolResultDirection Destination { get; }

    public ToolResult(string text, ToolResultDirection destination, string? clientText = null)
    {
        _text = text;
        Destination = destination;
        _clientText = clientText;
    }

    /// <summary>Constructs a structured (JSON-object) result -- used by every add-time rejection
    /// (not_on_menu/size_not_available/not_in_order/machine_unavailable/extras_blocked_category/
    /// extras_no_base_item, docs/persona-architecture.md section 6) exactly like tools.py passes a
    /// plain dict as ToolResult's first argument.</summary>
    public ToolResult(object payload, ToolResultDirection destination, string? clientText = null)
    {
        _payload = payload;
        Destination = destination;
        _clientText = clientText;
    }

    /// <summary>Text addressed to the model. Mirrors ToolResult.to_text(): a structured payload is
    /// JSON-serialized; a plain string result is returned as-is.</summary>
    public string ToText() => _text ?? (_payload is null ? "" : JsonSerializer.Serialize(_payload, PayloadJsonOptions));

    /// <summary>Text addressed to the connected client's own display, falling back to
    /// <see cref="ToText"/> when no separate client payload was supplied. Mirrors
    /// ToolResult.to_client_text().</summary>
    public string ToClientText() => _clientText ?? ToText();
}
