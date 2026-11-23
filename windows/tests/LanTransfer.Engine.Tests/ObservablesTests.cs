using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>引擎层 IObservable 辅助类型：ObservableState（对应 StateFlow）与 Subject（对应 SharedFlow）。</summary>
public sealed class ObservablesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void State_replays_current_value_and_pushes_only_changes_until_unsubscribed()
    {
        var state = new ObservableState<int>(1);
        var seen = new List<int>();
        var sub = state.Subscribe(seen.Add);
        state.Value = 1; // 相等的值不推送
        state.Value = 2;
        Assert.Equal(3, state.Update(v => v + 1));
        sub.Dispose();
        state.Value = 4;
        Assert.Equal(new[] { 1, 2, 3 }, seen);
        Assert.Equal(4, state.Value);
    }

    [Fact]
    public void State_allows_reentrant_update_and_unsubscribe_from_callback()
    {
        var state = new ObservableState<int>(0);
        var seen = new List<int>();
        IDisposable? sub = null;
        sub = state.Subscribe(v =>
        {
            seen.Add(v);
            if (v == 1)
            {
                state.Value = 2; // 回调中再次赋值
            }

            if (v == 2)
            {
                sub?.Dispose(); // 回调中退订
            }
        });
        state.Value = 1;
        state.Value = 3;
        Assert.Equal(new[] { 0, 1, 2 }, seen);
        Assert.Equal(3, state.Value);
    }

    [Fact]
    public void Subject_does_not_replay_and_delivers_to_every_subscriber()
    {
        var subject = new Subject<Unit>();
        subject.OnNext(Unit.Value);
        var a = 0;
        var b = 0;
        using var sa = subject.Subscribe(_ => a++);
        using var sb = subject.Subscribe(_ => b++);
        subject.OnNext(Unit.Value);
        subject.OnNext(Unit.Value);
        Assert.Equal(2, a);
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task FirstAsync_sees_current_value_waits_for_predicate_and_honours_cancellation()
    {
        var state = new ObservableState<string>("a");
        Assert.Equal("a", await state.FirstAsync(Ct));
        var waiting = state.FirstAsync(v => v.Length > 2, Ct);
        state.Value = "bb";
        Assert.False(waiting.IsCompleted);
        state.Value = "ccc";
        Assert.Equal("ccc", await waiting);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var never = state.FirstAsync(v => v == "never", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => never);
    }
}
