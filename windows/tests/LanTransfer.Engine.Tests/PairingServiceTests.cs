using LanTransfer.Domain;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>
/// PairingService 的单元测试（任务 16.3，与 Kotlin 侧 PairingServiceTest 对应）。C# 的定时器在线程池上触发，
/// 长时间推进 FakeClock 时按小步推进（<see cref="StepAsync"/>），使心跳在虚拟时间里照常发出。
/// </summary>
public sealed class PairingServiceTests
{
    private static readonly LocalInterface IfA = Kit.Iface("wlan0", 192, 168, 1, 2);
    private static readonly LocalInterface IfB = Kit.Iface("wlan0", 192, 168, 1, 3);
    private static readonly LocalInterface IfC = Kit.Iface("wlan0", 192, 168, 1, 4);

    /// <summary>一台带 PairingService 的测试设备；Notices 收集全部配对提示，Qr 收集二维码界面输出（调用 ShowQr 后）。</summary>
    private sealed class PairDevice
    {
        private readonly List<PairingNotice> _notices = [];
        private readonly List<QrScreenState> _qr = [];

        public PairDevice(TestDevice d, FakeClock clock, int seed)
        {
            D = d;
            Tokens = new PairingTokenRegistry(new SeededRng(seed));
            Service = new PairingService(
                d.Manager,
                d.Trust,
                Tokens,
                clock,
                new SeededRng(seed + 100),
                d.Identity,
                d.Transport.Monitor,
                listenPort: () => d.Port == 0 ? null : d.Port,
                cancelTransfers: id =>
                {
                    lock (Cancelled)
                    {
                        Cancelled.Add(id);
                    }

                    return Task.CompletedTask;
                });
            Service.Notices.Subscribe(n =>
            {
                lock (_notices)
                {
                    _notices.Add(n);
                }
            });
        }

        public TestDevice D { get; }

        public PairingTokenRegistry Tokens { get; }

        public PairingService Service { get; }

        public List<DeviceId> Cancelled { get; } = [];

        public IReadOnlyList<PairingNotice> Notices
        {
            get
            {
                lock (_notices)
                {
                    return [.. _notices];
                }
            }
        }

        public IReadOnlyList<QrScreenState> Qr
        {
            get
            {
                lock (_qr)
                {
                    return [.. _qr];
                }
            }
        }

        public QrScreenState.Showing LastQr => Assert.IsType<QrScreenState.Showing>(Qr[^1]);

        public IEnumerable<PairingResult> Finished => Notices.OfType<PairingNotice.Finished>().Select(f => f.Done.Result);

        public void ShowQr() => _ = Task.Run(
            async () =>
            {
                await foreach (var s in Service.QrScreenAsync(Kit.Ct))
                {
                    lock (_qr)
                    {
                        _qr.Add(s);
                    }
                }
            },
            Kit.Ct);

        public async Task<PairingUiState.Request> AwaitRequestAsync() =>
            (PairingUiState.Request)await Kit.Within(Service.State.FirstAsync(s => s is PairingUiState.Request { Sas: not null }, Kit.Ct));
    }

    private sealed class World
    {
        public World()
        {
            Net = new InMemoryNetwork(Clock.Time);
            A = new PairDevice(new TestDevice("A", 1, IfA, Net, Clock), Clock, 1);
            var b = new TestDevice("B", 2, IfB, Net, Clock);
            b.Listen();
            B = new PairDevice(b, Clock, 2);
        }

        public FakeClock Clock { get; } = new();

        public InMemoryNetwork Net { get; }

        public PairDevice A { get; }

        public PairDevice B { get; }

        public Target BAddress => new Target.Address(IfB.Address, B.D.Port);

        public PairDevice C() => new(new TestDevice("C", 3, IfC, Net, Clock), Clock, 3);
    }

    /// <summary>小步推进虚拟时间，每步留出真实时间让定时器的续体（心跳等）运行。</summary>
    private static async Task StepAsync(FakeClock clock, long ms, long step = 250)
    {
        while (ms > 0)
        {
            var d = Math.Min(step, ms);
            clock.AdvanceBy(d);
            ms -= d;
            await Task.Delay(2, Kit.Ct);
        }
    }

    private static string Retoken(string text, Bytes16? token = null, Bytes? fingerprint = null)
    {
        var p = QrCodec.Decode(text).Payload!;
        return QrCodec.Encode(p with { Token = token ?? p.Token, Fingerprint = fingerprint ?? p.Fingerprint });
    }

    private static PairingOutcome Done(PairingResult r, OpenFailure? f = null) => new PairingOutcome.Done(r, f);

    // ---- 点选配对 ----

