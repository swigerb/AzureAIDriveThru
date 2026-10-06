namespace Backend.Realtime;

/// <summary>
/// Non-generic factory for <see cref="Overridable{T}"/> -- the construction helpers live here
/// (with the type parameter on the method, not the type) so a generic type doesn't carry static
/// members that vary per closed instantiation (CA1000).
/// </summary>
public static class Overridable
{
    /// <summary>The caller did not override -- use the instance-level default.</summary>
    public static Overridable<T> Unset<T>() => default;

    /// <summary>The caller explicitly supplied a value (which may itself be null/default).</summary>
    public static Overridable<T> Of<T>(T? value) => new(true, value);
}

/// <summary>
/// Distinguishes "caller didn't override" (fall back to the instance-level default) from
/// "caller explicitly passed null" (meaning something specific -- e.g. "send no voice/instructions
/// field at all"). Port of app/backend/rtmt.py's `_VOICE_UNSET` / `_SYSTEM_MESSAGE_UNSET` /
/// `_REASONING_UNSET` sentinel-object pattern (plain `object()` instances in Python, distinct from
/// `None`) -- C# has no natural "distinct from null" sentinel without an explicit wrapper like
/// this one.
/// </summary>
public readonly struct Overridable<T>
{
    public bool HasValue { get; }
    public T? Value { get; }

    internal Overridable(bool hasValue, T? value)
    {
        HasValue = hasValue;
        Value = value;
    }
}
