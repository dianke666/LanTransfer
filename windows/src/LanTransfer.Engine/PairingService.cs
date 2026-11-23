using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// 配对服务（设计 C4.3），与 Kotlin 侧 PairingService.kt 同名镜像：执行 PairingReducer 的 effects。
// 全部状态只在一个 actor 任务里修改；连接的读取、计时、连接尝试都把结果作为命令送回 actor。

/// <summary>配对界面状态（由 PairingReducer 的会话推导）。</summary>
public abstract record PairingUiState
{
    private PairingUiState()
    {
    }

    public sealed record Idle : PairingUiState
    {
        public static Idle Instance { get; } = new();
    }

    /// <summary>发起方 / 扫描方：连接与握手中。</summary>
    public sealed record Connecting(PairingRole Role) : PairingUiState;

    /// <summary>发起方：已发出请求，等待对方确认（7.1）。</summary>
    public sealed record WaitingForPeer(PeerInfo Peer) : PairingUiState;

    /// <summary>发起方：显示验证码，等待对方决定（7.2）。</summary>
    public sealed record ShowingCode(PeerInfo Peer, string Sas) : PairingUiState;

    /// <summary>接收方确认界面（7.3）；Sas 非空后才能接受。</summary>
    public sealed record Request(RequestId Id, PeerInfo Peer, string? Sas) : PairingUiState;

    /// <summary>已接受 / 令牌已通过，等待完成或正在保存信任。</summary>
    public sealed record Completing(PeerInfo Peer, PairingRole Role) : PairingUiState;
}

/// <summary>接收方确认界面的标识；过期的界面上点击“接受”“拒绝”不起作用。</summary>
public readonly record struct RequestId(long Value);

/// <summary>配对的界面提示事件。</summary>
public abstract record PairingNotice
{
    private PairingNotice()
    {
    }

    /// <summary>本次配对结束；CONNECT_FAILED 时 Failure 为连接失败原因（E1）。</summary>
    public sealed record Finished(PairingDone Done, OpenFailure? Failure) : PairingNotice;

    /// <summary>接收方自动拒绝的请求（冷却期内或忙），不弹出界面（7.8、7.9）。</summary>
    public sealed record AutoRejected(DeviceId Peer, PairingResult Result) : PairingNotice;
}

public abstract record PairingOutcome
{
    private PairingOutcome()
    {
    }

    /// <summary>配对结束；CONNECT_FAILED 时 Failure 为连接失败原因。</summary>
    public sealed record Done(PairingResult Result, OpenFailure? Failure = null) : PairingOutcome;

    /// <summary>手动连接的目标已是可信设备：直接得到 Session，不进入配对（6.6）。</summary>
    public sealed record AlreadyTrusted(Session Session) : PairingOutcome;

    /// <summary>本机已有进行中的配对（7.9）。</summary>
    public sealed record Busy : PairingOutcome;

    /// <summary>二维码文本无法解析（23.6）。</summary>
    public sealed record InvalidQr(QrError Error) : PairingOutcome;
}

/// <summary>二维码界面（显示方）。</summary>
public abstract record QrScreenState
{
    private QrScreenState()
    {
    }

    public sealed record Showing(string Text, QrPayload Payload) : QrScreenState;

    /// <summary>没有活动接口（8.13）。</summary>
    public sealed record NoNetwork : QrScreenState
    {
        public static NoNetwork Instance { get; } = new();
    }
}

public sealed class PairingService : IDisposable
{
    /// <summary>二维码界面的检查周期（8.7 要求 2 s 内刷新）。</summary>
    public const long QrCheckMs = 1_000;

    /// <summary>入站配对连接在握手后这么久仍未发来请求时关闭。</summary>
    public const long RequestWaitMs = 10_000;

    private const int NonceSize = 32;

    /// <summary>Address 目标在握手前不知道对方设备 ID，连接阶段用全零占位。</summary>
    private static readonly DeviceId UnknownPeer = new(Bytes16.Of(new byte[16]));

