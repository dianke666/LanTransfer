using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanTransfer.App.Services;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.App.ViewModels;

/// <summary>会话界面 ViewModel（设计 C8.2）：文字收发、复制/粘贴/发送剪贴板、文件待发送与拖放、实时传输卡片。</summary>
public sealed partial class ConversationViewModel : ObservableObject, IDisposable
{
    private readonly AppController _app;
    private readonly Action<string> _showToast;
    private readonly Func<string, Task<bool>> _confirmSendAsTxt;
    private readonly Func<Task<bool>> _confirmReplaceSurrogate;
    private readonly Action<OpenFailure, DeviceId?> _handleOpenFailure;
    private readonly Action<LocateResult, StoredFileRef?, DirRef?> _handleLocateResult;

    private readonly Dictionary<TextKey, TextBubbleVm> _bubbleCache = new();
    private readonly List<IDisposable> _subs = [];

    public ConversationViewModel(
        AppController app,
        DeviceId peerId,
        string peerName,
        DeviceType peerType,
        string peerIp,
        Action<string> showToast,
        Func<string, Task<bool>> confirmSendAsTxt,
        Func<Task<bool>> confirmReplaceSurrogate,
        Action<OpenFailure, DeviceId?> handleOpenFailure,
        Action<LocateResult, StoredFileRef?, DirRef?> handleLocateResult)
    {
        _app = app;
        PeerId = peerId;
        _peerName = peerName;
        _peerType = peerType;
        _peerIp = peerIp;
        _showToast = showToast;
        _confirmSendAsTxt = confirmSendAsTxt;
        _confirmReplaceSurrogate = confirmReplaceSurrogate;
        _handleOpenFailure = handleOpenFailure;
        _handleLocateResult = handleLocateResult;

        ReloadPendingFiles();
        BindEngine();
    }

    public DeviceId PeerId { get; }

    [ObservableProperty]
    private string _peerName;

    [ObservableProperty]
    private DeviceType _peerType;

    [ObservableProperty]
    private string _peerIp;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _hasNetwork = true;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isDragOver;

    public ObservableCollection<TextBubbleVm> Messages { get; } = [];

    public ObservableCollection<TransferTaskCardVm> Tasks { get; } = [];

    public ObservableCollection<PendingFileVm> PendingFiles { get; } = [];

    public bool HasMessages => Messages.Count > 0;

    public bool HasTasks => Tasks.Count > 0;

    public bool HasPendingFiles => PendingFiles.Count > 0;

    public bool IsEmpty => !HasMessages && !HasTasks;

    public bool CanSendText => HasNetwork && InputText.Length > 0;

    public string PendingSummary => UiText.LabelPendingFiles(PendingFiles.Count);

    public string ConnectionBadge => IsConnected ? UiText.LabelConnectedBadge : UiText.LabelTrustedBadge;

    public string PeerTypeLabel => UiTextMapper.DeviceTypeLabel(PeerType);

    public event Action<string>? InsertIntoInputRequested;

    partial void OnInputTextChanged(string value) => OnPropertyChanged(nameof(CanSendText));

