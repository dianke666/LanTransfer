using CommunityToolkit.Mvvm.ComponentModel;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.App.ViewModels;

public enum NavPage
{
    Overview,
    Conversation,
    History,
    Settings,
}

public sealed partial class DeviceListItemVm : ObservableObject
{
    public required DeviceId DeviceId { get; init; }

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private DeviceType _deviceType;

    [ObservableProperty]
    private string _ipText = string.Empty;

    [ObservableProperty]
    private bool _isTrusted;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _isSelected;

    public string DeviceTypeLabel => UiTextMapper.DeviceTypeLabel(DeviceType);

    public string StatusBadgeText => IsConnected
        ? UiText.LabelConnectedBadge
        : IsTrusted
            ? UiText.LabelTrustedBadge
            : UiText.LabelUntrustedBadge;
}

public sealed partial class TextBubbleVm : ObservableObject
{
    public required TextKey Key { get; init; }

    public required Direction Direction { get; init; }

    public required string RawText { get; init; }

    public required DisplayText FullDisplay { get; init; }

    public required DisplayText? CollapsedDisplay { get; init; }

    public required string TimeText { get; init; }

    [ObservableProperty]
    private TextStatus _status;

    [ObservableProperty]
    private bool _isExpanded;

    public bool IsOutgoing => Direction == Direction.Send;

    public bool IsCollapsible => CollapsedDisplay is not null;

    public DisplayText ActiveDisplay => (!IsExpanded && CollapsedDisplay is not null) ? CollapsedDisplay : FullDisplay;

    public string VisibleText => ActiveDisplay.Display;

    public string ExpandButtonText => IsExpanded ? UiText.BtnCollapse : UiText.BtnExpand;

    public string StatusText => UiTextMapper.TextStatusLabel(Status);

    public bool ShowRetry => Direction == Direction.Send && Status == TextStatus.Failed;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ActiveDisplay));
        OnPropertyChanged(nameof(VisibleText));
        OnPropertyChanged(nameof(ExpandButtonText));
    }

    partial void OnStatusChanged(TextStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ShowRetry));
    }
}

public sealed class TransferFileRowVm
{
    public required int Index { get; init; }

    public required string Name { get; init; }

    public required string SizeText { get; init; }

    public required string StatusText { get; init; }

    public required int Percent { get; init; }

    public required bool CanOpen { get; init; }

    public StoredFileRef? StoredRef { get; init; }

    public string Mime { get; init; } = "application/octet-stream";

    public string? FullPath { get; init; }
}

public sealed class TransferTaskCardVm
{
    public required TransferId Id { get; init; }

    public required Direction Direction { get; init; }

    public required TaskState State { get; init; }

    public required string HeaderText { get; init; }

    public required string StatusText { get; init; }

    public required string ProgressSummary { get; init; }

    public required string SpeedEtaText { get; init; }

    public required int Percent { get; init; }

    public required bool CanCancel { get; init; }

    public required bool CanResume { get; init; }

    public required IReadOnlyList<TransferFileRowVm> Files { get; init; }
}

public sealed class PendingFileVm
{
    public required SourceRef Ref { get; init; }

    public required string Name { get; init; }

    public required string SizeText { get; init; }
}

public sealed class HistoryItemVm
{
    public required bool IsText { get; init; }

    public RecordId? TextRecordId { get; init; }

    public TextKey? TextKey { get; init; }

    public TransferId? TransferId { get; init; }

    public int FileIndex { get; init; }

    public required string TimeText { get; init; }

    public required string PeerName { get; init; }

    public required string DirectionText { get; init; }

    public required string StatusText { get; init; }

    public required string TitleText { get; init; }

    public string? FullText { get; init; }

    public string? SizeText { get; init; }

    public string? FullPath { get; init; }

    public StoredFileRef? StoredRef { get; init; }

    public string Mime { get; init; } = "application/octet-stream";

    public required bool CanOpenFile { get; init; }

    public required bool CanDelete { get; init; }

    public required bool IsInterruptedTask { get; init; }

    public required bool IsReceivedCompleted { get; init; }
}

public sealed partial class TrustedDeviceItemVm : ObservableObject
{
    public required DeviceId DeviceId { get; init; }

    public required string DisplayName { get; init; }

    public required DeviceType DeviceType { get; init; }

    [ObservableProperty]
    private bool _autoAccept;

    public string DeviceTypeLabel => UiTextMapper.DeviceTypeLabel(DeviceType);
}
