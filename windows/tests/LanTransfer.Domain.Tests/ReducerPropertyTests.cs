using System.Text;
using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;
using static LanTransfer.Domain.Tests.ReducerFixtures;
using Fx = LanTransfer.Domain.PairingEffect;
using Pe = LanTransfer.Domain.PairingEvent;
using Ps = LanTransfer.Domain.PairingSession;
using Te = LanTransfer.Domain.TransferEvent;
using Tf = LanTransfer.Domain.TransferEffect;
using Xe = LanTransfer.Domain.TextEvent;
using Xf = LanTransfer.Domain.TextEffect;

namespace LanTransfer.Domain.Tests;

/// <summary>Property 35–38 的 C# 实现，与 Kotlin 侧 ReducerPropertiesTest 对应。每个用例由一个种子决定，失败时打印种子即可重放。</summary>
public sealed class ReducerPropertyTests
{
    private static readonly Gen<int> Seeds = Gen.Choose(0, int.MaxValue - 1);

    // ---- Property 35：发起方与接收方的双方模拟 ----

    private abstract record PairAct
    {
        public sealed record Connect : PairAct;

        public sealed record ConnectFail : PairAct;

        /// <summary>向接收方投递 1 条消息（或在对方关闭后投递断开）。</summary>
        public sealed record ToR : PairAct;

        public sealed record ToI : PairAct;

        public sealed record Accept : PairAct;

        public sealed record Reject : PairAct;

        public sealed record Cancel : PairAct;

        public sealed record Advance(long Dt) : PairAct;

        /// <summary>网络中断：在途消息全部丢失（含 PAIR_COMPLETE 丢失），双方都检测到断开。</summary>
        public sealed record Break : PairAct;

        /// <summary>完成一方未完成的信任写入。</summary>
        public sealed record Store(bool Initiator, bool Ok) : PairAct;
    }

    private static PairAct RandomPairAct(Random r) => r.Next(20) switch
    {
        0 => new PairAct.Connect(),
        1 => new PairAct.ConnectFail(),
        >= 2 and <= 5 => new PairAct.ToR(),
        >= 6 and <= 9 => new PairAct.ToI(),
        10 or 11 => new PairAct.Accept(),
        12 => new PairAct.Reject(),
        13 => new PairAct.Cancel(),
        14 or 15 => new PairAct.Advance(r.Pick(r.NextInt64(0, 3_000), r.NextInt64(9_000, 16_000), r.NextInt64(59_000, 61_000))),
        16 => new PairAct.Break(),
        _ => new PairAct.Store(r.Next(2) == 0, r.Next(4) != 0),
    };

    /// <summary>三分之一完全随机；其余以成功流程为骨架（决定为接受、拒绝或缺省），并随机插入、替换动作。</summary>
    private static List<PairAct> PairScript(Random r)
    {
        if (r.Next(3) == 0)
        {
            return Enumerable.Range(0, r.Next(0, 41)).Select(_ => RandomPairAct(r)).ToList();
        }

        PairAct? decision = r.Next(20) switch
        {
            <= 11 => new PairAct.Accept(),
            <= 16 => new PairAct.Reject(),
            _ => null,
        };
        PairAct?[] happy =
        [
            new PairAct.Connect(), new PairAct.ToR(), new PairAct.ToI(), new PairAct.ToR(), decision, new PairAct.ToI(),
            new PairAct.Store(true, true), new PairAct.ToR(), new PairAct.Store(false, true),
        ];
        var result = new List<PairAct>();
        foreach (var a in happy.OfType<PairAct>())
        {
            if (r.Next(100) < 15)
            {
                result.Add(RandomPairAct(r));
            }

            result.Add(r.Next(100) < 8 ? RandomPairAct(r) : a);
        }

        return result;
    }

    /// <summary>
    /// 发起方 I 与接收方 R 之间的一条连接 + 双方的信任存储。消息按发送顺序投递；一方关闭后，对方收完已发出的消息再收到断开，
    /// 之后发往关闭方的消息全部丢失。信任写入是异步的：SaveTrust / CommitTrust 进入待完成状态，由 Store 动作完成。
    /// </summary>
    private sealed class PairSim
    {
        private readonly ConnId _conn = new(1);
        private readonly Bytes _nI;
        private readonly Bytes _nR;
        private readonly Queue<ProtocolMessage> _toR = new();
        private readonly Queue<ProtocolMessage> _toI = new();
        private long _now = 1_000_000;
        private bool _connected;
        private bool _iClosed;
        private bool _rClosed;
        private bool _broken;
        private bool _iNotified;
        private bool _rNotified;
        private bool _iSavePending;
        private bool _rCommitPending;
        private long? _requestAt;
        private long? _receivedAt;

        public PairSim(Random r)
        {
            _nI = r.RandomBytes(32);
            _nR = r.RandomBytes(32);
        }

        public PeerInfo IInfo { get; } = PeerInfoOf(1);

        public PeerInfo RInfo { get; } = PeerInfoOf(2);

        public PairingState Ini { get; private set; } = new();

        public PairingState Rec { get; private set; } = new();

        public bool ITrusted { get; private set; }

        public bool RTrusted { get; private set; }

        public bool RStaged { get; private set; }

        public bool RGotRequest { get; private set; }

        private bool CanSend() => _connected && !_broken && !_iClosed && !_rClosed;

        public void Start() => ApplyI(new Pe.StartPair(RInfo.DeviceId, _now));