    [Fact]
    public async Task Pairing_succeeds_with_matching_codes_and_only_initiator_records_address()
    {
        var w = new World();
        var outcome = w.A.Service.PairAsync(w.BAddress);
        var req = await w.B.AwaitRequestAsync();
        var code = (PairingUiState.ShowingCode)await Kit.Within(w.A.Service.State.FirstAsync(s => s is PairingUiState.ShowingCode, Kit.Ct));
        Assert.Equal(code.Sas, req.Sas);
        Assert.Equal(w.A.D.Id, req.Peer.DeviceId);
        w.B.Service.Respond(req.Id, accept: true);
        Assert.Equal(Done(PairingResult.Success), await Kit.Within(outcome));
        await Kit.WaitUntilAsync(() => w.B.D.Trust.Get(w.A.D.Id) is not null && w.B.D.Manager.GetSession(w.A.D.Id) is not null, "receiver committed");
        var ta = w.A.D.Trust.Get(w.B.D.Id)!;
        Assert.Equal(w.B.D.PublicKey, ta.PublicKey);
        Assert.Equal(IfB.Address, ta.LastAddress);
        Assert.Equal(w.B.D.Port, ta.LastPort);
        Assert.False(ta.AutoAccept);
        var tb = w.B.D.Trust.Get(w.A.D.Id)!;
        Assert.Equal(w.A.D.PublicKey, tb.PublicKey);
        Assert.Null(tb.LastAddress);
        var sa = w.A.D.Manager.GetSession(w.B.D.Id)!;
        var sb = w.B.D.Manager.GetSession(w.A.D.Id)!;
        var ack = new TextAck(new MessageId(Bytes16.Of(Enumerable.Repeat((byte)9, 16).ToArray())));
        sa.Send(ack);
        Assert.Equal(ack, await sb.Incoming.ReadAsync(Kit.Ct));
        await Kit.WaitUntilAsync(() => w.B.Finished.Contains(PairingResult.Success), "receiver notice");
        Assert.IsType<PairingUiState.Idle>(w.A.Service.State.Value);
        Assert.IsType<PairingUiState.Idle>(w.B.Service.State.Value);
    }

    [Fact]
    public async Task Manual_connect_to_trusted_device_returns_session_without_pairing_ui()
    {
        var w = new World();
        w.A.D.Trusts(w.B.D);
        w.B.D.Trusts(w.A.D);
        Assert.IsType<PairingOutcome.AlreadyTrusted>(await Kit.Within(w.A.Service.PairAsync(w.BAddress)));
        await Kit.Settle();
        Assert.Empty(w.A.Notices);
        Assert.Empty(w.B.Notices);
    }

    [Fact]
    public async Task Rejected_device_is_auto_rejected_without_ui_for_60s_then_shown_again()
    {
        var w = new World();
        var first = w.A.Service.PairAsync(w.BAddress);
        w.B.Service.Respond((await w.B.AwaitRequestAsync()).Id, accept: false);
        Assert.Equal(Done(PairingResult.Rejected), await Kit.Within(first));
        Assert.Equal(Done(PairingResult.Rejected), await Kit.Within(w.A.Service.PairAsync(w.BAddress)));
        await Kit.WaitUntilAsync(() => w.B.Notices.Contains(new PairingNotice.AutoRejected(w.A.D.Id, PairingResult.AutoRejected)), "auto reject notice");
        Assert.IsType<PairingUiState.Idle>(w.B.Service.State.Value);
        await StepAsync(w.Clock, 60_000);
        var third = w.A.Service.PairAsync(w.BAddress);
        w.B.Service.Respond((await w.B.AwaitRequestAsync()).Id, accept: false);
        Assert.Equal(Done(PairingResult.Rejected), await Kit.Within(third));
        Assert.Null(w.A.D.Trust.Get(w.B.D.Id));
        Assert.Null(w.B.D.Trust.Get(w.A.D.Id));
    }

    [Fact]
    public async Task Unanswered_request_still_waits_at_59999ms_and_times_out_at_60s()
    {
        var w = new World();
        var outcome = w.A.Service.PairAsync(w.BAddress);
        await w.B.AwaitRequestAsync();
        await StepAsync(w.Clock, 59_999);
        await Kit.Settle();
        Assert.False(outcome.IsCompleted);
        Assert.IsType<PairingUiState.Request>(w.B.Service.State.Value);
        w.Clock.AdvanceBy(1);
        Assert.Equal(Done(PairingResult.Timeout), await Kit.Within(outcome));
        await Kit.WaitUntilAsync(() => w.B.Service.State.Value is PairingUiState.Idle && w.B.Finished.Contains(PairingResult.Timeout), "receiver timeout");
    }

