using LanTransfer.Engine.Ports;

namespace LanTransfer.Testing;

/// <summary>
/// 虚拟时钟（与 Kotlin 侧 <c>FakeClock</c> 对应）。本类同时是 <see cref="TimeProvider"/>：引擎里经 <see cref="IClock.Time"/>
/// 创建的 Task.Delay、CancellationTokenSource 超时、PeriodicTimer 都挂在这里的虚拟定时器上，只在 <see cref="AdvanceBy"/> 时按到期顺序触发。
/// 另可用 <see cref="WallSkewMs"/> 模拟系统时间被修改（单调时间不受影响）。
/// <para>与 Kotlin runTest 的差异：定时器回调在 AdvanceBy 的调用线程上同步执行，但 await 之后的续体可能在线程池上运行，
/// 测试应等待可观察的结果（如 <c>FirstAsync</c>），而不是假定 AdvanceBy 返回时续体已经执行完。</para>
/// </summary>
public sealed class FakeClock : TimeProvider, IClock
{
    /// <summary>2025-10-09 16:53:20 UTC。</summary>
    public const long DefaultWallStartMs = 1_760_028_800_000L;

    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private readonly long _wallStartMs;
    private readonly TimeZoneInfo _zone;
    private long _nowMs;
    private long _sequence;

    public FakeClock(long wallStartMs = DefaultWallStartMs, TimeSpan? utcOffset = null)
    {
        _wallStartMs = wallStartMs;
        var offset = utcOffset ?? TimeSpan.FromHours(8);
        var id = offset == TimeSpan.Zero ? "UTC" : $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}";
        _zone = TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
    }

    /// <summary>加到 WallMs 上的偏移，模拟用户修改系统时间。</summary>
    public long WallSkewMs { get; set; }

    /// <summary>尚未触发或周期性的定时器个数（测试检查是否有遗留定时器）。</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>最早到期的定时器时刻；没有定时器时为 null（测试逐个推进到下一个定时器，保证续体在正确的虚拟时刻运行）。</summary>
    public long? NextTimerDueMs
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count == 0 ? null : _timers.Min(t => t.DueAt);
            }
        }
    }

    public TimeProvider Time => this;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => _zone;

    public long MonotonicMs()
    {
        lock (_gate)
        {
            return _nowMs;
        }
    }

    public long WallMs() => _wallStartMs + MonotonicMs() + WallSkewMs;

    public DateTime LocalNow() =>
        DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeMilliseconds(WallMs()).ToOffset(_zone.BaseUtcOffset).DateTime, DateTimeKind.Unspecified);

    public override long GetTimestamp() => MonotonicMs() * TimeSpan.TicksPerMillisecond;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(WallMs());

    /// <summary>推进虚拟时间；按到期时刻（同一时刻按创建顺序）依次触发到期的定时器，周期定时器可在一次推进中触发多次。</summary>
    public void AdvanceBy(long ms)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ms);
        long target;
        lock (_gate)
        {
            target = _nowMs + ms;
        }

        while (true)
        {
            FakeTimer? due;
            lock (_gate)
            {
                due = _timers.Where(t => t.DueAt <= target).OrderBy(t => t.DueAt).ThenBy(t => t.Sequence).FirstOrDefault();
                if (due is null)
                {
                    _nowMs = target;
                    return;
                }

                _nowMs = Math.Max(_nowMs, due.DueAt);
                if (due.PeriodMs is { } period)
                {
                    due.DueAt += period;
                }
                else
                {
                    _timers.Remove(due);
                }
            }

            due.Fire();
        }
    }

    public void AdvanceBy(TimeSpan d) => AdvanceBy((long)d.TotalMilliseconds);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private static long? ToMs(TimeSpan t) => t == Timeout.InfiniteTimeSpan ? null : Math.Max(0, (long)Math.Ceiling(t.TotalMilliseconds));

    private sealed class FakeTimer(FakeClock owner, TimerCallback callback, object? state) : ITimer
    {
        public long DueAt { get; set; }

        public long? PeriodMs { get; private set; }

        public long Sequence { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var dueMs = ToMs(dueTime);
            var periodMs = ToMs(period);
            bool fireNow;
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueMs is null)
                {
                    return true;
                }

                Sequence = owner._sequence++;
                DueAt = owner._nowMs + dueMs.Value;
                PeriodMs = periodMs is > 0 ? periodMs : null;
                fireNow = dueMs.Value == 0;
                if (fireNow && PeriodMs is { } p)
                {
                    DueAt += p;
                }

                if (!fireNow || PeriodMs is not null)
                {
                    owner._timers.Add(this);
                }
            }

            // 到期时间为 0：与真实定时器一样立即触发（在调用线程上同步执行）
            if (fireNow)
            {
                Fire();
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
