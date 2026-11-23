using System.Threading.Channels;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// 连接驱动（设计 C4.2、并发模型），与 Kotlin 侧 Connection.kt 对应：握手完成后的读循环、单写循环（P0 → P1 → P2）、
// 心跳与无响应判定。三个循环各是一个 Task，计时全部经 IClock.Time（测试中为 FakeClock）。

/// <summary>出站优先级：P0 控制（心跳、ACK、配对、取消、结果），P1 文字。文件块走 <see cref="IChunkSource"/>（P2）。</summary>
public enum Priority
{
    Control,
    Text,
}

public abstract record ConnState
{
    private ConnState()
    {
    }

    /// <summary>握手已完成，尚未成为 Session：只接受 C1.11 白名单中的消息。</summary>
    public sealed record Pending : ConnState
    {
        public static Pending Instance { get; } = new();
    }

    public sealed record Active : ConnState
    {
        public static Active Instance { get; } = new();
    }

    public sealed record Closed(CloseReason Reason) : ConnState;
}

/// <summary>文件块来源（P2）。写循环每次最多从一个源拉取 1 条消息；没有可写的数据时返回 null。Poll 不得阻塞。</summary>
public interface IChunkSource
{
    ProtocolMessage? Poll();
}

/// <summary><see cref="Connection.AttachChunkSource"/> 的句柄。</summary>
public sealed class ChunkSourceHandle
{
    private readonly Connection _owner;

    internal ChunkSourceHandle(Connection owner, IChunkSource source)
    {
        _owner = owner;
        Source = source;
    }

    internal IChunkSource Source { get; }

    /// <summary>源有新数据可写时调用，唤醒写循环。</summary>
    public void Signal() => _owner.Wake();

    public void Detach() => _owner.Detach(this);
}

public sealed class Connection
{
    /// <summary>Pending 阶段允许的消息（C1.11 白名单，另加 HEARTBEAT 与 SESSION_CLOSE）。</summary>
    internal static readonly IReadOnlySet<MessageType> PendingAllowed = new HashSet<MessageType>
    {
        MessageType.SessionResult,
        MessageType.SessionClose,
        MessageType.Heartbeat,
        MessageType.PairRequest,
        MessageType.PairNonce,
        MessageType.PairReveal,
        MessageType.PairDecision,
        MessageType.PairCancel,
        MessageType.PairComplete,
        MessageType.QrPairRequest,
        MessageType.QrPairResult,
    };

    private const int ReadBuffer = 64 * 1024;

    private readonly ITcpConn _sock;
    private readonly ConnReader _reader;
    private readonly RecordCipher _sendCipher;
    private readonly RecordReader _receiveStream;
    private readonly IClock _clock;
    private readonly SessionTimings _timings;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<Outgoing> _p0 = Channel.CreateUnbounded<Outgoing>();
    private readonly Channel<Outgoing> _p1 = Channel.CreateUnbounded<Outgoing>();
    private readonly List<ChunkSourceHandle> _sources = [];
    private readonly Channel<bool> _wakeup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<bool> _timerKick = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Channel<ProtocolMessage> _inbox = Channel.CreateUnbounded<ProtocolMessage>();
    private readonly ObservableState<ConnState> _state = new(ConnState.Pending.Instance);
    private readonly TaskCompletionSource<CloseReason> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private int _nextSource;
    private int _closing;
    private long _lastSentMs;
    private long _lastReceivedMs;
    private int _heartbeatQueued;

    // 测试观测点（只在测试中读取）：见 IsQuiescent
    private int _writerIdle;
    private long _framesReceived;
    private long _kickGen;
    private long _armedGen = -1;
    private long _armedAt = -1;

    internal Connection(
        ITcpConn sock,
        ConnReader reader,
        Role role,
        ProtocolVersion version,
        RecordCipher sendCipher,
        RecordCipher receiveCipher,
        IClock clock,
        SessionTimings? timings = null)
    {
        _sock = sock;
        _reader = reader;
        Role = role;
        Version = version;
        _sendCipher = sendCipher;
        _receiveStream = receiveCipher.OpenStream();
        _clock = clock;
        _timings = timings ?? SessionTimings.Default;
        _lastSentMs = clock.MonotonicMs();
        _lastReceivedMs = _lastSentMs;
    }