    [Fact]
    public async Task Third_device_gets_busy_while_request_is_shown()
    {
        var w = new World();
        var c = w.C();
        var fromA = w.A.Service.PairAsync(w.BAddress);
        var req = await w.B.AwaitRequestAsync();
        Assert.Equal(Done(PairingResult.Busy), await Kit.Within(c.Service.PairAsync(w.BAddress)));
        await Kit.WaitUntilAsync(() => w.B.Notices.Contains(new PairingNotice.AutoRejected(c.D.Id, PairingResult.Busy)), "busy notice");
        Assert.Equal(req.Id, Assert.IsType<PairingUiState.Request>(w.B.Service.State.Value).Id);
        w.B.Service.Respond(req.Id, accept: true);
        Assert.Equal(Done(PairingResult.Success), await Kit.Within(fromA));
    }

    [Fact]
    public async Task Initiator_cancel_dismisses_receiver_ui_and_nobody_saves_trust()
    {
        var w = new World();
        var outcome = w.A.Service.PairAsync(w.BAddress);
        await w.B.AwaitRequestAsync();
        w.A.Service.Cancel();
        Assert.Equal(Done(PairingResult.Cancelled), await Kit.Within(outcome));
        await Kit.WaitUntilAsync(() => w.B.Service.State.Value is PairingUiState.Idle && w.B.Finished.Contains(PairingResult.Cancelled), "receiver cancelled");
        Assert.Null(w.A.D.Trust.Get(w.B.D.Id));
        Assert.Null(w.B.D.Trust.Get(w.A.D.Id));
    }

    [Fact]
    public async Task Initiator_store_failure_closes_and_receiver_discards_staged_trust()
    {
        var w = new World();
        w.A.D.Trust.FailWrites = op => op == "upsert";
        var outcome = w.A.Service.PairAsync(w.BAddress);
        w.B.Service.Respond((await w.B.AwaitRequestAsync()).Id, accept: true);
        Assert.Equal(Done(PairingResult.StoreFailed), await Kit.Within(outcome));
        await Kit.WaitUntilAsync(() => w.B.Finished.Contains(PairingResult.Disconnected), "receiver disconnected");
        Assert.Null(w.B.D.Trust.Get(w.A.D.Id));
        Assert.Empty(w.A.D.Manager.Sessions.Value);
        Assert.Empty(w.B.D.Manager.Sessions.Value);
    }

    [Fact]
    public async Task Connection_loss_while_request_is_shown_dismisses_ui()
    {
        var w = new World();
        var outcome = w.A.Service.PairAsync(w.BAddress);
        await w.B.AwaitRequestAsync();
        Assert.Single(w.Net.Connections).Reset();
        Assert.Equal(Done(PairingResult.Disconnected), await Kit.Within(outcome));
        await Kit.WaitUntilAsync(() => w.B.Service.State.Value is PairingUiState.Idle && w.B.Finished.Contains(PairingResult.Disconnected), "receiver disconnected");
    }

    [Fact]
    public async Task Connect_failure_carries_open_failure()
    {
        var w = new World();
        Assert.Equal(
            Done(PairingResult.ConnectFailed, new OpenFailure.Unreachable()),
            await Kit.Within(w.A.Service.PairAsync(new Target.Address(IfC.Address, 42580))));
    }

    // ---- 扫码配对 ----

    [Fact]
    public async Task Qr_pairing_succeeds_and_used_token_is_refreshed_within_2s()
    {
        var w = new World();
        w.B.ShowQr();
        await Kit.WaitUntilAsync(() => w.B.Qr.Count > 0, "first qr");
        var text = w.B.LastQr.Text;
        Assert.Equal(Done(PairingResult.Success), await Kit.Within(w.A.Service.PairByQrAsync(text)));
        await Kit.WaitUntilAsync(() => w.B.D.Trust.Get(w.A.D.Id) is not null && w.B.Finished.Contains(PairingResult.Success), "display committed");
        Assert.Equal(w.B.D.PublicKey, w.A.D.Trust.Get(w.B.D.Id)!.PublicKey);
        Assert.Equal(w.A.D.PublicKey, w.B.D.Trust.Get(w.A.D.Id)!.PublicKey);
        Assert.Equal(PairingRole.QrDisplay, Assert.Single(w.B.Notices.OfType<PairingNotice.Finished>()).Done.Role);
        await StepAsync(w.Clock, 2_000);
        await Kit.WaitUntilAsync(() => w.B.LastQr.Text != text, "refreshed qr");
        Assert.Equal(Done(PairingResult.TokenInvalid), await Kit.Within(w.A.Service.PairByQrAsync(text))); // 已使用的令牌
    }

