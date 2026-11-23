using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.App;

/// <summary>引擎/领域/平台结果类型 → 界面文案（设计 C8.4、任务 23.5）。</summary>
public static class UiTextMapper
{
    public static string DeviceTypeLabel(DeviceType type) => type switch
    {
        DeviceType.Android => UiText.DeviceTypeAndroid,
        DeviceType.Windows => UiText.DeviceTypeWindows,
        DeviceType.Ios => UiText.DeviceTypeIos,
        _ => UiText.DeviceTypeAndroid,
    };

    public static string TextStatusLabel(TextStatus status) => status switch
    {
        TextStatus.Sending => UiText.StatusTextSending,
        TextStatus.Delivered => UiText.StatusTextDelivered,
        TextStatus.Failed => UiText.StatusTextFailed,
        TextStatus.Received => UiText.StatusTextReceived,
        _ => string.Empty,
    };

    public static string FileStateLabel(FileState state) => state switch
    {
        FileState.Waiting => UiText.StatusFileWaiting,
        FileState.Transferring => UiText.StatusFileTransferring,
        FileState.Verifying => UiText.StatusFileVerifying,
        FileState.WriteFailed => UiText.StatusFileWriteFailed,
        FileState.Completed => UiText.StatusFileCompleted,
        FileState.ReadFailed => UiText.StatusFileReadFailed,
        FileState.ChecksumFailed => UiText.StatusFileChecksumFailed,
        _ => string.Empty,
    };

    public static string InterruptLabel(InterruptInfo info) => info.Reason switch
    {
        InterruptReason.PeerUnresponsive => UiText.StatusInterruptPeerUnresponsive,
        InterruptReason.PeerClosed => UiText.StatusInterruptPeerClosed,
        InterruptReason.LocalNetworkLost => UiText.StatusInterruptLocalNetworkLost,
        InterruptReason.IntegrityFailed => UiText.StatusInterruptIntegrityFailed,
        InterruptReason.ProtocolError => UiText.StatusInterruptProtocolError,
        InterruptReason.WriteFailed => UiText.StatusInterruptWriteFailed(info.Detail ?? UiText.StatusFileWriteFailed),
        InterruptReason.AppExit => UiText.StatusInterruptAppExit,
        InterruptReason.AppKilled => UiText.StatusInterruptAppKilled,
        _ => UiText.StatusTaskInterrupted,
    };

    public static string TaskStateLabel(TaskState state, Direction direction, InterruptInfo? interrupt = null, bool checkingPrefix = false) => state switch
    {
        TaskState.Offering => UiText.StatusTaskOffering,
        TaskState.AwaitingUser => UiText.StatusTaskAwaitingUser,
        TaskState.Running => UiText.StatusTaskRunning,
        TaskState.Interrupted => interrupt is null ? UiText.StatusTaskInterrupted : $"{UiText.StatusTaskInterrupted}（{InterruptLabel(interrupt)}）",
        TaskState.Resuming => checkingPrefix ? UiText.StatusTaskVerifyingPrefix : UiText.StatusTaskResuming,
        TaskState.Completed => UiText.StatusTaskCompleted,
        TaskState.Rejected => direction == Direction.Send ? UiText.StatusTaskRejectedSender : UiText.StatusTaskRejectedReceiver,
        TaskState.NoResponse => direction == Direction.Send ? UiText.StatusTaskNoResponseSender : UiText.StatusTaskTimeoutReceiver,
        TaskState.NoSpace => UiText.StatusTaskNoSpace,
        TaskState.CannotSave => direction == Direction.Send ? UiText.StatusTaskCannotSaveSender : UiText.StatusTaskCannotSaveReceiver,
        TaskState.ConnectionLost => UiText.StatusTaskConnectionLost,
        TaskState.Cancelled => UiText.StatusTaskCancelled,
        TaskState.PeerCancelled => UiText.StatusTaskPeerCancelled,
        TaskState.OfferInvalid => UiText.StatusTaskOfferInvalid,
        _ => string.Empty,
    };

    public static string FileDisplayStatus(FileState fileState, TaskState taskState, Direction direction, InterruptInfo? interrupt) =>
        fileState is FileState.Completed or FileState.ReadFailed or FileState.ChecksumFailed or FileState.WriteFailed
            ? FileStateLabel(fileState)
            : taskState == TaskState.Running
                ? FileStateLabel(fileState)
                : TaskStateLabel(taskState, direction, interrupt);

    public static string OpenFailureMessage(OpenFailure failure, bool forSendFile = false, bool forResume = false) => failure switch
    {
        OpenFailure.NotLocal => UiText.ErrorNotLocal,
        OpenFailure.SelfConnection => UiText.ErrorConnectSelf,
        OpenFailure.BindFailed bf => bf.VpnSuspected ? UiText.ErrorBindFailedVpn : UiText.ErrorBindFailed,
        OpenFailure.NoVersionResponse => UiText.ErrorNoVersionResponse,
        OpenFailure.VersionIncompatible => UiText.ErrorVersionIncompatible,
        OpenFailure.HandshakeAuthFailed => UiText.SecurityAuthFailed,
        OpenFailure.KeyMismatch km => UiText.SecurityKeyMismatch(km.Name),
        OpenFailure.PeerKeyMismatch pkm => UiText.SecurityKeyMismatch(pkm.Name),
        OpenFailure.FingerprintMismatch => UiText.SecurityQrMismatch,
        OpenFailure.NotTrusted => UiText.ErrorPeerUnpaired,
        _ => forResume ? UiText.ErrorConnectResume : forSendFile ? UiText.ErrorConnectSendFile : UiText.ErrorConnectFailed,
    };

    public static string PairingResultMessage(PairingResult result, string? peerName, OpenFailure? failure) => result switch
    {
        PairingResult.Success => string.IsNullOrEmpty(peerName) ? UiText.ToastPairSuccess : UiText.ToastPairSuccessPeer(peerName),
        PairingResult.Rejected or PairingResult.AutoRejected => UiText.ToastPairRejected,
        PairingResult.Busy => UiText.ToastPairBusy,
        PairingResult.Timeout => UiText.ToastPairTimeout,
        PairingResult.TokenInvalid => UiText.ErrorQrTokenInvalid,
        PairingResult.ConnectFailed when failure is not null => OpenFailureMessage(failure),
        PairingResult.Cancelled => UiText.StatusTaskCancelled,
        _ => UiText.ToastPairFailed,
    };

    public static string LocateFailureMessage(LocateResult r) => r switch
    {
        LocateResult.NotFound => UiText.ErrorFileNotFound,
        LocateResult.NoHandler => UiText.ErrorNoFileHandler,
        LocateResult.NoAccess => UiText.ErrorReceiveDirInaccessible,
        _ => string.Empty,
    };

    public static string FormatEta(long? etaSeconds)
    {
        if (etaSeconds is null)
        {
            return UiText.LabelEtaUnknown;
        }

        var sec = Math.Max(0, etaSeconds.Value);
        if (sec < 60)
        {
            return $"{sec} 秒";
        }

        if (sec < 3600)
        {
            return $"{sec / 60} 分 {sec % 60} 秒";
        }

        return $"{sec / 3600} 小时 {(sec % 3600) / 60} 分";
    }

    public static string FormatSpeed(double bytesPerSec) =>
        $"{SizeFormatter.Format((long)Math.Max(0, bytesPerSec))}/s";

    public static string FormatRecordTime(RecordTime t)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(t.UtcMs).ToOffset(TimeSpan.FromMinutes(t.OffsetMin));
        return dto.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }
}
