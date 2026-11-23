package com.lantransfer.app.ui

import android.app.Application
import android.net.Uri
import android.provider.DocumentsContract
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.lantransfer.app.LanTransferApp
import com.lantransfer.app.platform.ThemeMode
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.text.UiTextMapper
import com.lantransfer.domain.DirRef
import com.lantransfer.domain.Direction
import com.lantransfer.domain.DisplayNameRules
import com.lantransfer.domain.FileState
import com.lantransfer.domain.NameCheck
import com.lantransfer.domain.PairingResult
import com.lantransfer.domain.PendingListRules
import com.lantransfer.domain.PickedItem
import com.lantransfer.domain.SizeFormatter
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.StoredFileRef
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TextRules
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.DiscoveryStatus
import com.lantransfer.engine.OpenFailure
import com.lantransfer.engine.PairingNotice
import com.lantransfer.engine.PairingOutcome
import com.lantransfer.engine.PairingUiState
import com.lantransfer.engine.ResumeResult
import com.lantransfer.engine.SendCheck
import com.lantransfer.engine.SendOfferResult
import com.lantransfer.engine.StartResult
import com.lantransfer.engine.Target
import com.lantransfer.engine.TextItem
import com.lantransfer.engine.TextNotice
import com.lantransfer.engine.TransferNotice
import com.lantransfer.engine.TransferTaskView
import com.lantransfer.engine.ports.ClipRead
import com.lantransfer.engine.ports.ClipWrite
import com.lantransfer.engine.ports.HistoryRecord
import com.lantransfer.engine.ports.IdentityLoad
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.engine.ports.LocateResult
import com.lantransfer.engine.ports.RecordId
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.engine.ports.WriteResult
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.TransferId
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

data class DeviceUiItem(
    val deviceId: DeviceId,
    val displayName: String,
    val deviceType: DeviceType,
    val addressText: String,
    val isTrusted: Boolean,
    val isConnected: Boolean,
    val isOnline: Boolean,
)

data class PendingFileUi(
    val ref: SourceRef,
    val name: String,
    val sizeText: String,
)

enum class HistoryFilter { ALL, TEXT, FILE }

data class HistoryUiItem(
    val isText: Boolean,
    val seq: Long,
    val textRecordId: RecordId?,
    val transferId: TransferId?,
    val fileIndex: Int,
    val timeText: String,
    val peerName: String,
    val direction: Direction,
    val statusText: String,
    val titleText: String,
    val fullText: String?,
    val fullPath: String?,
    val storedRef: StoredFileRef?,
    val mime: String,
    val isCompletedReceived: Boolean,
    val isInterrupted: Boolean,
    val isUnfinished: Boolean,
)

sealed interface AppAlert {
    data class Security(val title: String, val message: String) : AppAlert

    data class VersionIncompatible(val local: String, val remote: String) : AppAlert

    data class PeerUnpaired(val peerId: DeviceId?) : AppAlert

    data class SendTextAsFile(val peerId: DeviceId, val text: String) : AppAlert

    data class ReplaceSurrogate(val peerId: DeviceId, val text: String) : AppAlert

    data class ConfirmDeleteFile(val item: HistoryUiItem) : AppAlert

    data class ConfirmClearHistory(val unfinishedCount: Int) : AppAlert

    data class ConfirmUnpair(val device: TrustedDevice) : AppAlert

    data class LocateFallback(val result: LocateResult, val file: StoredFileRef?, val dir: DirRef?, val pathHint: String) : AppAlert

    data object BackgroundGuide : AppAlert
}

data class MainUiState(
    val localDeviceName: String = "",
    val localEndpoints: List<String> = emptyList(),
    val listenPort: Int? = null,
    val hasNetwork: Boolean = true,
    val discoveryUnavailable: Boolean = false,
    val listenFailed: Boolean = false,
    val identityReset: Boolean = false,
    val identitySaveFailed: Boolean = false,
    val notificationsOff: Boolean = false,
    val historyStoreFailed: Boolean = false,
    val devices: List<DeviceUiItem> = emptyList(),
    val trustedDevices: List<TrustedDevice> = emptyList(),
    val unfinishedCount: Int = 0,
    val pairingUi: PairingUiState = PairingUiState.Idle,
    val incomingOffers: List<TransferTaskView> = emptyList(),
    val themeMode: ThemeMode = ThemeMode.SYSTEM,
    val autoCopy: Boolean = false,
    val backgroundReceive: Boolean = false,
    val receiveDirDisplay: String = "",
    val historyItems: List<HistoryUiItem> = emptyList(),
    val historyFilter: HistoryFilter = HistoryFilter.ALL,
    val historyQuery: String = "",
    val activePeerId: DeviceId? = null,
    val activePeerName: String = "",
    val activePeerType: DeviceType = DeviceType.ANDROID,
    val activePeerAddress: String = "",
    val activePeerConnected: Boolean = false,
    val activeMessages: List<TextItem> = emptyList(),
    val activeTasks: List<TransferTaskView> = emptyList(),
    val pendingFiles: List<PendingFileUi> = emptyList(),
    val activeAlert: AppAlert? = null,
)

