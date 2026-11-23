using System.Threading.Channels;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

/// <summary>已注册的 Session：与一个对端设备之间唯一的已认证连接（C1.11）。</summary>
public sealed class Session
{
    private readonly TaskCompletionSource _unregistered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Session(PeerIdentity peer, DeviceId initiator, Connection connection, LocalInterface? via, bool initiatedLocally)
    {
        Peer = peer;
        Initiator = initiator;
        Connection = connection;
        Via = via;
        InitiatedLocally = initiatedLocally;
    }

    public PeerIdentity Peer { get; }

    /// <summary>发起方设备 ID（同时互连去重用）。</summary>
    public DeviceId Initiator { get; }

    /// <summary>连接所在的本地接口；找不到时为 null。</summary>
    public LocalInterface? Via { get; }

    public bool InitiatedLocally { get; }

    internal Connection Connection { get; }

    internal Task Unregistered => _unregistered.Task;

    public SessionInfo Info => new(Peer, Connection.Remote, InitiatedLocally, Connection.Version);

    /// <summary>应用消息（单一消费者，通常是 EngineHost 的分发器）；Session 关闭后通道完成。</summary>
    public ChannelReader<ProtocolMessage> Incoming => Connection.Incoming;

    public Task<CloseReason> Closed => Connection.Closed;

    public bool IsOpen => !Connection.IsClosedState;

    public ProtocolVersion Version => Connection.Version;

    public bool Send(ProtocolMessage msg, Priority priority = Priority.Control) => Connection.Enqueue(msg, priority);

    public ChunkSourceHandle AttachChunkSource(IChunkSource src) => Connection.AttachChunkSource(src);

    public override string ToString() => $"Session({Peer.DeviceId}, initiator={Initiator})";

    internal void MarkUnregistered() => _unregistered.TrySetResult();
}

/// <summary>握手完成、intent 为 PAIR / QR_PAIR 的连接（Pending），由 PairingService 驱动。</summary>
public sealed class PendingConnection
{
    private readonly SessionManager _manager;

    internal PendingConnection(PeerIdentity peer, Intent intent, Role role, Connection connection, LocalInterface? via, SessionManager manager)
    {
        Peer = peer;
        Intent = intent;
        Role = role;
        Connection = connection;
        Via = via;
        _manager = manager;
    }

    public PeerIdentity Peer { get; }

    public Intent Intent { get; }

    public Role Role { get; }

    public LocalInterface? Via { get; }

    internal Connection Connection { get; }

    /// <summary>收到的配对消息（只有一个消费者）。</summary>
    public ChannelReader<ProtocolMessage> Incoming => Connection.Incoming;

    public Task<CloseReason> Closed => Connection.Closed;

    public SocketAddr Remote => Connection.Remote;

    public bool Send(ProtocolMessage msg) => Connection.Enqueue(msg, Priority.Control);

    /// <summary>写出 <paramref name="last"/>（可为 null）后关闭。</summary>
    public void Close(ProtocolMessage? last = null) => Connection.SendAndClose(last, new CloseReason.Local(SessionCloseReason.Normal));

    /// <summary>配对完成（对方此时已是 Trusted_Device）：转为 Session 并注册；连接已关闭时返回 null。</summary>
    public Session? Promote() => _manager.Promote(this);
}

/// <summary>
/// 会话管理（设计 C4.1、C1.11），与 Kotlin 侧 <c>SessionManager</c> 同名镜像：监听与入站握手、出站 open（Device / Address / Qr 目标、
/// 10 s 共用预算、扫码每地址 5 s）、同一对端的 open 合并、同时互连去重、SESSION_RESULT 处理，以及接口消失检测。
/// 同时互连的判定规则与 Kotlin 侧的类注释相同（见 progress.md“任务 15 记录”）。
/// </summary>
public sealed class SessionManager : IDisposable
{
    /// <summary>监听端口：42580 → 42590–42599 → 系统分配（5.7、5.9）。</summary>
    public static readonly IReadOnlyList<int> ListenPorts = [ProtocolConstants.DefaultPort, .. Enumerable.Range(42590, 10), 0];

