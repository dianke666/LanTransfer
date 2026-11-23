using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Platform.Windows.Files;
using LanTransfer.Platform.Windows.Network;
using LanTransfer.Platform.Windows.Security;
using LanTransfer.Platform.Windows.Settings;
using LanTransfer.Platform.Windows.Storage;
using LanTransfer.Platform.Windows.SystemServices;
using LanTransfer.Protocol;

namespace LanTransfer.App.Services;

/// <summary>
/// 应用级服务容器（设计 C7、C8.2）：组装 EngineHost 与全部 Windows 平台适配器，
/// 持有各对端内存待发送列表、电源保活与通知激活回调。
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly ConcurrentDictionary<DeviceId, List<SourceRef>> _pendingFiles = new();
    private readonly Dictionary<string, long> _securityDebounce = new();
    private readonly List<IDisposable> _subs = [];

    public AppController(Action<NotificationAction> onNotificationAction)
    {
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanTransfer");
        Directory.CreateDirectory(DataDir);

        Lifecycle = new WpfAppLifecycle();
        Network = new WindowsNetworkMonitor();
        Transport = new WindowsTransport(Network);
        SettingsStore = new JsonSettingsStore(Path.Combine(DataDir, "settings.json"));
        IdentityStore = new DpapiIdentityStore(Path.Combine(DataDir, "identity.bin"), SettingsStore, CryptoRng.Instance);
        Database = new SqliteDatabase(Path.Combine(DataDir, "lantransfer.db"), SystemClock.Instance);
        TrustStore = new SqliteTrustStore(Database);
        HistoryStore = new SqliteHistoryStore(Database);
        TaskStore = new SqliteTaskStore(Database);
        ReceiveStorage = new FileSystemReceiveStorage(CollectKnownReceiveDirs);
        SourceFiles = new PathSourceFiles();
        TextCache = new WindowsOutgoingTextCache(Path.Combine(DataDir, "cache", "outgoing-text"));
        Clipboard = new Win32Clipboard();
        FileLocator = new ShellFileLocator(ReceiveStorage);
        Notifier = new AppNotificationNotifier(onNotificationAction);
        Firewall = new FirewallManager();
        PowerGuard = new PowerGuard();

        var ports = new EnginePorts(
            Transport,
            Network,
            IdentityStore,
            TrustStore,
            HistoryStore,
            TaskStore,
            SettingsStore,
            ReceiveStorage,
            SourceFiles,
            TextCache,
            Clipboard,
            Notifier,
            Lifecycle,
            SystemClock.Instance,
            CryptoRng.Instance);

        Host = new EngineHost(ports, DeviceType.Windows, FsProfile.Windows);
    }

    public string DataDir { get; }

    public WpfAppLifecycle Lifecycle { get; }

    public WindowsNetworkMonitor Network { get; }

    public WindowsTransport Transport { get; }

    public JsonSettingsStore SettingsStore { get; }

    public DpapiIdentityStore IdentityStore { get; }

    public SqliteDatabase Database { get; }

    public SqliteTrustStore TrustStore { get; }

    public SqliteHistoryStore HistoryStore { get; }

    public SqliteTaskStore TaskStore { get; }

    public FileSystemReceiveStorage ReceiveStorage { get; }

    public PathSourceFiles SourceFiles { get; }

    public WindowsOutgoingTextCache TextCache { get; }

    public Win32Clipboard Clipboard { get; }

    public ShellFileLocator FileLocator { get; }

    public AppNotificationNotifier Notifier { get; }

    public FirewallManager Firewall { get; }

    public PowerGuard PowerGuard { get; }

    public EngineHost Host { get; }

    public EngineServices? Services => Host.Services.Value;

    public async Task<StartResult> StartAsync()
    {
        var r = await Host.StartNetworkingAsync().ConfigureAwait(false);
        if (Host.Services.Value is { } svc && _subs.Count == 0)
        {
            _subs.Add(svc.Files.Tasks.Subscribe(tasks =>
            {
                var active = tasks.Any(t => t.Task.State is TaskState.Running or TaskState.Resuming);
                Application.Current?.Dispatcher.BeginInvoke(() => PowerGuard.SetTransferActive(active));
            }));
        }

        return r;
    }

    public List<SourceRef> GetPendingFiles(DeviceId peer) =>
        _pendingFiles.GetOrAdd(peer, _ => []);

    public void SetPendingFiles(DeviceId peer, IEnumerable<SourceRef> files) =>
        _pendingFiles[peer] = [.. files];

    public bool ShouldShowSecurityWarning(string key)
    {
        var now = SystemClock.Instance.MonotonicMs();
        lock (_securityDebounce)
        {
            if (_securityDebounce.TryGetValue(key, out var last) && now - last < 60_000)
            {
                return false;
            }

            _securityDebounce[key] = now;
            return true;
        }
    }

    public async Task ShutdownAsync(SessionCloseReason reason = SessionCloseReason.AppExit)
    {
        try
        {
            await Host.ShutdownNetworkingAsync(reason).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private IReadOnlyList<DirRef> CollectKnownReceiveDirs()
    {
        var dirs = new List<DirRef> { DirRef.Default.Instance, SettingsStore.Current().ReceiveDirectory };
        foreach (var t in TaskStore.Unfinished())
        {
            if (t.ReceiveDir is { } d)
            {
                dirs.Add(d);
            }
        }

        return dirs;
    }

    public void Dispose()
    {
        foreach (var s in _subs)
        {
            s.Dispose();
        }

        _subs.Clear();
        PowerGuard.Dispose();
        Host.Dispose();
        Notifier.Dispose();
        Network.Dispose();
        Database.Dispose();
    }
}
