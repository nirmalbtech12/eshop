using Microsoft.Extensions.Logging;

namespace eShop.Application.UnitTests;

internal sealed class RecordingPaymentLogger : ILogger<eShop.PaymentProcessor.Payments.PaymentService>
{
    public List<IReadOnlyList<KeyValuePair<string, object?>>> Records { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> fields) Records.Add(fields);
    }
}

internal sealed class PaymentClock : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public void Advance(TimeSpan duration) => ticks += duration.Ticks;
}

internal sealed class ManualPaymentTime : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (gate) return ticks; }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate) { timers.Add(timer); timer.Set(dueTime); }
        return timer;
    }
    public void Advance(TimeSpan amount)
    {
        List<ManualTimer> ready;
        lock (gate)
        {
            ticks += amount.Ticks;
            ready = timers.Where(t => t.Due <= ticks).ToList();
            foreach (var timer in ready) timer.Due = long.MaxValue;
        }
        foreach (var timer in ready) timer.Fire();
    }
    private sealed class ManualTimer(ManualPaymentTime owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public void Set(TimeSpan due) => Due = due == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + due.Ticks;
        public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner.gate) Set(dueTime); return true; }
        public void Fire() => callback(state);
        public void Dispose() { lock (owner.gate) { Due = long.MaxValue; owner.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
