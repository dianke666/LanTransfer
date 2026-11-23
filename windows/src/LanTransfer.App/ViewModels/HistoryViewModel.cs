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

public enum HistoryFilter
{
    All,
    Text,
    File,
}

/// <summary>删除文件记录的用户选择（需求 20.4、20.10）。</summary>
public enum DeleteFileChoice
{
    Cancel,
    RecordOnly,
    RecordAndLocalFile,
}

/// <summary>传输记录 ViewModel（设计 C8.2、需求 20）。</summary>
public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly AppController _app;
    private readonly Action<string> _showToast;
    private readonly Func<HistoryItemVm, bool, Task<DeleteFileChoice>> _confirmDeleteFile;
    private readonly Func<int, Task<bool>> _confirmClearAll;
    private readonly Action<LocateResult, StoredFileRef?, DirRef?> _handleLocateResult;

    private readonly List<HistoryItemVm> _all = [];
    private readonly List<IDisposable> _subs = [];
    private Dictionary<TransferId, TaskState> _liveStates = new();

    public HistoryViewModel(
        AppController app,
        Action<string> showToast,
        Func<HistoryItemVm, bool, Task<DeleteFileChoice>> confirmDeleteFile,
        Func<int, Task<bool>> confirmClearAll,
        Action<LocateResult, StoredFileRef?, DirRef?> handleLocateResult)
    {
        _app = app;
        _showToast = showToast;
        _confirmDeleteFile = confirmDeleteFile;
        _confirmClearAll = confirmClearAll;
        _handleLocateResult = handleLocateResult;

        if (_app.Services is { } svc)
        {
            _subs.Add(svc.Files.Tasks.Subscribe(tasks =>
            {
                _liveStates = tasks.ToDictionary(t => t.Id, t => t.Task.State);
                Dispatch(Reload);
            }));
        }

        Reload();
    }

    public ObservableCollection<HistoryItemVm> Items { get; } = [];

    [ObservableProperty]
    private HistoryFilter _filter = HistoryFilter.All;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _hasStoreError;

    public bool IsEmpty => Items.Count == 0;

    public bool IsFilterAll => Filter == HistoryFilter.All;

    public bool IsFilterText => Filter == HistoryFilter.Text;

    public bool IsFilterFile => Filter == HistoryFilter.File;

    partial void OnFilterChanged(HistoryFilter value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterText));
        OnPropertyChanged(nameof(IsFilterFile));
        ApplyFilter();
    }

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SetFilter(string name)
    {
        Filter = name switch
        {
            "text" => HistoryFilter.Text,
            "file" => HistoryFilter.File,
            _ => HistoryFilter.All,
        };
    }

    public void Reload()
    {
        _all.Clear();
        switch (_app.HistoryStore.List(10_000))
        {
            case ListResult<HistoryRecord>.Failed:
                HasStoreError = true;
                break;
            case ListResult<HistoryRecord>.Ok ok:
                HasStoreError = false;
                // 列表内最新置顶更利于桌面端浏览，或保留顺序（需求 20.2：存储按从早到晚返回，此处保留原始顺序）
                foreach (var rec in ok.Items)
                {
                    if (rec is HistoryRecord.Text tr)
                    {
                        var body = StrictUtf8.Decode(tr.Record.TextUtf8.Span) ?? string.Empty;
                        _all.Add(new HistoryItemVm
                        {
                            IsText = true,
                            TextRecordId = new RecordId(tr.Record.Seq),
                            TextKey = new TextKey(tr.Record.Peer, tr.Record.MessageId),
                            TimeText = UiTextMapper.FormatRecordTime(tr.Record.RecordedAt),
                            PeerName = tr.Record.PeerName,
                            DirectionText = tr.Record.Direction == Direction.Send ? UiText.LabelDirectionSend : UiText.LabelDirectionReceive,
                            StatusText = UiTextMapper.TextStatusLabel(tr.Record.Status),
                            TitleText = TextRules.NotificationPreview(body),
                            FullText = body,
                            CanOpenFile = false,
                            CanDelete = true,
                            IsInterruptedTask = false,
                            IsReceivedCompleted = false,
                        });
                    }
                    else if (rec is HistoryRecord.File fr)
                    {
                        var f = fr.Record.File;
                        var liveState = _liveStates.TryGetValue(f.TransferId, out var ls) ? ls : fr.Record.TaskState;
                        var blockedDelete = liveState is TaskState.Offering or TaskState.AwaitingUser or TaskState.Running or TaskState.Resuming;
                        var receivedCompleted = fr.Record.Direction == Direction.Receive && f.State == FileState.Completed && f.StoredRef is not null;
                        _all.Add(new HistoryItemVm
                        {
                            IsText = false,
                            TransferId = f.TransferId,
                            FileIndex = f.Index,
                            TimeText = UiTextMapper.FormatRecordTime(fr.Record.RecordedAt),
                            PeerName = fr.Record.PeerName,
                            DirectionText = fr.Record.Direction == Direction.Send ? UiText.LabelDirectionSend : UiText.LabelDirectionReceive,
                            StatusText = UiTextMapper.FileDisplayStatus(f.State, liveState, fr.Record.Direction, fr.Record.Interrupt),
                            TitleText = f.OriginalName,
                            SizeText = SizeFormatter.Format(f.Size),
                            FullPath = receivedCompleted ? f.FullPath : null,
                            StoredRef = f.StoredRef,
                            Mime = f.Mime,
                            CanOpenFile = receivedCompleted,
                            CanDelete = !blockedDelete,
                            IsInterruptedTask = liveState == TaskState.Interrupted,
                            IsReceivedCompleted = receivedCompleted,
                        });
                    }
                }

                break;
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var q = SearchQuery.Trim();
        foreach (var item in _all)
        {
            if (Filter == HistoryFilter.Text && !item.IsText)
            {
                continue;
            }

            if (Filter == HistoryFilter.File && item.IsText)
            {
                continue;
            }

            if (q.Length > 0 &&
                !item.TitleText.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !item.PeerName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !(item.FullText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            Items.Add(item);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task CopyTextAsync(HistoryItemVm? item)
    {
        if (item?.FullText is null)
        {
            return;
        }

        var r = await _app.Clipboard.WriteTextAsync(item.FullText);
        if (r is ClipWrite.Ok)
        {
            _showToast(item.FullText.Contains('\0') ? UiText.ToastCopiedWithNul : UiText.ToastCopied);
        }
        else
        {
            _showToast(UiText.ToastClipboardFailed);
        }
    }

    [RelayCommand]
    private async Task CopyPathAsync(HistoryItemVm? item)
    {
        if (item?.FullPath is null)
        {
            return;
        }

        var r = await _app.Clipboard.WriteTextAsync(item.FullPath);
        _showToast(r is ClipWrite.Ok ? UiText.ToastCopied : UiText.ToastClipboardFailed);
    }

    [RelayCommand]
    private void OpenFile(HistoryItemVm? item)
    {
        if (item?.StoredRef is not { } sf)
        {
            return;
        }

        var r = _app.FileLocator.OpenFile(sf, item.Mime);
        _handleLocateResult(r, sf, null);
    }

    [RelayCommand]
    private void RevealFile(HistoryItemVm? item)
    {
        if (item?.StoredRef is not { } sf)
        {
            return;
        }

        var r = _app.FileLocator.RevealInFolder(sf);
        _handleLocateResult(r, sf, null);
    }

    [RelayCommand]
    private async Task DeleteItemAsync(HistoryItemVm? item)
    {
        if (item is null || !item.CanDelete)
        {
            return;
        }

        if (item.IsText && item.TextRecordId is { } rid)
        {
            _app.HistoryStore.Delete(rid);
            if (item.TextKey is { } key && _app.Services is { } svc)
            {
                await svc.Text.ForgetAsync(new HashSet<TextKey> { key });
            }

            Reload();
            return;
        }

        if (item.TransferId is not { } tid)
        {
            return;
        }

        var localFileExists = item.IsReceivedCompleted && !string.IsNullOrEmpty(item.FullPath) && File.Exists(item.FullPath);
        DeleteFileChoice choice;
        if (item.IsInterruptedTask || localFileExists)
        {
            choice = await _confirmDeleteFile(item, localFileExists);
            if (choice == DeleteFileChoice.Cancel)
            {
                return;
            }
        }
        else
        {
            choice = DeleteFileChoice.RecordOnly;
        }

        // 20.10：已中断任务先放弃整个任务
        if (item.IsInterruptedTask && _app.Services is { } fsvc)
        {
            await fsvc.Files.AbandonAsync(tid);
        }

        if (choice == DeleteFileChoice.RecordAndLocalFile && !string.IsNullOrEmpty(item.FullPath))
        {
            try
            {
                if (File.Exists(item.FullPath))
                {
                    File.Delete(item.FullPath);
                }
            }
            catch
            {
                // 20.4：本地文件删除失败时保留该记录
                _showToast(UiText.ToastDeleteLocalFailed);
                Reload();
                return;
            }
        }

        _app.HistoryStore.DeleteFileRecord(tid, item.FileIndex);
        Reload();
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        var unfinished = _app.Services?.Files.UnfinishedCount.Value ?? 0;
        if (!await _confirmClearAll(unfinished))
        {
            return;
        }

        if (_app.Services is { } svc)
        {
            await svc.Files.AbandonAllAsync();
            _app.HistoryStore.ClearAll();
            await svc.Text.ForgetAllAsync();
        }
        else
        {
            _app.HistoryStore.ClearAll();
        }

        Reload();
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
