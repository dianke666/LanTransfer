package com.lantransfer.app.storage

import android.content.Context
import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.sqlite.db.SupportSQLiteDatabase

@Entity(tableName = "schema_meta")
data class SchemaMetaEntity(
    @PrimaryKey
    @ColumnInfo(name = "key")
    val key: String,
    @ColumnInfo(name = "value")
    val value: Long,
)

@Entity(tableName = "trusted_device")
data class TrustedDeviceEntity(
    @PrimaryKey
    @ColumnInfo(name = "device_id", typeAffinity = ColumnInfo.BLOB)
    val deviceId: ByteArray,
    @ColumnInfo(name = "display_name")
    val displayName: String,
    @ColumnInfo(name = "device_type")
    val deviceType: Int,
    @ColumnInfo(name = "public_key", typeAffinity = ColumnInfo.BLOB)
    val publicKey: ByteArray,
    @ColumnInfo(name = "auto_accept", defaultValue = "0")
    val autoAccept: Int = 0,
    @ColumnInfo(name = "paired_at")
    val pairedAt: Long,
    @ColumnInfo(name = "last_ip")
    val lastIp: Long? = null,
    @ColumnInfo(name = "last_port")
    val lastPort: Int? = null,
) {
    override fun equals(other: Any?): Boolean =
        this === other || (other is TrustedDeviceEntity && deviceId.contentEquals(other.deviceId))

    override fun hashCode(): Int = deviceId.contentHashCode()
}

@Entity(
    tableName = "text_message",
    indices = [
        Index(value = ["peer_id", "message_id"], unique = true, name = "ux_text_message_peer_msg"),
        Index(value = ["recorded_at", "seq"], name = "ix_text_message_order"),
    ],
)
data class TextMessageEntity(
    @PrimaryKey
    @ColumnInfo(name = "seq")
    val seq: Long,
    @ColumnInfo(name = "peer_id", typeAffinity = ColumnInfo.BLOB)
    val peerId: ByteArray,
    @ColumnInfo(name = "message_id", typeAffinity = ColumnInfo.BLOB)
    val messageId: ByteArray,
    @ColumnInfo(name = "direction")
    val direction: Int,
    @ColumnInfo(name = "peer_name")
    val peerName: String,
    @ColumnInfo(name = "text_utf8", typeAffinity = ColumnInfo.BLOB)
    val textUtf8: ByteArray,
    @ColumnInfo(name = "sent_at")
    val sentAt: Long,
    @ColumnInfo(name = "recorded_at")
    val recordedAt: Long,
    @ColumnInfo(name = "recorded_tz")
    val recordedTz: Int,
    @ColumnInfo(name = "status")
    val status: Int,
) {
    override fun equals(other: Any?): Boolean = this === other || (other is TextMessageEntity && seq == other.seq)

    override fun hashCode(): Int = seq.hashCode()
}

@Entity(
    tableName = "transfer_task",
    indices = [
        Index(value = ["recorded_at"], name = "ix_transfer_task_order"),
        Index(value = ["peer_id", "state"], name = "ix_transfer_task_peer_state"),
    ],
)
data class TransferTaskEntity(
    @PrimaryKey
    @ColumnInfo(name = "transfer_id", typeAffinity = ColumnInfo.BLOB)
    val transferId: ByteArray,
    @ColumnInfo(name = "peer_id", typeAffinity = ColumnInfo.BLOB)
    val peerId: ByteArray,
    @ColumnInfo(name = "peer_name")
    val peerName: String,
    @ColumnInfo(name = "direction")
    val direction: Int,
    @ColumnInfo(name = "state")
    val state: Int,
    @ColumnInfo(name = "interrupt_reason")
    val interruptReason: Int? = null,
    @ColumnInfo(name = "interrupt_detail")
    val interruptDetail: String? = null,
    @ColumnInfo(name = "receive_dir")
    val receiveDir: String? = null,
    @ColumnInfo(name = "recorded_at")
    val recordedAt: Long,
    @ColumnInfo(name = "recorded_tz")
    val recordedTz: Int,
    @ColumnInfo(name = "updated_at")
    val updatedAt: Long,
) {
    override fun equals(other: Any?): Boolean =
        this === other || (other is TransferTaskEntity && transferId.contentEquals(other.transferId))

    override fun hashCode(): Int = transferId.contentHashCode()
}