        private void ApplyI(PairingEvent e)
        {
            var step = PairingReducer.Reduce(Ini, e);
            Ini = step.State;
            foreach (var f in step.Effects)
            {
                switch (f)
                {
                    case Fx.Send send when CanSend():
                        _toR.Enqueue(send.Msg);
                        break;
                    case Fx.Close:
                        _iClosed = true;
                        _toI.Clear();
                        break;
                    case Fx.SaveTrust:
                        _iSavePending = true;
                        break;

                    // 之前的保存（无论是否已完成）都被撤销
                    case Fx.DeleteTrust:
                        ITrusted = false;
                        _iSavePending = false;
                        break;
                    case Fx.StageTrust or Fx.CommitTrust or Fx.DiscardStagedTrust:
                        Assert.Fail($"发起方不应暂存或提交信任：{f}");
                        break;
                }
            }

            if (_requestAt is null && Ini.Session is Ps.AwaitingPeer)
            {
                _requestAt = _now;
            }
        }

        private void ApplyR(PairingEvent e)
        {
            var step = PairingReducer.Reduce(Rec, e);
            Rec = step.State;
            var fromComplete = e is Pe.MessageReceived { Msg: PairComplete };
            foreach (var f in step.Effects)
            {
                switch (f)
                {
                    case Fx.Send send when CanSend():
                        _toI.Enqueue(send.Msg);
                        break;
                    case Fx.Close:
                        _rClosed = true;
                        _toR.Clear();
                        break;
                    case Fx.StageTrust:
                        RStaged = true;
                        break;
                    case Fx.CommitTrust:
                        Assert.True(fromComplete, "接收方只在收到 PAIR_COMPLETE 后提交信任");
                        _rCommitPending = true;
                        break;
                    case Fx.DiscardStagedTrust:
                        RStaged = false;
                        break;
                    case Fx.SaveTrust or Fx.DeleteTrust:
                        Assert.Fail($"接收方不应直接保存或删除信任：{f}");
                        break;
                }
            }
        }

        private void DeliverToR()
        {
            if (!_connected || _rClosed)
            {
                return;
            }

            if (_toR.TryDequeue(out var m))
            {
                if (m is PairRequest req)
                {
                    RGotRequest = true;
                    _receivedAt = _now;
                    ApplyR(new Pe.PairRequestReceived(_conn, IInfo, RInfo.PublicKey, req.Commitment, _nR, _now));
                }
                else
                {
                    ApplyR(new Pe.MessageReceived(_conn, m, _now));
                }
            }
            else if ((_iClosed || _broken) && !_rNotified)
            {
                _rNotified = true;
                ApplyR(new Pe.Disconnected(_conn, _now));
            }
        }

        private void DeliverToI()
        {
            if (!_connected || _iClosed)
            {
                return;
            }

            if (_toI.TryDequeue(out var m))
            {
                ApplyI(new Pe.MessageReceived(_conn, m, _now));
            }
            else if ((_rClosed || _broken) && !_iNotified)
            {
                _iNotified = true;
                ApplyI(new Pe.Disconnected(_conn, _now));
            }
        }

        public void Act(PairAct a)
        {
            switch (a)
            {
                case PairAct.Connect when Ini.Session is Ps.Connecting:
                    _connected = true;
                    ApplyI(new Pe.Connected(_conn, RInfo, IInfo.PublicKey, _nI, _now));
                    break;
                case PairAct.ConnectFail when Ini.Session is Ps.Connecting:
                    ApplyI(new Pe.ConnectFailed(_now));
                    break;
                case PairAct.ToR:
                    DeliverToR();
                    break;
                case PairAct.ToI:
                    DeliverToI();
                    break;
                case PairAct.Accept:
                    ApplyR(new Pe.UserDecision(true, _now));
                    break;
                case PairAct.Reject:
                    ApplyR(new Pe.UserDecision(false, _now));
                    break;
                case PairAct.Cancel:
                    ApplyI(new Pe.UserCancel(_now));
                    break;
                case PairAct.Advance adv:
                    _now += adv.Dt;
                    ApplyI(new Pe.Tick(_now));
                    ApplyR(new Pe.Tick(_now));
                    break;
                case PairAct.Break when _connected && !_broken:
                    _broken = true;
                    _toR.Clear();
                    _toI.Clear();
                    if (!_iClosed && !_iNotified)
                    {
                        _iNotified = true;
                        ApplyI(new Pe.Disconnected(_conn, _now));
                    }

                    if (!_rClosed && !_rNotified)
                    {
                        _rNotified = true;
                        ApplyR(new Pe.Disconnected(_conn, _now));
                    }

                    break;
                case PairAct.Store { Initiator: true } st when _iSavePending:
                    _iSavePending = false;
                    if (st.Ok)
                    {
                        ITrusted = true;
                    }

                    ApplyI(new Pe.TrustStored(RInfo.DeviceId, st.Ok));
                    break;
                case PairAct.Store { Initiator: false } st when _rCommitPending:
                    _rCommitPending = false;
                    if (st.Ok)
                    {
                        RTrusted = true;
                        RStaged = false;
                    }

                    ApplyR(new Pe.TrustStored(IInfo.DeviceId, st.Ok));
                    break;
            }

            CheckDeadlines();
        }

