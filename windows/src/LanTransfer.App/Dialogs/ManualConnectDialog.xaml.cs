using System.Windows;
using LanTransfer.App.Themes;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Protocol;

namespace LanTransfer.App.Dialogs;

/// <summary>手动输入 IPv4 与端口连接对话框（需求 6.2–6.5、6.8、6.9）。</summary>
public partial class ManualConnectDialog : Window
{
    private readonly PairingService _pairing;
    private readonly Action<DeviceId, string, DeviceType, string> _onConnected;
    private readonly Action<OpenFailure> _onFailure;

    public ManualConnectDialog(
        PairingService pairing,
        Action<DeviceId, string, DeviceType, string> onConnected,
        Action<OpenFailure> onFailure)
    {
        InitializeComponent();
        _pairing = pairing;
        _onConnected = onConnected;
        _onFailure = onFailure;
        ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => IpBox.Focus();
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        IpErrorText.Visibility = Visibility.Collapsed;
        PortErrorText.Visibility = Visibility.Collapsed;
        ResultBanner.Visibility = Visibility.Collapsed;

        var ip = AddressRules.ParseIpv4(IpBox.Text);
        var portParse = AddressRules.ParsePort(PortBox.Text);

        var valid = true;
        if (ip is null)
        {
            IpErrorText.Text = UiText.ErrorIpInvalid;
            IpErrorText.Visibility = Visibility.Visible;
            valid = false;
        }

        int port = ProtocolConstants.DefaultPort;
        switch (portParse)
        {
            case PortParse.Default d:
                port = d.Port;
                break;
            case PortParse.Ok ok:
                port = ok.Port;
                break;
            default:
                PortErrorText.Text = UiText.ErrorPortInvalid;
                PortErrorText.Visibility = Visibility.Visible;
                valid = false;
                break;
        }

        if (!valid || ip is null)
        {
            return;
        }

        ConnectBtn.IsEnabled = false;
        ConnectBtn.Content = UiText.BtnConnecting;
        try
        {
            var outcome = await _pairing.PairAsync(new Target.Address(ip.Value, port));
            switch (outcome)
            {
                case PairingOutcome.AlreadyTrusted at:
                    _onConnected(at.Session.Peer.DeviceId, at.Session.Peer.DisplayName, at.Session.Peer.DeviceType, $"{ip.Value}:{port}");
                    Close();
                    break;
                case PairingOutcome.Done { Result: PairingResult.Success }:
                    Close();
                    break;
                case PairingOutcome.Done { Result: PairingResult.ConnectFailed, Failure: { } f }:
                    if (f is OpenFailure.KeyMismatch or OpenFailure.PeerKeyMismatch or OpenFailure.HandshakeAuthFailed or OpenFailure.VersionIncompatible)
                    {
                        _onFailure(f);
                    }
                    else
                    {
                        ResultBannerText.Text = UiTextMapper.OpenFailureMessage(f);
                        ResultBanner.Visibility = Visibility.Visible;
                    }

                    break;
                case PairingOutcome.Busy:
                    ResultBannerText.Text = UiText.ToastPairBusy;
                    ResultBanner.Visibility = Visibility.Visible;
                    break;
            }
        }
        finally
        {
            ConnectBtn.IsEnabled = true;
            ConnectBtn.Content = UiText.BtnConnect;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