class MainViewModel(application: Application) : AndroidViewModel(application) {
    val app: LanTransferApp = application as LanTransferApp
    private val ctx get() = app.applicationContext

    private val _state = MutableStateFlow(
        MainUiState(
            localDeviceName = app.identityStore.displayName(),
            themeMode = app.settingsStore.theme.value,
            autoCopy = app.settingsStore.current().autoCopy,
            backgroundReceive = app.settingsStore.current().backgroundReceive,
            receiveDirDisplay = app.receiveStorage.formatDirDisplay(app.settingsStore.current().receiveDirectory),
        ),
    )
    val state: StateFlow<MainUiState> = _state.asStateFlow()

    private val _toasts = MutableSharedFlow<String>(extraBufferCapacity = 32)
    val toasts: SharedFlow<String> = _toasts.asSharedFlow()

    private val _openConversationEvents = MutableSharedFlow<DeviceId>(extraBufferCapacity = 8)
    val openConversationEvents: SharedFlow<DeviceId> = _openConversationEvents.asSharedFlow()

    private var activeMessagesJob: Job? = null
    private var allHistoryCache: List<HistoryUiItem> = emptyList()

    init {
        app.ensureNetworkingStarted()
        observeBaseStreams()
        observeEngineServices()
        reloadHistory()
    }

    fun showToast(msg: String) {
        if (msg.isNotEmpty()) _toasts.tryEmit(msg)
    }

    fun dismissAlert() {
        _state.update { it.copy(activeAlert = null) }
    }

    fun showBackgroundGuide() {
        _state.update { it.copy(activeAlert = AppAlert.BackgroundGuide) }
    }

    fun retryNetworking() {
        viewModelScope.launch {
            app.retryNetworking()
        }
    }

    fun selectDevice(item: DeviceUiItem) {
        if (item.isTrusted) {
            openConversation(item.deviceId)
            return
        }
        val svc = app.services ?: run {
            showToast(UiText.bannerListenFailed(ctx))
            return
        }
        viewModelScope.launch {
            when (val outcome = svc.pairing.pair(Target.Device(item.deviceId))) {
                is PairingOutcome.AlreadyTrusted -> openConversation(outcome.session.peer.deviceId)
                is PairingOutcome.Done -> {
                    val f = outcome.failure
                    if (outcome.result == PairingResult.SUCCESS) {
                        openConversation(item.deviceId)
                    } else if (outcome.result == PairingResult.CONNECT_FAILED && f != null) {
                        handleOpenFailure(f, item.deviceId)
                    }
                }
                PairingOutcome.Busy -> showToast(UiText.toastPairBusy(ctx))
                is PairingOutcome.InvalidQr -> showToast(UiText.errorQrUnrecognized(ctx))
            }
        }
    }

    fun openConversation(peerId: DeviceId) {
        val trusted = app.trustStore.get(peerId)
        val disc = app.services?.discovery?.devices?.value?.firstOrNull { it.deviceId == peerId }
        val resolvedName = trusted?.displayName ?: disc?.announcement?.displayName ?: peerId.value.toHex().take(8)
        val resolvedType = trusted?.deviceType ?: disc?.announcement?.deviceType ?: DeviceType.ANDROID
        val resolvedAddr = disc?.let { "${it.ip}:${it.announcement.port}" }
            ?: trusted?.lastAddress?.let { ip -> trusted.lastPort?.let { "$ip:$it" } ?: ip.toString() }
            ?: ""
        val connected = app.services?.sessions?.sessions?.value?.containsKey(peerId) == true

        _state.update {
            it.copy(
                activePeerId = peerId,
                activePeerName = resolvedName,
                activePeerType = resolvedType,
                activePeerAddress = resolvedAddr,
                activePeerConnected = connected,
                activeTasks = app.services?.files?.tasks?.value?.filter { t -> t.task.peer == peerId }.orEmpty(),
            )
        }
        refreshPendingFiles(peerId)

        activeMessagesJob?.cancel()
        val textFlow = app.services?.text?.conversation(peerId)
        if (textFlow != null) {
            activeMessagesJob = viewModelScope.launch {
                textFlow.collectLatest { items ->
                    _state.update { st ->
                        if (st.activePeerId == peerId) st.copy(activeMessages = items) else st
                    }
                }
            }
        } else {
            _state.update { it.copy(activeMessages = emptyList()) }
        }
        _openConversationEvents.tryEmit(peerId)
    }

