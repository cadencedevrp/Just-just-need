namespace StormsBackupSystem.Core;

/// <summary>
/// Works out when the next automatic backup is due.
/// Backups are anchored to a time of day (e.g. 03:00) and repeat every <c>intervalHours</c>
/// (24 by default). If the machine was off when a backup was due, the next run is in the past,
/// which makes the scheduler run it straight away (catch-up).
/// </summary>
public static class ScheduleCalculator
{
    public const int RetryAfterFailureMinutes = 60;
    public const int MaxRetries = 3;

    public static DateTime GetNextRun(
        DateTime? lastAttemptLocal,
        bool lastAttemptFailed,
        int consecutiveFailures,
        TimeSpan timeOfDay,
        int intervalHours,
        DateTime nowLocal)
    {
        var interval = TimeSpan.FromHours(Math.Clamp(intervalHours, 1, 24 * 31));
        var anchor = nowLocal.Date + timeOfDay;
        var reference = lastAttemptLocal ?? nowLocal;

        // first anchor + k * interval strictly after the reference time
        var k = FloorDiv((reference - anchor).Ticks, interval.Ticks) + 1;
        var next = anchor + TimeSpan.FromTicks(k * interval.Ticks);

        // After a failure, retry sooner (up to MaxRetries times) instead of waiting a full interval.
        if (lastAttemptLocal.HasValue && lastAttemptFailed && consecutiveFailures is > 0 and <= MaxRetries)
        {
            var retry = lastAttemptLocal.Value.AddMinutes(RetryAfterFailureMinutes);
            if (retry < next)
            {
                next = retry;
            }
        }

        return next;
    }

    private static long FloorDiv(long a, long b)
    {
        var q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0)))
        {
            q--;
        }

        return q;
    }
}
