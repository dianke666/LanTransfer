using System.ComponentModel;
using System.Windows;
using LanTransfer.App.Dialogs;
using LanTransfer.App.Services;
using LanTransfer.App.Themes;
using LanTransfer.App.ViewModels;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.App;

/// <summary>主窗口：承载侧边栏、页面视图与全部自定义中文对话框协调逻辑。</summary>
public partial class MainWindow : Window
{
    private readonly AppController _app;
    private readonly Dictionary<TransferId, IncomingOfferDialog> _offerDialogs = [];
    private PairingDialog? _pairingDialog;
    private QrPairingDialog? _qrDialog;

    public MainWindow(AppController app)
    {
        InitializeComponent();
        _app = app;

        ViewModel = new MainViewModel(
            app,
            ConfirmSendAsTxtAsync,
            ConfirmReplaceSurrogateAsync,
            ConfirmDeleteFileAsync,
            ConfirmClearAllAsync,
            ConfirmUnpairAsync,
            HandleOpenFailure,
            HandleLocateResult);

        DataContext = ViewModel;

        ViewModel.OpenQrDialogRequested += ShowQrDialog;
        ViewModel.OpenManualConnectRequested += ShowManualConnectDialog;
        ViewModel.PairingUiStateChanged += OnPairingUiStateChanged;
        ViewModel.IncomingOfferArrived += OnIncomingOfferArrived;
        ViewModel.IncomingOfferEnded += OnIncomingOfferEnded;

        ThemeManager.ApplyTitleBar(this);
        _app.Lifecycle.Attach(this);
        Activated += (_, _) => _app.Notifier.RefreshEnabled();
    }

    public MainViewModel ViewModel { get; }