    fun sendText(peerId: DeviceId, text: String, onCleared: () -> Unit) {
        val svc = app.services ?: return
        viewModelScope.launch {
            when (svc.text.send(peerId, text)) {
                is SendCheck.Sent -> onCleared()
                SendCheck.Empty -> Unit
                SendCheck.HasUnpairedSurrogate -> _state.update { it.copy(activeAlert = AppAlert.ReplaceSurrogate(peerId, text)) }
                is SendCheck.TooLong -> _state.update { it.copy(activeAlert = AppAlert.SendTextAsFile(peerId, text)) }
            }
        }
    }

    fun confirmReplaceSurrogateAndSend(peerId: DeviceId, text: String) {
        dismissAlert()
        val replaced = TextRules.replaceUnpaired(text)
        sendText(peerId, replaced) {}
    }

    fun confirmSendTextAsFile(peerId: DeviceId, text: String) {
        dismissAlert()
        val svc = app.services ?: return
        viewModelScope.launch(Dispatchers.IO) {
            when (val wr = svc.text.writeTextFile(text)) {
                is WriteResult.Ok -> {
                    when (val sr = svc.files.send(peerId, listOf(wr.value))) {
                        is SendOfferResult.ConnectFailed -> handleOpenFailure(sr.failure, peerId, forSendFile = true)
                        is SendOfferResult.Unreadable -> showToast(UiText.errorFileSizeUnknown(ctx))
                        else -> Unit
                    }
                }
                is WriteResult.Failed -> showToast(wr.detail)
            }
        }
    }

    fun retryText(peerId: DeviceId, item: TextItem) {
        if (peerId == item.key.peer) {
            app.services?.text?.retry(item.key.messageId)
        }
    }

    fun copyText(text: String) {
        viewModelScope.launch {
            val res = app.clipboard.writeText(text)
            val msg = when {
                res is ClipWrite.Ok && text.contains('\u0000') -> UiText.toastCopiedWithNul(ctx)
                res is ClipWrite.Ok -> UiText.toastCopied(ctx)
                else -> UiText.toastClipboardFailed(ctx)
            }
            showToast(msg)
        }
    }

    fun readClipboard(onText: (String) -> Unit) {
        viewModelScope.launch {
            when (val r = app.clipboard.readText()) {
                is ClipRead.Text -> onText(r.text)
                ClipRead.NoText -> showToast(UiText.toastClipboardEmpty(ctx))
                ClipRead.Failed -> showToast(UiText.toastClipboardFailed(ctx))
            }
        }
    }

    fun sendClipboardNow(peerId: DeviceId) {
        viewModelScope.launch {
            when (val r = app.clipboard.readText()) {
                is ClipRead.Text -> sendText(peerId, r.text) {}
                ClipRead.NoText -> showToast(UiText.toastClipboardEmpty(ctx))
                ClipRead.Failed -> showToast(UiText.toastClipboardFailed(ctx))
            }
        }
    }

    fun addPickedUris(peerId: DeviceId, uris: List<Uri>) {
        if (uris.isEmpty()) return
        val current = app.getPendingFiles(peerId)
        val picked = uris.map { uri ->
            val mime = runCatching { ctx.contentResolver.getType(uri) }.getOrNull()
            PickedItem(SourceRef(uri.toString()), isDirectory = mime == DocumentsContract.Document.MIME_TYPE_DIR)
        }
        val res = PendingListRules.add(current, picked)
        if (res.foldersSkipped) showToast(UiText.errorFoldersNotSupported(ctx))
        if (res.tooMany) {
            showToast(UiText.errorTooManyFiles(ctx))
            return
        }
        app.setPendingFiles(peerId, res.list)
        refreshPendingFiles(peerId)
    }

    fun removePendingFile(peerId: DeviceId, ref: SourceRef) {
        val next = app.getPendingFiles(peerId).filterNot { it == ref }
        app.setPendingFiles(peerId, next)
        refreshPendingFiles(peerId)
    }

