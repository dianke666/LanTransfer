package com.lantransfer.engine.ports

import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.Direction
import com.lantransfer.domain.InterruptInfo
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.Settings
import com.lantransfer.domain.TaskRecord
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TextRecord
import com.lantransfer.domain.TextStatus
import com.lantransfer.domain.TransferFile
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.TransferId
import kotlinx.coroutines.flow.Flow

// C5.3 持久化端口。所有写方法返回 StoreResult，失败只触发提示（20.8），不抛出异常。

sealed interface StoreResult {
    data object Ok : StoreResult

    /** appendText 命中 UNIQUE (peer_id, message_id)：重复消息（11.8）。 */
    data object Duplicate : StoreResult

    data class Failed(val detail: String) : StoreResult
}

sealed interface ListResult<out T> {
    data class Ok<T>(val items: List<T>) : ListResult<T>

    data class Failed(val detail: String) : ListResult<Nothing>
}

/** 启动时载入身份的结果（4.1、4.2、4.6）。 */
sealed interface IdentityLoad {
    /** 得到了可用（已保存）的身份。 */
    sealed interface Ready : IdentityLoad {
        val identity: DeviceIdentity
    }

    data class Loaded(override val identity: DeviceIdentity) : Ready

    /** 首次启动或应用数据被清除。 */
    data class Created(override val identity: DeviceIdentity) : Ready

    /** 已保存的身份无法读取或已损坏，已生成新身份；界面提示需要重新配对（4.6）。 */
    data class ResetCorrupted(override val identity: DeviceIdentity) : Ready

    /**
     * 需要新身份但无法保存（存储已满、Keystore 不可用）：不启动网络，主界面显示错误和“重试”（Q14）。
     * [corrupted] 为 true 时原身份已损坏（重试成功后结果为 ResetCorrupted）。
     */
    data class SaveFailed(val detail: String, val corrupted: Boolean) : IdentityLoad
}

/** 一条文字记录的标识：全局历史序号（M2 text_message.seq）。 */
@JvmInline
value class RecordId(val seq: Long)

/** 传输记录界面用的投影：TransferFile 加上所属任务的对端、对端名称快照、方向、记录时刻与任务状态（Data Models M1）。 */
data class FileRecord(
    val file: TransferFile,
    val peer: DeviceId,
    val peerName: String,
    val direction: Direction,
    val recordedAt: RecordTime,
    val taskState: TaskState,
    val interrupt: InterruptInfo? = null,
)

/** 传输记录中的一条：文字或文件。排序键为（recordedAt.utcMs, seq）（20.2）。 */
sealed interface HistoryRecord {
    val recordedAt: RecordTime
    val seq: Long

    data class Text(val record: TextRecord) : HistoryRecord {
        override val recordedAt: RecordTime get() = record.recordedAt
        override val seq: Long get() = record.seq
    }

    data class File(val record: FileRecord) : HistoryRecord {
        override val recordedAt: RecordTime get() = record.recordedAt
        override val seq: Long get() = record.file.seq
    }
}

interface IdentityStore {
    fun loadOrCreate(): IdentityLoad

    /** 设备显示名称存放在设置中，身份重置后保留（M4、M5）。 */
    fun displayName(): String

    /** 只接收 DisplayNameRules.validateUserInput 返回的清理后名称（4.4、4.9）。 */
    fun setDisplayName(name: String): StoreResult
}

interface TrustStore {
    val changes: Flow<Unit>

    fun all(): List<TrustedDevice>

    fun get(id: DeviceId): TrustedDevice?

    /** 同一设备 ID 整条替换（9.9）。 */
    fun upsert(d: TrustedDevice): StoreResult

    /** 同时删除“自动接收文件”设置（9.3）。 */
    fun delete(id: DeviceId): StoreResult

    fun setAutoAccept(id: DeviceId, on: Boolean): StoreResult

    /** 9.6 */
    fun updateName(id: DeviceId, name: String): StoreResult

    /** 最近一次成功连接的地址（C4.1）。 */
    fun recordAddress(id: DeviceId, ip: Ipv4, port: Int): StoreResult
}

interface HistoryStore {
    fun findText(peer: DeviceId, id: MessageId): TextRecord?

    /** 由存储分配 seq（忽略 r.seq）；（peer, messageId）已存在时返回 Duplicate（11.8）。 */
    fun appendText(r: TextRecord): StoreResult

    /** 每条消息只保留 1 条记录（20.7）；记录不存在时不做任何事（迟到的应答只影响 0 行）。 */
    fun updateTextStatus(id: MessageId, peer: DeviceId, s: TextStatus): StoreResult

    /** 插入或更新一个文件行；已存在的行保留原来的 seq。所属任务行不存在时一并创建。 */
    fun upsertFile(r: FileRecord): StoreResult

    /** 按记录时间、再按插入顺序排序，最多 [limit] 条（20.2）。 */
    fun list(limit: Int = 10_000): ListResult<HistoryRecord>

    /** 删除一条文字记录（20.4）。 */
    fun delete(id: RecordId): StoreResult

    /** 删除一条文件记录；任务没有剩余文件行时一并删除任务行（20.4、20.10）。 */
    fun deleteFileRecord(id: TransferId, index: Int): StoreResult

    /** 删除全部文字、任务和文件行（20.5），不动 trusted_device、pending_cancel 和 next_history_seq。 */
    fun clearAll(): StoreResult
}

interface TaskStore {
    /** 插入或更新任务行和它的全部文件行；新文件行按 index 顺序分配 seq，已存在的行保留原 seq。 */
    fun save(t: TaskRecord): StoreResult

    fun get(id: TransferId): TaskRecord?

    /** 未结束（码值 < 10）的任务（17.9）。 */
    fun unfinished(): List<TaskRecord>

    fun addPendingCancel(peer: DeviceId, id: TransferId): StoreResult

    /** 取出并删除该对端的全部待送达取消通知，按写入顺序（18.9）。 */
    fun takePendingCancels(peer: DeviceId): List<TransferId>
}

interface SettingsStore {
    /** 当前值与之后的每次变化。 */
    val changes: Flow<Settings>

    fun current(): Settings

    fun update(transform: (Settings) -> Settings): StoreResult
}
