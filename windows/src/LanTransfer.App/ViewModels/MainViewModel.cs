using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.App.Themes;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Platform.Windows.SystemServices;
using LanTransfer.Protocol;
using AppThemeMode = LanTransfer.Platform.Windows.Settings.ThemeMode;

namespace LanTransfer.App.ViewModels;

/// <summary>主窗口 ViewModel（设计 C8.2）：左侧设备列表、本机地址卡片、横幅提示、页面路由与全局事件转发。</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppController _app;
    private readonly DispatcherTimer _toastTimer;
    private readonly List<IDisposable> _subs = [];
    private string? _lastToastKey;

    public MainViewModel(
        AppController app,
        Func<string, Task<bool>> confirmSendAsTxt,
        Func<Task<bool>> confirmReplaceSurrogate,
        Func<HistoryItemVm, bool, Task<DeleteFileChoice>> confirmDeleteFile,
        Func<int, Task<bool>> confirmClearAll,
        Func<string, Task<bool>> confirmUnpair,
        Action<OpenFailure, DeviceId?> handleOpenFailure,
        Action<LocateResult, StoredFileRef?, DirRef?> handleLocateResult)
    {
        _app = app;
        _confirmSendAsTxt = confirmSendAsTxt;
        _confirmReplaceSurrogate = confirmReplaceSurrogate;
        _handleOpenFailure = handleOpenFailure;
        _handleLocateResult = handleLocateResult;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastMessage = string.Empty;
            _lastToastKey = null;
        };

        History = new HistoryViewModel(app, ShowToast, confirmDeleteFile, confirmClearAll, handleLocateResult);
        Settings = new SettingsViewModel(app, ShowToast, confirmUnpair, handleLocateResult, RefreshFirewallStatus);
        LocalDeviceName = app.IdentityStore.DisplayName();

        BindStreams();
        RefreshFirewallStatus();
    }

    private readonly Func<string, Task<bool>> _confirmSendAsTxt;
    private readonly Func<Task<bool>> _confirmReplaceSurrogate;
    private readonly Action<OpenFailure, DeviceId?> _handleOpenFailure;
    private readonly Action<LocateResult, StoredFileRef?, DirRef?> _handleLocateResult;

    public ObservableCollection<DeviceListItemVm> Devices { get; } = [];

    public HistoryViewModel History { get; }

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private NavPage _currentPage = NavPage.Overview;

    [ObservableProperty]
    private ConversationViewModel? _activeConversation;

    [ObservableProperty]
    private string _localDeviceName = string.Empty;

    [ObservableProperty]
    private string _localAddressSummary = UiText.LabelNotListening;

    [ObservableProperty]
    private string _localPortText = UiText.LabelNotListening;

    [ObservableProperty]
    private bool _hasNetwork = true;

    [ObservableProperty]
    private bool _discoveryUnavailable;

    [ObservableProperty]
    private bool _listenFailed;

    [ObservableProperty]
    private bool _identityReset;

    [ObservableProperty]
    private bool _identitySaveFailed;

    [ObservableProperty]
    private bool _notificationsOff;

    [ObservableProperty]
    private bool _firewallWarning;

    [ObservableProperty]
    private bool _historyStoreFailed;

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private int _unfinishedCount;

    public bool HasDevices => Devices.Count > 0;

    public bool IsOverviewPage => CurrentPage == NavPage.Overview;

    public bool IsConversationPage => CurrentPage == NavPage.Conversation;

    public bool IsHistoryPage => CurrentPage == NavPage.History;

    public bool IsSettingsPage => CurrentPage == NavPage.Settings;

    public bool HasToast => !string.IsNullOrEmpty(ToastMessage);

    public bool HasUnfinished => UnfinishedCount > 0;

    public event Action? OpenQrDialogRequested;

    public event Action? OpenManualConnectRequested;

    public event Action<PairingUiState>? PairingUiStateChanged;

    public event Action<TransferTaskView>? IncomingOfferArrived;

    public event Action<TransferId>? IncomingOfferEnded;

    partial void OnCurrentPageChanged(NavPage value)
    {
        OnPropertyChanged(nameof(IsOverviewPage));
        OnPropertyChanged(nameof(IsConversationPage));
        OnPropertyChanged(nameof(IsHistoryPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        if (value == NavPage.History)
        {
            History.Reload();
        }
    }

    partial void OnToastMessageChanged(string value) => OnPropertyChanged(nameof(HasToast));

    partial void OnUnfinishedCountChanged(int value) => OnPropertyChanged(nameof(HasUnfinished));

    public void ShowToast(string message)
    {
        Dispatch(() =>
        {
            if (string.Equals(_lastToastKey, message, StringComparison.Ordinal))
            {
                _toastTimer.Stop();
                _toastTimer.Start();
                return;
            }

            _lastToastKey = message;
            ToastMessage = message;
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    public void BindEngineServices()
    {
        if (_app.Services is not { } svc)
        {
            return;
        }

        _subs.Add(svc.Discovery.Devices.Subscribe(_ => Dispatch(RebuildDeviceList)));
        _subs.Add(svc.Discovery.Status.Subscribe(st => Dispatch(() => DiscoveryUnavailable = st is DiscoveryStatus.Unavailable)));
        _subs.Add(svc.Sessions.Sessions.Subscribe(_ => Dispatch(RebuildDeviceList)));
        _subs.Add(svc.Sessions.InboundFailures.Subscribe(f => Dispatch(() => _handleOpenFailure(f.Failure, f.Peer))));
        _subs.Add(svc.Pairing.State.Subscribe(st => Dispatch(() => PairingUiStateChanged?.Invoke(st))));
        _subs.Add(svc.Pairing.Notices.Subscribe(n =>
        {
            if (n is PairingNotice.Finished fin)
            {
                Dispatch(() =>
                {
                    var peerName = _app.TrustStore.Get(fin.Done.Peer)?.DisplayName
                        ?? _app.Services?.Discovery.Devices.Value.FirstOrDefault(d => d.DeviceId == fin.Done.Peer)?.Announcement.DisplayName;
                    ShowToast(UiTextMapper.PairingResultMessage(fin.Done.Result, peerName, fin.Failure));
                });
            }
        }));
        _subs.Add(svc.Text.Notices.Subscribe(n => Dispatch(() =>
        {
            switch (n)
            {
                case TextNotice.ConnectFailed cf:
                    _handleOpenFailure(cf.Failure, cf.Peer);
                    break;
                case TextNotice.StoreFailed:
                    HistoryStoreFailed = true;
                    break;
                case TextNotice.AutoCopyFailed:
                    ShowToast(UiText.ToastClipboardFailed);
                    break;
            }
        })));
        _subs.Add(svc.Files.UnfinishedCount.Subscribe(c => Dispatch(() => UnfinishedCount = c)));

        var knownAwaiting = new HashSet<TransferId>();
        _subs.Add(svc.Files.Tasks.Subscribe(tasks => Dispatch(() =>
        {
            var currentAwaiting = new HashSet<TransferId>();
            foreach (var tv in tasks)
            {
                if (tv.Task.Direction == Direction.Receive && tv.Task.State == TaskState.AwaitingUser)
                {
                    currentAwaiting.Add(tv.Id);
                    if (knownAwaiting.Add(tv.Id))
                    {
                        IncomingOfferArrived?.Invoke(tv);
                    }
                }
            }

            foreach (var old in knownAwaiting.Where(id => !currentAwaiting.Contains(id)).ToList())
            {
                knownAwaiting.Remove(old);
                IncomingOfferEnded?.Invoke(old);
            }
        })));

        _subs.Add(svc.Files.Notices.Subscribe(n => Dispatch(() =>
        {
            switch (n)
            {
                case TransferNotice.StoreFailed:
                    HistoryStoreFailed = true;
                    break;
                case TransferNotice.PendingCancelNotSaved:
                    ShowToast(UiText.ToastPendingCancelFailed);
                    break;
            }
        })));

        Dispatch(() =>
        {
            RebuildDeviceList();
            UpdateLocalSummary();
        });
    }

    private void BindStreams()
    {
        _subs.Add(_app.Network.Interfaces.Subscribe(_ => Dispatch(UpdateLocalSummary)));
        _subs.Add(_app.Host.Networking.Subscribe(r => Dispatch(() =>
        {
            ListenFailed = r is StartResult.ListenFailed;
            IdentitySaveFailed = r is StartResult.IdentityNotSaved;
            UpdateLocalSummary();
        })));
        _subs.Add(_app.Host.Identity.Subscribe(id => Dispatch(() =>
        {
            IdentityReset = id is IdentityLoad.ResetCorrupted;
            IdentitySaveFailed = id is IdentityLoad.SaveFailed;
            LocalDeviceName = _app.IdentityStore.DisplayName();
        })));
        _subs.Add(_app.Notifier.Enabled.Subscribe(on => Dispatch(() => NotificationsOff = !on)));
        _subs.Add(_app.TrustStore.Changes.Subscribe(_ => Dispatch(RebuildDeviceList)));
        _subs.Add(_app.SettingsStore.Changes.Subscribe(_ => Dispatch(() => LocalDeviceName = _app.IdentityStore.DisplayName())));
    }

    public void RefreshFirewallStatus()
    {
        _ = Task.Run(() =>
        {
            var st = _app.Firewall.CheckStatus();
            Dispatch(() =>
            {
                FirewallWarning = st == FirewallStatus.NeedsRepair;
                Settings.FirewallNeedsRepair = FirewallWarning;
            });
        });
    }

    private void UpdateLocalSummary()
    {
        LocalDeviceName = _app.IdentityStore.DisplayName();
        var ifaces = _app.Network.Interfaces.Value;
        HasNetwork = ifaces.Count > 0;
        var port = _app.Host.ListenPort;
        LocalPortText = port is { } p ? UiText.LabelListenPort(p) : UiText.LabelNotListening;
        if (ifaces.Count == 0)
        {
            LocalAddressSummary = UiText.BannerNoNetwork;
        }
        else
        {
            var first = ifaces[0].Address.ToString();
            LocalAddressSummary = port is { } lp ? $"{first}:{lp}" : first;
        }
    }

    private void RebuildDeviceList()
    {
        var discovered = _app.Services?.Discovery.Devices.Value ?? ValueList<DeviceEntry>.Empty;
        var trusted = _app.TrustStore.All();
        var sessions = _app.Services?.Sessions.Sessions.Value ?? ValueMap<DeviceId, SessionInfo>.Empty;
        var selectedId = ActiveConversation?.PeerId;

        var list = new List<DeviceListItemVm>();
        var added = new HashSet<DeviceId>();

        foreach (var d in discovered)
        {
            added.Add(d.DeviceId);
            list.Add(new DeviceListItemVm
            {
                DeviceId = d.DeviceId,
                DisplayName = d.Announcement.DisplayName,
                DeviceType = d.Announcement.DeviceType,
                IpText = $"{d.Ip}:{d.Announcement.Port}",
                IsTrusted = d.Trusted,
                IsConnected = sessions.ContainsKey(d.DeviceId),
                IsOnline = true,
                IsSelected = selectedId == d.DeviceId && CurrentPage == NavPage.Conversation,
            });
        }

        // 已信任但暂未收到广播的设备也列在下方，方便直连（9.2）
        foreach (var t in trusted)
        {
            if (added.Contains(t.DeviceId))
            {
                continue;
            }

            list.Add(new DeviceListItemVm
            {
                DeviceId = t.DeviceId,
                DisplayName = t.DisplayName,
                DeviceType = t.DeviceType,
                IpText = t.LastAddress is { } ip && t.LastPort is { } p ? $"{ip}:{p}" : UiText.LabelTrustedBadge,
                IsTrusted = true,
                IsConnected = sessions.ContainsKey(t.DeviceId),
                IsOnline = false,
                IsSelected = selectedId == t.DeviceId && CurrentPage == NavPage.Conversation,
            });
        }

        Devices.Clear();
        foreach (var item in list)
        {
            Devices.Add(item);
        }

        OnPropertyChanged(nameof(HasDevices));
    }

    [RelayCommand]
    private async Task SelectDeviceAsync(DeviceListItemVm? item)
    {
        if (item is null || _app.Services is not { } svc)
        {
            return;
        }

        if (item.IsTrusted)
        {
            OpenConversation(item.DeviceId, item.DisplayName, item.DeviceType, item.IpText);
            return;
        }

        var outcome = await svc.Pairing.PairAsync(new Target.Device(item.DeviceId));
        switch (outcome)
        {
            case PairingOutcome.AlreadyTrusted at:
                OpenConversation(at.Session.Peer.DeviceId, at.Session.Peer.DisplayName, at.Session.Peer.DeviceType, item.IpText);
                break;
            case PairingOutcome.Done { Result: PairingResult.Success }:
                OpenConversation(item.DeviceId, item.DisplayName, item.DeviceType, item.IpText);
                break;
            case PairingOutcome.Done { Result: PairingResult.ConnectFailed, Failure: { } f }:
                _handleOpenFailure(f, item.DeviceId);
                break;
        }
    }

    public void OpenConversation(DeviceId peerId, string? name = null, DeviceType? type = null, string? ipText = null)
    {
        var trusted = _app.TrustStore.Get(peerId);
        var disc = _app.Services?.Discovery.Devices.Value.FirstOrDefault(d => d.DeviceId == peerId);
        var resolvedName = name ?? trusted?.DisplayName ?? disc?.Announcement.DisplayName ?? peerId.Value.ToHex()[..8];
        var resolvedType = type ?? trusted?.DeviceType ?? disc?.Announcement.DeviceType ?? DeviceType.Android;
        var resolvedIp = ipText ?? (disc is not null ? $"{disc.Ip}:{disc.Announcement.Port}" : trusted?.LastAddress?.ToString() ?? string.Empty);

        if (ActiveConversation?.PeerId != peerId)
        {
            ActiveConversation?.Dispose();
            ActiveConversation = new ConversationViewModel(
                _app,
                peerId,
                resolvedName,
                resolvedType,
                resolvedIp,
                ShowToast,
                _confirmSendAsTxt,
                _confirmReplaceSurrogate,
                _handleOpenFailure,
                _handleLocateResult);
        }

        CurrentPage = NavPage.Conversation;
        foreach (var d in Devices)
        {
            d.IsSelected = d.DeviceId == peerId;
        }
    }

    [RelayCommand]
    private void OpenHistory()
    {
        CurrentPage = NavPage.History;
        foreach (var d in Devices)
        {
            d.IsSelected = false;
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        CurrentPage = NavPage.Settings;
        foreach (var d in Devices)
        {
            d.IsSelected = false;
        }
    }

    [RelayCommand]
    private void OpenQrPairing() => OpenQrDialogRequested?.Invoke();

    [RelayCommand]
    private void OpenManualConnect() => OpenManualConnectRequested?.Invoke();

    [RelayCommand]
    private async Task CopyLocalAddressAsync()
    {
        if (!HasNetwork)
        {
            return;
        }

        var r = await _app.Clipboard.WriteTextAsync(LocalAddressSummary);
        _showToast(r is ClipWrite.Ok ? UiText.ToastCopied : UiText.ToastClipboardFailed);
    }

    private void _showToast(string s) => ShowToast(s);

    [RelayCommand]
    private void ToggleQuickTheme()
    {
        Settings.ThemeMode = ThemeManager.IsDark ? AppThemeMode.Light : AppThemeMode.Dark;
    }

    [RelayCommand]
    private async Task RepairFirewallAsync()
    {
        await _app.Firewall.LaunchElevatedRepairAsync();
        RefreshFirewallStatus();
    }

    [RelayCommand]
    private void OpenNotificationSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task RetryStartNetworkingAsync()
    {
        var r = await _app.StartAsync();
        if (r is StartResult.Running)
        {
            BindEngineServices();
        }
    }

    private static void Dispatch(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        _toastTimer.Stop();
        ActiveConversation?.Dispose();
        History.Dispose();
        Settings.Dispose();
        foreach (var s in _subs)
        {
            s.Dispose();
        }

        _subs.Clear();
    }
}