    fun clearPendingFiles(peerId: DeviceId) {
        app.setPendingFiles(peerId, emptyList())
        refreshPendingFiles(peerId)
    }

    fun sendPendingFiles(peerId: DeviceId) {
        val files = app.getPendingFiles(peerId)
        val svc = app.services ?: return
        if (files.isEmpty()) return
        viewModelScope.launch(Dispatchers.IO) {
            when (val res = svc.files.send(peerId, files)) {
                is SendOfferResult.Sent -> {
                    app.setPendingFiles(peerId, emptyList())
                    refreshPendingFiles(peerId)
                }
                is SendOfferResult.Unreadable -> showToast(UiText.errorFileSizeUnknown(ctx))
                SendOfferResult.TooMany -> showToast(UiText.errorTooManyFiles(ctx))
                is SendOfferResult.ConnectFailed -> handleOpenFailure(res.failure, peerId, forSendFile = true)
                SendOfferResult.Empty -> Unit
            }
        }
    }

    fun acceptOffer(id: TransferId) {
        app.services?.files?.accept(id)
    }

    fun rejectOffer(id: TransferId) {
        app.services?.files?.reject(id)
    }

    fun cancelTransfer(id: TransferId) {
        app.services?.files?.cancel(id)
    }

    fun resumeTransfer(id: TransferId, peerId: DeviceId) {
        val svc = app.services ?: return
        viewModelScope.launch(Dispatchers.IO) {
            when (val r = svc.files.resume(id)) {
                is ResumeResult.ConnectFailed -> handleOpenFailure(r.failure, peerId, forResume = true)
                else -> Unit
            }
        }
    }

    fun pairByManual(ipInput: String, portInput: String, onResult: (String?) -> Unit) {
        val ip = com.lantransfer.domain.AddressRules.parseIpv4(ipInput)
        if (ip == null) {
            onResult(UiText.errorIpInvalid(ctx))
            return
        }
        val port = when (val pp = com.lantransfer.domain.AddressRules.parsePort(portInput)) {
            is com.lantransfer.domain.PortParse.Default -> pp.port
            is com.lantransfer.domain.PortParse.Ok -> pp.port
            com.lantransfer.domain.PortParse.Invalid -> {
                onResult(UiText.errorPortInvalid(ctx))
                return
            }
        }
        val svc = app.services ?: run {
            onResult(UiText.bannerListenFailed(ctx))
            return
        }
        viewModelScope.launch {
            when (val outcome = svc.pairing.pair(Target.Address(ip, port))) {
                is PairingOutcome.AlreadyTrusted -> {
                    onResult(null)
                    openConversation(outcome.session.peer.deviceId)
                }
                is PairingOutcome.Done -> {
                    val f = outcome.failure
                    if (outcome.result == PairingResult.SUCCESS) {
                        onResult(null)
                    } else if (outcome.result == PairingResult.CONNECT_FAILED && f != null) {
                        if (f is OpenFailure.KeyMismatch || f is OpenFailure.PeerKeyMismatch || f is OpenFailure.HandshakeAuthFailed || f is OpenFailure.VersionIncompatible) {
                            handleOpenFailure(f, null)
                            onResult(null)
                        } else {
                            onResult(UiTextMapper.openFailureMessage(ctx, f))
                        }
                    } else {
                        onResult(UiTextMapper.pairingResultMessage(ctx, outcome.result, null, f))
                    }
                }
                PairingOutcome.Busy -> onResult(UiText.toastPairBusy(ctx))
                is PairingOutcome.InvalidQr -> onResult(UiText.errorQrUnrecognized(ctx))
            }
        }
    }

    fun pairByQrText(qrText: String, onDone: (Boolean, String?) -> Unit) {
        val svc = app.services ?: run {
            onDone(false, UiText.bannerListenFailed(ctx))
            return
        }
        viewModelScope.launch {
            when (val outcome = svc.pairing.pairByQr(qrText)) {
                is PairingOutcome.AlreadyTrusted -> {
                    onDone(true, null)
                    openConversation(outcome.session.peer.deviceId)
                }
                is PairingOutcome.Done -> {
                    val f = outcome.failure
                    if (outcome.result == PairingResult.SUCCESS) {
                        onDone(true, null)
                    } else {
                        if (f != null) handleOpenFailure(f, null)
                        onDone(false, UiTextMapper.pairingResultMessage(ctx, outcome.result, null, f))
                    }
                }
                PairingOutcome.Busy -> onDone(false, UiText.toastPairBusy(ctx))
                is PairingOutcome.InvalidQr -> onDone(false, UiText.errorQrUnrecognized(ctx))
            }
        }
    }

