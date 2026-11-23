using System.Windows;
using LanTransfer.App.Themes;
using LanTransfer.Engine;

namespace LanTransfer.App.Dialogs;

/// <summary>点选配对确认与验证码展示对话框（需求 7.1–7.4）。</summary>
public partial class PairingDialog : Window
{
    private readonly PairingService _pairing;
    private RequestId? _requestId;
    private bool _isInitiator;

    public PairingDialog(PairingService pairing)
    {
        InitializeComponent();
        _pairing = pairing;
        ThemeManager.ApplyTitleBar(this);
        Closing += OnDialogClosing;
    }

    public void UpdateState(PairingUiState state)
    {
        _requestId = null;
        _isInitiator = false;
        SasCard.Visibility = Visibility.Collapsed;
        BusyBar.Visibility = Visibility.Collapsed;
        AcceptBtn.Visibility = Visibility.Collapsed;
        RejectBtn.Visibility = Visibility.Collapsed;
        CancelBtn.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case PairingUiState.Connecting:
                _isInitiator = true;
                HeadingText.Text = UiText.BtnConnecting;
                SubheadingText.Text = UiText.LabelEncryptedHint;
                BusyBar.Visibility = Visibility.Visible;
                CancelBtn.Visibility = Visibility.Visible;
                break;

            case PairingUiState.WaitingForPeer wp:
                _isInitiator = true;
                HeadingText.Text = UiText.StatusPairWaiting;
                SubheadingText.Text = $"{wp.Peer.DisplayName} · {UiTextMapper.DeviceTypeLabel(wp.Peer.DeviceType)}";
                BusyBar.Visibility = Visibility.Visible;
                CancelBtn.Visibility = Visibility.Visible;
                break;

            case PairingUiState.ShowingCode sc:
                _isInitiator = true;
                HeadingText.Text = UiText.StatusPairWaiting;
                SubheadingText.Text = $"{sc.Peer.DisplayName} · {UiTextMapper.DeviceTypeLabel(sc.Peer.DeviceType)}";
                SasCodeText.Text = FormatSas(sc.Sas);
                SasCard.Visibility = Visibility.Visible;
                CancelBtn.Visibility = Visibility.Visible;
                break;

            case PairingUiState.Request req:
                _requestId = req.Id;
                HeadingText.Text = UiText.LabelPairRequestFrom(req.Peer.DisplayName);
                var ipPart = req.Peer.Ip is { } ip ? $" · {ip}" : string.Empty;
                SubheadingText.Text = $"{UiTextMapper.DeviceTypeLabel(req.Peer.DeviceType)}{ipPart}";
                if (req.Sas is not null)
                {
                    SasCodeText.Text = FormatSas(req.Sas);
                    SasCard.Visibility = Visibility.Visible;
                    AcceptBtn.IsEnabled = true;
                }
                else
                {
                    BusyBar.Visibility = Visibility.Visible;
                    AcceptBtn.IsEnabled = false;
                }

                AcceptBtn.Visibility = Visibility.Visible;
                RejectBtn.Visibility = Visibility.Visible;
                break;

            case PairingUiState.Completing c:
                HeadingText.Text = UiText.StatusPairCompleting;
                SubheadingText.Text = c.Peer.DisplayName;
                BusyBar.Visibility = Visibility.Visible;
                break;
        }
    }

    private static string FormatSas(string sas) => sas.Length == 6 ? $"{sas[..3]} {sas[3..]}" : sas;

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (_requestId is { } id)
        {
            _pairing.Respond(id, accept: true);
        }
    }

    private void OnRejectClick(object sender, RoutedEventArgs e)
    {
        if (_requestId is { } id)
        {
            _pairing.Respond(id, accept: false);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _pairing.Cancel();

    private void OnDialogClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isInitiator)
        {
            _pairing.Cancel();
        }
        else if (_requestId is { } id)
        {
            _pairing.Respond(id, accept: false);
        }
    }
}
