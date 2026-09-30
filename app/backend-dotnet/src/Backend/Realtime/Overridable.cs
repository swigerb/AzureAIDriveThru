namespace Backend.Realtime;

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

    private Overridable(bool hasValue, T? value)
    {
        HasValue = hasValue;
        Value = value;
    }

    /// <summary>The caller did not override -- use the instance-level default.</summary>
    public static Overridable<T> Unset => new(false, default);

    /// <summary>The caller explicitly supplied a value (which may itself be null/default).</summary>
    public static Overridable<T> Of(T? value) => new(true, value);
}