    fun respondPairing(id: com.lantransfer.engine.RequestId, accept: Boolean) {
        app.services?.pairing?.respond(id, accept)
    }

    fun cancelPairing() {
        app.services?.pairing?.cancel()
    }

    fun setThemeMode(mode: ThemeMode) {
        app.settingsStore.setTheme(mode)
        _state.update { it.copy(themeMode = mode) }
    }

    fun toggleQuickTheme() {
        val next = if (_state.value.themeMode == ThemeMode.DARK) ThemeMode.LIGHT else ThemeMode.DARK
        setThemeMode(next)
    }

    fun setAutoCopy(on: Boolean) {
        app.settingsStore.update { it.copy(autoCopy = on) }
    }

    fun setBackgroundReceive(on: Boolean) {
        app.settingsStore.update { it.copy(backgroundReceive = on) }
        if (on && !app.settingsStore.backgroundGuideShown.value) {
            app.settingsStore.markBackgroundGuideShown()
            showBackgroundGuide()
        }
    }

    fun saveDeviceName(input: String): String? = when (val check = DisplayNameRules.validateUserInput(input)) {
        is NameCheck.Ok -> {
            app.identityStore.setDisplayName(check.cleaned)
            _state.update { it.copy(localDeviceName = check.cleaned) }
            showToast(if (check.removedAny) UiText.toastNameCharsRemoved(ctx) else UiText.toastNameSaved(ctx))
            null
        }
        else -> UiText.errorNameInvalid(ctx)
    }

    fun setReceiveTreeUri(uri: Uri): String? {
        val candidate = DirRef.Tree(uri.toString())
        if (!app.receiveStorage.isWritable(candidate)) {
            return UiText.errorDirNotWritable(ctx)
        }
        app.settingsStore.update { it.copy(receiveDirectory = candidate) }
        _state.update { it.copy(receiveDirDisplay = app.receiveStorage.formatDirDisplay(candidate)) }
        return null
    }

    fun openReceiveDirectory() {
        val dir = app.settingsStore.current().receiveDirectory
        val res = app.fileLocator.openDirectory(dir)
        handleLocateResult(res, null, dir, app.receiveStorage.formatDirDisplay(dir))
    }

    fun openStoredFile(file: StoredFileRef, mime: String, pathHint: String) {
        val res = app.fileLocator.openFile(file, mime)
        handleLocateResult(res, file, null, pathHint)
    }

    fun revealStoredFile(file: StoredFileRef, pathHint: String) {
        val res = app.fileLocator.revealInFolder(file)
        handleLocateResult(res, file, null, pathHint)
    }

    fun setAutoAccept(peerId: DeviceId, on: Boolean) {
        viewModelScope.launch(Dispatchers.IO) {
            app.trustStore.setAutoAccept(peerId, on)
        }
    }

    fun requestUnpair(device: TrustedDevice) {
        _state.update { it.copy(activeAlert = AppAlert.ConfirmUnpair(device)) }
    }

    fun confirmUnpair(device: TrustedDevice) {
        dismissAlert()
        viewModelScope.launch(Dispatchers.IO) {
            val svc = app.services
            if (svc != null) svc.pairing.unpair(device.deviceId) else app.trustStore.delete(device.deviceId)
        }
    }

    fun setHistoryFilter(filter: HistoryFilter) {
        _state.update { it.copy(historyFilter = filter) }
        applyHistoryFilter()
    }

    fun setHistoryQuery(query: String) {
        _state.update { it.copy(historyQuery = query) }
        applyHistoryFilter()
    }

