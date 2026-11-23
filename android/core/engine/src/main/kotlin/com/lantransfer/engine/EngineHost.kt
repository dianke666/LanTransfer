package com.lantransfer.engine

import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.FsProfile
import com.lantransfer.domain.PairingTokenRegistry
import com.lantransfer.domain.Rng
import com.lantransfer.engine.ports.AppLifecycle
import com.lantransfer.engine.ports.Clipboard
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.HistoryStore
import com.lantransfer.engine.ports.IdentityLoad
import com.lantransfer.engine.ports.IdentityStore
import com.lantransfer.engine.ports.NetworkMonitor
import com.lantransfer.engine.ports.Notifier
import com.lantransfer.engine.ports.OutgoingTextCache
import com.lantransfer.engine.ports.ReceiveStorage
import com.lantransfer.engine.ports.SettingsStore
import com.lantransfer.engine.ports.SourceFiles
import com.lantransfer.engine.ports.TaskStore
import com.lantransfer.engine.ports.Transport
import com.lantransfer.engine.ports.TrustStore
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.EphemeralKeySource
import com.lantransfer.protocol.ProtocolMessage
import com.lantransfer.protocol.SessionCloseReason
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

// 引擎宿主（设计 C4.1）：Android 的 Application 容器 / 前台服务与 Windows 宿主只和它交互。
// - startNetworking：第一次调用时 loadOrCreate 身份并组装全部服务（随后执行两个服务的 restoreOnStartup）；
//   新身份无法保存时不启动网络并报告（Q14）。然后先监听、后启动发现；监听全部失败时不发公告（5.9）。
// - shutdownNetworking：停止发现、关闭监听与全部 Session（21.4、21.9）；之后可再次 startNetworking。
// - 分发器：每个已注册 Session 的应用消息依次交给 TextService、FileTransferEngine；配对消息由 PairingService 直接读取
//   PendingConnection，不经过这里。PairingService.unpair 经 cancelTransfers 接到 FileTransferEngine.cancelPeer（Q7）。

/** EngineHost 需要的全部平台适配器（由 Android Application 容器 / Windows 宿主创建）。 */
class EnginePorts(
    val transport: Transport,
    val network: NetworkMonitor,
    val identity: IdentityStore,
    val trust: TrustStore,
    val history: HistoryStore,
    val tasks: TaskStore,
    val settings: SettingsStore,
    val storage: ReceiveStorage,
    val sources: SourceFiles,
    val textCache: OutgoingTextCache,
    val clipboard: Clipboard,
    val notifier: Notifier,
    val lifecycle: AppLifecycle,
    val clock: Clock,
    val rng: Rng,
    val keys: EphemeralKeySource = EphemeralKeySource.secure(),
)

/** 身份载入成功后组装的服务（进程内只组装一次）。 */
class EngineServices internal constructor(
    val identity: DeviceIdentity,
    val sessions: SessionManager,
    val discovery: DiscoveryService,
    val pairing: PairingService,
    val text: TextService,
    val files: FileTransferEngine,
)

/** [EngineHost.startNetworking] 的结果。 */
sealed interface StartResult {
    /** 已在 [port] 上监听并启动了发现（端口用于公告、二维码与主界面显示，5.7、6.1）。 */
    data class Running(val port: Int) : StartResult

    /** 默认端口与全部备用端口都无法监听：不发送公告，主界面提示传输服务启动失败（5.9）。 */
    data object ListenFailed : StartResult

    /** 新身份无法保存：不监听、不公告，主界面显示错误和“重试”（Q14）。 */
    data class IdentityNotSaved(val detail: String) : StartResult
}

