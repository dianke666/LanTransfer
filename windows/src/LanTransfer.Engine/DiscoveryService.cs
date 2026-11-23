using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// 设备发现（设计 C4.6，需求 3.2、3.6、5.1–5.5、5.10），与 Kotlin 侧 DiscoveryService.kt 同名镜像：
// - 发送：每个活动接口每 2 s（±0.2 s 抖动）向定向广播地址和组播组各发 1 份公告；新接口出现时立即在该接口上发送。
// - 接收：数据报 → AcceptsInbound → AnnouncementCodec.Decode → DeviceListReducer；1 s 的 Tick 驱动过期移除（10 s）。
// - UDP 端口无法打开，或某一轮在全部接口上都发送失败时为 Unavailable；之后某一轮发送成功时回到 Running。

/// <summary>发现服务的状态（主界面据此显示“自动发现不可用”，5.10）。</summary>
public abstract record DiscoveryStatus
{
    private DiscoveryStatus()
    {
    }

    public sealed record Stopped : DiscoveryStatus
    {
        public static Stopped Instance { get; } = new();
    }

    public sealed record Running : DiscoveryStatus
    {
        public static Running Instance { get; } = new();
    }

    /// <summary>UDP 端口无法打开或发送全部失败；手动连接与扫码配对不受影响。</summary>
    public sealed record Unavailable(string Reason) : DiscoveryStatus;
}

/// <summary>发现相关的时限（需求与设计规定的数值；只为测试注入而做成参数）。</summary>
public sealed record DiscoveryTimings(
    long IntervalMs = 2_000,
    long JitterMs = 200,
    long TickMs = 1_000)
{
    public static DiscoveryTimings Default { get; } = new();
}

public sealed class DiscoveryService : IDisposable
{
    /// <summary>组播组 239.255.42.58（C1 发现）。</summary>
    public static readonly Ipv4 Multicast = AddressRules.ParseIpv4(ProtocolConstants.MulticastGroup)!.Value;

    public const string SendFailed = "无法在任何接口上发送公告";

    private readonly ITransport _transport;
    private readonly INetworkMonitor _network;
    private readonly DeviceIdentity _identity;
    private readonly DeviceType _deviceType;
    private readonly ITrustStore _trust;
    private readonly IClock _clock;
    private readonly IRng _rng;
    private readonly Func<string> _displayName;
    private readonly ProtocolVersion _version;
    private readonly DiscoveryTimings _timings;

    private readonly Lock _gate = new();
    private readonly ObservableState<ValueList<DeviceEntry>> _devices = new(ValueList<DeviceEntry>.Empty);
    private readonly ObservableState<DiscoveryStatus> _status = new(DiscoveryStatus.Stopped.Instance);

    private DeviceListState _state;
    private CancellationTokenSource? _runCts;
    private IUdpEndpoint? _endpoint;
    private IDisposable? _trustSub;
    private IDisposable? _ifaceSub;

    public DiscoveryService(
        ITransport transport,
        INetworkMonitor network,
        DeviceIdentity identity,
        DeviceType deviceType,
        ITrustStore trust,
        IClock clock,
        IRng rng,
        Func<string>? displayName = null,
        ProtocolVersion? version = null,
        DiscoveryTimings? timings = null)
    {
        _transport = transport;
        _network = network;
        _identity = identity;
        _deviceType = deviceType;
        _trust = trust;
        _clock = clock;
        _rng = rng;
        _displayName = displayName ?? (() => identity.DisplayName);
        _version = version ?? ProtocolVersion.Current;
        _timings = timings ?? DiscoveryTimings.Default;
        _state = new DeviceListState(identity.DeviceId, []);
    }

    /// <summary>设备列表（按首次出现的顺序，不含本机）。</summary>
    public IObservableState<ValueList<DeviceEntry>> Devices => _devices;

    public IObservableState<DiscoveryStatus> Status => _status;

    /// <summary>设备列表中的地址：最近一次有效公告的来源 IP 与公告中的监听端口（供 SessionManager 的 addressBook）。</summary>
    public SocketAddr? AddressOf(DeviceId id)
    {
        foreach (var e in _devices.Value)
        {
            if (e.DeviceId == id)
            {
                return new SocketAddr(e.Ip, e.Announcement.Port);
            }
        }

        return null;
    }