    fun reloadHistory() {
        viewModelScope.launch(Dispatchers.IO) {
            val liveStates = app.services?.files?.tasks?.value?.associate { it.id to it.task.state }.orEmpty()
            when (val res = app.historyStore.list()) {
                is ListResult.Failed -> _state.update { it.copy(historyStoreFailed = true) }
                is ListResult.Ok -> {
                    val mapped = res.items.asReversed().map { rec ->
                        when (rec) {
                            is HistoryRecord.Text -> {
                                val r = rec.record
                                val full = r.textUtf8.toByteArray().decodeToString()
                                HistoryUiItem(
                                    isText = true,
                                    seq = r.seq,
                                    textRecordId = RecordId(r.seq),
                                    transferId = null,
                                    fileIndex = 0,
                                    timeText = UiTextMapper.formatRecordTime(r.recordedAt),
                                    peerName = r.peerName,
                                    direction = r.direction,
                                    statusText = UiTextMapper.textStatusLabel(ctx, r.status),
                                    titleText = TextRules.notificationPreview(full),
                                    fullText = full,
                                    fullPath = null,
                                    storedRef = null,
                                    mime = "text/plain",
                                    isCompletedReceived = false,
                                    isInterrupted = false,
                                    isUnfinished = false,
                                )
                            }
                            is HistoryRecord.File -> {
                                val fr = rec.record
                                val f = fr.file
                                val live = liveStates[f.transferId] ?: fr.taskState
                                HistoryUiItem(
                                    isText = false,
                                    seq = f.seq,
                                    textRecordId = null,
                                    transferId = f.transferId,
                                    fileIndex = f.index,
                                    timeText = UiTextMapper.formatRecordTime(fr.recordedAt),
                                    peerName = fr.peerName,
                                    direction = fr.direction,
                                    statusText = UiTextMapper.fileDisplayStatus(ctx, f.state, live, fr.direction, fr.interrupt),
                                    titleText = "${f.finalName ?: f.originalName}  (${SizeFormatter.format(f.size)})",
                                    fullText = null,
                                    fullPath = f.fullPath,
                                    storedRef = f.storedRef,
                                    mime = f.mime,
                                    isCompletedReceived = fr.direction == Direction.RECEIVE && f.state == FileState.COMPLETED && f.storedRef != null,
                                    isInterrupted = live == TaskState.INTERRUPTED,
                                    isUnfinished = !live.isTerminal,
                                )
                            }
                        }
                    }
                    allHistoryCache = mapped
                    withContext(Dispatchers.Main) { applyHistoryFilter() }
                }
            }
        }
    }

    fun requestDeleteHistoryItem(item: HistoryUiItem) {
        if (item.isText) {
            val rid = item.textRecordId ?: return
            viewModelScope.launch(Dispatchers.IO) {
                if (app.historyStore.delete(rid) is StoreResult.Failed) {
                    _state.update { it.copy(historyStoreFailed = true) }
                }
                reloadHistory()
            }
            return
        }
        if (item.isCompletedReceived || item.isInterrupted) {
            _state.update { it.copy(activeAlert = AppAlert.ConfirmDeleteFile(item)) }
        } else {
            deleteFileRecord(item, deleteLocalFile = false)
        }
    }

    fun deleteFileRecord(item: HistoryUiItem, deleteLocalFile: Boolean) {
        dismissAlert()
        val tid = item.transferId ?: return
        viewModelScope.launch(Dispatchers.IO) {
            if (deleteLocalFile && item.storedRef != null) {
                val ok = runCatching {
                    val v = item.storedRef.value
                    if (v.startsWith("content://")) {
                        val uri = Uri.parse(v)
                        runCatching { DocumentsContract.deleteDocument(ctx.contentResolver, uri) }.getOrElse {
                            ctx.contentResolver.delete(uri, null, null) > 0
                        }
                    } else {
                        File(v).delete()
                    }
                }.getOrDefault(false)
                if (!ok) showToast(UiText.toastDeleteLocalFailed(ctx))
            }
            if (item.isInterrupted) {
                app.services?.files?.cancel(tid)
            }
            if (app.historyStore.deleteFileRecord(tid, item.fileIndex) is StoreResult.Failed) {
                _state.update { it.copy(historyStoreFailed = true) }
            }
            reloadHistory()
        }
    }

    fun requestClearHistory() {
        _state.update { it.copy(activeAlert = AppAlert.ConfirmClearHistory(it.unfinishedCount)) }
    }

    fun confirmClearHistory() {
        dismissAlert()
        viewModelScope.launch(Dispatchers.IO) {
            app.services?.files?.abandonAll()
            if (app.historyStore.clearAll() is StoreResult.Failed) {
                _state.update { it.copy(historyStoreFailed = true) }
            }
            reloadHistory()
        }
    }

    fun handleLocateResult(result: LocateResult, file: StoredFileRef?, dir: DirRef?, pathHint: String) {
        if (result == LocateResult.OK) return
        _state.update { it.copy(activeAlert = AppAlert.LocateFallback(result, file, dir, pathHint)) }
    }