    [Fact]
    public async Task Wrong_token_five_failures_and_expiry_invalidate_token_and_refresh_qr()
    {
        var w = new World();
        w.B.ShowQr();
        await Kit.WaitUntilAsync(() => w.B.Qr.Count > 0, "first qr");
        var text = w.B.LastQr.Text;
        Assert.Equal(Done(PairingResult.TokenInvalid), await Kit.Within(w.A.Service.PairByQrAsync(Retoken(text, token: Bytes16.Of(Enumerable.Repeat((byte)7, 16).ToArray())))));
        Assert.Null(w.B.D.Trust.Get(w.A.D.Id));
        for (var i = 0; i < 4; i++)
        {
            await Kit.Within(w.A.Service.PairByQrAsync(Retoken(text, token: Bytes16.Of(Enumerable.Repeat((byte)8, 16).ToArray()))));
        }

        Assert.Equal(Done(PairingResult.TokenInvalid), await Kit.Within(w.A.Service.PairByQrAsync(text))); // 失败 5 次后正确的令牌也失效
        await StepAsync(w.Clock, 2_000);
        await Kit.WaitUntilAsync(() => w.B.LastQr.Text != text, "refreshed after failures");
        var refreshed = w.B.LastQr.Text;
        await StepAsync(w.Clock, (5 * 60_000) - 5_000, step: 1_000);
        Assert.Equal(refreshed, w.B.LastQr.Text); // 5 min 内不刷新
        await StepAsync(w.Clock, 10_000, step: 1_000);
        await Kit.WaitUntilAsync(() => w.B.LastQr.Text != refreshed, "refreshed after expiry");
        var afterExpiry = w.B.LastQr.Text;
        Assert.Equal(Done(PairingResult.TokenInvalid), await Kit.Within(w.A.Service.PairByQrAsync(refreshed))); // 已过期
        Assert.Equal(Done(PairingResult.Success), await Kit.Within(w.A.Service.PairByQrAsync(afterExpiry)));
    }

    [Fact]
    public async Task Fingerprint_mismatch_never_sends_token_and_token_stays_usable()
    {
        var w = new World();
        w.B.ShowQr();
        await Kit.WaitUntilAsync(() => w.B.Qr.Count > 0, "first qr");
        var text = w.B.LastQr.Text;
        Assert.Equal(
            Done(PairingResult.ConnectFailed, new OpenFailure.FingerprintMismatch()),
            await Kit.Within(w.A.Service.PairByQrAsync(Retoken(text, fingerprint: Bytes.Of(new byte[32])))));
        await Kit.Settle();
        Assert.Empty(w.B.Notices);
        Assert.Equal(text, w.B.LastQr.Text);
        Assert.Equal(Done(PairingResult.Success), await Kit.Within(w.A.Service.PairByQrAsync(text)));
    }

    [Fact]
    public async Task Unparsable_qr_fails_and_no_network_is_shown_until_interface_returns()
    {
        var w = new World();
        Assert.IsType<PairingOutcome.InvalidQr>(await w.A.Service.PairByQrAsync("LT1:!!!"));
        w.B.D.Transport.Monitor.Set([]);
        w.B.ShowQr();
        await Kit.WaitUntilAsync(() => w.B.Qr.Count > 0, "first qr");
        Assert.IsType<QrScreenState.NoNetwork>(w.B.Qr[^1]);
        w.B.D.Transport.Monitor.Set([IfB]);
        await StepAsync(w.Clock, 1_000);
        await Kit.WaitUntilAsync(() => w.B.Qr[^1] is QrScreenState.Showing, "qr shown again");
        Assert.Equal(new[] { IfB.Address.Bits }, w.B.LastQr.Payload.Ipv4List);
    }

    // ---- 解除配对 ----

    [Fact]
    public async Task Unpair_deletes_trust_cancels_transfers_and_closes_session_within_2s()
    {
        var w = new World();
        var outcome = w.A.Service.PairAsync(w.BAddress);
        w.B.Service.Respond((await w.B.AwaitRequestAsync()).Id, accept: true);
        Assert.Equal(Done(PairingResult.Success), await Kit.Within(outcome));
        await Kit.WaitUntilAsync(() => w.B.D.Manager.GetSession(w.A.D.Id) is not null, "receiver session");
        var sb = w.B.D.Manager.GetSession(w.A.D.Id)!;
        var start = w.Clock.MonotonicMs();
        await w.A.Service.UnpairAsync(w.B.D.Id).WaitAsync(TimeSpan.FromSeconds(10), Kit.Ct);
        Assert.True(w.Clock.MonotonicMs() - start < 2_000);
        Assert.Null(w.A.D.Trust.Get(w.B.D.Id));
        Assert.Equal(new[] { w.B.D.Id }, w.A.Cancelled);
        Assert.Equal(new CloseReason.PeerClosed(SessionCloseReason.Unpaired), await Kit.Within(sb.Closed));
    }
}
