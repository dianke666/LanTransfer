using LanTransfer.Protocol;
using Fx = LanTransfer.Domain.PairingEffect;
using Ps = LanTransfer.Domain.PairingSession;

namespace LanTransfer.Domain;

/// <summary>纯归约器的一步：新状态与交给引擎层执行的 effects（设计 C3.5），与 Kotlin 侧 <c>Step</c> 对应。</summary>
public sealed record Step<TState, TEffect>(TState State, ValueList<TEffect> Effects);

/// <summary>引擎层分配的连接编号；归约器只用它区分“当前配对连接”和其他连接。</summary>
public readonly record struct ConnId(long Value);

/// <summary>握手后得到的对方信息；保存信任时由引擎层补上配对时刻，组成 TrustedDevice。</summary>
public sealed record PeerInfo(DeviceId DeviceId, string DisplayName, DeviceType DeviceType, Bytes PublicKey, Ipv4? Ip = null, int? Port = null);

public enum PairingRole
{
    Initiator,
    Receiver,
    QrScanner,
    QrDisplay,
}

/// <summary>进行中的配对（M3.4），与 Kotlin 侧 <c>PairingSession</c> 对应。Deadline 为本阶段的截止时刻（单调时钟毫秒），到期由 Tick 处理。</summary>
public abstract record PairingSession(DeviceId Peer, PairingRole Role)
{
    /// <summary>发起方或扫描方：连接与握手中（点选 10 s，扫码 4 个候选地址 × 5 s）。QrToken 非空表示扫码。</summary>
    public sealed record Connecting(DeviceId Peer, Bytes16? QrToken, long Deadline)
        : PairingSession(Peer, QrToken is null ? PairingRole.Initiator : PairingRole.QrScanner);

    /// <summary>发起方：已发出 PAIR_REQUEST，显示“等待对方确认”。</summary>
    public sealed record AwaitingPeer(DeviceId Peer, ConnId Conn, PeerInfo Info, Bytes LocalPublicKey, Bytes NI, long Deadline)
        : PairingSession(Peer, PairingRole.Initiator);

    /// <summary>发起方：收到 PAIR_NONCE、已发出 PAIR_REVEAL，显示 SAS，继续等待对方决定。</summary>
    public sealed record ShowingSas(DeviceId Peer, ConnId Conn, PeerInfo Info, string Sas, long Deadline)
        : PairingSession(Peer, PairingRole.Initiator);

    /// <summary>接收方：已发出 PAIR_NONCE，显示请求；收到 PAIR_REVEAL 并校验承诺后 Sas 非空，才允许接受。</summary>
    public sealed record AwaitingUser(DeviceId Peer, ConnId Conn, PeerInfo Info, Bytes LocalPublicKey, Bytes Commitment, Bytes NR, string? Sas, long Deadline)
        : PairingSession(Peer, PairingRole.Receiver);

    /// <summary>扫描方：已发出 QR_PAIR_REQUEST，等待 QR_PAIR_RESULT。</summary>
    public sealed record AwaitingQrResult(DeviceId Peer, ConnId Conn, PeerInfo Info, long Deadline)
        : PairingSession(Peer, PairingRole.QrScanner);

    /// <summary>接收方或显示方：已暂存信任，等待 PAIR_COMPLETE。显示方持有令牌租约 Lease。</summary>
    public sealed record AwaitingComplete(DeviceId Peer, ConnId Conn, PeerInfo Info, PairingRole Role, TokenLease? Lease, long Deadline)
        : PairingSession(Peer, Role);

    /// <summary>正在保存（发起方、扫描方）或提交（接收方、显示方）信任，等待 TrustStored。</summary>
    public sealed record Saving(DeviceId Peer, ConnId Conn, PeerInfo Info, PairingRole Role, TokenLease? Lease)
        : PairingSession(Peer, Role);
}

public sealed record PairingDone(DeviceId Peer, PairingRole Role, PairingResult Result);

/// <summary>
/// 配对服务的内存状态：至多 1 个进行中的配对（7.9）、按设备 ID 记录的用户拒绝时刻（60 s 冷却，7.8）、最近一次结局。
/// 不持久化，进程结束后未提交的暂存信任随之丢弃（7.12）。
/// </summary>
public sealed record PairingState(PairingSession? Session, ValueMap<DeviceId, long> RejectedAt, PairingDone? Last)
{
    public PairingState()
        : this(null, ValueMap<DeviceId, long>.Empty, null)
    {
    }
}

