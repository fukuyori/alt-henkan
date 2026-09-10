namespace AltHenkan;

internal sealed record AppSettings
{
    internal const int DefaultLongPressMilliseconds = 600;
    internal const int MinimumLongPressMilliseconds = 200;
    internal const int MaximumLongPressMilliseconds = 3000;

    public bool Enabled { get; init; } = true;

    public bool LeftAltLongPressEnabled { get; init; } = true;

    public bool RightAltLongPressEnabled { get; init; } = true;

    public int LongPressMilliseconds { get; init; } = DefaultLongPressMilliseconds;

    public AppSettings Normalize()
    {
        return this with
        {
            LongPressMilliseconds = Math.Clamp(
                LongPressMilliseconds,
                MinimumLongPressMilliseconds,
                MaximumLongPressMilliseconds)
        };
    }
}