    private readonly ITransport _transport;
    private readonly INetworkMonitor _network;
    private readonly DeviceIdentity _identity;
    private readonly DeviceType _deviceType;
    private readonly ITrustStore _trust;
    private readonly IClock _clock;
    private readonly IEphemeralKeySource _keys;
    private readonly Func<string> _displayName;
    private readonly Func<DeviceId, SocketAddr?> _addressBook;
    private readonly ProtocolVersion _version;
    private readonly SessionTimings _timings;
    private readonly LocalNetworkGuard _guard;
    private readonly Lock _gate = new();
    private readonly Dictionary<DeviceId, Session> _registered = new();
    private readonly Dictionary<DeviceId, Attempt> _outgoing = new();
    private readonly HashSet<Live> _live = [];
    private readonly ObservableState<ValueMap<DeviceId, SessionInfo>> _sessions = new(ValueMap<DeviceId, SessionInfo>.Empty);
    private readonly Subject<SessionEvent> _events = new();
    private readonly Subject<InboundFailure> _failures = new();
    private readonly Channel<PendingConnection> _pending = Channel.CreateUnbounded<PendingConnection>();
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable _interfaceWatch;
    private ITcpListener? _listener;

    public SessionManager(
        ITransport transport,
        INetworkMonitor network,
        DeviceIdentity identity,
        DeviceType deviceType,
        ITrustStore trust,
        IClock clock,
        IEphemeralKeySource? keys = null,
        Func<string>? displayName = null,
        Func<DeviceId, SocketAddr?>? addressBook = null,
        ProtocolVersion? version = null,
        SessionTimings? timings = null)
    {
        _transport = transport;
        _network = network;
        _identity = identity;
        _deviceType = deviceType;
        _trust = trust;
        _clock = clock;
        _keys = keys ?? EphemeralKeySource.Secure;
        _displayName = displayName ?? (() => identity.DisplayName);
        _addressBook = addressBook ?? (_ => null);
        _version = version ?? ProtocolVersion.Current;
        _timings = timings ?? SessionTimings.Default;
        _guard = new LocalNetworkGuard(transport, network);
        _interfaceWatch = network.Interfaces.Subscribe(OnInterfaces);
    }

    /// <summary>一次出站尝试（同一对端的并发 open 共用）。</summary>
    private sealed class Attempt
    {
        public TaskCompletionSource<OpenResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Authenticated;

        public volatile bool Aborted;

        public volatile ITcpConn? Sock;

        public volatile Connection? Connection;
    }

    /// <summary>一条存活的连接及其本地地址（接口消失检测用）。</summary>
    private sealed record Live(Connection Connection, Ipv4 LocalIp, Task Removed);

    public IObservableState<ValueMap<DeviceId, SessionInfo>> Sessions => _sessions;

    public IObservable<SessionEvent> Events => _events;

    public IObservable<InboundFailure> InboundFailures => _failures;

    /// <summary>入站的配对连接（intent = PAIR / QR_PAIR），交给 PairingService。</summary>
    public ChannelReader<PendingConnection> PendingInbound => _pending.Reader;

    private LocalIdentity Local => new(_identity.DeviceId, _identity.KeyPair, _displayName(), _deviceType);

    private DeviceId SelfId => _identity.DeviceId;

    public Session? GetSession(DeviceId peer)
    {
        lock (_gate)
        {
            return _registered.GetValueOrDefault(peer);
        }
    }

    public void Dispose()
    {
        _interfaceWatch.Dispose();
        _cts.Cancel();
    }

    private void OnInterfaces(ValueList<LocalInterface> ifaces)
    {
        var addresses = ifaces.Select(i => i.Address).ToHashSet();
        List<Live> lost;
        lock (_gate)
        {
            lost = [.. _live.Where(l => !addresses.Contains(l.LocalIp))];
        }

        foreach (var l in lost)
        {
            l.Connection.Close(new CloseReason.LocalNetworkLost());
        }
    }

    // ---- 监听 ----

    /// <summary>依次尝试 42580、42590–42599，最后由系统分配（5.7、5.9）。</summary>
    public ListenResult StartListening()
    {
        if (_transport.Listen(ListenPorts) is not { } l)
        {
            return new ListenResult.Failed();
        }

        _listener = l;
        var ct = _cts.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (await l.AcceptAsync(ct).ConfigureAwait(false) is { } c)
                    {
                        _ = Task.Run(() => HandleInboundAsync(c, ct), CancellationToken.None);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);
        return new ListenResult.Ok(l.Port);
    }