    partial void OnHasNetworkChanged(bool value) => OnPropertyChanged(nameof(CanSendText));

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(ConnectionBadge));

    private void BindEngine()
    {
        _subs.Add(_app.Network.Interfaces.Subscribe(ifaces =>
            Dispatch(() => HasNetwork = ifaces.Count > 0)));

        if (_app.Services is not { } svc)
        {
            return;
        }

        _subs.Add(svc.Sessions.Sessions.Subscribe(map =>
            Dispatch(() => IsConnected = map.ContainsKey(PeerId))));

        _subs.Add(svc.Text.Conversation(PeerId).Subscribe(items =>
            Dispatch(() => SyncMessages(items))));

        _subs.Add(svc.Files.Tasks.Subscribe(all =>
            Dispatch(() => SyncTasks(all))));
    }

    private void SyncMessages(ValueList<TextItem> items)
    {
        var seen = new HashSet<TextKey>();
        Messages.Clear();
        foreach (var item in items)
        {
            seen.Add(item.Key);
            if (_bubbleCache.TryGetValue(item.Key, out var existing))
            {
                existing.Status = item.Status;
                Messages.Add(existing);
                continue;
            }

            var full = TextDisplayMapper.Map(item.Body);
            DisplayText? collapsed = TextRules.Collapse(item.Body) is Collapse.Collapsed c
                ? TextDisplayMapper.Map(item.Body[..c.PrefixEnd])
                : null;
            var vm = new TextBubbleVm
            {
                Key = item.Key,
                Direction = item.Direction,
                RawText = item.Body,
                FullDisplay = full,
                CollapsedDisplay = collapsed,
                TimeText = UiTextMapper.FormatRecordTime(item.RecordedAt),
                Status = item.Status,
            };
            _bubbleCache[item.Key] = vm;
            Messages.Add(vm);
        }

        foreach (var stale in _bubbleCache.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _bubbleCache.Remove(stale);
        }

        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SyncTasks(ValueList<TransferTaskView> all)
    {
        Tasks.Clear();
        foreach (var tv in all.Where(t => t.Task.Peer == PeerId).OrderByDescending(t => t.Task.RecordedAt.UtcMs))
        {
            var t = tv.Task;
            var rows = new List<TransferFileRowVm>(t.Files.Count);
            for (var i = 0; i < t.Files.Count; i++)
            {
                var f = t.Files[i];
                var fp = i < tv.Files.Count ? tv.Files[i] : new Progress(f.State == FileState.Completed ? 100 : 0, f.Persisted, f.Size, 0, null);
                var canOpen = t.Direction == Direction.Receive && f.State == FileState.Completed && f.StoredRef is not null;
                rows.Add(new TransferFileRowVm
                {
                    Index = f.Index,
                    Name = f.FinalName ?? f.OriginalName,
                    SizeText = SizeFormatter.Format(f.Size),
                    StatusText = UiTextMapper.FileDisplayStatus(f.State, t.State, t.Direction, t.Interrupt),
                    Percent = fp.Percent,
                    CanOpen = canOpen,
                    StoredRef = f.StoredRef,
                    Mime = f.Mime,
                    FullPath = f.FullPath,
                });
            }

            var dirLabel = t.Direction == Direction.Send ? UiText.LabelDirectionSend : UiText.LabelDirectionReceive;
            var totalSize = t.Files.Sum(f => f.Size);
            var speedEta = t.State == TaskState.Running
                ? $"{UiText.LabelSpeed(UiTextMapper.FormatSpeed(tv.Progress.BytesPerSec))} · {UiText.LabelEta(UiTextMapper.FormatEta(tv.Progress.EtaSec))}"
                : UiText.LabelTotalSize(SizeFormatter.Format(totalSize));

            Tasks.Add(new TransferTaskCardVm
            {
                Id = t.TransferId,
                Direction = t.Direction,
                State = t.State,
                HeaderText = $"{dirLabel} · {UiText.LabelFileCount(t.Files.Count)}",
                StatusText = UiTextMapper.TaskStateLabel(t.State, t.Direction, t.Interrupt, tv.CheckingPrefix),
                ProgressSummary = $"{SizeFormatter.Format(tv.Progress.Transferred)} / {SizeFormatter.Format(tv.Progress.Total)} ({tv.Progress.Percent}%)",
                SpeedEtaText = speedEta,
                Percent = tv.Progress.Percent,
                CanCancel = t.State is TaskState.Offering or TaskState.Running or TaskState.Resuming or TaskState.Interrupted,
                CanResume = t.State == TaskState.Interrupted,
                Files = rows,
            });
        }

        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task SendTextAsync()
    {
        if (!CanSendText)
        {
            return;
        }

        var text = InputText;
        if (await SendTextCoreAsync(text, clearInputOnSuccess: true))
        {
            InputText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task PasteAsync()
    {
        var read = await _app.Clipboard.ReadTextAsync();
        switch (read)
        {
            case ClipRead.Text t:
                InsertIntoInputRequested?.Invoke(t.Body);
                break;
            case ClipRead.NoText:
                _showToast(UiText.ToastClipboardEmpty);
                break;
            default:
                _showToast(UiText.ToastClipboardFailed);
                break;
        }
    }

    [RelayCommand]
    private async Task SendClipboardAsync()
    {
        if (!HasNetwork)
        {
            return;
        }

        var read = await _app.Clipboard.ReadTextAsync();
        switch (read)
        {
            case ClipRead.Text t:
                await SendTextCoreAsync(t.Body, clearInputOnSuccess: false);
                break;
            case ClipRead.NoText:
                _showToast(UiText.ToastClipboardEmpty);
                break;
            default:
                _showToast(UiText.ToastClipboardFailed);
                break;
        }
    }

    private async Task<bool> SendTextCoreAsync(string text, bool clearInputOnSuccess)
    {
        if (_app.Services is not { } svc)
        {
            return false;
        }

        var res = await svc.Text.SendAsync(PeerId, text);
        switch (res)
        {
            case SendCheck.Sent:
                return true;
            case SendCheck.HasUnpairedSurrogate:
                if (await _confirmReplaceSurrogate())
                {
                    var replaced = TextRules.ReplaceUnpaired(text);
                    var retry = await svc.Text.SendAsync(PeerId, replaced);
                    if (retry is SendCheck.TooLong)
                    {
                        return await HandleTooLongAsync(replaced, clearInputOnSuccess);
                    }

                    return retry is SendCheck.Sent;
                }

                return false;
            case SendCheck.TooLong:
                return await HandleTooLongAsync(text, clearInputOnSuccess);
            default:
                return false;
        }
    }

    private async Task<bool> HandleTooLongAsync(string text, bool clearInput)
    {
        if (_app.Services is not { } svc)
        {
            return false;
        }

        if (!await _confirmSendAsTxt(UiText.DialogTextTooLong))
        {
            return false;
        }

        if (svc.Text.WriteTextFile(text) is not WriteResult<SourceRef>.Ok ok)
        {
            _showToast(UiText.StatusFileWriteFailed);
            return false;
        }

        var send = await svc.Files.SendAsync(PeerId, [ok.Value]);
        if (send is SendOfferResult.ConnectFailed cf)
        {
            _handleOpenFailure(cf.Failure, PeerId);
            return false;
        }

        return clearInput && send is SendOfferResult.Sent;
    }

    [RelayCommand]
    private void RetryText(TextBubbleVm? bubble)
    {
        if (bubble is not null)
        {
            _app.Services?.Text.Retry(bubble.Key.MessageId);
        }
    }

    [RelayCommand]
    private void ToggleExpand(TextBubbleVm? bubble)
    {
        if (bubble is not null)
        {
            bubble.IsExpanded = !bubble.IsExpanded;
        }
    }

    [RelayCommand]
    private async Task CopyBubbleAsync(TextBubbleVm? bubble)
    {
        if (bubble is null)
        {
            return;
        }

        await CopyRawTextAsync(bubble.RawText);
    }

    public async Task CopyRawTextAsync(string rawText)
    {
        var r = await _app.Clipboard.WriteTextAsync(rawText);
        if (r is ClipWrite.Ok)
        {
            _showToast(rawText.Contains('\0') ? UiText.ToastCopiedWithNul : UiText.ToastCopied);
        }
        else
        {
            _showToast(UiText.ToastClipboardFailed);
        }
    }

    [RelayCommand]
    private void PickFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = UiText.BtnSendFiles,
        };
        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            AddFilePaths(dlg.FileNames);
        }
    }

    public void AddFilePaths(IEnumerable<string> paths)
    {
        var current = _app.GetPendingFiles(PeerId);
        var incoming = new List<PickedItem>();
        foreach (var path in paths)
        {
            var isDir = Directory.Exists(path);
            incoming.Add(new PickedItem(new SourceRef(path), isDir));
        }

        var outcome = PendingListRules.Add(current, incoming);
        if (outcome.FoldersSkipped)
        {
            _showToast(UiText.ErrorFoldersNotSupported);
        }

        if (outcome.TooMany)
        {
            _showToast(UiText.ErrorTooManyFiles);
            return;
        }

        _app.SetPendingFiles(PeerId, outcome.List);
        ReloadPendingFiles();
    }

    [RelayCommand]
    private void RemovePendingFile(PendingFileVm? item)
    {
        if (item is null)
        {
            return;
        }

        var list = _app.GetPendingFiles(PeerId).Where(r => r != item.Ref).ToList();
        _app.SetPendingFiles(PeerId, list);
        ReloadPendingFiles();
    }

    [RelayCommand]
    private void ClearPendingFiles()
    {
        _app.SetPendingFiles(PeerId, []);
        ReloadPendingFiles();
    }

    [RelayCommand]
    private async Task SendPendingFilesAsync()
    {
        if (!HasNetwork || _app.Services is not { } svc)
        {
            return;
        }

        var refs = _app.GetPendingFiles(PeerId);
        if (refs.Count == 0)
        {
            return;
        }

        var result = await svc.Files.SendAsync(PeerId, refs);
        switch (result)
        {
            case SendOfferResult.Sent:
                _app.SetPendingFiles(PeerId, []);
                ReloadPendingFiles();
                break;
            case SendOfferResult.ConnectFailed cf:
                _handleOpenFailure(cf.Failure, PeerId);
                break;
            case SendOfferResult.TooMany:
                _showToast(UiText.ErrorTooManyFiles);
                break;
            case SendOfferResult.Unreadable:
                _showToast(UiText.ErrorFileSizeUnknown);
                break;
        }
    }

    [RelayCommand]
    private void CancelTask(TransferTaskCardVm? card)
    {
        if (card is not null)
        {
            _app.Services?.Files.Cancel(card.Id);
        }
    }

    [RelayCommand]
    private async Task ResumeTaskAsync(TransferTaskCardVm? card)
    {
        if (card is null || _app.Services is not { } svc)
        {
            return;
        }

        var res = await svc.Files.ResumeAsync(card.Id);
        if (res is ResumeResult.ConnectFailed cf)
        {
            _handleOpenFailure(cf.Failure, PeerId);
        }
    }

    [RelayCommand]
    private void OpenFile(TransferFileRowVm? row)
    {
        if (row?.StoredRef is not { } sf)
        {
            return;
        }

        var r = _app.FileLocator.OpenFile(sf, row.Mime);
        _handleLocateResult(r, sf, null);
    }

    [RelayCommand]
    private void RevealFile(TransferFileRowVm? row)
    {
        if (row?.StoredRef is not { } sf)
        {
            return;
        }

        var r = _app.FileLocator.RevealInFolder(sf);
        _handleLocateResult(r, sf, null);
    }

    private void ReloadPendingFiles()
    {
        PendingFiles.Clear();
        foreach (var sr in _app.GetPendingFiles(PeerId))
        {
            var meta = _app.SourceFiles.Describe(sr);
            PendingFiles.Add(new PendingFileVm
            {
                Ref = sr,
                Name = meta?.Name ?? Path.GetFileName(sr.Value),
                SizeText = meta is not null ? SizeFormatter.Format(meta.Size) : UiText.LabelEtaUnknown,
            });
        }

        OnPropertyChanged(nameof(HasPendingFiles));
        OnPropertyChanged(nameof(PendingSummary));
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