    fun handleOpenFailure(failure: OpenFailure, peer: DeviceId?, forSendFile: Boolean = false, forResume: Boolean = false) {
        when (failure) {
            is OpenFailure.KeyMismatch -> {
                if (app.shouldShowSecurityWarning("km:${failure.name}:${peer?.value?.toHex()}")) {
                    _state.update {
                        it.copy(activeAlert = AppAlert.Security(UiText.securityTitle(ctx), UiText.securityKeyMismatch(ctx, failure.name)))
                    }
                }
            }
            is OpenFailure.PeerKeyMismatch -> {
                if (app.shouldShowSecurityWarning("pkm:${failure.name}:${peer?.value?.toHex()}")) {
                    _state.update {
                        it.copy(activeAlert = AppAlert.Security(UiText.securityTitle(ctx), UiText.securityKeyMismatch(ctx, failure.name)))
                    }
                }
            }
            OpenFailure.HandshakeAuthFailed -> {
                if (app.shouldShowSecurityWarning("auth")) {
                    _state.update {
                        it.copy(activeAlert = AppAlert.Security(UiText.securityTitle(ctx), UiText.securityAuthFailed(ctx)))
                    }
                }
            }
            is OpenFailure.VersionIncompatible -> {
                _state.update {
                    it.copy(activeAlert = AppAlert.VersionIncompatible(failure.local.toString(), failure.remote.toString()))
                }
            }
            OpenFailure.NotTrusted -> {
                _state.update { it.copy(activeAlert = AppAlert.PeerUnpaired(peer)) }
            }
            else -> showToast(UiTextMapper.openFailureMessage(ctx, failure, forSendFile, forResume))
        }
    }

    private fun refreshPendingFiles(peerId: DeviceId) {
        viewModelScope.launch(Dispatchers.IO) {
            val refs = app.getPendingFiles(peerId)
            val mapped = refs.map { ref ->
                val meta = app.sourceFiles.describe(ref)
                PendingFileUi(
                    ref = ref,
                    name = meta?.name ?: Uri.parse(ref.value).lastPathSegment ?: ref.value,
                    sizeText = meta?.let { SizeFormatter.format(it.size) } ?: "",
                )
            }
            _state.update { st -> if (st.activePeerId == peerId) st.copy(pendingFiles = mapped) else st }
        }
    }

    private fun applyHistoryFilter() {
        val st = _state.value
        val q = st.historyQuery.trim().lowercase()
        val filtered = allHistoryCache.filter { item ->
            val kindOk = when (st.historyFilter) {
                HistoryFilter.ALL -> true
                HistoryFilter.TEXT -> item.isText
                HistoryFilter.FILE -> !item.isText
            }
            if (!kindOk) return@filter false
            if (q.isEmpty()) return@filter true
            item.peerName.lowercase().contains(q) ||
                item.titleText.lowercase().contains(q) ||
                (item.fullText?.lowercase()?.contains(q) == true) ||
                (item.fullPath?.lowercase()?.contains(q) == true)
        }
        _state.update { it.copy(historyItems = filtered) }
    }

    private fun rebuildDevices() {
        viewModelScope.launch(Dispatchers.IO) {
            val discovered = app.services?.discovery?.devices?.value.orEmpty()
            val trusted = app.trustStore.all()
            val sessions = app.services?.sessions?.sessions?.value.orEmpty()
            val list = mutableListOf<DeviceUiItem>()
            val seen = HashSet<DeviceId>()

            for (d in discovered) {
                seen += d.deviceId
                list += DeviceUiItem(
                    deviceId = d.deviceId,
                    displayName = d.announcement.displayName,
                    deviceType = d.announcement.deviceType,
                    addressText = "${d.ip}:${d.announcement.port}",
                    isTrusted = d.trusted,
                    isConnected = sessions.containsKey(d.deviceId),
                    isOnline = true,
                )
            }
            for (t in trusted) {
                if (t.deviceId in seen) continue
                val addr = t.lastAddress?.let { ip -> t.lastPort?.let { "$ip:$it" } ?: ip.toString() }
                    ?: UiText.labelTrustedBadge(ctx)
                list += DeviceUiItem(
                    deviceId = t.deviceId,
                    displayName = t.displayName,
                    deviceType = t.deviceType,
                    addressText = addr,
                    isTrusted = true,
                    isConnected = sessions.containsKey(t.deviceId),
                    isOnline = false,
                )
            }
            _state.update { st ->
                val activeConn = st.activePeerId?.let { sessions.containsKey(it) } ?: false
                st.copy(
                    devices = list,
                    trustedDevices = trusted,
                    activePeerConnected = activeConn,
                )
            }
        }
    }

