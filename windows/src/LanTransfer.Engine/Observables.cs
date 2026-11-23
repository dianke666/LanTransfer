namespace LanTransfer.Engine;

// 引擎层对外暴露状态的方式（任务 14.2）：IObservable<T>。不依赖 System.Reactive，只提供引擎需要的最小实现：
// ObservableState<T> 对应 Kotlin 的 MutableStateFlow（订阅时先收到当前值，之后只推送变化），
// Subject<T> 对应 MutableSharedFlow（只推送订阅之后的事件）。

/// <summary>无信息的事件值（对应 Kotlin 的 <c>Unit</c>）。</summary>
public readonly record struct Unit
{
    public static Unit Value => default;

    public override string ToString() => "()";
}

/// <summary>只读状态（对应 Kotlin 的 <c>StateFlow</c>）：订阅时立即收到当前值，之后每次变化收到新值。</summary>
public interface IObservableState<out T> : IObservable<T>
{
    T Value { get; }
}

/// <summary>
/// 可变状态。赋相等的值（<see cref="EqualityComparer{T}.Default"/>）不推送；
/// 推送在锁内按顺序进行，订阅者在回调中再次赋值是安全的（同一线程可重入），但不得在回调中阻塞等待其他线程赋值。
/// </summary>
public sealed class ObservableState<T>(T initial) : IObservableState<T>
{
    private readonly Lock _gate = new();
    private readonly ObserverList<T> _observers = new();
    private T _value = initial;

    public T Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }

        set
        {
            lock (_gate)
            {
                if (EqualityComparer<T>.Default.Equals(_value, value))
                {
                    return;
                }

                _value = value;
                _observers.OnNext(value);
            }
        }
    }

    /// <summary>原子地基于当前值计算新值；返回新值。</summary>
    public T Update(Func<T, T> transform)
    {
        lock (_gate)
        {
            var next = transform(_value);
            Value = next;
            return next;
        }
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            observer.OnNext(_value);
            return _observers.Add(observer);
        }
    }
}

/// <summary>事件流（对应 Kotlin 的 <c>MutableSharedFlow</c>，无重放）：订阅者只收到订阅之后发出的事件。</summary>
public sealed class Subject<T> : IObservable<T>
{
    private readonly Lock _gate = new();
    private readonly ObserverList<T> _observers = new();

    public void OnNext(T value)
    {
        lock (_gate)
        {
            _observers.OnNext(value);
        }
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            return _observers.Add(observer);
        }
    }
}

/// <summary>订阅者列表（写时复制，推送时遍历快照；退订可以在任意线程、包括推送回调中进行）。</summary>
internal sealed class ObserverList<T>
{
    private readonly Lock _gate = new();
    private volatile IObserver<T>[] _items = [];

    public IDisposable Add(IObserver<T> observer)
    {
        lock (_gate)
        {
            _items = [.. _items, observer];
        }

        return new Subscription(this, observer);
    }

    public void OnNext(T value)
    {
        foreach (var o in _items)
        {
            o.OnNext(value);
        }
    }

    private void Remove(IObserver<T> observer)
    {
        lock (_gate)
        {
            _items = [.. _items.Where(o => !ReferenceEquals(o, observer))];
        }
    }

    private sealed class Subscription(ObserverList<T> owner, IObserver<T> observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(observer);
            }
        }
    }
}

/// <summary>IObservable 的小工具。</summary>
public static class ObservableExtensions
{
    /// <summary>用委托订阅。</summary>
    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext) =>
        source.Subscribe(new ActionObserver<T>(onNext));

    /// <summary>等待第一个满足 <paramref name="predicate"/> 的值（对 <see cref="IObservableState{T}"/> 包括当前值）。</summary>
    public static async Task<T> FirstAsync<T>(this IObservable<T> source, Func<T, bool> predicate, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = source.Subscribe(v =>
        {
            if (predicate(v))
            {
                tcs.TrySetResult(v);
            }
        });
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task.ConfigureAwait(false);
    }

    public static Task<T> FirstAsync<T>(this IObservable<T> source, CancellationToken ct = default) => source.FirstAsync(_ => true, ct);

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => onNext(value);
    }
}