        /// <summary>时间只随 Advance 推进且每次都送 Tick，因此满 60 s 后不能再有未结束的请求（7.7）。</summary>
        private void CheckDeadlines()
        {
            if (_requestAt is { } sent && _now >= sent + 60_000)
            {
                Assert.False(Ini.Session is Ps.AwaitingPeer or Ps.ShowingSas, "发起方在 PAIR_REQUEST 满 60 s 后仍在等待");
            }

            if (_receivedAt is { } got && _now >= got + 60_000)
            {
                Assert.False(Rec.Session is Ps.AwaitingUser, "接收方在收到请求满 60 s 后仍在显示");
            }
        }

        private bool DisconnectPending() => _connected &&
            (((_iClosed || _broken) && !_rClosed && !_rNotified) || ((_rClosed || _broken) && !_iClosed && !_iNotified));

        /// <summary>排空：投递全部在途消息、完成全部存储操作，再推进时间，直到双方都没有进行中的配对。</summary>
        public void Drain()
        {
            for (var round = 0; round < 100; round++)
            {
                for (var guard = 0; guard < 50 && (_toR.Count > 0 || _toI.Count > 0 || DisconnectPending()); guard++)
                {
                    DeliverToR();
                    DeliverToI();
                }

                Act(new PairAct.Store(true, true));
                Act(new PairAct.Store(false, true));
                if (Ini.Session is null && Rec.Session is null && !_iSavePending && !_rCommitPending)
                {
                    return;
                }

                Act(new PairAct.Advance(16_000));
            }
        }
    }

    private static readonly PairingResult[] NegativeResults = [PairingResult.Rejected, PairingResult.Cancelled, PairingResult.Timeout];

    /// <summary>运行一个用例并检查 Property 35，返回发起方结局。</summary>
    private static PairingResult RunPairing(int seed)
    {
        var r = new Random(seed);
        var sim = new PairSim(r);
        sim.Start();
        foreach (var a in PairScript(r))
        {
            sim.Act(a);
        }

        sim.Drain();
        var clue = $"seed={seed}";
        Assert.True(sim.Ini.Session is null, clue);
        Assert.True(sim.Rec.Session is null, clue);
        var iResult = sim.Ini.Last?.Result;
        var rResult = sim.Rec.Last?.Result;
        Assert.True(iResult is not null, clue);
        if (sim.RGotRequest)
        {
            Assert.True(rResult is not null, clue);
        }

        Assert.True(sim.ITrusted == (iResult == PairingResult.Success), $"{clue} 发起方 {iResult}");
        Assert.True(sim.RTrusted == (rResult == PairingResult.Success), $"{clue} 接收方 {rResult}");
        Assert.False(sim.RStaged, clue);
        if ((iResult is { } ir && NegativeResults.Contains(ir)) || (rResult is { } rr && NegativeResults.Contains(rr)))
        {
            Assert.False(sim.ITrusted || sim.RTrusted, clue);
        }

        return iResult!.Value;
    }

    // Feature: lan-file-transfer, Property 35: 配对结局与信任写入一致
    // Validates: Requirements 7.4, 7.6, 7.7, 7.10, 7.12
    [PbtProperty(1000)]
    public Property Property35_PairingOutcomeMatchesTrust() => Prop.ForAll(Arb.From(Seeds), seed =>
    {
        RunPairing(seed);
    });

    [Fact]
    public void Property35_CoversEveryOutcome()
    {
        var seen = Enumerable.Range(0, 1000).Select(RunPairing).ToHashSet();
        PairingResult[] required =
        [
            PairingResult.Success, PairingResult.Rejected, PairingResult.Timeout,
            PairingResult.Cancelled, PairingResult.Disconnected, PairingResult.ConnectFailed,
        ];
        foreach (var result in required)
        {
            Assert.Contains(result, seen);
        }
    }

    // ---- Property 36：接收方自动拒绝 ----

    private abstract record RecvAct
    {
        public sealed record Request(int K) : RecvAct;

        public sealed record Reveal(bool Good, bool Stale) : RecvAct;

        public sealed record Decide(bool Accept) : RecvAct;

        public sealed record Complete : RecvAct;

        public sealed record StoreDone(bool Ok) : RecvAct;

        public sealed record Drop : RecvAct;

        public sealed record PeerCancel : RecvAct;

        public sealed record Advance(long Dt) : RecvAct;
    }

    private static RecvAct RandomRecvAct(Random r) => r.Next(19) switch
    {
        <= 3 => new RecvAct.Request(r.Next(3)),
        <= 6 => new RecvAct.Reveal(r.Next(8) != 0, r.Next(8) == 0),
        <= 9 => new RecvAct.Decide(r.Next(3) != 0),
        10 or 11 => new RecvAct.Complete(),
        12 or 13 => new RecvAct.StoreDone(r.Next(4) != 0),
        14 => new RecvAct.Drop(),
        15 => new RecvAct.PeerCancel(),
        _ => new RecvAct.Advance(r.Pick(r.NextInt64(0, 5_000), r.NextInt64(14_000, 16_000), r.NextInt64(59_000, 61_000))),
    };

    /// <summary>参考模型：无 / 显示中（是否已揭示）/ 等待 PAIR_COMPLETE / 提交中。</summary>
    private abstract record RecvModel
    {
        public sealed record Idle : RecvModel;

        public sealed record Showing(int K, ConnId Conn, Bytes NI, bool Revealed, long Deadline) : RecvModel;

        public sealed record Completing(int K, ConnId Conn, long Deadline) : RecvModel;

        public sealed record Saving(int K, ConnId Conn) : RecvModel;
    }