    /// <summary>打开 UDP 端口并开始收发；<paramref name="listenPort"/> 是写进公告的 TCP 监听端口。已在运行时先停止再重新开始。</summary>
    public void Start(int listenPort)
    {
        Stop();
        IUdpEndpoint ep;
        switch (_transport.OpenUdp(ProtocolConstants.DiscoveryPort))
        {
            case UdpOpenResult.Failed f:
                _status.Value = new DiscoveryStatus.Unavailable(f.Reason);
                return;
            case UdpOpenResult.Ok ok:
                ep = ok.Endpoint;
                break;
            default:
                return;
        }

        var initial = _network.Interfaces.Value;
        var trusted = TrustedIds();
        Update(s => s with { Interfaces = new ValueSet<LocalInterface>(initial), Trusted = trusted });
        _status.Value = DiscoveryStatus.Running.Instance;

        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var known = new HashSet<LocalInterface>(initial);
        var ifaceGate = new Lock();

        var trustSub = _trust.Changes.Subscribe(_ =>
        {
            var ids = TrustedIds();
            Update(s => DeviceListReducer.Reduce(s, new DeviceListEvent.TrustChanged(ids)));
        });

        var ifaceSub = _network.Interfaces.Subscribe(now =>
        {
            List<LocalInterface> added;
            lock (ifaceGate)
            {
                added = [.. now.Where(i => !known.Contains(i))];
                known = [.. now];
            }

            Update(s => s with { Interfaces = new ValueSet<LocalInterface>(now) });
            if (added.Count > 0)
            {
                _ = Task.Run(() => SendRoundAsync(ep, added, listenPort, ct), CancellationToken.None);
            }
        });

        lock (_gate)
        {
            _endpoint = ep;
            _runCts = cts;
            _trustSub = trustSub;
            _ifaceSub = ifaceSub;
        }

        // 接收循环
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var d = await ep.ReceiveAsync(ct).ConfigureAwait(false);
                        if (d is null)
                        {
                            break;
                        }

                        var bytes = d.Data.ToArray();
                        Update(s => DeviceListReducer.OnDatagram(s, bytes, d.Src, d.Via, _clock.MonotonicMs()));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);

        // 过期检查 Tick 循环（1 s）
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(_timings.TickMs), _clock.Time, ct).ConfigureAwait(false);
                        Update(s => DeviceListReducer.Reduce(s, new DeviceListEvent.Tick(_clock.MonotonicMs())));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);

        // 周期发送循环：启动时立即发送第一轮
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        await SendRoundAsync(ep, _network.Interfaces.Value, listenPort, ct).ConfigureAwait(false);
                        var waitMs = Math.Max(0, _timings.IntervalMs + Jitter());
                        await Task.Delay(TimeSpan.FromMilliseconds(waitMs), _clock.Time, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);
    }

    /// <summary>停止收发并关闭 UDP 端口；清空设备列表（之后收不到公告，列表内容已不可信）。</summary>
    public void Stop()
    {
        CancellationTokenSource? cts;
        IUdpEndpoint? ep;
        IDisposable? trustSub;
        IDisposable? ifaceSub;
        lock (_gate)
        {
            cts = _runCts;
            ep = _endpoint;
            trustSub = _trustSub;
            ifaceSub = _ifaceSub;
            _runCts = null;
            _endpoint = null;
            _trustSub = null;
            _ifaceSub = null;
        }

        trustSub?.Dispose();
        ifaceSub?.Dispose();
        cts?.Cancel();
        cts?.Dispose();
        ep?.Close();
        Update(s => s with { Entries = ValueList<DeviceEntry>.Empty });
        _status.Value = DiscoveryStatus.Stopped.Instance;
    }

    public void Dispose() => Stop();

    /// <summary>在 <paramref name="ifaces"/> 上各发一份到定向广播与组播组；接口非空而全部发送失败时为 Unavailable，否则为 Running。</summary>
    private async Task SendRoundAsync(IUdpEndpoint ep, IReadOnlyList<LocalInterface> ifaces, int listenPort, CancellationToken ct)
    {
        if (ifaces.Count == 0 || ct.IsCancellationRequested)
        {
            return;
        }

        var bytes = AnnouncementCodec.Encode(new Announcement(_identity.DeviceId, _displayName(), _deviceType, _version, (ushort)listenPort));
        var ok = 0;
        foreach (var i in ifaces)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (await ep.SendAsync(i, i.Broadcast, ProtocolConstants.DiscoveryPort, bytes, ct).ConfigureAwait(false))
            {
                ok++;
            }

            if (await ep.SendAsync(i, Multicast, ProtocolConstants.DiscoveryPort, bytes, ct).ConfigureAwait(false))
            {
                ok++;
            }
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_endpoint, ep))
            {
                return;
            }

            _status.Value = ok > 0 ? DiscoveryStatus.Running.Instance : new DiscoveryStatus.Unavailable(SendFailed);
        }
    }

    /// <summary>[-JitterMs, JitterMs] 内的均匀抖动。</summary>
    private long Jitter()
    {
        var span = 2 * _timings.JitterMs + 1;
        if (span <= 1)
        {
            return 0;
        }

        var b = _rng.NextBytes(4);
        var v = ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
        return (v % span) - _timings.JitterMs;
    }

    private ValueSet<DeviceId> TrustedIds() => new(_trust.All().Select(d => d.DeviceId));

    private void Update(Func<DeviceListState, DeviceListState> f)
    {
        lock (_gate)
        {
            var next = f(_state);
            if (next != _state)
            {
                _state = next;
                _devices.Value = next.Entries;
            }
        }
    }
}
