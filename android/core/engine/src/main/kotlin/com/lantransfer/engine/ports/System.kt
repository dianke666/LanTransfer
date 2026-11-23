package com.lantransfer.engine.ports

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.StoredFileRef
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.TransferId
import kotlinx.coroutines.flow.StateFlow
import java.time.LocalDateTime

// C5.4 系统服务端口。随机数端口 Rng 定义在领域层（com.lantransfer.domain.Rng），Noise 临时密钥来源
// EphemeralKeySource 定义在协议层，引擎层直接复用。

sealed interface ClipRead {
    data class Text(val text: String) : ClipRead

    data object NoText : ClipRead

    /** 无法访问剪贴板（被占用超过 1 s 等，13.9）。 */
    data object Failed : ClipRead
}

sealed interface ClipWrite {
    data object Ok : ClipWrite

    /** 写入失败，或读回后逐码点比对不一致（13.3）。 */
    data object Failed : ClipWrite
}

interface Clipboard {
    suspend fun readText(): ClipRead

    suspend fun writeText(s: String): ClipWrite
}

/** 对应 19.6 / 19.5 / 19.3 / 19.11 的提示。 */
enum class LocateResult { OK, NOT_FOUND, NO_HANDLER, NO_FILE_MANAGER, NO_ACCESS }

interface FileLocator {
    fun openFile(f: StoredFileRef, mime: String): LocateResult

    fun revealInFolder(f: StoredFileRef): LocateResult

    fun openDirectory(dir: DirRef): LocateResult
}

/** 通知的标识：平台适配层映射为固定的 (tag, id)，用于更新和移除通知（21.1、21.8）。 */
sealed interface NotifyKey {
    data class Text(val peer: DeviceId, val messageId: MessageId) : NotifyKey

    data class PairingRequest(val peer: DeviceId) : NotifyKey

    /** 任务级通知：文件接收请求、传输进度、任务问题。 */
    data class Transfer(val transferId: TransferId) : NotifyKey

    /** 单个文件完成或出错。 */
    data class File(val transferId: TransferId, val index: Int) : NotifyKey
}

enum class RequestKind { PAIRING, FILE_OFFER }

interface Notifier {
    /** 未授权或被关闭时为 false，主界面提示（21.11）。 */
    val enabled: StateFlow<Boolean>

    /** 21.6 */
    fun text(key: NotifyKey, peerName: String, preview: String)

    /** 21.7 */
    fun fileCompleted(key: NotifyKey, peerName: String, fileName: String, file: StoredFileRef)

    /** 21.8；fileCount 只在 FILE_OFFER 时给出。 */
    fun request(key: NotifyKey, peerName: String, kind: RequestKind, fileCount: Int?)

    /** 21.10 */
    fun problem(key: NotifyKey, peerName: String, fileName: String?, status: String)

    /** 21.1，Android 专用（前台服务通知）；Windows 实现可忽略。 */
    fun progress(task: TransferId, peerName: String, fileName: String, percent: Int)

    fun cancel(key: NotifyKey)
}

interface AppLifecycle {
    /** Background_State。 */
    val background: StateFlow<Boolean>
}

interface Clock {
    /** 单调时钟毫秒（计时、超时只用它）。 */
    fun monotonicMs(): Long

    /** UTC 毫秒（记录时刻、sent_at）。 */
    fun wallMs(): Long

    /** 本机当前本地时间（回退文件名、显示）。 */
    fun localNow(): LocalDateTime
}

/** 生产环境时钟。 */
object SystemClock : Clock {
    override fun monotonicMs(): Long = System.nanoTime() / 1_000_000

    override fun wallMs(): Long = System.currentTimeMillis()

    override fun localNow(): LocalDateTime = LocalDateTime.now()
}
