package com.lantransfer.app.ui.text

import android.content.Context
import com.lantransfer.domain.Direction
import com.lantransfer.domain.FileState
import com.lantransfer.domain.InterruptInfo
import com.lantransfer.domain.InterruptReason
import com.lantransfer.domain.PairingResult
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.SizeFormatter
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TextStatus
import com.lantransfer.engine.OpenFailure
import com.lantransfer.engine.ports.LocateResult
import com.lantransfer.protocol.DeviceType
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Locale

/** 引擎 / 领域 / 平台结果类型 → 简体中文文案映射（设计 C8.4、任务 23.5）。 */
object UiTextMapper {
    private val timeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm", Locale.US)

    fun deviceTypeLabel(ctx: Context, type: DeviceType): String = when (type) {
        DeviceType.ANDROID -> UiText.deviceTypeAndroid(ctx)
        DeviceType.WINDOWS -> UiText.deviceTypeWindows(ctx)
        DeviceType.IOS -> UiText.deviceTypeIos(ctx)
    }

    fun textStatusLabel(ctx: Context, status: TextStatus): String = when (status) {
        TextStatus.SENDING -> UiText.statusTextSending(ctx)
        TextStatus.DELIVERED -> UiText.statusTextDelivered(ctx)
        TextStatus.FAILED -> UiText.statusTextFailed(ctx)
        TextStatus.RECEIVED -> UiText.statusTextReceived(ctx)
    }

    fun fileStateLabel(ctx: Context, state: FileState): String = when (state) {
        FileState.WAITING -> UiText.statusFileWaiting(ctx)
        FileState.TRANSFERRING -> UiText.statusFileTransferring(ctx)
        FileState.VERIFYING -> UiText.statusFileVerifying(ctx)
        FileState.WRITE_FAILED -> UiText.statusFileWriteFailed(ctx)
        FileState.COMPLETED -> UiText.statusFileCompleted(ctx)
        FileState.READ_FAILED -> UiText.statusFileReadFailed(ctx)
        FileState.CHECKSUM_FAILED -> UiText.statusFileChecksumFailed(ctx)
    }

    fun interruptLabel(ctx: Context, info: InterruptInfo): String = when (info.reason) {
        InterruptReason.PEER_UNRESPONSIVE -> UiText.statusInterruptPeerUnresponsive(ctx)
        InterruptReason.PEER_CLOSED -> UiText.statusInterruptPeerClosed(ctx)
        InterruptReason.LOCAL_NETWORK_LOST -> UiText.statusInterruptLocalNetworkLost(ctx)
        InterruptReason.INTEGRITY_FAILED -> UiText.statusInterruptIntegrityFailed(ctx)
        InterruptReason.PROTOCOL_ERROR -> UiText.statusInterruptProtocolError(ctx)
        InterruptReason.WRITE_FAILED -> UiText.statusInterruptWriteFailed(ctx, info.detail ?: UiText.statusFileWriteFailed(ctx))
        InterruptReason.APP_EXIT -> UiText.statusInterruptAppExit(ctx)
        InterruptReason.APP_KILLED -> UiText.statusInterruptAppKilled(ctx)
    }

    fun taskStateLabel(
        ctx: Context,
        state: TaskState,
        direction: Direction,
        interrupt: InterruptInfo? = null,
        checkingPrefix: Boolean = false,
    ): String = when (state) {
        TaskState.OFFERING -> UiText.statusTaskOffering(ctx)
        TaskState.AWAITING_USER -> UiText.statusTaskAwaitingUser(ctx)
        TaskState.RUNNING -> UiText.statusTaskRunning(ctx)
        TaskState.INTERRUPTED -> if (interrupt == null) {
            UiText.statusTaskInterrupted(ctx)
        } else {
            "${UiText.statusTaskInterrupted(ctx)}（${interruptLabel(ctx, interrupt)}）"
        }
        TaskState.RESUMING -> if (checkingPrefix) UiText.statusTaskVerifyingPrefix(ctx) else UiText.statusTaskResuming(ctx)
        TaskState.COMPLETED -> UiText.statusTaskCompleted(ctx)
        TaskState.REJECTED -> if (direction == Direction.SEND) UiText.statusTaskRejectedSender(ctx) else UiText.statusTaskRejectedReceiver(ctx)
        TaskState.NO_RESPONSE -> if (direction == Direction.SEND) UiText.statusTaskNoResponseSender(ctx) else UiText.statusTaskTimeoutReceiver(ctx)
        TaskState.NO_SPACE -> UiText.statusTaskNoSpace(ctx)
        TaskState.CANNOT_SAVE -> if (direction == Direction.SEND) UiText.statusTaskCannotSaveSender(ctx) else UiText.statusTaskCannotSaveReceiver(ctx)
        TaskState.CONNECTION_LOST -> UiText.statusTaskConnectionLost(ctx)
        TaskState.CANCELLED -> UiText.statusTaskCancelled(ctx)
        TaskState.PEER_CANCELLED -> UiText.statusTaskPeerCancelled(ctx)
        TaskState.OFFER_INVALID -> UiText.statusTaskOfferInvalid(ctx)
    }