@Entity(
    tableName = "transfer_file",
    primaryKeys = ["transfer_id", "file_index"],
    indices = [
        Index(value = ["seq"], unique = true, name = "ux_transfer_file_seq"),
    ],
)
data class TransferFileEntity(
    @ColumnInfo(name = "transfer_id", typeAffinity = ColumnInfo.BLOB)
    val transferId: ByteArray,
    @ColumnInfo(name = "file_index")
    val fileIndex: Int,
    @ColumnInfo(name = "seq")
    val seq: Long,
    @ColumnInfo(name = "original_name")
    val originalName: String,
    @ColumnInfo(name = "size")
    val size: Long,
    @ColumnInfo(name = "mime")
    val mime: String,
    @ColumnInfo(name = "mtime")
    val mtime: Long? = null,
    @ColumnInfo(name = "state")
    val state: Int,
    @ColumnInfo(name = "persisted", defaultValue = "0")
    val persisted: Long = 0L,
    @ColumnInfo(name = "sha256", typeAffinity = ColumnInfo.BLOB)
    val sha256: ByteArray? = null,
    @ColumnInfo(name = "source_ref")
    val sourceRef: String? = null,
    @ColumnInfo(name = "temp_ref")
    val tempRef: String? = null,
    @ColumnInfo(name = "started_at")
    val startedAt: String? = null,
    @ColumnInfo(name = "last_write_at")
    val lastWriteAt: Long? = null,
    @ColumnInfo(name = "final_name")
    val finalName: String? = null,
    @ColumnInfo(name = "full_path")
    val fullPath: String? = null,
    @ColumnInfo(name = "stored_ref")
    val storedRef: String? = null,
) {
    override fun equals(other: Any?): Boolean =
        this === other || (other is TransferFileEntity && transferId.contentEquals(other.transferId) && fileIndex == other.fileIndex)

    override fun hashCode(): Int = 31 * transferId.contentHashCode() + fileIndex
}

@Entity(
    tableName = "pending_cancel",
    primaryKeys = ["peer_id", "transfer_id"],
)
data class PendingCancelEntity(
    @ColumnInfo(name = "peer_id", typeAffinity = ColumnInfo.BLOB)
    val peerId: ByteArray,
    @ColumnInfo(name = "transfer_id", typeAffinity = ColumnInfo.BLOB)
    val transferId: ByteArray,
    @ColumnInfo(name = "created_at")
    val createdAt: Long,
) {
    override fun equals(other: Any?): Boolean =
        this === other || (other is PendingCancelEntity && peerId.contentEquals(other.peerId) && transferId.contentEquals(other.transferId))

    override fun hashCode(): Int = 31 * peerId.contentHashCode() + transferId.contentHashCode()
}

@Dao
interface LanTransferDao {
    // ---- schema_meta ----
    @Query("SELECT value FROM schema_meta WHERE key = :key")
    fun getMeta(key: String): Long?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun putMeta(entity: SchemaMetaEntity)

    // ---- trusted_device ----
    @Query("SELECT * FROM trusted_device ORDER BY paired_at ASC")
    fun allTrusted(): List<TrustedDeviceEntity>

    @Query("SELECT * FROM trusted_device WHERE device_id = :id")
    fun getTrusted(id: ByteArray): TrustedDeviceEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertTrusted(entity: TrustedDeviceEntity)

    @Query("DELETE FROM trusted_device WHERE device_id = :id")
    fun deleteTrusted(id: ByteArray)

    @Query("UPDATE trusted_device SET auto_accept = :autoAccept WHERE device_id = :id")
    fun updateAutoAccept(id: ByteArray, autoAccept: Int)

    @Query("UPDATE trusted_device SET display_name = :name WHERE device_id = :id")
    fun updateTrustedName(id: ByteArray, name: String)

    @Query("UPDATE trusted_device SET last_ip = :ip, last_port = :port WHERE device_id = :id")
    fun updateTrustedAddress(id: ByteArray, ip: Long, port: Int)

    // ---- text_message ----
    @Query("SELECT * FROM text_message WHERE peer_id = :peerId AND message_id = :msgId")
    fun findText(peerId: ByteArray, msgId: ByteArray): TextMessageEntity?