    private static ConnId RecvConn(RecvModel m) => m switch
    {
        RecvModel.Showing x => x.Conn,
        RecvModel.Completing x => x.Conn,
        RecvModel.Saving x => x.Conn,
        _ => new ConnId(0),
    };

    private static int RecvPeerIndex(RecvModel m) => m switch
    {
        RecvModel.Showing x => x.K,
        RecvModel.Completing x => x.K,
        RecvModel.Saving x => x.K,
        _ => 0,
    };

    private static bool Matches(Ps? s, RecvModel m, PeerInfo[] peers) => m switch
    {
        RecvModel.Showing x => s is Ps.AwaitingUser u && u.Peer == peers[x.K].DeviceId && u.Conn == x.Conn && (u.Sas is not null) == x.Revealed && u.Deadline == x.Deadline,
        RecvModel.Completing x => s is Ps.AwaitingComplete c && c.Peer == peers[x.K].DeviceId && c.Conn == x.Conn && c.Role == PairingRole.Receiver && c.Deadline == x.Deadline,
        RecvModel.Saving x => s is Ps.Saving v && v.Peer == peers[x.K].DeviceId && v.Conn == x.Conn && v.Role == PairingRole.Receiver,
        _ => s is null,
    };

    private static Fx.Finished Finished(PeerInfo p, PairingResult result) => new(new PairingDone(p.DeviceId, PairingRole.Receiver, result));

    private static void RunAutoReject(int seed)
    {
        var r = new Random(seed);
        var local = PeerInfoOf(2);
        var peers = Enumerable.Range(0, 3).Select(k => PeerInfoOf(10 + k)).ToArray();
        var nR = r.RandomBytes(32);
        var s = new PairingState();
        RecvModel model = new RecvModel.Idle();
        var rejectedAt = new Dictionary<int, long>();
        var now = 0L;
        var nextConn = 100L;
        var count = r.Next(1, 61);
        for (var n = 0; n < count; n++)
        {
            var a = RandomRecvAct(r);
            var before = model;
            var curConn = RecvConn(before);
            var curPeer = peers[RecvPeerIndex(before)];
            PairingEvent ev;
            PairingEffect[] expected;
            switch (a)
            {
                case RecvAct.Request req:
                {
                    var conn = new ConnId(nextConn++);
                    var info = peers[req.K];
                    var nI = r.RandomBytes(32);
                    var c = Bytes.Of(VerificationCode.Commitment(info.PublicKey.ToArray(), local.PublicKey.ToArray(), nI.ToArray()));
                    ev = new Pe.PairRequestReceived(conn, info, local.PublicKey, c, nR, now);
                    if (rejectedAt.TryGetValue(req.K, out var rejected) && now - rejected < 60_000)
                    {
                        expected = [new Fx.Send(conn, new PairDecision(PairDecisionCode.Rejected)), new Fx.Close(conn), new Fx.AutoRejected(info.DeviceId, PairingResult.AutoRejected)];
                    }
                    else if (before is not RecvModel.Idle)
                    {
                        expected = [new Fx.Send(conn, new PairDecision(PairDecisionCode.Busy)), new Fx.Close(conn), new Fx.AutoRejected(info.DeviceId, PairingResult.Busy)];
                    }
                    else
                    {
                        model = new RecvModel.Showing(req.K, conn, nI, false, now + 60_000);
                        expected = [new Fx.Send(conn, new PairNonce(nR)), new Fx.ShowRequest(info), new Fx.ArmTimer(now + 60_000)];
                    }

                    break;
                }

                case RecvAct.Reveal rv:
                {
                    var sh = before as RecvModel.Showing;
                    var conn = rv.Stale ? new ConnId(1) : curConn;
                    var nonce = rv.Good && sh is not null ? sh.NI : r.RandomBytes(32);
                    ev = new Pe.MessageReceived(conn, new PairReveal(nonce), now);
                    if (rv.Stale || sh is null || sh.Revealed)
                    {
                        expected = [];
                    }
                    else if (rv.Good)
                    {
                        model = sh with { Revealed = true };
                        expected = [new Fx.ShowSas(VerificationCode.Sas(peers[sh.K].PublicKey.ToArray(), local.PublicKey.ToArray(), sh.NI.ToArray(), nR.ToArray()))];
                    }
                    else
                    {
                        model = new RecvModel.Idle();
                        expected = [new Fx.DismissRequest(), new Fx.Close(conn), Finished(peers[sh.K], PairingResult.CodeMismatch)];
                    }

                    break;
                }

                case RecvAct.Decide d:
                {
                    ev = new Pe.UserDecision(d.Accept, now);
                    if (before is not RecvModel.Showing { Revealed: true } sh)
                    {
                        expected = [];
                    }
                    else if (d.Accept)
                    {
                        model = new RecvModel.Completing(sh.K, sh.Conn, now + 15_000);
                        expected = [new Fx.StageTrust(peers[sh.K]), new Fx.Send(sh.Conn, new PairDecision(PairDecisionCode.Accepted)), new Fx.ArmTimer(now + 15_000)];
                    }
                    else
                    {
                        model = new RecvModel.Idle();
                        rejectedAt[sh.K] = now;
                        expected = [new Fx.Send(sh.Conn, new PairDecision(PairDecisionCode.Rejected)), new Fx.Close(sh.Conn), Finished(peers[sh.K], PairingResult.Rejected)];
                    }

                    break;
                }

                case RecvAct.Complete:
                {
                    ev = new Pe.MessageReceived(curConn, PairComplete.Instance, now);
                    if (before is RecvModel.Completing c)
                    {
                        model = new RecvModel.Saving(c.K, c.Conn);
                        expected = [new Fx.CommitTrust(peers[c.K].DeviceId)];
                    }
                    else
                    {
                        expected = [];
                    }

                    break;
                }

                case RecvAct.StoreDone sd:
                {
                    ev = new Pe.TrustStored(curPeer.DeviceId, sd.Ok);
                    if (before is RecvModel.Saving sv)
                    {
                        model = new RecvModel.Idle();
                        expected = sd.Ok
                            ? [new Fx.PromoteSession(sv.Conn), Finished(peers[sv.K], PairingResult.Success)]
                            : [new Fx.DiscardStagedTrust(peers[sv.K].DeviceId), new Fx.Close(sv.Conn), Finished(peers[sv.K], PairingResult.StoreFailed)];
                    }
                    else
                    {
                        expected = [];
                    }

                    break;
                }

                case RecvAct.Drop:
                {
                    ev = new Pe.Disconnected(curConn, now);
                    switch (before)
                    {
                        case RecvModel.Showing x:
                            model = new RecvModel.Idle();
                            expected = [new Fx.DismissRequest(), Finished(peers[x.K], PairingResult.Disconnected)];
                            break;
                        case RecvModel.Completing x:
                            model = new RecvModel.Idle();
                            expected = [new Fx.DiscardStagedTrust(peers[x.K].DeviceId), Finished(peers[x.K], PairingResult.Disconnected)];
                            break;
                        default:
                            expected = [];
                            break;
                    }

                    break;
                }

                case RecvAct.PeerCancel:
                {
                    ev = new Pe.MessageReceived(curConn, new PairCancel(PairCancelReason.User), now);
                    switch (before)
                    {
                        case RecvModel.Showing x:
                            model = new RecvModel.Idle();
                            expected = [new Fx.DismissRequest(), new Fx.Close(x.Conn), Finished(peers[x.K], PairingResult.Cancelled)];
                            break;
                        case RecvModel.Completing x:
                            model = new RecvModel.Idle();
                            expected = [new Fx.DiscardStagedTrust(peers[x.K].DeviceId), new Fx.Close(x.Conn), Finished(peers[x.K], PairingResult.Cancelled)];
                            break;
                        default:
                            expected = [];
                            break;
                    }

                    break;
                }

                case RecvAct.Advance adv:
                {
                    now += adv.Dt;
                    ev = new Pe.Tick(now);
                    switch (before)
                    {
                        case RecvModel.Showing x when now >= x.Deadline:
                            model = new RecvModel.Idle();
                            expected =
                            [
                                new Fx.Send(x.Conn, new PairDecision(PairDecisionCode.Timeout)), new Fx.DismissRequest(), new Fx.Close(x.Conn),
                                Finished(peers[x.K], PairingResult.Timeout),
                            ];
                            break;
                        case RecvModel.Completing x when now >= x.Deadline:
                            model = new RecvModel.Idle();
                            expected = [new Fx.DiscardStagedTrust(peers[x.K].DeviceId), new Fx.Close(x.Conn), Finished(peers[x.K], PairingResult.Disconnected)];
                            break;
                        default:
                            expected = [];
                            break;
                    }

                    break;
                }

                default:
                    throw new InvalidOperationException($"unknown act {a}");
            }

            var step = PairingReducer.Reduce(s, ev);
            s = step.State;
            var clue = $"seed={seed} act={a} model={before}";
            Assert.True(expected.SequenceEqual(step.Effects), $"{clue} effects=[{string.Join(", ", step.Effects)}] expected=[{string.Join(", ", expected)}]");
            Assert.True(Matches(s.Session, model, peers), $"{clue} session={s.Session} expectedModel={model}");

            // 只有空闲时才会显示新请求，所以任一时刻显示中的请求至多 1 个
            Assert.Equal(before is RecvModel.Idle && model is RecvModel.Showing, step.Effects.Any(x => x is Fx.ShowRequest));
        }
    }