    fun fileDisplayStatus(
        ctx: Context,
        fileState: FileState,
        taskState: TaskState,
        direction: Direction,
        interrupt: InterruptInfo?,
    ): String = when {
        fileState == FileState.COMPLETED ||
            fileState == FileState.READ_FAILED ||
            fileState == FileState.CHECKSUM_FAILED ||
            fileState == FileState.WRITE_FAILED -> fileStateLabel(ctx, fileState)
        taskState == TaskState.RUNNING -> fileStateLabel(ctx, fileState)
        else -> taskStateLabel(ctx, taskState, direction, interrupt)
    }

    fun openFailureMessage(
        ctx: Context,
        failure: OpenFailure,
        forSendFile: Boolean = false,
        forResume: Boolean = false,
    ): String = when (failure) {
        OpenFailure.NotLocal -> UiText.errorNotLocal(ctx)
        OpenFailure.SelfConnection -> UiText.errorConnectSelf(ctx)
        is OpenFailure.BindFailed -> if (failure.vpnSuspected) UiText.errorBindFailedVpn(ctx) else UiText.errorBindFailed(ctx)
        OpenFailure.NoVersionResponse -> UiText.errorNoVersionResponse(ctx)
        is OpenFailure.VersionIncompatible -> UiText.errorVersionIncompatible(ctx)
        OpenFailure.HandshakeAuthFailed -> UiText.securityAuthFailed(ctx)
        is OpenFailure.KeyMismatch -> UiText.securityKeyMismatch(ctx, failure.name)
        is OpenFailure.PeerKeyMismatch -> UiText.securityKeyMismatch(ctx, failure.name)
        OpenFailure.FingerprintMismatch -> UiText.securityQrMismatch(ctx)
        OpenFailure.NotTrusted -> UiText.errorPeerUnpaired(ctx)
        else -> when {
            forResume -> UiText.errorConnectResume(ctx)
            forSendFile -> UiText.errorConnectSendFile(ctx)
            else -> UiText.errorConnectFailed(ctx)
        }
    }

    fun pairingResultMessage(
        ctx: Context,
        result: PairingResult,
        peerName: String?,
        failure: OpenFailure?,
    ): String = when (result) {
        PairingResult.SUCCESS -> if (peerName.isNullOrBlank()) UiText.toastPairSuccess(ctx) else UiText.toastPairSuccessPeer(ctx, peerName)
        PairingResult.REJECTED, PairingResult.AUTO_REJECTED -> UiText.toastPairRejected(ctx)
        PairingResult.BUSY -> UiText.toastPairBusy(ctx)
        PairingResult.TIMEOUT -> UiText.toastPairTimeout(ctx)
        PairingResult.TOKEN_INVALID -> UiText.errorQrTokenInvalid(ctx)
        PairingResult.CONNECT_FAILED -> if (failure != null) openFailureMessage(ctx, failure) else UiText.errorConnectFailed(ctx)
        PairingResult.CANCELLED -> UiText.statusTaskCancelled(ctx)
        else -> UiText.toastPairFailed(ctx)
    }

    fun locateFailureMessage(ctx: Context, result: LocateResult): String = when (result) {
        LocateResult.OK -> ""
        LocateResult.NOT_FOUND -> UiText.errorFileNotFound(ctx)
        LocateResult.NO_HANDLER -> UiText.errorNoFileHandler(ctx)
        LocateResult.NO_FILE_MANAGER -> UiText.errorReceiveDirInaccessible(ctx)
        LocateResult.NO_ACCESS -> UiText.errorReceiveDirInaccessible(ctx)
    }

    fun formatEta(ctx: Context, etaSec: Long?): String {
        if (etaSec == null) return UiText.labelEtaUnknown(ctx)
        val s = etaSec.coerceAtLeast(0L)
        return when {
            s < 60 -> "$s 秒"
            s < 3600 -> "${s / 60} 分 ${s % 60} 秒"
            else -> "${s / 3600} 小时 ${(s % 3600) / 60} 分"
        }
    }

    fun formatSpeed(bytesPerSec: Long): String = "${SizeFormatter.format(bytesPerSec.coerceAtLeast(0L))}/s"

    fun formatRecordTime(time: RecordTime): String {
        val offset = ZoneOffset.ofTotalSeconds(time.offsetMin * 60)
        return Instant.ofEpochMilli(time.utcMs).atOffset(offset).format(timeFormatter)
    }
}
