using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// 引擎宿主（设计 C4.1），与 Kotlin 侧 EngineHost.kt 同名镜像：Windows 宿主与 ViewModel 只和它交互。
// - StartNetworkingAsync：第一次调用时 LoadOrCreate 身份并组装全部服务（随后执行两个服务的 RestoreOnStartupAsync）；
//   新身份无法保存时不启动网络并报告（Q14）。然后先监听、后启动发现；监听全部失败时不发公告（5.9）。
// - ShutdownNetworkingAsync：停止发现、关闭监听与全部 Session（21.4、21.9）；之后可再次 StartNetworkingAsync。
// - 分发器：每个已注册 Session 的应用消息依次交给 TextService、FileTransferEngine；配对消息由 PairingService 直接读取
//   PendingConnection，不经过这里。PairingService.UnpairAsync 经 cancelTransfers 接到 FileTransferEngine.CancelPeerAsync（Q7）。

/// <summary>EngineHost 需要的全部平台适配器（由 Windows 宿主创建）。</summary>
public sealed class EnginePorts(
    ITransport transport,
    INetworkMonitor network,
    IIdentityStore identity,
    ITrustStore trust,
    IHistoryStore history,
    ITaskStore tasks,
    ISettingsStore settings,
    IReceiveStorage storage,
    ISourceFiles sources,
    IOutgoingTextCache textCache,
    IClipboard clipboard,
    INotifier notifier,
    IAppLifecycle lifecycle,
    IClock clock,
    IRng rng,
    IEphemeralKeySource? keys = null)
{
    public ITransport Transport { get; } = transport;

    public INetworkMonitor Network { get; } = network;

    public IIdentityStore Identity { get; } = identity;

    public ITrustStore Trust { get; } = trust;

    public IHistoryStore History { get; } = history;

    public ITaskStore Tasks { get; } = tasks;

    public ISettingsStore Settings { get; } = settings;

    public IReceiveStorage Storage { get; } = storage;

    public ISourceFiles Sources { get; } = sources;

    public IOutgoingTextCache TextCache { get; } = textCache;

    public IClipboard Clipboard { get; } = clipboard;

    public INotifier Notifier { get; } = notifier;

    public IAppLifecycle Lifecycle { get; } = lifecycle;

    public IClock Clock { get; } = clock;

    public IRng Rng { get; } = rng;

    public IEphemeralKeySource Keys { get; } = keys ?? EphemeralKeySource.Secure;
}

/// <summary>身份载入成功后组装的服务（进程内只组装一次）。</summary>
public sealed class EngineServices : IDisposable
{
    private readonly IDisposable _dispatchSub;
    private readonly CancellationTokenSource _cts;

    internal EngineServices(
        DeviceIdentity identity,
        SessionManager sessions,
        DiscoveryService discovery,
        PairingService pairing,
        TextService text,
        FileTransferEngine files,
        IDisposable dispatchSub,
        CancellationTokenSource cts)
    {
        Identity = identity;
        Sessions = sessions;
        Discovery = discovery;
        Pairing = pairing;
        Text = text;
        Files = files;
        _dispatchSub = dispatchSub;
        _cts = cts;
    }

    public DeviceIdentity Identity { get; }

    public SessionManager Sessions { get; }

    public DiscoveryService Discovery { get; }

    public PairingService Pairing { get; }

    public TextService Text { get; }

    public FileTransferEngine Files { get; }

    public void Dispose()
    {
        _dispatchSub.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        Discovery.Dispose();
        Pairing.Dispose();
        Text.Dispose();
        Files.Dispose();
        Sessions.Dispose();
    }
}

/// <summary><see cref="EngineHost.StartNetworkingAsync"/> 的结果。</summary>
public abstract record StartResult
{
    private StartResult()
    {
    }

    /// <summary>已在 <see cref="Port"/> 上监听并启动了发现（端口用于公告、二维码与主界面显示，5.7、6.1）。</summary>
    public sealed record Running(int Port) : StartResult;

    /// <summary>默认端口与全部备用端口都无法监听：不发送公告，主界面提示传输服务启动失败（5.9）。</summary>
    public sealed record ListenFailed : StartResult
    {
        public static ListenFailed Instance { get; } = new();
    }

    /// <summary>新身份无法保存：不监听、不公告，主界面显示错误和“重试”（Q14）。</summary>
    public sealed record IdentityNotSaved(string Detail) : StartResult;
}