    /// <summary>停止监听并主动关闭全部 Session 与 Pending 连接（21.4、21.9）。</summary>
    public async Task ShutdownAsync(SessionCloseReason reason)
    {
        _listener?.Close();
        _listener = null;
        List<Live> all;
        List<Session> sessions;
        lock (_gate)
        {
            all = [.. _live];
            sessions = [.. _registered.Values];
        }

        foreach (var l in all)
        {
            l.Connection.SendAndClose(new SessionClose(reason), new CloseReason.Local(reason));
        }

        foreach (var s in sessions)
        {
            s.Connection.SendAndClose(new SessionClose(reason), new CloseReason.Local(reason));
        }

        await Task.WhenAll([.. all.Select(l => l.Removed), .. sessions.Select(s => s.Unregistered)]).ConfigureAwait(false);
    }

    /// <summary>主动关闭与 <paramref name="peer"/> 的 Session（先发 SESSION_CLOSE）。</summary>
    public async Task CloseAsync(DeviceId peer, SessionCloseReason reason)
    {
        if (GetSession(peer) is { } s)
        {
            await s.Connection.CloseGracefullyAsync(reason).ConfigureAwait(false);
            await s.Unregistered.ConfigureAwait(false);
        }
    }

    private async Task HandleInboundAsync(ITcpConn conn, CancellationToken ct)
    {
        try
        {
            if (_guard.AcceptInbound(conn) is not { } route)
            {
                conn.Close(); // 来源不在本地子网内：静默关闭（2.9）
                return;
            }

            var deadline = _clock.MonotonicMs() + _timings.InboundHandshakeMs;
            var h = await Handshake.RespondAsync(conn, Local, _keys, _clock, deadline, _version, ct).ConfigureAwait(false);
            if (h is not HandshakeOutcome.Done done)
            {
                conn.Close();
                if (h is HandshakeOutcome.Failed { Failure: OpenFailure.VersionIncompatible or OpenFailure.HandshakeAuthFailed } f)
                {
                    _failures.OnNext(new InboundFailure(conn.Remote, null, f.Failure));
                }

                return;
            }

            var peer = done.Peer;
            if (peer.DeviceId == SelfId)
            {
                conn.Close();
                return;
            }

            var c = NewConnection(conn, done, Role.Responder);
            if (done.Intent == Intent.Session)
            {
                AcceptSession(c, peer, route.Via);
            }
            else
            {
                _pending.Writer.TryWrite(new PendingConnection(peer, done.Intent, Role.Responder, c, route.Via, this));
            }
        }
        catch (OperationCanceledException)
        {
            conn.Close();
        }
    }

    /// <summary>接收方处理 intent = SESSION（C1.11）。</summary>
    private void AcceptSession(Connection c, PeerIdentity peer, LocalInterface? via)
    {
        var trusted = _trust.Get(peer.DeviceId);
        if (trusted is null)
        {
            c.SendAndClose(new SessionResult(SessionResultCode.NotTrusted), new CloseReason.Local(SessionCloseReason.Normal));
            return;
        }

        if (trusted.PublicKey != peer.PublicKey)
        {
            c.SendAndClose(new SessionResult(SessionResultCode.KeyMismatch), new CloseReason.Local(SessionCloseReason.Normal));
            _failures.OnNext(new InboundFailure(c.Remote, peer.DeviceId, new OpenFailure.KeyMismatch(trusted.DisplayName)));
            return;
        }

        Session? superseded = null;
        Attempt? abandoned = null;
        Session? duplicateOf = null;
        Session? accepted = null;
        lock (_gate)
        {
            var p = peer.DeviceId;
            var peerSmaller = p.Value.CompareTo(SelfId.Value) < 0;
            var s = _registered.GetValueOrDefault(p);
            var o = _outgoing.GetValueOrDefault(p);
            var accept = s is not null ? s.Initiator == p || peerSmaller
                : o is { Authenticated: true } ? peerSmaller
                : true;
            if (!accept)
            {
                duplicateOf = s;
            }
            else
            {
                if (s is not null)
                {
                    superseded = s;
                    _registered.Remove(p);
                }

                if (o is not null)
                {
                    o.Aborted = true;
                    _outgoing.Remove(p);
                    abandoned = o;
                }

                c.Enqueue(new SessionResult(SessionResultCode.Accepted), Priority.Control);
                c.Activate();
                accepted = Register(c, peer, initiator: p, via);
            }
        }

        if (accepted is null)
        {
            c.SendAndClose(new SessionResult(SessionResultCode.Duplicate), new CloseReason.Local(SessionCloseReason.Duplicate));
            duplicateOf?.Send(Heartbeat.Instance);
            return;
        }

        superseded?.Connection.Close(new CloseReason.Superseded());
        if (abandoned is not null)
        {
            abandoned.Result.TrySetResult(new OpenResult.Ok(accepted));
            Abandon(abandoned);
        }

        AfterRegister(accepted);
    }

