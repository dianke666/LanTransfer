package com.lantransfer.app

import android.app.Application
import android.content.Context
import com.lantransfer.app.platform.AndroidClipboard
import com.lantransfer.app.platform.AndroidFileLocator
import com.lantransfer.app.platform.AndroidNetworkMonitor
import com.lantransfer.app.platform.AndroidNotifier
import com.lantransfer.app.platform.AndroidOutgoingTextCache
import com.lantransfer.app.platform.AndroidReceiveStorage
import com.lantransfer.app.platform.AndroidTransport
import com.lantransfer.app.platform.ContentResolverSourceFiles
import com.lantransfer.app.platform.DataStoreSettingsStore
import com.lantransfer.app.platform.KeystoreIdentityStore
import com.lantransfer.app.platform.ProcessLifecycleAppLifecycle
import com.lantransfer.app.platform.SecureRandomRng
import com.lantransfer.app.storage.LanTransferDatabase
import com.lantransfer.app.storage.RoomHistoryStore
import com.lantransfer.app.storage.RoomTaskStore
import com.lantransfer.app.storage.RoomTrustStore
import com.lantransfer.domain.DirRef
import com.lantransfer.domain.FsProfile
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.TaskState
import com.lantransfer.engine.EngineHost
import com.lantransfer.engine.EnginePorts
import com.lantransfer.engine.EngineServices
import com.lantransfer.engine.StartResult
import com.lantransfer.engine.ports.SystemClock
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.SessionCloseReason
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import java.util.concurrent.ConcurrentHashMap

/**
 * Android 应用级服务容器（设计 C6.1、C4.1）：
 * 持有单一 EngineHost、Room 数据库、全部平台适配器、内存待发送文件队列，
 * 并协调前后台切换时的前台服务启停与 30 s 自动关闭网络策略（需求 21.1–21.4、21.9）。
 */
class LanTransferApp : Application() {
    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    lateinit var database: LanTransferDatabase
        private set
    lateinit var settingsStore: DataStoreSettingsStore
        private set
    lateinit var identityStore: KeystoreIdentityStore
        private set
    lateinit var trustStore: RoomTrustStore
        private set
    lateinit var historyStore: RoomHistoryStore
        private set
    lateinit var taskStore: RoomTaskStore
        private set
    lateinit var networkMonitor: AndroidNetworkMonitor
        private set
    lateinit var transport: AndroidTransport
        private set
    lateinit var receiveStorage: AndroidReceiveStorage
        private set
    lateinit var sourceFiles: ContentResolverSourceFiles
        private set
    lateinit var textCache: AndroidOutgoingTextCache
        private set
    lateinit var clipboard: AndroidClipboard
        private set
    lateinit var fileLocator: AndroidFileLocator
        private set
    lateinit var notifier: AndroidNotifier
        private set
    lateinit var lifecyclePort: ProcessLifecycleAppLifecycle
        private set
    lateinit var engineHost: EngineHost
        private set

    private val pendingFiles = ConcurrentHashMap<DeviceId, List<SourceRef>>()
    private val securityDebounce = HashMap<String, Long>()
    private var backgroundShutdownJob: Job? = null

    val services: EngineServices? get() = engineHost.services.value

    override fun attachBaseContext(base: Context) {
        super.attachBaseContext(LocaleHelper.wrap(base))
    }

