namespace Sattleme.Bitcoin.Tests.Support;

/// <summary>
/// The clock of a test: the date is the test's (it moves only when the test moves it); elapsed
/// times are the machine's, plus what the test moved; the timers are the machine's (the lock's
/// short waits).
/// </summary>
public sealed class ManualTime : TimeProvider
{
    private readonly object _lock = new();
    private DateTimeOffset _now;
    private long _moved;

    /// <summary>A clock at <paramref name="now"/>.</summary>
    public ManualTime(DateTimeOffset now)
    {
        _now = now;
    }

    /// <summary>A clock at 2026-10-24 12:00 UTC.</summary>
    public ManualTime()
        : this(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero))
    {
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    /// <inheritdoc/>
    public override long GetTimestamp()
    {
        return base.GetTimestamp() + Interlocked.Read(ref _moved);
    }

    /// <summary>The date, in Unix seconds.</summary>
    public long UnixNow => GetUtcNow().ToUnixTimeSeconds();

    /// <summary>The date, as Smartstore stores dates.</summary>
    public DateTime UtcNow => GetUtcNow().UtcDateTime;

    /// <summary>Moves the date by <paramref name="by"/>.</summary>
    public void Advance(TimeSpan by)
    {
        lock (_lock)
        {
            _now += by;
        }

        Interlocked.Add(ref _moved, (long)(by.TotalSeconds * TimestampFrequency));
    }
}
