namespace AltHenkan;

internal static class HookMaintenancePolicy
{
    public const int RefreshIntervalMilliseconds = 30_000;
    public const int MinimumIdleMilliseconds = 1_000;

    public static bool ShouldRefresh(
        long sinceLastRefreshMilliseconds,
        uint idleMilliseconds,
        bool anyKeyDown,
        bool altGestureActive,
        bool refreshRequested)
    {
        return !anyKeyDown && !altGestureActive &&
               idleMilliseconds >= MinimumIdleMilliseconds &&
               (refreshRequested || sinceLastRefreshMilliseconds >= RefreshIntervalMilliseconds);
    }

    public static uint CalculateIdleMilliseconds(uint now, uint lastInput)
    {
        var elapsed = unchecked(now - lastInput);
        // Handle tick-count wrap, but conservatively reject a future timestamp.
        return elapsed <= int.MaxValue ? elapsed : 0;
    }
}
