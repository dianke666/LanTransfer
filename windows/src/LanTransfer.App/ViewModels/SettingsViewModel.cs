using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.App.Themes;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Platform.Windows.SystemServices;
using AppThemeMode = LanTransfer.Platform.Windows.Settings.ThemeMode;

namespace LanTransfer.App.ViewModels;

/// <summary>设置界面 ViewModel（设计 C8.2、需求 4、9、13、16、19、3.8）。</summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppController _app;
    private readonly Action<string> _showToast;
    private readonly Func<string, Task<bool>> _confirmUnpair;
    private readonly Action<LocateResult, StoredFileRef?, DirRef?> _handleLocateResult;
    private readonly Action _refreshFirewall;
    private readonly List<IDisposable> _subs = [];

    public SettingsViewModel(
        AppController app,
        Action<string> showToast,
        Func<string, Task<bool>> confirmUnpair,
        Action<LocateResult, StoredFileRef?, DirRef?> handleLocateResult,
        Action refreshFirewall)
    {
        _app = app;
        _showToast = showToast;
        _confirmUnpair = confirmUnpair;
        _handleLocateResult = handleLocateResult;
        _refreshFirewall = refreshFirewall;

        _deviceNameInput = _app.IdentityStore.DisplayName();
        _autoCopy = _app.SettingsStore.Current().AutoCopy;
        _themeMode = _app.SettingsStore.Theme.Value;
        RefreshDirDisplay();
        RefreshTrusted();
        RefreshInterfaces();

        _subs.Add(_app.SettingsStore.Changes.Subscribe(s => Dispatch(() =>
        {
            _autoCopy = s.AutoCopy;
            OnPropertyChanged(nameof(AutoCopy));
            RefreshDirDisplay();
        })));
        _subs.Add(_app.TrustStore.Changes.Subscribe(_ => Dispatch(RefreshTrusted)));
        _subs.Add(_app.Network.Interfaces.Subscribe(_ => Dispatch(RefreshInterfaces)));
    }

    [ObservableProperty]
    private string _deviceNameInput;

    [ObservableProperty]
    private string _nameError = string.Empty;

    [ObservableProperty]
    private string _receiveDirDisplay = string.Empty;

    [ObservableProperty]
    private string _dirError = string.Empty;

    [ObservableProperty]
    private bool _autoCopy;

    [ObservableProperty]
    private AppThemeMode _themeMode;

    [ObservableProperty]
    private bool _firewallNeedsRepair;

    public ObservableCollection<TrustedDeviceItemVm> TrustedDevices { get; } = [];

    public ObservableCollection<string> NetworkEndpoints { get; } = [];

    public bool HasTrustedDevices => TrustedDevices.Count > 0;

    public bool IsThemeSystem => ThemeMode == AppThemeMode.System;

    public bool IsThemeLight => ThemeMode == AppThemeMode.Light;

    public bool IsThemeDark => ThemeMode == AppThemeMode.Dark;

    public string DeviceIdHex => _app.Services?.Identity.DeviceId.Value.ToHex() ?? string.Empty;

    partial void OnAutoCopyChanged(bool value)
    {
        _app.SettingsStore.Update(s => s with { AutoCopy = value });
    }

    partial void OnThemeModeChanged(AppThemeMode value)
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        _app.SettingsStore.SetTheme(value);
        ThemeManager.Apply(value);
    }

    [RelayCommand]
    private void SetTheme(string mode)
    {
        ThemeMode = mode switch
        {
            "light" => AppThemeMode.Light,
            "dark" => AppThemeMode.Dark,
            _ => AppThemeMode.System,
        };
    }

    [RelayCommand]
    private void SaveDeviceName()
    {
        switch (DisplayNameRules.ValidateUserInput(DeviceNameInput))
        {
            case NameCheck.Ok ok:
                NameError = string.Empty;
                _app.IdentityStore.SetDisplayName(ok.Cleaned);
                if (ok.RemovedAny)
                {
                    // 4.8：替换输入框内容并显示 1–3 s 提示
                    DeviceNameInput = ok.Cleaned;
                    _showToast(UiText.ToastNameCharsRemoved);
                }
                else
                {
                    DeviceNameInput = ok.Cleaned;
                    _showToast(UiText.ToastNameSaved);
                }

                break;
            default:
                // 4.5：拒绝保存并原样保留输入框内容
                NameError = UiText.ErrorNameInvalid;
                break;
        }
    }

    [RelayCommand]
    private void ChooseReceiveDirectory()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = UiText.BtnChangeDir,
            InitialDirectory = _app.ReceiveStorage.ResolvePath(_app.SettingsStore.Current().ReceiveDirectory),
        };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            return;
        }

        var candidate = new DirRef.Path(dlg.FolderName);
        if (!_app.ReceiveStorage.IsWritable(candidate))
        {
            DirError = UiText.ErrorDirNotWritable;
            return;
        }

        DirError = string.Empty;
        _app.SettingsStore.Update(s => s with { ReceiveDirectory = candidate });
        RefreshDirDisplay();
    }

    [RelayCommand]
    private void OpenReceiveDirectory()
    {
        var dir = _app.SettingsStore.Current().ReceiveDirectory;
        var res = _app.FileLocator.OpenDirectory(dir);
        _handleLocateResult(res, null, dir);
    }

    public void ToggleAutoAccept(TrustedDeviceItemVm item, bool on)
    {
        _app.TrustStore.SetAutoAccept(item.DeviceId, on);
    }

    [RelayCommand]
    private async Task UnpairDeviceAsync(TrustedDeviceItemVm? item)
    {
        if (item is null)
        {
            return;
        }

        if (!await _confirmUnpair(item.DisplayName))
        {
            return;
        }

        if (_app.Services is { } svc)
        {
            await svc.Pairing.UnpairAsync(item.DeviceId);
        }
        else
        {
            _app.TrustStore.Delete(item.DeviceId);
        }
    }

    [RelayCommand]
    private async Task RepairFirewallAsync()
    {
        await _app.Firewall.LaunchElevatedRepairAsync();
        _refreshFirewall();
    }

    private void RefreshDirDisplay()
    {
        var dir = _app.SettingsStore.Current().ReceiveDirectory;
        ReceiveDirDisplay = _app.ReceiveStorage.ResolvePath(dir);
    }

    private void RefreshTrusted()
    {
        TrustedDevices.Clear();
        foreach (var d in _app.TrustStore.All())
        {
            TrustedDevices.Add(new TrustedDeviceItemVm
            {
                DeviceId = d.DeviceId,
                DisplayName = d.DisplayName,
                DeviceType = d.DeviceType,
                AutoAccept = d.AutoAccept,
            });
        }

        OnPropertyChanged(nameof(HasTrustedDevices));
    }

    private void RefreshInterfaces()
    {
        NetworkEndpoints.Clear();
        var port = _app.Host.ListenPort;
        foreach (var iface in _app.Network.Interfaces.Value)
        {
            var suffix = port is { } p ? $":{p}" : string.Empty;
            NetworkEndpoints.Add($"{iface.Name} · {iface.Address}{suffix}");
        }
    }

    private static void Dispatch(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    public void Dispose()
    {
        foreach (var s in _subs)
        {
            s.Dispose();
        }

        _subs.Clear();
    }
}