    private sealed record Outgoing(ProtocolMessage? Message, CloseReason? CloseAfter);

    public Role Role { get; }

    public ProtocolVersion Version { get; }

    public IObservableState<ConnState> State => _state;

    /// <summary>收到的消息（HEARTBEAT 与 SESSION_CLOSE 由连接自己处理，不出现在这里）；连接关闭后通道完成。只能有一个消费者。</summary>
    public ChannelReader<ProtocolMessage> Incoming => _inbox.Reader;

    public Task<CloseReason> Closed => _closed.Task;

    public SocketAddr Local => _sock.Local;

    public SocketAddr Remote => _sock.Remote;

    internal bool IsClosedState => _state.Value is ConnState.Closed;

    /// <summary>已处理的完整帧数（测试观测点）。</summary>
    internal long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>
    /// 测试观测点：写循环空闲（队列与各源都没有可写的数据），计时循环已按最近一次写出重新挂起到将来的时刻，且已处理 <paramref name="framesReceived"/> 个帧。
    /// FakeClock 推进到下一个定时器之前用它等待连接稳定，保证记录到的时刻精确。
    /// </summary>
    internal bool IsQuiescent(long framesReceived) =>
        IsClosedState || (Volatile.Read(ref _writerIdle) == 1 && Interlocked.Read(ref _armedGen) == Interlocked.Read(ref _kickGen)
            && Interlocked.Read(ref _armedAt) > _clock.MonotonicMs() && FramesReceived == framesReceived);

    internal void Start()
    {
        var ct = _cts.Token;
        _ = Task.Run(() => ReadLoopAsync(ct), CancellationToken.None);
        _ = Task.Run(() => WriteLoopAsync(ct), CancellationToken.None);
        _ = Task.Run(() => TimerLoopAsync(ct), CancellationToken.None);
    }

    /// <summary>Pending → Active（SESSION_RESULT(ACCEPTED) 或配对完成）。</summary>
    public bool Activate()
    {
        lock (_gate)
        {
            if (_state.Value is not ConnState.Pending)
            {
                return false;
            }

            _state.Value = ConnState.Active.Instance;
            return true;
        }
    }

    /// <summary>入队一条消息；连接正在关闭或已关闭时返回 false。</summary>
    public bool Enqueue(ProtocolMessage msg, Priority priority)
    {
        if (Volatile.Read(ref _closing) == 1)
        {
            return false;
        }

        var ok = (priority == Priority.Control ? _p0 : _p1).Writer.TryWrite(new Outgoing(msg, null));
        Wake();
        return ok;
    }

    public ChunkSourceHandle AttachChunkSource(IChunkSource src)
    {
        var h = new ChunkSourceHandle(this, src);
        lock (_sources)
        {
            _sources.Add(h);
        }

        Wake();
        return h;
    }

    internal void Wake()
    {
        Volatile.Write(ref _writerIdle, 0); // 有新数据：在写循环取走之前不算空闲
        _wakeup.Writer.TryWrite(true);
    }

    private void KickTimer()
    {
        Interlocked.Increment(ref _kickGen);
        _timerKick.Writer.TryWrite(true);
    }

    internal void Detach(ChunkSourceHandle h)
    {
        lock (_sources)
        {
            var i = _sources.IndexOf(h);
            if (i < 0)
            {
                return;
            }

            _sources.RemoveAt(i);
            if (i < _nextSource)
            {
                _nextSource--;
            }

            if (_nextSource >= _sources.Count)
            {
                _nextSource = 0;
            }
        }
    }

