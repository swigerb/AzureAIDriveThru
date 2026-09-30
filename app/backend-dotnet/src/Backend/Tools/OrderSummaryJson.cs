using System.Text.Json;
using Backend.Ordering;

namespace Backend.Tools;

/// <summary>Serializes an <see cref="OrderSummary"/> to JSON with the exact wire shape Python's
/// Pydantic <c>OrderSummary.model_dump_json()</c> produces (field names already pinned via
/// <c>[JsonPropertyName]</c> on <see cref="OrderSummary"/>/<see cref="OrderItem"/> -- this class
/// only centralizes the serializer options so update_order/get_order/reset_order never each
/// construct their own, potentially drifting, <see cref="JsonSerializerOptions"/>).</summary>
internal static class OrderSummaryJson
{
    private static readonly JsonSerializerOptions Options = new();

    public static string Serialize(OrderSummary summary) => JsonSerializer.Serialize(summary, Options);
}
