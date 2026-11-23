using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using LanTransfer.App.Themes;
using LanTransfer.Domain;
using LanTransfer.Engine;
using QRCoder;

namespace LanTransfer.App.Dialogs;

/// <summary>二维码配对展示对话框（设计 C7.5、需求 8.1、8.7、8.13）。</summary>
public partial class QrPairingDialog : Window
{
    private readonly PairingService _pairing;
    private readonly CancellationTokenSource _cts = new();

    public QrPairingDialog(PairingService pairing)
    {
        InitializeComponent();
        _pairing = pairing;
        ThemeManager.ApplyTitleBar(this);
        Loaded += (_, _) => _ = RunLoopAsync();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _cts.Dispose();
        };
    }

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (var state in _pairing.QrScreenAsync(_cts.Token))
            {
                Dispatcher.Invoke(() => Render(state));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Render(QrScreenState state)
    {
        if (state is QrScreenState.Showing s)
        {
            QrImage.Source = GenerateQrBitmap(s.Text);
            QrImage.Visibility = Visibility.Visible;
            NoNetworkPanel.Visibility = Visibility.Collapsed;
            var ips = string.Join("  ·  ", s.Payload.Ipv4List.Select(bits => $"{new Ipv4(bits)}:{s.Payload.Port}"));
            AddressesText.Text = ips;
        }
        else
        {
            QrImage.Source = null;
            QrImage.Visibility = Visibility.Collapsed;
            NoNetworkPanel.Visibility = Visibility.Visible;
            AddressesText.Text = string.Empty;
        }
    }

    private static BitmapImage GenerateQrBitmap(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(8);
        using var ms = new MemoryStream(bytes);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