    @Insert(onConflict = OnConflictStrategy.IGNORE)
    fun insertText(entity: TextMessageEntity): Long

    @Query("UPDATE text_message SET status = :status WHERE peer_id = :peerId AND message_id = :msgId")
    fun updateTextStatus(peerId: ByteArray, msgId: ByteArray, status: Int)

    @Query("SELECT * FROM text_message ORDER BY recorded_at ASC, seq ASC LIMIT :limit")
    fun listTexts(limit: Int): List<TextMessageEntity>

    @Query("DELETE FROM text_message WHERE seq = :seq")
    fun deleteText(seq: Long)

    @Query("DELETE FROM text_message")
    fun clearTexts()

    // ---- transfer_task & transfer_file ----
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertTask(entity: TransferTaskEntity)

    @Query("SELECT * FROM transfer_task WHERE transfer_id = :tid")
    fun getTask(tid: ByteArray): TransferTaskEntity?

    @Query("SELECT * FROM transfer_task WHERE state < 10 ORDER BY recorded_at ASC")
    fun unfinishedTasks(): List<TransferTaskEntity>

    @Query("SELECT * FROM transfer_task ORDER BY recorded_at ASC")
    fun allTasks(): List<TransferTaskEntity>

    @Query("SELECT * FROM transfer_file WHERE transfer_id = :tid ORDER BY file_index ASC")
    fun filesForTask(tid: ByteArray): List<TransferFileEntity>

    @Query("SELECT * FROM transfer_file WHERE transfer_id = :tid AND file_index = :idx")
    fun getFile(tid: ByteArray, idx: Int): TransferFileEntity?

    @Query("SELECT * FROM transfer_file")
    fun allFiles(): List<TransferFileEntity>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    fun upsertFile(entity: TransferFileEntity)

    @Query("DELETE FROM transfer_file WHERE transfer_id = :tid AND file_index = :idx")
    fun deleteFile(tid: ByteArray, idx: Int)

    @Query("SELECT COUNT(*) FROM transfer_file WHERE transfer_id = :tid")
    fun countFiles(tid: ByteArray): Int

    @Query("DELETE FROM transfer_task WHERE transfer_id = :tid")
    fun deleteTask(tid: ByteArray)

    @Query("DELETE FROM transfer_file")
    fun clearFiles()

    @Query("DELETE FROM transfer_task")
    fun clearTasks()

    // ---- pending_cancel ----
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    fun insertPendingCancel(entity: PendingCancelEntity)

    @Query("SELECT * FROM pending_cancel WHERE peer_id = :peerId ORDER BY created_at ASC")
    fun getPendingCancels(peerId: ByteArray): List<PendingCancelEntity>

    @Query("DELETE FROM pending_cancel WHERE peer_id = :peerId")
    fun deletePendingCancels(peerId: ByteArray)
}

@Database(
    entities = [
        SchemaMetaEntity::class,
        TrustedDeviceEntity::class,
        TextMessageEntity::class,
        TransferTaskEntity::class,
        TransferFileEntity::class,
        PendingCancelEntity::class,
    ],
    version = 1,
    exportSchema = true,
)
abstract class LanTransferDatabase : RoomDatabase() {
    abstract fun dao(): LanTransferDao

    fun allocateSeq(): Long {
        val d = dao()
        val next = d.getMeta(KEY_NEXT_SEQ) ?: 1L
        d.putMeta(SchemaMetaEntity(KEY_NEXT_SEQ, next + 1L))
        return next
    }

    companion object {
        const val DB_NAME = "lantransfer.db"
        const val KEY_NEXT_SEQ = "next_history_seq"

        fun create(context: Context): LanTransferDatabase =
            Room.databaseBuilder(context.applicationContext, LanTransferDatabase::class.java, DB_NAME)
                .setJournalMode(JournalMode.WRITE_AHEAD_LOGGING)
                .addCallback(
                    object : Callback() {
                        override fun onCreate(db: SupportSQLiteDatabase) {
                            db.execSQL("INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('$KEY_NEXT_SEQ', 1)")
                        }
                    },
                )
                .build()

        fun createInMemory(context: Context): LanTransferDatabase =
            Room.inMemoryDatabaseBuilder(context.applicationContext, LanTransferDatabase::class.java)
                .allowMainThreadQueries()
                .build()
    }
}