    /// <summary>放弃一次出站尝试：已完成握手的发 SESSION_CLOSE(DUPLICATE)，否则直接关闭套接字。</summary>
    private static void Abandon(Attempt a)
    {
        if (a.Connection is { } c)
        {
            c.SendAndClose(new SessionClose(SessionCloseReason.Duplicate), new CloseReason.Local(SessionCloseReason.Duplicate));
        }
        else
        {
            a.Sock?.Close();
        }
    }

    /// <summary>在锁内调用。</summary>
    private Session Register(Connection c, PeerIdentity peer, DeviceId initiator, LocalInterface? via)
    {
        var s = new Session(peer, initiator, c, via, initiatedLocally: initiator == SelfId);
        _registered[peer.DeviceId] = s;
        PublishLocked();
        return s;
    }

    private void PublishLocked() => _sessions.Value = new ValueMap<DeviceId, SessionInfo>(_registered.Select(kv => KeyValuePair.Create(kv.Key, kv.Value.Info)));

    /// <summary>注册之后：发出事件、更新可信设备名称（9.6）与最近成功地址（C4.1）、存在 Session 时加快接口轮询（18.8）、关闭时注销。</summary>
    private void AfterRegister(Session s)
    {
        _events.OnNext(new SessionEvent.Opened(s));
        if (_trust.Get(s.Peer.DeviceId) is { } t && t.DisplayName != s.Peer.DisplayName)
        {
            _trust.UpdateName(s.Peer.DeviceId, s.Peer.DisplayName);
        }

        if (s.InitiatedLocally)
        {
            _trust.RecordAddress(s.Peer.DeviceId, s.Connection.Remote.Ip, s.Connection.Remote.Port);
        }

        _network.SetFastPolling(true);
        _ = s.Closed.ContinueWith(
            t =>
            {
                try
                {
                    bool anyLeft;
                    lock (_gate)
                    {
                        if (_registered.TryGetValue(s.Peer.DeviceId, out var cur) && ReferenceEquals(cur, s))
                        {
                            _registered.Remove(s.Peer.DeviceId);
                            PublishLocked();
                        }

                        anyLeft = _registered.Count > 0;
                    }

                    if (!anyLeft)
                    {
                        _network.SetFastPolling(false);
                    }

                    _events.OnNext(new SessionEvent.Closed(s, t.Result));
                }
                finally
                {
                    s.MarkUnregistered();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal Session? Promote(PendingConnection p)
    {
        Session? old;
        Session s;
        lock (_gate)
        {
            if (p.Connection.IsClosedState)
            {
                return null;
            }

            _registered.Remove(p.Peer.DeviceId, out old);
            p.Connection.Activate();
            s = Register(p.Connection, p.Peer, initiator: p.Role == Role.Initiator ? SelfId : p.Peer.DeviceId, p.Via);
        }

        old?.Connection.Close(new CloseReason.Superseded());
        AfterRegister(s);
        return s;
    }

    private Connection NewConnection(ITcpConn conn, HandshakeOutcome.Done done, Role role)
    {
        var c = new Connection(conn, done.Reader, role, done.Version, done.Send, done.Receive, _clock, _timings);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Live(c, conn.Local.Ip, removed.Task);
        lock (_gate)
        {
            _live.Add(entry);
        }

        _ = c.Closed.ContinueWith(
            _ =>
            {
                try
                {
                    lock (_gate)
                    {
                        _live.Remove(entry);
                    }
                }
                finally
                {
                    removed.TrySetResult();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        c.Start();
        return c;
    }

    // ---- 出站 ----

    /// <summary>
    /// 建立连接。<paramref name="intent"/> 为 null 时收到 msg2 后按信任关系选择 SESSION 或 PAIR；PAIR / QR_PAIR 由 PairingService 显式传入
    /// （传入 SESSION 等同于 null）。Qr 目标总是 QR_PAIR。
    /// </summary>
    public Task<OpenResult> OpenAsync(Target target, Intent? intent = null)
    {
        var pairIntent = intent is Intent.Session ? null : intent;
        return target switch
        {
            Target.Device d => OpenDeviceAsync(d.Id, pairIntent),
            Target.Address a => ConnectToAsync(a.Ip, a.Port, expected: null, pairIntent, qr: null, _clock.MonotonicMs() + _timings.OpenMs, preset: null),
            Target.Qr q => OpenQrAsync(q.Payload),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    private async Task<OpenResult> OpenDeviceAsync(DeviceId id, Intent? pairIntent)
    {
        var deadline = _clock.MonotonicMs() + _timings.OpenMs;
        var address = _addressBook(id)
            ?? (_trust.Get(id) is { LastAddress: { } ip, LastPort: { } port } ? new SocketAddr(ip, port) : null);
        if (pairIntent is not null)
        {
            return address is { } addr
                ? await ConnectToAsync(addr.Ip, addr.Port, id, pairIntent, qr: null, deadline, preset: null).ConfigureAwait(false)
                : new OpenResult.Failed(new OpenFailure.Unreachable());
        }

        Attempt attempt;
        var fresh = false;
        lock (_gate)
        {
            if (_registered.TryGetValue(id, out var existing))
            {
                return new OpenResult.Ok(existing);
            }

            if (!_outgoing.TryGetValue(id, out attempt!))
            {
                attempt = new Attempt();
                _outgoing[id] = attempt;
                fresh = true;
            }
        }

        if (fresh)
        {
            _ = Task.Run(
                async () =>
                {
                    OpenResult r;
                    try
                    {
                        r = address is { } addr
                            ? await ConnectToAsync(addr.Ip, addr.Port, id, null, qr: null, deadline, attempt).ConfigureAwait(false)
                            : new OpenResult.Failed(new OpenFailure.Unreachable());
                    }
                    catch (OperationCanceledException)
                    {
                        r = new OpenResult.Failed(new OpenFailure.Timeout());
                    }

                    attempt.Result.TrySetResult(r);
                    lock (_gate)
                    {
                        if (_outgoing.TryGetValue(id, out var cur) && ReferenceEquals(cur, attempt))
                        {
                            _outgoing.Remove(id);
                        }
                    }
                },
                CancellationToken.None);
        }

        return await attempt.Result.Task.ConfigureAwait(false);
    }

    private async Task<OpenResult> OpenQrAsync(QrPayload p)
    {
        var ifaces = _network.Interfaces.Value;
        var ips = p.Ipv4List.Select(b => new Ipv4(b)).ToList();
        var candidates = AddressRules.QrCandidates(ips, ifaces);
        if (candidates.Count == 0)
        {
            var self = ips.Any(ip => AddressRules.Classify(ip, ifaces) is Route.Self);
            return new OpenResult.Failed(self ? new OpenFailure.SelfConnection() : new OpenFailure.NotLocal());
        }

        OpenFailure last = new OpenFailure.Unreachable();
        foreach (var (ip, _) in candidates)
        {
            var r = await ConnectToAsync(ip, p.Port, expected: null, Intent.QrPair, p, _clock.MonotonicMs() + _timings.QrPerAddressMs, preset: null).ConfigureAwait(false);
            if (r is not OpenResult.Failed failed)
            {
                return r;
            }

            last = failed.Failure;
            if (StopsQr(last))
            {
                break;
            }
        }

        return new OpenResult.Failed(last);
    }

    /// <summary>扫码时遇到这些失败不再尝试后面的候选地址：安全类失败、版本不兼容与自连接；网络类失败继续尝试下一个。</summary>
    private static bool StopsQr(OpenFailure f) => f is OpenFailure.FingerprintMismatch or OpenFailure.HandshakeAuthFailed
        or OpenFailure.KeyMismatch or OpenFailure.VersionIncompatible or OpenFailure.SelfConnection;

    /// <summary>
    /// 一次连接尝试：LocalNetworkGuard → 前导 → NoiseXX（msg2 后做身份检查）→ SESSION_RESULT。
    /// <paramref name="preset"/> 是 Device 目标预先登记的尝试；Address 目标在 msg2 之后（知道对端设备 ID 时）才登记。
    /// </summary>
    private async Task<OpenResult> ConnectToAsync(Ipv4 ip, int port, DeviceId? expected, Intent? pairIntent, QrPayload? qr, long deadline, Attempt? preset)
    {
        var ct = _cts.Token;
        var attempt = preset;
        var g = await _guard.ConnectAsync(ip, port, deadline - _clock.MonotonicMs(), ct).ConfigureAwait(false);
        if (g is not LocalNetworkGuard.Outcome.Connected connected)
        {
            return new OpenResult.Failed(((LocalNetworkGuard.Outcome.Failed)g).Failure);
        }

        var conn = connected.Conn;
        if (attempt is not null)
        {
            attempt.Sock = conn;
            if (attempt.Aborted)
            {
                conn.Close();
                return await attempt.Result.Task.ConfigureAwait(false);
            }
        }

        var outcome = await Handshake.InitiateAsync(
            conn,
            Local,
            _keys,
            _clock,
            deadline,
            _version,
            peer =>
            {
                var (decision, a) = Decide(peer, expected, pairIntent, qr, attempt);
                attempt = a;
                return decision;
            },
            ct).ConfigureAwait(false);
        HandshakeOutcome.Done done;
        switch (outcome)
        {
            case HandshakeOutcome.Failed f:
                conn.Close();
                return attempt is { Aborted: true } ? await attempt.Result.Task.ConfigureAwait(false) : new OpenResult.Failed(f.Failure);
            case HandshakeOutcome.Merged m:
                conn.Close();
                return await AwaitRetainedAsync(m.Peer.DeviceId, attempt, deadline).ConfigureAwait(false);
            default:
                done = (HandshakeOutcome.Done)outcome;
                break;
        }

        var c = NewConnection(conn, done, Role.Initiator);
        if (done.Intent != Intent.Session)
        {
            return new OpenResult.Pairing(new PendingConnection(done.Peer, done.Intent, Role.Initiator, c, connected.Via, this));
        }

        var at = attempt!;
        at.Connection = c;
        if (at.Aborted)
        {
            Abandon(at);
            return await at.Result.Task.ConfigureAwait(false);
        }

        var first = await Deadline.RunAsync(_clock, deadline, async t => await c.Incoming.WaitToReadAsync(t).ConfigureAwait(false) && c.Incoming.TryRead(out var m) ? m : null, ct)
            .ConfigureAwait(false);
        OpenResult result;
        if (first.Expired)
        {
            c.Close(new CloseReason.Local(SessionCloseReason.Normal));
            result = new OpenResult.Failed(new OpenFailure.Timeout());
        }
        else if (at.Aborted)
        {
            Abandon(at);
            result = await at.Result.Task.ConfigureAwait(false);
        }
        else
        {
            result = await OnSessionResultAsync(c, done.Peer, first.Value, at, connected.Via, deadline).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_outgoing.TryGetValue(done.Peer.DeviceId, out var cur) && ReferenceEquals(cur, at))
            {
                _outgoing.Remove(done.Peer.DeviceId);
            }
        }

        return result;
    }

    private async Task<OpenResult> OnSessionResultAsync(Connection c, PeerIdentity peer, ProtocolMessage? first, Attempt a, LocalInterface via, long deadline)
    {
        if (first is not SessionResult sr)
        {
            c.Close(new CloseReason.ProtocolError("expected SESSION_RESULT"));
            return new OpenResult.Failed(new OpenFailure.ProtocolError());
        }

        switch (sr.Result)
        {
            case SessionResultCode.Accepted:
                Session? old = null;
                Session? s = null;
                lock (_gate)
                {
                    if (!a.Aborted)
                    {
                        if (_outgoing.TryGetValue(peer.DeviceId, out var cur) && ReferenceEquals(cur, a))
                        {
                            _outgoing.Remove(peer.DeviceId);
                        }

                        _registered.Remove(peer.DeviceId, out old);
                        s = Register(c, peer, initiator: SelfId, via);
                    }
                }

                old?.Connection.Close(new CloseReason.Superseded());
                if (s is null)
                {
                    Abandon(a);
                    return await a.Result.Task.ConfigureAwait(false);
                }

                AfterRegister(s);
                return new OpenResult.Ok(s);
            case SessionResultCode.NotTrusted:
                c.Close(new CloseReason.Local(SessionCloseReason.Normal));
                return new OpenResult.Failed(new OpenFailure.NotTrusted());
            case SessionResultCode.KeyMismatch:
                c.Close(new CloseReason.Local(SessionCloseReason.Normal));
                return new OpenResult.Failed(new OpenFailure.PeerKeyMismatch(_trust.Get(peer.DeviceId)?.DisplayName ?? peer.DisplayName));
            default:
                c.Close(new CloseReason.Local(SessionCloseReason.Duplicate));
                lock (_gate)
                {
                    if (_outgoing.TryGetValue(peer.DeviceId, out var cur) && ReferenceEquals(cur, a))
                    {
                        _outgoing.Remove(peer.DeviceId);
                    }
                }

                return await AwaitRetainedAsync(peer.DeviceId, a, deadline).ConfigureAwait(false);
        }
    }

    /// <summary>等待按 C1.11 保留下来的那条连接注册完成（在剩余预算内）。</summary>
    private async Task<OpenResult> AwaitRetainedAsync(DeviceId peer, Attempt? mine, long deadline)
    {
        if (mine is { Aborted: true })
        {
            return await mine.Result.Task.ConfigureAwait(false);
        }

        Attempt? other;
        lock (_gate)
        {
            if (_registered.TryGetValue(peer, out var s))
            {
                return new OpenResult.Ok(s);
            }

            other = _outgoing.GetValueOrDefault(peer);
            if (ReferenceEquals(other, mine))
            {
                other = null;
            }
        }

        if (other is not null)
        {
            Attempt o = other;
            var r = await Deadline.RunAsync(_clock, deadline, t => o.Result.Task.WaitAsync(t), _cts.Token).ConfigureAwait(false);
            return r.Expired ? new OpenResult.Failed(new OpenFailure.Timeout()) : r.Value;
        }

        var w = await Deadline.RunAsync(_clock, deadline, t => _sessions.FirstAsync(m => m.ContainsKey(peer), t), _cts.Token).ConfigureAwait(false);
        return !w.Expired && GetSession(peer) is { } session ? new OpenResult.Ok(session) : new OpenResult.Failed(new OpenFailure.Timeout());
    }

    /// <summary>发起方读完 msg2 后的身份检查（规范第 6 节）与 intent 选择、同一对端合并。</summary>
    private (Msg2Decision Decision, Attempt? Attempt) Decide(PeerIdentity peer, DeviceId? expected, Intent? pairIntent, QrPayload? qr, Attempt? attempt)
    {
        (Msg2Decision, Attempt?) Stop(OpenFailure? f) => (new Msg2Decision.Stop(f), attempt);
        if (peer.DeviceId == SelfId)
        {
            return Stop(new OpenFailure.SelfConnection());
        }

        if (qr is not null && Bytes.Of(Fingerprint.Of(peer.PublicKey.Span)) != qr.Fingerprint)
        {
            return Stop(new OpenFailure.FingerprintMismatch());
        }

        if (expected is { } e && peer.DeviceId != e)
        {
            return Stop(new OpenFailure.Unreachable());
        }

        var trusted = _trust.Get(peer.DeviceId);
        if (trusted is not null && trusted.PublicKey != peer.PublicKey)
        {
            return Stop(new OpenFailure.KeyMismatch(trusted.DisplayName));
        }

        var intent = pairIntent ?? (trusted is not null ? Intent.Session : Intent.Pair);
        if (intent != Intent.Session)
        {
            return (new Msg2Decision.Proceed(intent), attempt);
        }

        lock (_gate)
        {
            if (attempt is { Aborted: true } || _registered.ContainsKey(peer.DeviceId))
            {
                return Stop(null);
            }

            var other = _outgoing.GetValueOrDefault(peer.DeviceId);
            if (other is not null && !ReferenceEquals(other, attempt))
            {
                return Stop(null);
            }

            var a = attempt ?? new Attempt();
            _outgoing[peer.DeviceId] = a;
            a.Authenticated = true;
            return (new Msg2Decision.Proceed(Intent.Session), a);
        }
    }
}