public abstract record PairingEvent
{
    /// <summary>发起方：用户点选设备或手动连接得到非信任设备（6.6、7.1）。引擎随即开始连接。</summary>
    public sealed record StartPair(DeviceId Peer, long Now) : PairingEvent;

    /// <summary>扫描方：扫到有效的 QR_Payload（8.4）。</summary>
    public sealed record StartQrPair(DeviceId Peer, Bytes16 Token, long Now) : PairingEvent;

    /// <summary>发起方 / 扫描方：连接与握手完成。NI 为本次配对的 32 字节随机数（扫码时不使用）。</summary>
    public sealed record Connected(ConnId Conn, PeerInfo Info, Bytes LocalPublicKey, Bytes NI, long Now) : PairingEvent;

    public sealed record ConnectFailed(long Now) : PairingEvent;

    /// <summary>接收方：收到 PAIR_REQUEST。NR 为引擎生成的 32 字节随机数，只在接受该请求时使用。</summary>
    public sealed record PairRequestReceived(ConnId Conn, PeerInfo Info, Bytes LocalPublicKey, Bytes Commitment, Bytes NR, long Now) : PairingEvent;

    /// <summary>显示方：收到 QR_PAIR_REQUEST，引擎已用 PairingTokenRegistry 校验；Lease 为 null 表示令牌无效。</summary>
    public sealed record QrRequestChecked(ConnId Conn, PeerInfo Info, TokenLease? Lease, long Now) : PairingEvent;

    /// <summary>PAIR_NONCE、PAIR_REVEAL、PAIR_DECISION、PAIR_CANCEL、PAIR_COMPLETE、QR_PAIR_RESULT。</summary>
    public sealed record MessageReceived(ConnId Conn, ProtocolMessage Msg, long Now) : PairingEvent;

    /// <summary>接收方用户点击“接受”或“拒绝”。</summary>
    public sealed record UserDecision(bool Accept, long Now) : PairingEvent;

    /// <summary>发起方 / 扫描方用户点击“取消”（7.10）。</summary>
    public sealed record UserCancel(long Now) : PairingEvent;

    public sealed record TrustStored(DeviceId Peer, bool Ok) : PairingEvent;

    public sealed record Disconnected(ConnId Conn, long Now) : PairingEvent;

    public sealed record Tick(long Now) : PairingEvent;
}

public abstract record PairingEffect
{
    public sealed record Send(ConnId Conn, ProtocolMessage Msg) : PairingEffect;

    public sealed record Close(ConnId Conn) : PairingEffect;

    /// <summary>放弃尚未完成的连接尝试。</summary>
    public sealed record CancelConnect : PairingEffect;

    /// <summary>请在 AtMono 或之后送来 Tick。</summary>
    public sealed record ArmTimer(long AtMono) : PairingEffect;

    /// <summary>接收方确认界面：对方名称、类型、IP 与“接受”“拒绝”（7.3）。</summary>
    public sealed record ShowRequest(PeerInfo Info) : PairingEffect;

    public sealed record ShowSas(string Sas) : PairingEffect;

    /// <summary>关闭接收方确认界面（7.7、7.10、7.12）。</summary>
    public sealed record DismissRequest : PairingEffect;

    /// <summary>发起方 / 扫描方：保存信任（整条替换同一设备 ID，9.9）。</summary>
    public sealed record SaveTrust(PeerInfo Info) : PairingEffect;

    /// <summary>接收方 / 显示方：暂存信任，收到 PAIR_COMPLETE 后再提交。</summary>
    public sealed record StageTrust(PeerInfo Info) : PairingEffect;

    public sealed record CommitTrust(DeviceId Peer) : PairingEffect;

    public sealed record DiscardStagedTrust(DeviceId Peer) : PairingEffect;

    /// <summary>删除可能已保存的信任（7.12）。</summary>
    public sealed record DeleteTrust(DeviceId Peer) : PairingEffect;

    public sealed record MarkTokenUsed(TokenLease Lease) : PairingEffect;

    public sealed record ReleaseToken(TokenLease Lease) : PairingEffect;

    /// <summary>当前连接直接转为 Session（7.4、8.4）。</summary>
    public sealed record PromoteSession(ConnId Conn) : PairingEffect;