    // Feature: lan-file-transfer, Property 36: 配对请求自动拒绝
    // Validates: Requirements 7.8, 7.9
    [PbtProperty(1000)]
    public Property Property36_AutoReject() => Prop.ForAll(Arb.From(Seeds), RunAutoReject);

    // ---- Property 37：文字发送 ----

    private abstract record TextAct
    {
        public sealed record Send(int K) : TextAct;

        public sealed record Retry(int K) : TextAct;

        public sealed record Ack(int K) : TextAct;

        public sealed record Corrupt(int K) : TextAct;

        public sealed record Advance(long Dt) : TextAct;

        public sealed record Restart : TextAct;
    }

    private static TextAct RandomTextAct(Random r) => r.Next(14) switch
    {
        <= 2 => new TextAct.Send(r.Next(3)),
        3 or 4 => new TextAct.Retry(r.Next(3)),
        <= 8 => new TextAct.Ack(r.Next(3)),
        9 => new TextAct.Corrupt(r.Next(3)),
        <= 12 => new TextAct.Advance(r.Pick(r.NextInt64(0, 3_000), r.NextInt64(9_000, 11_000), r.NextInt64(20_000, 100_000))),
        _ => new TextAct.Restart(),
    };

    private sealed record TextModel(TextStatus Status, long? Deadline);

    private static readonly TextKey[] TextKeys = Enumerable.Range(0, 3)
        .Select(k => new TextKey(DeviceIdOf(20 + (k % 2)), new MessageId(Bytes16.Of(Enumerable.Range(0, 16).Select(b => (byte)((k * 16) + b)).ToArray()))))
        .ToArray();