    private fun observeBaseStreams() {
        viewModelScope.launch {
            app.networkMonitor.interfaces.collectLatest { ifaces ->
                val port = app.engineHost.listenPort
                val eps = ifaces.map { i -> if (port != null) "${i.address}:$port" else i.address.toString() }
                _state.update { it.copy(hasNetwork = ifaces.isNotEmpty(), localEndpoints = eps, listenPort = port) }
            }
        }
        viewModelScope.launch {
            app.engineHost.networking.collectLatest { net ->
                val port = (net as? StartResult.Running)?.port
                val ifaces = app.networkMonitor.interfaces.value
                val eps = ifaces.map { i -> if (port != null) "${i.address}:$port" else i.address.toString() }
                _state.update {
                    it.copy(
                        listenPort = port,
                        localEndpoints = eps,
                        listenFailed = net is StartResult.ListenFailed,
                        identitySaveFailed = net is StartResult.IdentityNotSaved,
                    )
                }
            }
        }
        viewModelScope.launch {
            app.engineHost.identity.collectLatest { id ->
                _state.update {
                    it.copy(
                        identityReset = id is IdentityLoad.ResetCorrupted,
                        identitySaveFailed = id is IdentityLoad.SaveFailed,
                        localDeviceName = app.identityStore.displayName(),
                    )
                }
            }
        }
        viewModelScope.launch {
            app.notifier.enabled.collectLatest { on ->
                _state.update { it.copy(notificationsOff = !on) }
            }
        }
        viewModelScope.launch {
            app.settingsStore.changes.collectLatest { s ->
                _state.update {
                    it.copy(
                        autoCopy = s.autoCopy,
                        backgroundReceive = s.backgroundReceive,
                        receiveDirDisplay = app.receiveStorage.formatDirDisplay(s.receiveDirectory),
                        localDeviceName = app.identityStore.displayName(),
                    )
                }
            }
        }
        viewModelScope.launch {
            app.settingsStore.theme.collectLatest { m ->
                _state.update { it.copy(themeMode = m) }
            }
        }
        viewModelScope.launch {
            app.trustStore.changes.collectLatest { rebuildDevices() }
        }
    }

    private fun observeEngineServices() {
        viewModelScope.launch {
            app.engineHost.services.collectLatest { svc ->
                if (svc == null) return@collectLatest
                rebuildDevices()

                launch { svc.discovery.devices.collectLatest { rebuildDevices() } }
                launch {
                    svc.discovery.status.collectLatest { st ->
                        _state.update { it.copy(discoveryUnavailable = st is DiscoveryStatus.Unavailable) }
                    }
                }
                launch { svc.sessions.sessions.collectLatest { rebuildDevices() } }
                launch {
                    svc.sessions.inboundFailures.collect { f ->
                        handleOpenFailure(f.failure, f.peer)
                    }
                }
                launch {
                    svc.pairing.state.collectLatest { p ->
                        _state.update { it.copy(pairingUi = p) }
                    }
                }
                launch {
                    svc.pairing.notices.collect { n ->
                        if (n is PairingNotice.Finished) {
                            val name = app.trustStore.get(n.done.peer)?.displayName
                                ?: svc.discovery.devices.value.firstOrNull { it.deviceId == n.done.peer }?.announcement?.displayName
                            showToast(UiTextMapper.pairingResultMessage(ctx, n.done.result, name, n.failure))
                        }
                    }
                }
                launch {
                    svc.text.notices.collect { n ->
                        when (n) {
                            is TextNotice.ConnectFailed -> handleOpenFailure(n.failure, n.peer)
                            is TextNotice.StoreFailed -> _state.update { it.copy(historyStoreFailed = true) }
                            TextNotice.AutoCopyFailed -> showToast(UiText.toastClipboardFailed(ctx))
                        }
                    }
                }
                launch {
                    svc.files.unfinishedCount().collectLatest { c ->
                        _state.update { it.copy(unfinishedCount = c) }
                    }
                }
                launch {
                    svc.files.tasks.collectLatest { tasks ->
                        val offers = tasks.filter { it.task.direction == Direction.RECEIVE && it.task.state == TaskState.AWAITING_USER }
                        _state.update { st ->
                            val peerTasks = st.activePeerId?.let { pid -> tasks.filter { it.task.peer == pid } }.orEmpty()
                            st.copy(incomingOffers = offers, activeTasks = peerTasks)
                        }
                        reloadHistory()
                    }
                }
                launch {
                    svc.files.notices.collect { n ->
                        when (n) {
                            is TransferNotice.StoreFailed -> _state.update { it.copy(historyStoreFailed = true) }
                            is TransferNotice.PendingCancelNotSaved -> showToast(UiText.toastPendingCancelFailed(ctx))
                        }
                    }
                }
            }
        }
    }
}
