package com.lantransfer.domain

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.TransferId
import com.lantransfer.protocol.X25519KeyPair
import java.time.LocalDateTime

// M1 领域实体（设计 Data Models → M1）。字节字段用按内容比较的 Bytes（设计草图中的 ByteArray 在 data class 中按引用比较）。

/** 传输方向。 */
enum class Direction(val code: Int) {
    SEND(0),
    RECEIVE(1),
    ;

    companion object {
        fun fromCode(code: Int): Direction? = entries.firstOrNull { it.code == code }
    }
}

/** 记录时刻，以及当时本机的 UTC 偏移（20.1）。排序只看 [utcMs]，显示时用 utcMs + offsetMin 还原当时的本机时间。 */
data class RecordTime(val utcMs: Long, val offsetMin: Int)

/** 发送方的源文件引用：Android 为 content:// 或 file:// URI，Windows 为完整路径。 */
@JvmInline
value class SourceRef(val value: String)

/** 接收方临时文件引用（MediaStore 条目、SAF 文档 URI 或文件路径）。 */
@JvmInline
value class TempRef(val value: String)

/** 已接收完成的文件引用，供“打开文件”“打开所在位置”使用（19.8）。 */
@JvmInline
value class StoredFileRef(val value: String)

/** 本机身份。私钥只在 IdentityStore 和 NoiseXX 内部使用（4.7）。 */
class DeviceIdentity(val deviceId: DeviceId, val keyPair: X25519KeyPair, val displayName: String)

data class TrustedDevice(
    val deviceId: DeviceId,
    val displayName: String,
    val deviceType: DeviceType,
    val publicKey: Bytes,
    val autoAccept: Boolean = false,
    val pairedAtUtcMs: Long,
    /** 最近一次成功连接的地址（C4.1）。 */
    val lastAddress: Ipv4? = null,
    val lastPort: Int? = null,
)

data class TextRecord(
    val seq: Long,
    val peer: DeviceId,
    /** 写入记录时对方显示名称的快照（20.1）。 */
    val peerName: String,
    val messageId: MessageId,
    val direction: Direction,
    val textUtf8: Bytes,
    val sentAtUtcMs: Long,
    val recordedAt: RecordTime,
    val status: TextStatus,
)

/** 中断原因；[detail] 为写入失败的具体原因（17.6）。 */
data class InterruptInfo(val reason: InterruptReason, val detail: String? = null)

data class TransferFile(
    val transferId: TransferId,
    val index: Int,
    val seq: Long,
    /** 以下三项取自 File_Offer；RESTART 时更新 size、mtime（17.5）。 */
    val originalName: String,
    val size: Long,
    val mime: String,
    val mtime: Long?,
    val state: FileState,
    /** 接收方为已落盘字节数，发送方为已确认字节数。 */
    val persisted: Long = 0,
    /** 已知时填写：发送方读完文件后，接收方收到 FILE_END 后。 */
    val sha256: Bytes? = null,
    /** 仅发送方。 */
    val sourceRef: SourceRef? = null,
    /** 以下三项仅接收方；startedAt 持久化，保证续传后回退文件名仍用最初开始接收的时刻（16.9）。 */
    val tempRef: TempRef? = null,
    val startedAt: LocalDateTime? = null,
    val lastWriteAtUtcMs: Long? = null,
    /** 以下三项仅接收方，且状态为 COMPLETED（19.8）。 */
    val finalName: String? = null,
    val fullPath: String? = null,
    val storedRef: StoredFileRef? = null,
)

/** 传输任务（C5.3 中的 TaskRecord，包含 files）。 */
data class TransferTask(
    val transferId: TransferId,
    val peer: DeviceId,
    val peerName: String,
    val direction: Direction,
    val state: TaskState,
    /** 只在 state = INTERRUPTED 时非空。 */
    val interrupt: InterruptInfo?,
    /** 仅接收方使用，接受 Offer 时固定（16.3）。 */
    val receiveDir: DirRef?,
    /** 也是任务的创建时刻：发出或收到 File_Offer 时（20.1）。 */
    val recordedAt: RecordTime,
    val updatedAtUtcMs: Long,
    val files: List<TransferFile>,
)

/** C5.3 中的 TaskRecord 就是 TransferTask。 */
typealias TaskRecord = TransferTask

/** 对“已中断”任务点击取消后待送达的取消通知（18.9）。 */
data class PendingCancel(val peer: DeviceId, val transferId: TransferId, val createdAtUtcMs: Long)

/** 接收目录：序列化为 `default`、`tree:<uri>` 或 `path:<绝对路径>`。 */
sealed interface DirRef {
    /** 使用时才解析为实际位置（16.1、16.2）。 */
    data object Default : DirRef

    /** Android：SAF 目录树 URI。 */
    data class Tree(val uri: String) : DirRef

    /** Windows：绝对路径。 */
    data class Path(val path: String) : DirRef

    fun serialize(): String = when (this) {
        Default -> DEFAULT
        is Tree -> TREE_PREFIX + uri
        is Path -> PATH_PREFIX + path
    }

    companion object {
        private const val DEFAULT = "default"
        private const val TREE_PREFIX = "tree:"
        private const val PATH_PREFIX = "path:"

        /** 不认识的格式或前缀后为空时返回 null（调用方改用默认值并记录日志，M5）。 */
        fun parse(s: String): DirRef? = when {
            s == DEFAULT -> Default
            s.startsWith(TREE_PREFIX) && s.length > TREE_PREFIX.length -> Tree(s.substring(TREE_PREFIX.length))
            s.startsWith(PATH_PREFIX) && s.length > PATH_PREFIX.length -> Path(s.substring(PATH_PREFIX.length))
            else -> null
        }
    }
}

/** 设置项（M5）。backgroundReceive 只在 Android 上使用。 */
data class Settings(
    val receiveDirectory: DirRef = DirRef.Default,
    val autoCopy: Boolean = false,
    val backgroundReceive: Boolean = false,
)