public sealed class EngineHost(
    EnginePorts ports,
    DeviceType deviceType,
    FsProfile profile,
    SessionTimings? sessionTimings = null,
    TransferTimings? transferTimings = null,
    DiscoveryTimings? discoveryTimings = null) : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ObservableState<IdentityLoad?> _identity = new(null);
    private readonly ObservableState<EngineServices?> _services = new(null);
    private readonly ObservableState<StartResult?> _networking = new(null);

    /// <summary>最近一次 LoadOrCreate 的结果（主界面据此显示“身份已重置”或“无法保存身份”，4.6、Q14）；尚未载入时为 null。</summary>
    public IObservableState<IdentityLoad?> Identity => _identity;

    /// <summary>组装好的服务；身份载入成功之前为 null。</summary>
    public IObservableState<EngineServices?> Services => _services;

    /// <summary>网络状态：最近一次 StartNetworkingAsync 的结果；从未启动或已 ShutdownNetworkingAsync 时为 null。</summary>
    public IObservableState<StartResult?> Networking => _networking;

    /// <summary>实际监听端口；未在监听时为 null。</summary>
    public int? ListenPort => (_networking.Value as StartResult.Running)?.Port;

    /// <summary>启动网络（见文件头）。已在运行时直接返回当前结果；上次失败（身份无法保存、监听失败）时重新尝试。</summary>
    public async Task<StartResult> StartNetworkingAsync(CancellationToken ct = default)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_networking.Value is StartResult.Running running)
            {
                return running;
            }

            var g = _services.Value;
            if (g is null)
            {
                var load = ports.Identity.LoadOrCreate();
                _identity.Value = load;
                if (load is IdentityLoad.SaveFailed sf)
                {
                    return Publish(new StartResult.IdentityNotSaved(sf.Detail));
                }

                if (load is not IdentityLoad.Ready ready)
                {
                    return Publish(new StartResult.IdentityNotSaved("unknown identity load result"));
                }

                g = Build(ready.Identity);
                _services.Value = g;
                await g.Text.RestoreOnStartupAsync().ConfigureAwait(false);
                await g.Files.RestoreOnStartupAsync().ConfigureAwait(false);
            }

            switch (g.Sessions.StartListening())
            {
                case ListenResult.Failed:
                    return Publish(StartResult.ListenFailed.Instance);
                case ListenResult.Ok ok:
                    _networking.Value = new StartResult.Running(ok.Port); // 先于发现：PairingService 的二维码要用端口
                    g.Discovery.Start(ok.Port);
                    return new StartResult.Running(ok.Port);
                default:
                    return Publish(StartResult.ListenFailed.Instance);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>停止发现、关闭监听，并以 <paramref name="reason"/> 关闭全部 Session（21.4、21.9）。</summary>
    public async Task ShutdownNetworkingAsync(SessionCloseReason reason)
    {
        await _mutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_services.Value is { } g)
            {
                g.Discovery.Stop();
                await g.Sessions.ShutdownAsync(reason).ConfigureAwait(false);
            }

            _networking.Value = null;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose()
    {
        _services.Value?.Dispose();
        _mutex.Dispose();
    }

    private StartResult Publish(StartResult r)
    {
        _networking.Value = r;
        return r;
    }

    private EngineServices Build(DeviceIdentity identity)
    {
        var p = ports;
        var name = () => p.Identity.DisplayName();
        var discovery = new DiscoveryService(p.Transport, p.Network, identity, deviceType, p.Trust, p.Clock, p.Rng, name, timings: discoveryTimings);
        var sessions = new SessionManager(
            p.Transport, p.Network, identity, deviceType, p.Trust, p.Clock, p.Keys, name,
            addressBook: discovery.AddressOf, timings: sessionTimings);

        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        var dispatchSub = sessions.Events.Subscribe(e =>
        {
            if (e is SessionEvent.Opened opened)
            {
                _ = Task.Run(() => PumpSessionAsync(opened.Session, ct), CancellationToken.None);
            }
        });

        var text = new TextService(sessions, p.History, p.Trust, p.Clipboard, p.Notifier, p.Settings, p.Lifecycle, p.TextCache, p.Clock, p.Rng);
        var files = new FileTransferEngine(
            sessions, p.Storage, p.Sources, p.Tasks, p.Trust, p.Notifier, p.Settings, p.Lifecycle, p.TextCache, p.Clock, p.Rng, profile, transferTimings);
        var pairing = new PairingService(
            sessions, p.Trust, new PairingTokenRegistry(p.Rng), p.Clock, p.Rng, identity, p.Network, name,
            listenPort: () => ListenPort,
            cancelTransfers: peer => files.CancelPeerAsync(peer));

        return new EngineServices(identity, sessions, discovery, pairing, text, files, dispatchSub, cts);
    }

    private async Task PumpSessionAsync(Session session, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in session.Incoming.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Dispatch(session, msg);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>应用消息先交给 TextService，不是文字消息再交给 FileTransferEngine；两者都不认识的消息忽略。</summary>
    private void Dispatch(Session session, ProtocolMessage msg)
    {
        if (_services.Value is not { } g)
        {
            return;
        }

        if (!g.Text.OnMessage(session, msg))
        {
            g.Files.OnMessage(session, msg);
        }
    }
}