    private static readonly Bytes[] TextBodies = new[] { "a", "你好\n世界", "😀 \0" }.Select(t => Bytes.Of(Encoding.UTF8.GetBytes(t))).ToArray();

    private static readonly long[] SentAt = [1_700_000_000_000, 1_700_000_000_001, 1_700_000_000_002];

    private static Xf.Transmit TransmitOf(int k) => new(TextKeys[k].Peer, new Text(TextKeys[k].MessageId, SentAt[k], TextBodies[k]));

    private static void RunText(int seed)
    {
        var r = new Random(seed);
        var received = new TextRecord(
            99, DeviceIdOf(30), "对方", new MessageId(Bytes16.Of(Enumerable.Repeat((byte)0x77, 16).ToArray())), Direction.Receive,
            TextBodies[0], 0, new RecordTime(0, 480), TextStatus.Received);
        var s = new TextState();
        var model = new Dictionary<int, TextModel>();
        var now = 0L;
        var count = r.Next(1, 60);
        for (var n = 0; n < count; n++)
        {
            var a = RandomTextAct(r);
            var expected = new List<TextEffect>();
            Step<TextState, TextEffect> step;
            switch (a)
            {
                case TextAct.Send x:
                    if (!model.ContainsKey(x.K))
                    {
                        model[x.K] = new TextModel(TextStatus.Sending, now + 10_000);
                        expected.Add(TransmitOf(x.K));
                        expected.Add(new Xf.ArmTimer(now + 10_000));
                    }

                    step = TextReducer.Reduce(s, new Xe.Send(TextKeys[x.K], TextBodies[x.K], SentAt[x.K], now));
                    break;
                case TextAct.Retry x:
                    if (model.TryGetValue(x.K, out var m1) && m1.Status == TextStatus.Failed)
                    {
                        model[x.K] = new TextModel(TextStatus.Sending, now + 10_000);
                        expected.Add(new Xf.SaveStatus(TextKeys[x.K], TextStatus.Sending));
                        expected.Add(TransmitOf(x.K));
                        expected.Add(new Xf.ArmTimer(now + 10_000));
                    }

                    step = TextReducer.Reduce(s, new Xe.Retry(TextKeys[x.K], now));
                    break;
                case TextAct.Ack x:
                    if (model.TryGetValue(x.K, out var m2) && m2.Status is TextStatus.Sending or TextStatus.Failed)
                    {
                        model[x.K] = new TextModel(TextStatus.Delivered, null);
                        expected.Add(new Xf.SaveStatus(TextKeys[x.K], TextStatus.Delivered));
                    }

                    step = TextReducer.Reduce(s, new Xe.AckReceived(TextKeys[x.K]));
                    break;
                case TextAct.Corrupt x:
                    if (model.TryGetValue(x.K, out var m3) && m3.Status == TextStatus.Sending)
                    {
                        model[x.K] = new TextModel(TextStatus.Failed, null);
                        expected.Add(new Xf.SaveStatus(TextKeys[x.K], TextStatus.Failed));
                    }

                    step = TextReducer.Reduce(s, new Xe.CorruptReceived(TextKeys[x.K]));
                    break;
                case TextAct.Advance x:
                    now += x.Dt;
                    foreach (var (k, m) in model.ToList())
                    {
                        if (m.Status == TextStatus.Sending && m.Deadline is { } d && now >= d)
                        {
                            model[k] = new TextModel(TextStatus.Failed, null);
                            expected.Add(new Xf.SaveStatus(TextKeys[k], TextStatus.Failed));
                        }
                    }

                    step = TextReducer.Reduce(s, new Xe.Tick(now));
                    break;
                default: // Restart
                {
                    var records = model
                        .Select(kv => new TextRecord(kv.Key, TextKeys[kv.Key].Peer, "对方", TextKeys[kv.Key].MessageId, Direction.Send, TextBodies[kv.Key], SentAt[kv.Key], new RecordTime(0, 480), kv.Value.Status))
                        .Append(received)
                        .ToList();
                    foreach (var (k, m) in model.ToList())
                    {
                        if (m.Status == TextStatus.Sending)
                        {
                            expected.Add(new Xf.SaveStatus(TextKeys[k], TextStatus.Failed));
                        }

                        model[k] = new TextModel(m.Status == TextStatus.Sending ? TextStatus.Failed : m.Status, null);
                    }

                    step = TextReducer.Reduce(new TextState(), new Xe.Restored(new ValueList<TextRecord>(records)));
                    break;
                }
            }

            s = step.State;
            var clue = $"seed={seed} act={a}";
            Assert.True(new HashSet<TextEffect>(step.Effects).SetEquals(expected), $"{clue} effects=[{string.Join(", ", step.Effects)}] expected=[{string.Join(", ", expected)}]");
            Assert.Equal(expected.Count, step.Effects.Count);
            Assert.Equal(model.Count, s.Outgoing.Count);
            for (var k = 0; k < 3; k++)
            {
                s.Outgoing.TryGetValue(TextKeys[k], out var o);
                model.TryGetValue(k, out var m);
                Assert.Equal(m?.Status, o?.Status);
                Assert.Equal(m?.Deadline, o?.Deadline);
                Assert.Equal(m is null ? null : m.Status == TextStatus.Failed, o?.ShowRetry);
                if (o is not null)
                {
                    Assert.Equal(TextKeys[k], o.Key);
                    Assert.Equal(TextBodies[k], o.Body);
                    Assert.Equal(SentAt[k], o.SentAtUtcMs);
                }
            }
        }
    }

