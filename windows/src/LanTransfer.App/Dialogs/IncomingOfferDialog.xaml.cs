using System.Windows;
using LanTransfer.App.Themes;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Protocol;

namespace LanTransfer.App.Dialogs;

/// <summary>非模态文件接收确认对话框（每个 File_Offer 一个，需求 15.1、15.3、15.6、15.8）。</summary>
public partial class IncomingOfferDialog : Window
{
    private readonly FileTransferEngine _files;
    private readonly TransferId _transferId;
    private bool _decided;

    public IncomingOfferDialog(FileTransferEngine files, TransferTaskView view)
    {
        InitializeComponent();
        _files = files;
        _transferId = view.Id;
        ThemeManager.ApplyTitleBar(this);

        var t = view.Task;
        TitleText.Text = UiText.LabelIncomingOfferFrom(t.PeerName);
        var total = t.Files.Sum(f => f.Size);
        SummaryText.Text = $"{UiText.LabelFileCount(t.Files.Count)} · {UiText.LabelTotalSize(SizeFormatter.Format(total))}";
        FileList.ItemsSource = t.Files.Select(f => new { Name = f.OriginalName, SizeText = SizeFormatter.Format(f.Size) }).ToList();
        Closing += (_, _) =>
        {
            if (!_decided)
            {
                _decided = true;
                _files.Reject(_transferId);
            }
        };
    }

    public void CloseBecauseEnded()
    {
        _decided = true;
        Close();
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        if (_decided)
        {
            return;
        }

        _decided = true;
        _files.Accept(_transferId);
        Close();
    }

    private void OnRejectClick(object sender, RoutedEventArgs e)
    {
        if (_decided)
        {
            return;
        }

        _decided = true;
        _files.Reject(_transferId);
        Close();
    }
}