    /// <summary>写出已入队的 P0 消息和 <paramref name="last"/> 之后以 <paramref name="reason"/> 关闭；此后不再接受新的出站消息。超过 CloseGraceMs 仍未写出时强制关闭。</summary>
    internal void SendAndClose(ProtocolMessage? last, CloseReason reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) == 1)
        {
            return;
        }

        _p0.Writer.TryWrite(new Outgoing(last, reason));
        Wake();
        var ct = _cts.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(_timings.CloseGraceMs), _clock.Time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                Terminate(reason);
            },
            CancellationToken.None);
    }

    /// <summary>主动关闭：先发送 SESSION_CLOSE(reason)，再关闭 TCP 连接（C1.11）。</summary>
    public Task<CloseReason> CloseGracefullyAsync(SessionCloseReason reason)
    {
        SendAndClose(new SessionClose(reason), new CloseReason.Local(reason));
        return Closed;
    }

    /// <summary>立即关闭，不发送任何消息。</summary>
    public void Close(CloseReason reason) => Terminate(reason);

    private void Terminate(CloseReason reason)
    {
        lock (_gate)
        {
            if (_state.Value is ConnState.Closed)
            {
                return;
            }

            _state.Value = new ConnState.Closed(reason);
        }

        Volatile.Write(ref _closing, 1);
        _sock.Close();
        _inbox.Writer.TryComplete();
        _closed.TrySetResult(reason);
        _cts.Cancel();
    }

    // ---- 读循环：socket → RecordReader → FrameDecoder → 分发 ----

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var decoder = new FrameDecoder(Version);
        var buf = new byte[ReadBuffer];
        try
        {
            while (true)
            {
                var n = await _reader.ReadSomeAsync(buf, ct).ConfigureAwait(false);
                if (n < 0)
                {
                    Terminate(new CloseReason.PeerClosed(null));
                    return;
                }

                var r = _receiveStream.Feed(buf.AsSpan(0, n));
                foreach (var c in r.Chunks)
                {
                    if (!Handle(decoder.Feed(c.Span)))
                    {
                        return;
                    }
                }

                if (r.IntegrityError)
                {
                    Terminate(new CloseReason.IntegrityFailed());
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>处理一批解码结果；返回 false 表示连接已关闭或正在关闭，读循环应停止（之后不再解析任何字节）。</summary>
    private bool Handle(DecodeBatch batch)
    {
        foreach (var m in batch.Messages)
        {
            Interlocked.Exchange(ref _lastReceivedMs, _clock.MonotonicMs());
            var keepGoing = Dispatch(m);
            Interlocked.Increment(ref _framesReceived);
            if (!keepGoing)
            {
                return false;
            }
        }

        if (batch.Error is { } e)
        {
            Terminate(new CloseReason.ProtocolError(e.ToString()));
            return false;
        }

        return true;
    }

    private bool Dispatch(ProtocolMessage m)
    {
        if (Volatile.Read(ref _closing) == 1)
        {
            return false;
        }

        switch (m)
        {
            case Heartbeat:
                return true;
            case SessionClose sc:
                Terminate(new CloseReason.PeerClosed(sc.Reason));
                return false;
        }

        if (_state.Value is ConnState.Pending)
        {
            if (!PendingAllowed.Contains(m.Type))
            {
                SendAndClose(new SessionResult(SessionResultCode.NotTrusted), new CloseReason.PendingViolation());
                return false;
            }

            // 由读循环原子地切换为 Active，使对方紧随其后的应用消息不会被当成 Pending 违规：
            // 发起方收到 ACCEPTED；接收方收到 PAIR_COMPLETE（之后的信任提交失败时由 PairingService 关闭连接）
            var activates = (Role == Role.Initiator && m is SessionResult { Result: SessionResultCode.Accepted })
                || (Role == Role.Responder && m is PairComplete);
            if (activates)
            {
                Activate();
            }
        }

        _inbox.Writer.TryWrite(m);
        return true;
    }

    // ---- 写循环：P0 → P1 → P2（各源轮流各取 1 块） ----

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var item = NextOutgoing();
                if (item is null)
                {
                    // 先公布“空闲”再复查一次，避免与 Wake() 交错时漏掉刚入队的数据
                    Volatile.Write(ref _writerIdle, 1);
                    item = NextOutgoing();
                    if (item is null)
                    {
                        await _wakeup.Reader.ReadAsync(ct).ConfigureAwait(false);
                        continue;
                    }

                    Volatile.Write(ref _writerIdle, 0);
                }

                var msg = item.Message;
                if (msg is not null && !(msg is Heartbeat && SkipHeartbeat()))
                {
                    var frame = FrameEncoder.Encode(msg, Version);
                    if (frame.Bytes is { } bytes)
                    {
                        byte[] record;
                        try
                        {
                            record = _sendCipher.Seal(bytes);
                        }
                        catch (InvalidOperationException)
                        {
                            Terminate(new CloseReason.ProtocolError("record counter exhausted"));
                            return;
                        }

                        if (!await _sock.WriteAsync(record, ct).ConfigureAwait(false))
                        {
                            Terminate(new CloseReason.PeerClosed(null));
                            return;
                        }

                        Interlocked.Exchange(ref _lastSentMs, _clock.MonotonicMs());
                        KickTimer();
                    }

                    // 载荷超限（调用方保证不会发生）时只丢弃这一条
                }

                if (msg is Heartbeat)
                {
                    // 写出或跳过都要让计时循环重新计算：排队期间它只等无响应时刻
                    Volatile.Write(ref _heartbeatQueued, 0);
                    KickTimer();
                }

                if (item.CloseAfter is { } reason)
                {
                    Terminate(reason);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>心跳出队时如果最近 HeartbeatMs 内已经发送过其他帧，就不再发送（只在空闲满 5 s 时发送 HEARTBEAT）。</summary>
    private bool SkipHeartbeat() => _clock.MonotonicMs() - Interlocked.Read(ref _lastSentMs) < _timings.HeartbeatMs;

    private Outgoing? NextOutgoing()
    {
        if (_p0.Reader.TryRead(out var a))
        {
            return a;
        }

        if (_p1.Reader.TryRead(out var b))
        {
            return b;
        }

        return PollSources() is { } m ? new Outgoing(m, null) : null;
    }

    private ProtocolMessage? PollSources()
    {
        lock (_sources)
        {
            var n = _sources.Count;
            for (var k = 0; k < n; k++)
            {
                var i = (_nextSource + k) % n;
                if (_sources[i].Source.Poll() is { } m)
                {
                    _nextSource = (i + 1) % n;
                    return m;
                }
            }

            return null;
        }
    }

    // ---- 计时：空闲 5 s 发送 HEARTBEAT；15 s 未收到任何帧以“对方无响应”关闭 ----

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // 先清掉积压的唤醒，再读代数：之后的每次写出都会留下新的唤醒，不会被这里吞掉
                while (_timerKick.Reader.TryRead(out _))
                {
                }

                var gen = Interlocked.Read(ref _kickGen);

                var now = _clock.MonotonicMs();
                var deadAt = Interlocked.Read(ref _lastReceivedMs) + _timings.UnresponsiveMs;
                if (now >= deadAt)
                {
                    Terminate(new CloseReason.PeerUnresponsive());
                    return;
                }

                var beatAt = Interlocked.Read(ref _lastSentMs) + _timings.HeartbeatMs;
                if (now >= beatAt && Volatile.Read(ref _heartbeatQueued) == 0 && Volatile.Read(ref _closing) == 0)
                {
                    Volatile.Write(ref _heartbeatQueued, 1);
                    _p0.Writer.TryWrite(new Outgoing(Heartbeat.Instance, null));
                    Wake();
                }

                var next = Volatile.Read(ref _heartbeatQueued) == 1 ? deadAt : Math.Min(beatAt, deadAt);
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, next - now)), _clock.Time, delayCts.Token);
                var kick = _timerKick.Reader.WaitToReadAsync(delayCts.Token).AsTask();
                // 还有积压的唤醒时不公布“已挂起”：马上会重新计算，否则测试可能在重新计算之前推进时钟
                if (_timerKick.Reader.Count == 0)
                {
                    Interlocked.Exchange(ref _armedGen, gen);
                    Interlocked.Exchange(ref _armedAt, next);
                }
                await Task.WhenAny(delay, kick).ConfigureAwait(false);
                Interlocked.Exchange(ref _armedAt, -1);
                await delayCts.CancelAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