    // Feature: lan-file-transfer, Property 37: 文字发送状态迁移
    // Validates: Requirements 11.5, 11.6, 11.7, 12.12
    [PbtProperty(1000)]
    public Property Property37_TextStatusTransitions() => Prop.ForAll(Arb.From(Seeds), RunText);

    // ---- Property 38：传输任务 ----

    private static readonly HashSet<FileState> ActiveFileStates = [FileState.Transferring, FileState.Verifying];
    private static readonly HashSet<FileState> DeadEndFileStates = [FileState.ReadFailed, FileState.ChecksumFailed];
    private static readonly HashSet<TaskState> PendingTaskStates = [TaskState.Running, TaskState.Resuming, TaskState.Offering, TaskState.AwaitingUser];
    private static readonly HashSet<TaskState> AcceptedTaskStates = [TaskState.Running, TaskState.Interrupted, TaskState.Resuming];
    private static readonly InterruptReason[] InterruptReasons = Enum.GetValues<InterruptReason>();
    private static readonly OfferDecision[] OfferDecisions = Enum.GetValues<OfferDecision>();

    private static ValueList<ResumePlanEntry> RandomPlan(Random r, TransferTask t)
    {
        if (t.Direction == Direction.Send)
        {
            var state = t.Files.Select(file => new ResumeFileState((uint)file.Index, (ResumeFileStatus)r.Next(4), (ulong)r.NextInt64(0, file.Size + 2))).ToList();
            var stats = t.Files.Select(file => (SourceStat?)(r.Next(5) == 0 ? new SourceStat(file.Size + 1, file.Mtime) : new SourceStat(file.Size, file.Mtime))).ToList();
            return new ValueList<ResumePlanEntry>(ResumePlanner.Plan(t, state, stats));
        }

        return new ValueList<ResumePlanEntry>(t.Files.Select(file => r.Next(3) switch
        {
            0 => new ResumePlanEntry(file.Index, ResumeAction.Skip, 0),
            1 => new ResumePlanEntry(file.Index, ResumeAction.Continue, r.NextInt64(0, file.Size + 1)),
            _ => new ResumePlanEntry(file.Index, ResumeAction.Restart, 0, file.Size + r.NextInt64(0, 3), file.Mtime),
        }).ToList());
    }

    private static TransferEvent CommonEvent(Random r, TransferTask t) => r.Next(14) switch
    {
        0 => new Te.OfferTimedOut(),
        <= 3 => new Te.Disconnected(InterruptReasons[r.Next(InterruptReasons.Length)]),
        4 => new Te.LocalCancel(r.Next(2) == 0, r.Next(4) == 0 ? TransferCancelReason.Unpaired : TransferCancelReason.User),
        5 => new Te.PeerCancelled(r.Next(2) == 0 ? TransferCancelReason.User : TransferCancelReason.UnknownTransfer),
        <= 8 => new Te.ResumeRequested(),
        9 => new Te.ResumeFailed(InterruptReason.PeerUnresponsive),
        <= 12 => new Te.ResumePlanned(RandomPlan(r, t)),
        _ => new Te.Restored(),
    };

    /// <summary>按当前任务现场生成事件：文件序号 70% 取当前文件，其余随机（含不存在的序号）。</summary>
    private static TransferEvent RandomTransferEvent(Random r, TransferTask t)
    {
        if (r.Next(100) < 8)
        {
            return CommonEvent(r, t);
        }

        var cur = t.Files.FirstOrDefault(file => !file.State.IsTerminal());
        var i = cur is not null && r.Next(10) < 7 ? cur.Index : r.Next(t.Files.Count + 1);
        var target = t.Files.FirstOrDefault(file => file.Index == i);
        var size = target?.Size ?? 0;
        var persisted = target?.Persisted ?? 0;
        if (t.Direction == Direction.Send)
        {
            return r.Next(14) switch
            {
                0 or 1 => new Te.OfferReplied(r.Next(10) < 7 ? OfferDecision.Accept : OfferDecisions[r.Next(OfferDecisions.Length)]),
                2 => new Te.SourceReadFailed(i),
                <= 5 => new Te.FileEndSent(i, r.RandomBytes(32)),
                <= 8 => new Te.AckReceived(i, r.NextInt64(0, size + 1)),
                _ => new Te.ResultReceived(
                    i,
                    r.Next(10) switch
                    {
                        <= 6 => FileResultStatus.Completed,
                        7 or 8 => FileResultStatus.ChecksumFailed,
                        _ => FileResultStatus.WriteFailed,
                    },
                    "磁盘已满"),
            };
        }

        switch (r.Next(16))
        {
            case 0:
                return new Te.UserAccepted(DirRef.Default.Instance, r.Next(10) != 0);
            case 1:
                return new Te.UserRejected();
            case <= 6:
            {
                var np = r.Next(10) == 0 ? size + 1 : Math.Min(size, persisted + r.NextInt64(1, 70_000));
                return new Te.ChunkWritten(i, Math.Max(np, persisted), FixtureLocalNow, 1_700_000_000_000);
            }

            case <= 9:
                return new Te.EndReceived(i, r.RandomBytes(32), FixtureLocalNow);
            case <= 12:
                return new Te.VerifyDone(i, r.Next(10) switch
                {
                    <= 6 => new VerifyOutcome.Committed($"f{i}.bin", $"D:\\接收\\f{i}.bin", new StoredFileRef($"D:\\接收\\f{i}.bin")),
                    7 or 8 => new VerifyOutcome.ChecksumMismatch(),
                    _ => new VerifyOutcome.CommitFailed("重命名失败"),
                });
            case 13:
                return new Te.WriteFailed(i, "存储空间不足");
            default:
                return new Te.PeerReadFailed(i);
        }
    }