class EngineHost(
    private val ports: EnginePorts,
    private val deviceType: DeviceType,
    private val profile: FsProfile,
    private val scope: CoroutineScope,
    /** 读写盘协程的上下文（生产环境传 Dispatchers.IO）。 */
    private val io: CoroutineContext = EmptyCoroutineContext,
    private val sessionTimings: SessionTimings = SessionTimings(),
    private val transferTimings: TransferTimings = TransferTimings(),
    private val discoveryTimings: DiscoveryTimings = DiscoveryTimings(),
) {
    private val mutex = Mutex()
    private val identityFlow = MutableStateFlow<IdentityLoad?>(null)
    private val servicesFlow = MutableStateFlow<EngineServices?>(null)
    private val networkingFlow = MutableStateFlow<StartResult?>(null)

    /** 最近一次 loadOrCreate 的结果（主界面据此显示“身份已重置”或“无法保存身份”，4.6、Q14）；尚未载入时为 null。 */
    val identity: StateFlow<IdentityLoad?> get() = identityFlow

    /** 组装好的服务；身份载入成功之前为 null。 */
    val services: StateFlow<EngineServices?> get() = servicesFlow

    /** 网络状态：最近一次 startNetworking 的结果；从未启动或已 shutdownNetworking 时为 null。 */
    val networking: StateFlow<StartResult?> get() = networkingFlow

    /** 实际监听端口；未在监听时为 null。 */
    val listenPort: Int? get() = (networkingFlow.value as? StartResult.Running)?.port

    /** 启动网络（见文件头）。已在运行时直接返回当前结果；上次失败（身份无法保存、监听失败）时重新尝试。 */
    suspend fun startNetworking(): StartResult = mutex.withLock {
        (networkingFlow.value as? StartResult.Running)?.let { return it }
        val g = servicesFlow.value ?: when (val load = ports.identity.loadOrCreate()) {
            is IdentityLoad.SaveFailed -> {
                identityFlow.value = load
                return publish(StartResult.IdentityNotSaved(load.detail))
            }
            is IdentityLoad.Ready -> {
                identityFlow.value = load
                build(load.identity).also {
                    servicesFlow.value = it
                    it.text.restoreOnStartup()
                    it.files.restoreOnStartup()
                }
            }
        }
        when (val l = g.sessions.startListening()) {
            ListenResult.Failed -> publish(StartResult.ListenFailed)
            is ListenResult.Ok -> {
                networkingFlow.value = StartResult.Running(l.port) // 先于发现：PairingService 的二维码要用端口
                g.discovery.start(l.port)
                StartResult.Running(l.port)
            }
        }
    }

    /** 停止发现、关闭监听，并以 [reason]（Android 进入后台为 BACKGROUND，Windows 退出为 APP_EXIT）关闭全部 Session。 */
    suspend fun shutdownNetworking(reason: SessionCloseReason) {
        mutex.withLock {
            servicesFlow.value?.let { g ->
                g.discovery.stop()
                g.sessions.shutdown(reason)
            }
            networkingFlow.value = null
        }
    }

    private fun publish(r: StartResult): StartResult {
        networkingFlow.value = r
        return r
    }

    private fun build(identity: DeviceIdentity): EngineServices {
        val p = ports
        val name = { p.identity.displayName() }
        val discovery = DiscoveryService(p.transport, p.network, identity, deviceType, p.trust, p.clock, p.rng, scope, name, timings = discoveryTimings)
        val sessions = SessionManager(
            p.transport, p.network, identity, deviceType, p.trust, p.clock, scope, p.keys, name,
            addressBook = discovery::addressOf, timings = sessionTimings,
        )
        // 先订阅 Session 事件（UNDISPATCHED），再创建其余服务、开始监听：不会漏掉任何 Session
        scope.launch(start = CoroutineStart.UNDISPATCHED) {
            sessions.events.collect { e ->
                if (e is SessionEvent.Opened) scope.launch { for (m in e.session.incoming) dispatch(e.session, m) }
            }
        }
        val text = TextService(sessions, p.history, p.trust, p.clipboard, p.notifier, p.settings, p.lifecycle, p.textCache, p.clock, p.rng, scope)
        val files = FileTransferEngine(
            sessions, p.storage, p.sources, p.tasks, p.trust, p.notifier, p.settings, p.lifecycle, p.textCache, p.clock, p.rng, scope, profile, io,
            transferTimings,
        )
        val pairing = PairingService(
            sessions, p.trust, PairingTokenRegistry(p.rng), p.clock, p.rng, scope, identity, p.network, name,
            listenPort = { listenPort },
            cancelTransfers = { peer -> files.cancelPeer(peer) },
        )
        return EngineServices(identity, sessions, discovery, pairing, text, files)
    }

    /**
     * 应用消息先交给 TextService，不是文字消息再交给 FileTransferEngine；两者都不认识的消息忽略。
     * 服务在开始监听之前就已发布，所以这里总能取到。
     */
    private fun dispatch(session: Session, msg: ProtocolMessage) {
        val g = servicesFlow.value ?: return
        if (!g.text.onMessage(session, msg)) g.files.onMessage(session, msg)
    }
}