    private readonly SessionManager _sessions;
    private readonly ITrustStore _trust;
    private readonly PairingTokenRegistry _tokens;
    private readonly IClock _clock;
    private readonly IRng _rng;
    private readonly DeviceIdentity _identity;
    private readonly INetworkMonitor _network;
    private readonly Func<string> _displayName;
    private readonly Func<int?> _listenPort;
    private readonly Func<DeviceId, Task> _cancelTransfers;
    private readonly Channel<Cmd> _inbox = Channel.CreateUnbounded<Cmd>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ObservableState<PairingUiState> _ui = new(PairingUiState.Idle.Instance);
    private readonly Subject<PairingNotice> _notices = new();
    private readonly CancellationTokenSource _cts = new();

    // ---- 只在 actor 中访问 ----
    private readonly Dictionary<ConnId, Entry> _conns = new();
    private readonly Dictionary<DeviceId, PeerInfo> _staged = new();
    private PairingState _pairing = new();
    private long _nextConn = 1;
    private TaskCompletionSource<PairingOutcome>? _outcome;
    private OpenFailure? _lastFailure;
    private Session? _alreadyTrusted;

    public PairingService(
        SessionManager sessions,
        ITrustStore trust,
        PairingTokenRegistry tokens,
        IClock clock,
        IRng rng,
        DeviceIdentity identity,
        INetworkMonitor network,
        Func<string>? displayName = null,
        Func<int?>? listenPort = null,
        Func<DeviceId, Task>? cancelTransfers = null)
    {
        _sessions = sessions;
        _trust = trust;
        _tokens = tokens;
        _clock = clock;
        _rng = rng;
        _identity = identity;
        _network = network;
        _displayName = displayName ?? (() => identity.DisplayName);
        _listenPort = listenPort ?? (() => null);
        _cancelTransfers = cancelTransfers ?? (_ => Task.CompletedTask);
        var ct = _cts.Token;
        _ = Task.Run(() => ActorAsync(ct), CancellationToken.None);
        _ = Task.Run(() => InboundAsync(ct), CancellationToken.None);
    }

    private sealed class Entry(PendingConnection pending)
    {
        public PendingConnection Pending { get; } = pending;

        public CancellationTokenSource Reader { get; } = new();

        public bool Requested { get; set; }
    }

    private abstract record Cmd
    {
        public sealed record Event(PairingEvent E) : Cmd;

        public sealed record StartPair(Target Target, bool ForcePair, TaskCompletionSource<PairingOutcome> Result) : Cmd;

        public sealed record StartQr(QrPayload Payload, TaskCompletionSource<PairingOutcome> Result) : Cmd;

        public sealed record OpenDone(OpenResult R) : Cmd;

        public sealed record Inbound(PendingConnection P) : Cmd;

        public sealed record Message(ConnId Conn, ProtocolMessage Msg) : Cmd;

        public sealed record Closed(ConnId Conn) : Cmd;

        public sealed record IdleCheck(ConnId Conn) : Cmd;

        public sealed record Respond(RequestId Id, bool Accept) : Cmd;
    }

    public IObservableState<PairingUiState> State => _ui;

    public IObservable<PairingNotice> Notices => _notices;

    public void Dispose() => _cts.Cancel();

    /// <summary>
    /// 点选配对与手动连接共用（6.6、7.1）：对方是可信设备且公钥一致时直接得到 Session（AlreadyTrusted）；
    /// <paramref name="forcePair"/> 为 true 时总是配对（对方已解除配对后的“重新配对”，9.5）。
    /// </summary>
    public Task<PairingOutcome> PairAsync(Target target, bool forcePair = false)
    {
        var d = new TaskCompletionSource<PairingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Writer.TryWrite(new Cmd.StartPair(target, forcePair, d));
        return d.Task;
    }

    /// <summary>发起方 / 扫描方取消（7.10）。</summary>
    public void Cancel() => _inbox.Writer.TryWrite(new Cmd.Event(new PairingEvent.UserCancel(_clock.MonotonicMs())));