    private static int IndexOfFirst(IReadOnlyList<TransferFile> files, Func<TransferFile, bool> match)
    {
        for (var i = 0; i < files.Count; i++)
        {
            if (match(files[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static void CheckTransferStep(TransferTask before, TransferEvent e, Step<TransferTask, TransferEffect> step, string clue)
    {
        var after = step.State;
        if (before.State.IsTerminal())
        {
            Assert.True(before == after && step.Effects.Count == 0, $"{clue} 终态任务不再迁移");
            return;
        }

        var files = after.Files;
        Assert.Equal(before.Files.Count, files.Count);
        Assert.True(files.Count(f => ActiveFileStates.Contains(f.State)) <= 1, $"{clue} 同一任务至多 1 个文件处于 TRANSFERRING 或 VERIFYING");
        var cur = IndexOfFirst(files, f => !f.State.IsTerminal());
        if (cur >= 0)
        {
            Assert.True(files.Skip(cur + 1).All(f => f.State == FileState.Waiting || f.State.IsTerminal()), $"{clue} 当前文件之后的文件尚未开始");
        }

        Assert.True((after.Interrupt is not null) == (after.State == TaskState.Interrupted), clue);
        if (after.State == TaskState.Completed)
        {
            Assert.Equal(-1, cur);
        }

        if (e is Te.Restored)
        {
            Assert.False(PendingTaskStates.Contains(after.State), $"{clue} 启动恢复后不存在进行中或等待响应的任务");
        }

        if ((e is Te.LocalCancel or Te.PeerCancelled) && AcceptedTaskStates.Contains(before.State))
        {
            Assert.True(before.Files.Equals(files), $"{clue} 取消时保留已完成的文件");
            var deletes = step.Effects.OfType<Tf.DeleteTemp>().Select(d => d.Index).Order().ToList();
            List<int> expected = before.Direction == Direction.Receive
                ? before.Files.Where(f => !f.State.IsTerminal()).Select(f => f.Index).Order().ToList()
                : [];
            Assert.Equal(expected, deletes);
        }

        var newlyDead = Enumerable.Range(0, files.Count).Any(i => !before.Files[i].State.IsTerminal() && DeadEndFileStates.Contains(files[i].State));
        if (newlyDead)
        {
            Assert.True(after.State is TaskState.Running or TaskState.Completed, $"{clue} 读取失败或校验失败后继续下一个文件");
            if (after.Direction == Direction.Send && after.State == TaskState.Running)
            {
                Assert.Equal(FileState.Transferring, files[cur].State);
                Assert.Contains((TransferEffect)new Tf.StartFile(files[cur].Index, files[cur].Persisted), step.Effects);
            }
        }

        foreach (var sf in step.Effects.OfType<Tf.StartFile>())
        {
            Assert.NotEqual(-1, cur);
            Assert.Equal(files[cur].Index, sf.Index);
            Assert.Equal(FileState.Transferring, files[cur].State);
        }
    }

    private static void RunTransfer(int seed, ISet<TaskState>? seenTask = null, ISet<FileState>? seenFile = null)
    {
        var r = new Random(seed);
        var direction = r.Next(2) == 0 ? Direction.Send : Direction.Receive;
        var sizes = Enumerable.Range(0, r.Next(1, 6)).Select(_ => r.Pick(0, 1, 100, 70_000, r.NextInt64(0, 200_000))).ToArray();
        var initial = direction == Direction.Send ? TaskState.Offering : r.Next(5) == 0 ? TaskState.Running : TaskState.AwaitingUser;
        var t = MakeTask(direction, initial, sizes);
        seenTask?.Add(t.State);
        var count = r.Next(1, 80);
        for (var n = 0; n < count; n++)
        {
            var e = RandomTransferEvent(r, t);
            var step = TransferReducer.Reduce(t, e);
            CheckTransferStep(t, e, step, $"seed={seed} event={e} before={t.State} files=[{string.Join(", ", t.Files.Select(f => f.State))}]");
            t = step.State;
            seenTask?.Add(t.State);
            foreach (var f in t.Files)
            {
                seenFile?.Add(f.State);
            }
        }
    }

    // Feature: lan-file-transfer, Property 38: 传输任务归约不变量
    // Validates: Requirements 14.6, 14.7, 17.2, 17.9, 18.3
    [PbtProperty(1000)]
    public Property Property38_TransferInvariants() => Prop.ForAll(Arb.From(Seeds), seed => RunTransfer(seed));

    [Fact]
    public void Property38_CoversEveryState()
    {
        var seenTask = new HashSet<TaskState>();
        var seenFile = new HashSet<FileState>();
        for (var seed = 0; seed < 1000; seed++)
        {
            RunTransfer(seed, seenTask, seenFile);
        }

        Assert.True(Enum.GetValues<TaskState>().All(seenTask.Contains), $"seen task states: {string.Join(", ", seenTask)}");
        Assert.True(Enum.GetValues<FileState>().All(seenFile.Contains), $"seen file states: {string.Join(", ", seenFile)}");
    }
}