    public bool AllowClose { get; set; }

    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // 需求 21.3：关闭主窗口最小化到托盘并继续后台监听
            e.Cancel = true;
            Hide();
            _app.Lifecycle.Recompute();
            return;
        }

        foreach (var dlg in _offerDialogs.Values.ToList())
        {
            dlg.CloseBecauseEnded();
        }

        _offerDialogs.Clear();
        ClosePairingDialogSilently();
        _qrDialog?.Close();
        ViewModel.Dispose();
        base.OnClosing(e);
    }

    private void ShowQrDialog()
    {
        if (_app.Services is not { } svc)
        {
            ViewModel.ShowToast(UiText.BannerListenFailed);
            return;
        }

        if (_qrDialog is not null)
        {
            _qrDialog.Activate();
            return;
        }

        _qrDialog = new QrPairingDialog(svc.Pairing) { Owner = this };
        _qrDialog.Closed += (_, _) => _qrDialog = null;
        _qrDialog.Show();
    }

    private void ShowManualConnectDialog()
    {
        if (_app.Services is not { } svc)
        {
            ViewModel.ShowToast(UiText.BannerListenFailed);
            return;
        }

        var dlg = new ManualConnectDialog(
            svc.Pairing,
            (id, name, type, ipText) => ViewModel.OpenConversation(id, name, type, ipText),
            f => HandleOpenFailure(f, null))
        {
            Owner = this,
        };
        dlg.ShowDialog();
    }

    private void OnPairingUiStateChanged(PairingUiState state)
    {
        if (state is PairingUiState.Idle)
        {
            ClosePairingDialogSilently();
            return;
        }

        if (_app.Services is not { } svc)
        {
            return;
        }

        if (_pairingDialog is null)
        {
            _pairingDialog = new PairingDialog(svc.Pairing) { Owner = IsVisible ? this : null };
            _pairingDialog.Closed += (_, _) => _pairingDialog = null;
            _pairingDialog.UpdateState(state);
            _pairingDialog.Show();
        }
        else
        {
            _pairingDialog.UpdateState(state);
            if (!_pairingDialog.IsActive)
            {
                _pairingDialog.Activate();
            }
        }
    }

    private void ClosePairingDialogSilently()
    {
        if (_pairingDialog is null)
        {
            return;
        }

        var dlg = _pairingDialog;
        _pairingDialog = null;
        dlg.UpdateState(PairingUiState.Idle.Instance);
        dlg.Close();
    }

    private void OnIncomingOfferArrived(TransferTaskView view)
    {
        if (_app.Services is not { } svc || _offerDialogs.ContainsKey(view.Id))
        {
            return;
        }

        var dlg = new IncomingOfferDialog(svc.Files, view)
        {
            Owner = IsVisible ? this : null,
        };
        _offerDialogs[view.Id] = dlg;
        dlg.Closed += (_, _) => _offerDialogs.Remove(view.Id);
        dlg.Show();
    }

    private void OnIncomingOfferEnded(TransferId id)
    {
        if (_offerDialogs.Remove(id, out var dlg))
        {
            dlg.CloseBecauseEnded();
        }
    }

    private Task<bool> ConfirmSendAsTxtAsync(string message)
    {
        var outcome = AppDialog.ShowDialog(
            this,
            UiText.AppName,
            message,
            UiText.BtnSendAsTxt,
            cancelText: UiText.BtnCancel,
            tone: DialogTone.Warning);
        return Task.FromResult(outcome == DialogOutcome.Primary);
    }

    private Task<bool> ConfirmReplaceSurrogateAsync()
    {
        var outcome = AppDialog.ShowDialog(
            this,
            UiText.DialogUnpairedSurrogate,
            UiText.DialogUnpairedSurrogateDesc,
            UiText.BtnReplaceAndSend,
            cancelText: UiText.BtnCancel,
            tone: DialogTone.Warning);
        return Task.FromResult(outcome == DialogOutcome.Primary);
    }

    private Task<DeleteFileChoice> ConfirmDeleteFileAsync(HistoryItemVm item, bool isInterrupted)
    {
        if (isInterrupted)
        {
            var r = AppDialog.ShowDialog(
                this,
                UiText.DialogDeleteFileTitle,
                UiText.DialogDeleteInterrupted,
                UiText.BtnDelete,
                cancelText: UiText.BtnCancel,
                tone: DialogTone.Danger);
            return Task.FromResult(r == DialogOutcome.Primary ? DeleteFileChoice.RecordOnly : DeleteFileChoice.Cancel);
        }

        var outcome = AppDialog.ShowDialog(
            this,
            UiText.DialogDeleteFileTitle,
            UiText.DialogDeleteFileDesc,
            UiText.BtnDeleteWithFile,
            secondaryText: UiText.BtnDeleteRecordOnly,
            cancelText: UiText.BtnCancel,
            tone: DialogTone.Danger);

        return Task.FromResult(outcome switch
        {
            DialogOutcome.Primary => DeleteFileChoice.RecordAndLocalFile,
            DialogOutcome.Secondary => DeleteFileChoice.RecordOnly,
            _ => DeleteFileChoice.Cancel,
        });
    }

    private Task<bool> ConfirmClearAllAsync(int unfinishedCount)
    {
        var msg = unfinishedCount > 0
            ? UiText.DialogClearHistoryUnfinished(unfinishedCount)
            : UiText.DialogClearHistory;
        var outcome = AppDialog.ShowDialog(
            this,
            UiText.DialogClearHistoryTitle,
            msg,
            UiText.BtnClear,
            cancelText: UiText.BtnCancel,
            tone: DialogTone.Danger);
        return Task.FromResult(outcome == DialogOutcome.Primary);
    }

    private Task<bool> ConfirmUnpairAsync(string peerName)
    {
        var outcome = AppDialog.ShowDialog(
            this,
            UiText.DialogUnpairTitle,
            UiText.DialogUnpairDesc(peerName),
            UiText.BtnUnpair,
            cancelText: UiText.BtnCancel,
            tone: DialogTone.Danger);
        return Task.FromResult(outcome == DialogOutcome.Primary);
    }

    public void HandleOpenFailure(OpenFailure failure, DeviceId? peer)
    {
        switch (failure)
        {
            case OpenFailure.KeyMismatch km:
            {
                var key = $"km:{km.Name}:{peer?.Value.ToHex()}";
                if (_app.ShouldShowSecurityWarning(key))
                {
                    AppDialog.ShowDialog(
                        IsVisible ? this : null,
                        UiText.SecurityTitle,
                        UiText.SecurityKeyMismatch(km.Name),
                        UiText.BtnConfirm,
                        tone: DialogTone.Danger);
                }

                break;
            }

            case OpenFailure.PeerKeyMismatch pkm:
            {
                var key = $"pkm:{pkm.Name}:{peer?.Value.ToHex()}";
                if (_app.ShouldShowSecurityWarning(key))
                {
                    AppDialog.ShowDialog(
                        IsVisible ? this : null,
                        UiText.SecurityTitle,
                        UiText.SecurityKeyMismatch(pkm.Name),
                        UiText.BtnConfirm,
                        tone: DialogTone.Danger);
                }

                break;
            }

            case OpenFailure.HandshakeAuthFailed:
            {
                if (_app.ShouldShowSecurityWarning("auth_failed"))
                {
                    AppDialog.ShowDialog(
                        IsVisible ? this : null,
                        UiText.SecurityTitle,
                        UiText.SecurityAuthFailed,
                        UiText.BtnConfirm,
                        tone: DialogTone.Danger);
                }

                break;
            }

            case OpenFailure.VersionIncompatible vi:
                AppDialog.ShowDialog(
                    IsVisible ? this : null,
                    UiText.AppName,
                    $"{UiText.ErrorVersionIncompatible}\n{UiText.LabelVersionDetail(vi.Local, vi.Remote)}",
                    UiText.BtnConfirm,
                    tone: DialogTone.Warning);
                break;

            case OpenFailure.NotTrusted:
            {
                var outcome = AppDialog.ShowDialog(
                    IsVisible ? this : null,
                    UiText.AppName,
                    UiText.ErrorPeerUnpaired,
                    UiText.BtnRepairPair,
                    cancelText: UiText.BtnCancel,
                    tone: DialogTone.Warning);
                if (outcome == DialogOutcome.Primary && peer is { } pid && _app.Services is { } svc)
                {
                    _ = svc.Pairing.PairAsync(new Target.Device(pid));
                }

                break;
            }

            default:
                ViewModel.ShowToast(UiTextMapper.OpenFailureMessage(failure));
                break;
        }
    }

    public void HandleLocateResult(LocateResult result, StoredFileRef? file, DirRef? dir)
    {
        switch (result)
        {
            case LocateResult.Ok:
                break;

            case LocateResult.NoHandler:
            {
                var outcome = AppDialog.ShowDialog(
                    this,
                    UiText.AppName,
                    UiText.ErrorNoFileHandler,
                    UiText.BtnRevealInFolder,
                    cancelText: UiText.BtnClose,
                    tone: DialogTone.Info);
                if (outcome == DialogOutcome.Primary && file is { } f)
                {
                    HandleLocateResult(_app.FileLocator.RevealInFolder(f), file, dir);
                }

                break;
            }

            case LocateResult.NotFound:
            {
                var outcome = AppDialog.ShowDialog(
                    this,
                    UiText.AppName,
                    UiText.ErrorFileNotFound,
                    UiText.BtnOpenReceiveDir,
                    cancelText: UiText.BtnClose,
                    tone: DialogTone.Warning);
                if (outcome == DialogOutcome.Primary)
                {
                    var targetDir = dir ?? _app.SettingsStore.Current().ReceiveDirectory;
                    HandleLocateResult(_app.FileLocator.OpenDirectory(targetDir), null, targetDir);
                }

                break;
            }

            case LocateResult.NoAccess:
            {
                var targetDir = dir ?? _app.SettingsStore.Current().ReceiveDirectory;
                var displayPath = file?.Value ?? _app.ReceiveStorage.ResolvePath(targetDir);
                var outcome = AppDialog.ShowDialog(
                    this,
                    UiText.AppName,
                    UiText.ErrorReceiveDirInaccessible,
                    UiText.BtnCopyPath,
                    cancelText: UiText.BtnClose,
                    tone: DialogTone.Warning);
                if (outcome == DialogOutcome.Primary)
                {
                    _ = CopyPathAndToastAsync(displayPath);
                }

                break;
            }
        }
    }

    private async Task CopyPathAndToastAsync(string path)
    {
        var r = await _app.Clipboard.WriteTextAsync(path);
        ViewModel.ShowToast(r is ClipWrite.Ok ? UiText.ToastCopied : UiText.ToastClipboardFailed);
    }
}