    override fun onCreate() {
        super.onCreate()
        database = LanTransferDatabase.create(this)
        settingsStore = DataStoreSettingsStore(this, appScope)
        identityStore = KeystoreIdentityStore(this, settingsStore, SecureRandomRng)
        trustStore = RoomTrustStore(database)
        historyStore = RoomHistoryStore(database)
        taskStore = RoomTaskStore(database, SystemClock)
        networkMonitor = AndroidNetworkMonitor(this, appScope)
        transport = AndroidTransport(this, networkMonitor)
        receiveStorage = AndroidReceiveStorage(this) { collectKnownReceiveDirs() }
        sourceFiles = ContentResolverSourceFiles(this)
        textCache = AndroidOutgoingTextCache(this)
        clipboard = AndroidClipboard(this)
        fileLocator = AndroidFileLocator(this)
        notifier = AndroidNotifier(this)
        lifecyclePort = ProcessLifecycleAppLifecycle().also { it.attach() }

        val ports = EnginePorts(
            transport = transport,
            network = networkMonitor,
            identity = identityStore,
            trust = trustStore,
            history = historyStore,
            tasks = taskStore,
            settings = settingsStore,
            storage = receiveStorage,
            sources = sourceFiles,
            textCache = textCache,
            clipboard = clipboard,
            notifier = notifier,
            lifecycle = lifecyclePort,
            clock = SystemClock,
            rng = SecureRandomRng,
        )

        engineHost = EngineHost(
            ports = ports,
            deviceType = DeviceType.ANDROID,
            profile = FsProfile.ANDROID,
            scope = appScope,
            io = Dispatchers.IO,
        )

        observeLifecycleAndService()
    }

    fun ensureNetworkingStarted() {
        appScope.launch {
            engineHost.startNetworking()
            syncForegroundServiceState()
        }
    }

    suspend fun retryNetworking(): StartResult = engineHost.startNetworking().also {
        syncForegroundServiceState()
    }

    fun getPendingFiles(peer: DeviceId): List<SourceRef> = pendingFiles[peer].orEmpty()

    fun setPendingFiles(peer: DeviceId, files: List<SourceRef>) {
        if (files.isEmpty()) pendingFiles.remove(peer) else pendingFiles[peer] = files
    }

    fun hasActiveTransfers(): Boolean =
        services?.files?.tasks?.value?.any {
            it.task.state == TaskState.RUNNING ||
                it.task.state == TaskState.RESUMING ||
                it.task.state == TaskState.OFFERING ||
                it.task.state == TaskState.AWAITING_USER
        } ?: false

    fun shouldShowSecurityWarning(key: String): Boolean {
        val now = SystemClock.monotonicMs()
        synchronized(securityDebounce) {
            val prev = securityDebounce[key]
            if (prev != null && now - prev < 60_000L) return false
            securityDebounce[key] = now
            return true
        }
    }

    fun syncForegroundServiceState() {
        val keepAlive = settingsStore.current().backgroundReceive || hasActiveTransfers()
        if (keepAlive) {
            TransferForegroundService.start(this)
        } else {
            TransferForegroundService.stop(this)
        }
    }

    private fun observeLifecycleAndService() {
        appScope.launch {
            lifecyclePort.background.collectLatest { isBg ->
                if (!isBg) {
                    backgroundShutdownJob?.cancel()
                    backgroundShutdownJob = null
                    notifier.refreshEnabled()
                    engineHost.startNetworking()
                    syncForegroundServiceState()
                } else {
                    syncForegroundServiceState()
                    if (!settingsStore.current().backgroundReceive && !hasActiveTransfers()) {
                        backgroundShutdownJob?.cancel()
                        backgroundShutdownJob = appScope.launch {
                            delay(30_000L)
                            if (lifecyclePort.background.value &&
                                !settingsStore.current().backgroundReceive &&
                                !hasActiveTransfers()
                            ) {
                                engineHost.shutdownNetworking(SessionCloseReason.BACKGROUND)
                                TransferForegroundService.stop(this@LanTransferApp)
                            }
                        }
                    }
                }
            }
        }

        appScope.launch {
            settingsStore.changes.collectLatest {
                syncForegroundServiceState()
            }
        }

        appScope.launch {
            engineHost.services.collectLatest { svc ->
                if (svc == null) return@collectLatest
                svc.files.tasks.collectLatest {
                    syncForegroundServiceState()
                }
            }
        }
    }

    private fun collectKnownReceiveDirs(): List<DirRef> {
        val dirs = mutableListOf(DirRef.Default, settingsStore.current().receiveDirectory)
        for (t in taskStore.unfinished()) {
            t.receiveDir?.let { dirs += it }
        }
        return dirs
    }
}