    /// <summary>自动拒绝的请求（不显示界面）：Result 为 AutoRejected（冷却期内）或 Busy。</summary>
    public sealed record AutoRejected(DeviceId Peer, PairingResult Result) : PairingEffect;

    /// <summary>本次配对结束，界面显示对应提示。</summary>
    public sealed record Finished(PairingDone Done) : PairingEffect;
}

/// <summary>配对状态机（M3.4，需求 7、8），与 Kotlin 侧 <c>PairingReducer</c> 对应。不认识或迟到的消息一律忽略（设计 E 表）。</summary>
public static class PairingReducer
{
    public const long ConnectTimeoutMs = 10_000;
    public const long QrConnectTimeoutMs = 4 * 5_000;
    public const long RequestTimeoutMs = 60_000;
    public const long CooldownMs = 60_000;

    /// <summary>等待 PAIR_COMPLETE / QR_PAIR_RESULT 的上限；与心跳的 15 s 断线判定一致（7.12）。</summary>
    public const long CompleteTimeoutMs = 15_000;

    public static Step<PairingState, PairingEffect> Reduce(PairingState s, PairingEvent e) => e switch
    {
        PairingEvent.StartPair x => Start(s, new Ps.Connecting(x.Peer, null, x.Now + ConnectTimeoutMs)),
        PairingEvent.StartQrPair x => Start(s, new Ps.Connecting(x.Peer, x.Token, x.Now + QrConnectTimeoutMs)),
        PairingEvent.Connected x => OnConnected(s, x),
        PairingEvent.ConnectFailed => s.Session is Ps.Connecting c ? Done(s, c, PairingResult.ConnectFailed) : Of(s),
        PairingEvent.PairRequestReceived x => OnPairRequest(s, x),
        PairingEvent.QrRequestChecked x => OnQrRequest(s, x),
        PairingEvent.MessageReceived x => OnMessage(s, x),
        PairingEvent.UserDecision x => OnDecision(s, x),
        PairingEvent.UserCancel => OnCancel(s),
        PairingEvent.TrustStored x => OnTrustStored(s, x),
        PairingEvent.Disconnected x => OnDisconnected(s, x),
        PairingEvent.Tick x => OnTick(s, x.Now),
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    private static Step<PairingState, PairingEffect> Of(PairingState s, params PairingEffect[] effects) => new(s, new ValueList<PairingEffect>(effects));

    private static Step<PairingState, PairingEffect> Start(PairingState s, Ps.Connecting c) =>
        s.Session is not null ? Of(s) : Of(s with { Session = c }, new Fx.ArmTimer(c.Deadline));

    private static Step<PairingState, PairingEffect> Done(PairingState s, Ps session, PairingResult result, params PairingEffect[] effects)
    {
        var d = new PairingDone(session.Peer, session.Role, result);
        return Of(s with { Session = null, Last = d }, [.. effects, new Fx.Finished(d)]);
    }

    private static Step<PairingState, PairingEffect> OnConnected(PairingState s, PairingEvent.Connected e)
    {
        if (s.Session is not Ps.Connecting c)
        {
            return Of(s, new Fx.Close(e.Conn));
        }

        var peer = e.Info.DeviceId;
        if (c.QrToken is { } token)
        {
            var qr = new Ps.AwaitingQrResult(peer, e.Conn, e.Info, e.Now + CompleteTimeoutMs);
            return Of(s with { Session = qr }, new Fx.Send(e.Conn, new QrPairRequest(token)), new Fx.ArmTimer(qr.Deadline));
        }

        var commitment = VerificationCode.Commitment(e.LocalPublicKey.ToArray(), e.Info.PublicKey.ToArray(), e.NI.ToArray());
        var next = new Ps.AwaitingPeer(peer, e.Conn, e.Info, e.LocalPublicKey, e.NI, e.Now + RequestTimeoutMs);
        return Of(s with { Session = next }, new Fx.Send(e.Conn, new PairRequest(Bytes.Of(commitment))), new Fx.ArmTimer(next.Deadline));
    }

    private static Step<PairingState, PairingEffect> OnPairRequest(PairingState s, PairingEvent.PairRequestReceived e)
    {
        var peer = e.Info.DeviceId;
        if (s.RejectedAt.TryGetValue(peer, out var rejected) && e.Now - rejected < CooldownMs)
        {
            return Of(s, AutoReject(e.Conn, peer, PairDecisionCode.Rejected, PairingResult.AutoRejected));
        }

        if (s.Session is not null)
        {
            return Of(s, AutoReject(e.Conn, peer, PairDecisionCode.Busy, PairingResult.Busy));
        }

        var next = new Ps.AwaitingUser(peer, e.Conn, e.Info, e.LocalPublicKey, e.Commitment, e.NR, null, e.Now + RequestTimeoutMs);
        return Of(
            s with { Session = next, RejectedAt = s.RejectedAt.Remove(peer) },
            new Fx.Send(e.Conn, new PairNonce(e.NR)),
            new Fx.ShowRequest(e.Info),
            new Fx.ArmTimer(next.Deadline));
    }

    private static PairingEffect[] AutoReject(ConnId conn, DeviceId peer, PairDecisionCode code, PairingResult result) =>
        [new Fx.Send(conn, new PairDecision(code)), new Fx.Close(conn), new Fx.AutoRejected(peer, result)];

    private static Step<PairingState, PairingEffect> OnQrRequest(PairingState s, PairingEvent.QrRequestChecked e)
    {
        if (e.Lease is not { } lease || s.Session is not null)
        {
            var effects = new List<PairingEffect>();
            if (e.Lease is { } l)
            {
                effects.Add(new Fx.ReleaseToken(l));
            }

            effects.Add(new Fx.Send(e.Conn, new QrPairResult(QrPairResultCode.TokenInvalid)));
            effects.Add(new Fx.Close(e.Conn));
            return Of(s, [.. effects]);
        }

        var next = new Ps.AwaitingComplete(e.Info.DeviceId, e.Conn, e.Info, PairingRole.QrDisplay, lease, e.Now + CompleteTimeoutMs);
        return Of(
            s with { Session = next },
            new Fx.Send(e.Conn, new QrPairResult(QrPairResultCode.Ok)),
            new Fx.StageTrust(e.Info),
            new Fx.ArmTimer(next.Deadline));
    }

    private static ConnId? ConnOf(Ps? session) => session switch
    {
        Ps.AwaitingPeer x => x.Conn,
        Ps.ShowingSas x => x.Conn,
        Ps.AwaitingUser x => x.Conn,
        Ps.AwaitingQrResult x => x.Conn,
        Ps.AwaitingComplete x => x.Conn,
        Ps.Saving x => x.Conn,
        _ => null,
    };

    private static long? DeadlineOf(Ps? session) => session switch
    {
        Ps.Connecting x => x.Deadline,
        Ps.AwaitingPeer x => x.Deadline,
        Ps.ShowingSas x => x.Deadline,
        Ps.AwaitingUser x => x.Deadline,
        Ps.AwaitingQrResult x => x.Deadline,
        Ps.AwaitingComplete x => x.Deadline,
        _ => null,
    };

    private static Step<PairingState, PairingEffect> OnMessage(PairingState s, PairingEvent.MessageReceived e)
    {
        var session = s.Session;
        if (session is null || ConnOf(session) != e.Conn)
        {
            return Of(s);
        }

        switch (session)
        {
            case Ps.AwaitingPeer ap when e.Msg is PairNonce nonce:
            {
                var sas = VerificationCode.Sas(ap.LocalPublicKey.ToArray(), ap.Info.PublicKey.ToArray(), ap.NI.ToArray(), nonce.Nonce.ToArray());
                var next = new Ps.ShowingSas(ap.Peer, ap.Conn, ap.Info, sas, ap.Deadline);
                return Of(s with { Session = next }, new Fx.Send(ap.Conn, new PairReveal(ap.NI)), new Fx.ShowSas(sas));
            }

            // 揭示 nI 之前不可能被接受：按协议错误处理
            case Ps.AwaitingPeer ap when e.Msg is PairDecision d:
                return Done(s, ap, DecisionResult(d.Decision) ?? PairingResult.Disconnected, new Fx.Close(ap.Conn));

            case Ps.ShowingSas ss when e.Msg is PairDecision d:
                return DecisionResult(d.Decision) is { } r
                    ? Done(s, ss, r, new Fx.Close(ss.Conn))
                    : Of(s with { Session = new Ps.Saving(ss.Peer, ss.Conn, ss.Info, PairingRole.Initiator, null) }, new Fx.SaveTrust(ss.Info));

            case Ps.AwaitingUser au when e.Msg is PairReveal reveal:
            {
                if (au.Sas is not null)
                {
                    return Of(s);
                }

                var pkI = au.Info.PublicKey.ToArray();
                var pkR = au.LocalPublicKey.ToArray();
                var nI = reveal.Nonce.ToArray();
                if (!VerificationCode.VerifyCommitment(au.Commitment.ToArray(), pkI, pkR, nI))
                {
                    return Done(s, au, PairingResult.CodeMismatch, new Fx.DismissRequest(), new Fx.Close(au.Conn));
                }

                var sas = VerificationCode.Sas(pkI, pkR, nI, au.NR.ToArray());
                return Of(s with { Session = au with { Sas = sas } }, new Fx.ShowSas(sas));
            }

            case Ps.AwaitingUser au when e.Msg is PairCancel cancel:
                return Done(s, au, CancelResult(cancel.Reason), new Fx.DismissRequest(), new Fx.Close(au.Conn));

            case Ps.AwaitingQrResult aq when e.Msg is QrPairResult result:
                return result.Result == QrPairResultCode.Ok
                    ? Of(s with { Session = new Ps.Saving(aq.Peer, aq.Conn, aq.Info, PairingRole.QrScanner, null) }, new Fx.SaveTrust(aq.Info))
                    : Done(s, aq, PairingResult.TokenInvalid, new Fx.Close(aq.Conn));

            case Ps.AwaitingComplete ac when e.Msg is PairComplete:
                return Of(s with { Session = new Ps.Saving(ac.Peer, ac.Conn, ac.Info, ac.Role, ac.Lease) }, new Fx.CommitTrust(ac.Peer));

            case Ps.AwaitingComplete ac when e.Msg is PairCancel cancel:
                return Done(s, ac, CancelResult(cancel.Reason), [.. AbortStaged(ac), new Fx.Close(ac.Conn)]);

            default:
                return Of(s);
        }
    }

    /// <summary>Accepted → null（继续），其余 → 对应结局。</summary>
    private static PairingResult? DecisionResult(PairDecisionCode code) => code switch
    {
        PairDecisionCode.Accepted => null,
        PairDecisionCode.Rejected => PairingResult.Rejected,
        PairDecisionCode.Busy => PairingResult.Busy,
        PairDecisionCode.Timeout => PairingResult.Timeout,
        _ => PairingResult.Disconnected,
    };

    private static PairingResult CancelResult(PairCancelReason reason) =>
        reason == PairCancelReason.Timeout ? PairingResult.Timeout : PairingResult.Cancelled;

    /// <summary>放弃已暂存的信任；显示方同时归还令牌租约。</summary>
    private static PairingEffect[] AbortStaged(Ps.AwaitingComplete a) =>
        a.Lease is { } l ? [new Fx.DiscardStagedTrust(a.Peer), new Fx.ReleaseToken(l)] : [new Fx.DiscardStagedTrust(a.Peer)];

    private static Step<PairingState, PairingEffect> OnDecision(PairingState s, PairingEvent.UserDecision e)
    {
        // 还没有显示验证码，不能决定
        if (s.Session is not Ps.AwaitingUser au || au.Sas is null)
        {
            return Of(s);
        }

        if (e.Accept)
        {
            var next = new Ps.AwaitingComplete(au.Peer, au.Conn, au.Info, PairingRole.Receiver, null, e.Now + CompleteTimeoutMs);
            return Of(
                s with { Session = next },
                new Fx.StageTrust(au.Info),
                new Fx.Send(au.Conn, new PairDecision(PairDecisionCode.Accepted)),
                new Fx.ArmTimer(next.Deadline));
        }

        var step = Done(s, au, PairingResult.Rejected, new Fx.Send(au.Conn, new PairDecision(PairDecisionCode.Rejected)), new Fx.Close(au.Conn));
        return step with { State = step.State with { RejectedAt = s.RejectedAt.SetItem(au.Peer, e.Now) } };
    }

    private static Step<PairingState, PairingEffect> OnCancel(PairingState s) => s.Session switch
    {
        Ps.Connecting c => Done(s, c, PairingResult.Cancelled, new Fx.CancelConnect()),
        Ps.AwaitingPeer ap => Done(s, ap, PairingResult.Cancelled, new Fx.Send(ap.Conn, new PairCancel(PairCancelReason.User)), new Fx.Close(ap.Conn)),
        Ps.ShowingSas ss => Done(s, ss, PairingResult.Cancelled, new Fx.Send(ss.Conn, new PairCancel(PairCancelReason.User)), new Fx.Close(ss.Conn)),
        Ps.AwaitingQrResult aq => Done(s, aq, PairingResult.Cancelled, new Fx.Close(aq.Conn)),
        _ => Of(s),
    };

    private static Step<PairingState, PairingEffect> OnTrustStored(PairingState s, PairingEvent.TrustStored e)
    {
        if (s.Session is not Ps.Saving sv || sv.Peer != e.Peer)
        {
            return Of(s);
        }

        if (!e.Ok)
        {
            // 接收方 / 显示方提交失败：丢弃暂存；显示方归还令牌租约
            var cleanup = new List<PairingEffect>();
            if (sv.Role is PairingRole.Receiver or PairingRole.QrDisplay)
            {
                cleanup.Add(new Fx.DiscardStagedTrust(sv.Peer));
            }

            if (sv.Lease is { } l)
            {
                cleanup.Add(new Fx.ReleaseToken(l));
            }

            return Done(s, sv, PairingResult.StoreFailed, [.. cleanup, new Fx.Close(sv.Conn)]);
        }

        return sv.Role switch
        {
            PairingRole.Initiator or PairingRole.QrScanner =>
                Done(s, sv, PairingResult.Success, new Fx.Send(sv.Conn, PairComplete.Instance), new Fx.PromoteSession(sv.Conn)),
            PairingRole.Receiver => Done(s, sv, PairingResult.Success, new Fx.PromoteSession(sv.Conn)),
            _ => sv.Lease is { } lease
                ? Done(s, sv, PairingResult.Success, new Fx.MarkTokenUsed(lease), new Fx.PromoteSession(sv.Conn))
                : Done(s, sv, PairingResult.Success, new Fx.PromoteSession(sv.Conn)),
        };
    }

    private static Step<PairingState, PairingEffect> OnDisconnected(PairingState s, PairingEvent.Disconnected e)
    {
        var session = s.Session;
        if (session is null || ConnOf(session) != e.Conn)
        {
            return Of(s);
        }

        return session switch
        {
            Ps.AwaitingUser au => Done(s, au, PairingResult.Disconnected, new Fx.DismissRequest()),
            Ps.AwaitingComplete ac => Done(s, ac, PairingResult.Disconnected, AbortStaged(ac)),

            // 发起方 / 扫描方可能已经保存：删除（7.12）。接收方 / 显示方已收到 PAIR_COMPLETE，结局由 TrustStored 决定
            Ps.Saving sv when sv.Role is PairingRole.Initiator or PairingRole.QrScanner =>
                Done(s, sv, PairingResult.Disconnected, new Fx.DeleteTrust(sv.Peer)),
            Ps.Saving => Of(s),
            _ => Done(s, session, PairingResult.Disconnected),
        };
    }

    private static Step<PairingState, PairingEffect> OnTick(PairingState s, long now)
    {
        var pruned = s.RejectedAt.Filter(kv => now - kv.Value < CooldownMs);
        var b = pruned.Count == s.RejectedAt.Count ? s : s with { RejectedAt = pruned };
        var session = b.Session;
        if (session is null || DeadlineOf(session) is not { } deadline || now < deadline)
        {
            return Of(b);
        }

        return session switch
        {
            Ps.Connecting c => Done(b, c, PairingResult.ConnectFailed, new Fx.CancelConnect()),
            Ps.AwaitingPeer ap => Done(b, ap, PairingResult.Timeout, new Fx.Send(ap.Conn, new PairCancel(PairCancelReason.Timeout)), new Fx.Close(ap.Conn)),
            Ps.ShowingSas ss => Done(b, ss, PairingResult.Timeout, new Fx.Send(ss.Conn, new PairCancel(PairCancelReason.Timeout)), new Fx.Close(ss.Conn)),
            Ps.AwaitingUser au => Done(
                b,
                au,
                PairingResult.Timeout,
                new Fx.Send(au.Conn, new PairDecision(PairDecisionCode.Timeout)),
                new Fx.DismissRequest(),
                new Fx.Close(au.Conn)),
            Ps.AwaitingQrResult aq => Done(b, aq, PairingResult.Timeout, new Fx.Close(aq.Conn)),
            Ps.AwaitingComplete ac => Done(b, ac, PairingResult.Disconnected, [.. AbortStaged(ac), new Fx.Close(ac.Conn)]),
            _ => Of(b),
        };
    }
}