    /// <summary>接收方点击“接受”或“拒绝”（7.4、7.6）。</summary>
    public void Respond(RequestId request, bool accept) => _inbox.Writer.TryWrite(new Cmd.Respond(request, accept));

    /// <summary>扫描方（8.4–8.12）：解析二维码，连接时在发送 msg3 之前比对公钥指纹（SessionManager 内完成，10.7）。</summary>
    public Task<PairingOutcome> PairByQrAsync(string qrText)
    {
        var r = QrCodec.Decode(qrText);
        if (r.Payload is not { } payload)
        {
            return Task.FromResult<PairingOutcome>(new PairingOutcome.InvalidQr(r.Error!.Value));
        }

        var d = new TaskCompletionSource<PairingOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Writer.TryWrite(new Cmd.StartQr(payload, d));
        return d.Task;
    }

    /// <summary>
    /// 显示方（8.1、8.7、8.13）：每秒检查令牌是否需要刷新以及本机地址集合是否变化，任一变化都重新生成令牌和二维码；
    /// 没有活动接口（或尚未监听）时输出 NoNetwork。
    /// </summary>
    public async IAsyncEnumerable<QrScreenState> QrScreenAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        IReadOnlyList<Ipv4>? shownIps = null;
        int? shownPort = null;
        string? shownName = null;
        QrScreenState? shown = null;
        while (!ct.IsCancellationRequested)
        {
            var ips = AddressRules.QrAddressList(_network.Interfaces.Value);
            var port = _listenPort();
            var name = _displayName();
            QrScreenState? next = null;
            if (ips.Count == 0 || port is null)
            {
                if (shown is not QrScreenState.NoNetwork)
                {
                    next = QrScreenState.NoNetwork.Instance;
                }
            }
            else
            {
                var now = _clock.MonotonicMs();
                bool needs;
                lock (_tokens)
                {
                    needs = _tokens.NeedsRefresh(now);
                }

                if (shown is not QrScreenState.Showing || shownIps is null || !shownIps.SequenceEqual(ips) || port != shownPort || name != shownName || needs)
                {
                    PairingToken token;
                    lock (_tokens)
                    {
                        token = _tokens.Issue(now);
                    }

                    var fp = Bytes.Of(Fingerprint.Of(_identity.KeyPair.PublicKey.Span));
                    var payload = new QrPayload(_identity.DeviceId, name, new ValueList<uint>(ips.Select(i => i.Bits)), (ushort)port.Value, fp, token.Value);
                    next = new QrScreenState.Showing(QrCodec.Encode(payload), payload);
                }
            }

            if (next is not null)
            {
                shown = next;
                shownIps = ips;
                shownPort = port;
                shownName = name;
                yield return next;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(QrCheckMs), _clock.Time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// 解除配对（9.3、10.8）：删除信任（含“自动接收文件”设置），对进行中的任务按本机取消处理，再发送 SESSION_CLOSE(UNPAIRED)。
    /// Session 关闭最多等待 CloseGraceMs，整个过程在 2 s 内完成。
    /// </summary>
    public async Task UnpairAsync(DeviceId peer)
    {
        _trust.Delete(peer);
        await _cancelTransfers(peer).ConfigureAwait(false);
        await _sessions.CloseAsync(peer, SessionCloseReason.Unpaired).ConfigureAwait(false);
    }

    // ---- actor ----

    private async Task ActorAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var cmd in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Handle(cmd);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task InboundAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var p in _sessions.PendingInbound.ReadAllAsync(ct).ConfigureAwait(false))
            {
                _inbox.Writer.TryWrite(new Cmd.Inbound(p));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Post(Cmd cmd) => _inbox.Writer.TryWrite(cmd);

    private void Handle(Cmd cmd)
    {
        var now = _clock.MonotonicMs();
        switch (cmd)
        {
            case Cmd.Event e:
                Step(e.E);
                break;
            case Cmd.StartPair sp:
                if (_pairing.Session is not null)
                {
                    sp.Result.TrySetResult(new PairingOutcome.Busy());
                    return;
                }

                Begin(sp.Result);
                Step(new PairingEvent.StartPair(sp.Target is Target.Device d ? d.Id : UnknownPeer, now));
                var intent = sp.ForcePair ? Intent.Pair : (Intent?)null;
                _ = Task.Run(async () => Post(new Cmd.OpenDone(await _sessions.OpenAsync(sp.Target, intent).ConfigureAwait(false))), CancellationToken.None);
                break;
            case Cmd.StartQr sq:
                if (_pairing.Session is not null)
                {
                    sq.Result.TrySetResult(new PairingOutcome.Busy());
                    return;
                }

                Begin(sq.Result);
                Step(new PairingEvent.StartQrPair(sq.Payload.DeviceId, sq.Payload.Token, now));
                _ = Task.Run(async () => Post(new Cmd.OpenDone(await _sessions.OpenAsync(new Target.Qr(sq.Payload)).ConfigureAwait(false))), CancellationToken.None);
                break;
            case Cmd.OpenDone od:
                OnOpenDone(od.R, now);
                break;
            case Cmd.Inbound ib:
                {
                    var id = Register(ib.P, requested: false);
                    var ct = _cts.Token;
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(RequestWaitMs), _clock.Time, ct).ConfigureAwait(false);
                                Post(new Cmd.IdleCheck(id));
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        },
                        CancellationToken.None);
                    break;
                }

            case Cmd.Message m:
                OnMessage(m.Conn, m.Msg, now);
                break;
            case Cmd.Closed c:
                if (_conns.ContainsKey(c.Conn))
                {
                    Step(new PairingEvent.Disconnected(c.Conn, now));
                    if (_conns.Remove(c.Conn, out var gone))
                    {
                        gone.Reader.Cancel();
                    }
                }

                break;
            case Cmd.IdleCheck ic:
                if (_conns.TryGetValue(ic.Conn, out var idle) && !idle.Requested)
                {
                    idle.Pending.Close(); // 入站连接迟迟不发请求：关闭
                }

                break;
            case Cmd.Respond r:
                if (_pairing.Session is PairingSession.AwaitingUser au && au.Conn.Value == r.Id.Value)
                {
                    Step(new PairingEvent.UserDecision(r.Accept, now));
                }

                break;
        }
    }

    private void Begin(TaskCompletionSource<PairingOutcome> result)
    {
        _outcome = result;
        _lastFailure = null;
        _alreadyTrusted = null;
    }

    private void OnOpenDone(OpenResult r, long now)
    {
        if (_pairing.Session is not PairingSession.Connecting)
        {
            // 已取消或本机计时已到期：丢弃迟到的连接
            if (r is OpenResult.Pairing late)
            {
                late.Connection.Close();
            }

            return;
        }

        switch (r)
        {
            case OpenResult.Pairing p:
                var id = Register(p.Connection, requested: true);
                Step(new PairingEvent.Connected(id, InfoOf(p.Connection), _identity.KeyPair.PublicKey, Bytes.Of(_rng.NextBytes(NonceSize)), now));
                break;
            case OpenResult.Ok ok:
                _alreadyTrusted = ok.Session;
                Step(new PairingEvent.ConnectFailed(now));
                break;
            case OpenResult.Failed f:
                _lastFailure = f.Failure;
                Step(new PairingEvent.ConnectFailed(now));
                break;
        }
    }

    /// <summary>登记一条配对连接：读取消息、监视关闭。接收方读到 PAIR_COMPLETE 后停止读取，之后的消息属于 Session。</summary>
    private ConnId Register(PendingConnection p, bool requested)
    {
        var id = new ConnId(_nextConn++);
        var e = new Entry(p) { Requested = requested };
        _conns[id] = e;
        var rt = e.Reader.Token;
        var reader = Task.Run(
            async () =>
            {
                try
                {
                    while (await p.Incoming.WaitToReadAsync(rt).ConfigureAwait(false))
                    {
                        while (!rt.IsCancellationRequested && p.Incoming.TryRead(out var m))
                        {
                            Post(new Cmd.Message(id, m));
                            if (m is PairComplete && p.Role == Role.Responder)
                            {
                                return;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);
        _ = Task.Run(
            async () =>
            {
                // 等读取结束后再报告断开：保证对方关闭前发来的最后一条消息（PAIR_CANCEL、PAIR_DECISION 等）先被处理
                await p.Closed.ConfigureAwait(false);
                await reader.ConfigureAwait(false);
                Post(new Cmd.Closed(id));
            },
            CancellationToken.None);
        return id;
    }

    /// <summary>对方信息；只有本机发起时记录地址与端口（入站连接的对端端口不是监听端口）。</summary>
    private static PeerInfo InfoOf(PendingConnection p) =>
        new(p.Peer.DeviceId, p.Peer.DisplayName, p.Peer.DeviceType, p.Peer.PublicKey, p.Remote.Ip, p.Role == Role.Initiator ? p.Remote.Port : null);

    private void OnMessage(ConnId conn, ProtocolMessage msg, long now)
    {
        if (!_conns.TryGetValue(conn, out var e))
        {
            return;
        }

        if (!e.Requested)
        {
            // 接收方 / 显示方：第一条消息必须是对应的请求；自动拒绝在发出 PAIR_NONCE 之前由归约器判定（7.8、7.9）
            switch (msg)
            {
                case PairRequest pr when e.Pending.Intent == Intent.Pair:
                    e.Requested = true;
                    Step(new PairingEvent.PairRequestReceived(conn, InfoOf(e.Pending), _identity.KeyPair.PublicKey, pr.Commitment, Bytes.Of(_rng.NextBytes(NonceSize)), now));
                    break;
                case QrPairRequest qr when e.Pending.Intent == Intent.QrPair:
                    e.Requested = true;
                    TokenCheck check;
                    lock (_tokens)
                    {
                        check = _tokens.Check(qr.Token.ToArray(), now);
                    }

                    Step(new PairingEvent.QrRequestChecked(conn, InfoOf(e.Pending), (check as TokenCheck.Ok)?.Lease, now));
                    break;
                default:
                    e.Pending.Close();
                    break;
            }

            return;
        }

        Step(new PairingEvent.MessageReceived(conn, msg, now));
    }

    private void Step(PairingEvent e)
    {
        var s = PairingReducer.Reduce(_pairing, e);
        _pairing = s.State;

        // 发起方 / 扫描方进入保存阶段后不再读取，且在执行 Finished 等 effects 前先更新 UI 状态，避免调用方看到旧状态
        if (_pairing.Session is PairingSession.Saving { Role: PairingRole.Initiator or PairingRole.QrScanner } saving && _conns.TryGetValue(saving.Conn, out var entry))
        {
            entry.Reader.Cancel();
        }

        _ui.Value = UiOf(_pairing.Session);
        foreach (var f in s.Effects)
        {
            Execute(f);
        }
    }

    private void Execute(PairingEffect f)
    {
        switch (f)
        {
            case PairingEffect.Send s:
                if (_conns.TryGetValue(s.Conn, out var se))
                {
                    se.Pending.Send(s.Msg);
                }

                break;
            case PairingEffect.Close c:
                if (_conns.TryGetValue(c.Conn, out var ce))
                {
                    ce.Pending.Close();
                }

                break;
            case PairingEffect.CancelConnect:
                break; // 连接尝试自行结束；结果到达时若已不在 Connecting 就关闭（OnOpenDone）
            case PairingEffect.ArmTimer t:
                {
                    var ct = _cts.Token;
                    var delay = Math.Max(0, t.AtMono - _clock.MonotonicMs());
                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(delay), _clock.Time, ct).ConfigureAwait(false);
                                Post(new Cmd.Event(new PairingEvent.Tick(_clock.MonotonicMs())));
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        },
                        CancellationToken.None);
                    break;
                }

            case PairingEffect.ShowRequest or PairingEffect.ShowSas or PairingEffect.DismissRequest:
                break; // 界面状态由会话推导
            case PairingEffect.SaveTrust st:
                {
                    var ok = _trust.Upsert(Trusted(st.Info)) is StoreResult.Ok;
                    Post(new Cmd.Event(new PairingEvent.TrustStored(st.Info.DeviceId, ok)));
                    break;
                }

            case PairingEffect.StageTrust sg:
                _staged[sg.Info.DeviceId] = sg.Info;
                break;
            case PairingEffect.CommitTrust ct2:
                {
                    var ok = _staged.Remove(ct2.Peer, out var info) && _trust.Upsert(Trusted(info)) is StoreResult.Ok;
                    Post(new Cmd.Event(new PairingEvent.TrustStored(ct2.Peer, ok)));
                    break;
                }

            case PairingEffect.DiscardStagedTrust ds:
                _staged.Remove(ds.Peer);
                break;
            case PairingEffect.DeleteTrust dt:
                _trust.Delete(dt.Peer);
                break;
            case PairingEffect.MarkTokenUsed mu:
                lock (_tokens)
                {
                    _tokens.MarkUsed(mu.Lease);
                }

                break;
            case PairingEffect.ReleaseToken rl:
                lock (_tokens)
                {
                    _tokens.Release(rl.Lease);
                }

                break;
            case PairingEffect.PromoteSession ps:
                if (_conns.Remove(ps.Conn, out var pe))
                {
                    pe.Reader.Cancel();
                    pe.Pending.Promote();
                }

                break;
            case PairingEffect.AutoRejected ar:
                _notices.OnNext(new PairingNotice.AutoRejected(ar.Peer, ar.Result));
                break;
            case PairingEffect.Finished fin:
                OnFinished(fin.Done);
                break;
        }
    }

    /// <summary>整条替换同一设备 ID 的旧条目（9.9），“自动接收文件”回到默认的关闭。</summary>
    private TrustedDevice Trusted(PeerInfo info) =>
        new(info.DeviceId, info.DisplayName, info.DeviceType, info.PublicKey, AutoAccept: false, PairedAtUtcMs: _clock.WallMs(), info.Port is null ? null : info.Ip, info.Port);

    private void OnFinished(PairingDone done)
    {
        var mine = done.Role is PairingRole.Initiator or PairingRole.QrScanner;
        if (mine && _alreadyTrusted is { } session)
        {
            _outcome?.TrySetResult(new PairingOutcome.AlreadyTrusted(session));
            _outcome = null;
            _alreadyTrusted = null;
            return;
        }

        var failure = done.Result == PairingResult.ConnectFailed ? _lastFailure ?? new OpenFailure.Timeout() : null;
        _notices.OnNext(new PairingNotice.Finished(done, failure));
        if (mine)
        {
            _outcome?.TrySetResult(new PairingOutcome.Done(done.Result, failure));
            _outcome = null;
            _lastFailure = null;
        }
    }

    private static PairingUiState UiOf(PairingSession? s) => s switch
    {
        null => PairingUiState.Idle.Instance,
        PairingSession.Connecting c => new PairingUiState.Connecting(c.Role),
        PairingSession.AwaitingPeer a => new PairingUiState.WaitingForPeer(a.Info),
        PairingSession.ShowingSas sas => new PairingUiState.ShowingCode(sas.Info, sas.Sas),
        PairingSession.AwaitingUser u => new PairingUiState.Request(new RequestId(u.Conn.Value), u.Info, u.Sas),
        PairingSession.AwaitingQrResult q => new PairingUiState.Completing(q.Info, PairingRole.QrScanner),
        PairingSession.AwaitingComplete ac => new PairingUiState.Completing(ac.Info, ac.Role),
        PairingSession.Saving sv => new PairingUiState.Completing(sv.Info, sv.Role),
        _ => PairingUiState.Idle.Instance,
    };
}
