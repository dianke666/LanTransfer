package com.lantransfer.engine.ports

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.DirStatus
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.SourceStat
import com.lantransfer.domain.StoredFileRef
import com.lantransfer.domain.TempInfo
import com.lantransfer.domain.TempRef
import com.lantransfer.protocol.TransferId

// C5.2 文件端口。临时文件不会以最终文件名出现（16.12）；提交时才做文件名处理后的重名处理与不覆盖重命名（16.11）。

/** 不带返回值的 I/O 结果。[Failed.detail] 是给用户看的失败原因（17.6）。 */
sealed interface IoResult {
    data object Ok : IoResult

    data class Failed(val detail: String) : IoResult
}

sealed interface WriteResult<out T> {
    data class Ok<T>(val value: T) : WriteResult<T>

    data class Failed(val detail: String) : WriteResult<Nothing>
}

sealed interface ReadResult<out T> {
    data class Ok<T>(val value: T) : ReadResult<T>

    data class Failed(val detail: String) : ReadResult<Nothing>
}

/** 临时文件写入器。 */
interface TempWriter {
    fun write(b: ByteArray, off: Int = 0, len: Int = b.size - off): IoResult

    /** 落盘（fsync）；接收方在校验与提交之前调用。 */
    fun flushSync(): IoResult

    fun close()
}

/** 顺序读取器（源文件或续传时重新计算哈希的临时文件）。 */
interface ChunkReader {
    /** 读入 buf[off, off + len)，Ok(n) 中 n ≥ 1 为读到的字节数，n = -1 表示 EOF；读取失败返回 Failed（14.7）。 */
    fun read(buf: ByteArray, off: Int = 0, len: Int = buf.size - off): ReadResult<Int>

    fun close()
}

sealed interface CommitResult {
    data class Ok(val finalName: String, val fullPath: String, val storedRef: StoredFileRef) : CommitResult

    data class Failed(val detail: String) : CommitResult
}

/** 源文件元数据：名称不含路径；无法识别类型时 mime 为 application/octet-stream（14.4、14.9）。 */
data class SourceMeta(val name: String, val size: Long, val mtime: Long?, val mime: String, val isDirectory: Boolean) {
    companion object {
        const val OCTET_STREAM = "application/octet-stream"
    }
}

interface ReceiveStorage {
    /** 目录不存在但可以创建时也算 USABLE（写第一个字节之前再创建，16.14）。 */
    fun probe(dir: DirRef): DirStatus

    /** 创建目录及缺失的上级目录（16.14）。 */
    fun ensureDirectory(dir: DirRef): Boolean

    /** 选择新目录时检查（16.13）。 */
    fun isWritable(dir: DirRef): Boolean

    /** 可用空间；无法获知时为 null（15.5）。 */
    fun freeSpace(dir: DirRef): Long?

    /** 供 NameCollisionResolver 使用；Windows 不区分大小写。 */
    fun exists(dir: DirRef, name: String): Boolean

    /** 该任务该文件的临时文件引用；已存在时返回同一个（续传）。 */
    fun createTemp(dir: DirRef, task: TransferId, index: Int): TempRef

    /** 截断到 offset 后追加。 */
    fun openAppend(temp: TempRef, offset: Long): WriteResult<TempWriter>

    fun openRead(temp: TempRef): ReadResult<ChunkReader>

    /** null 表示临时文件不存在（RESUME_STATE 报 NONE）。 */
    fun length(temp: TempRef): Long?

    /** 内部做重名处理和不覆盖重命名。 */
    fun commit(temp: TempRef, sanitizedName: String): CommitResult

    fun delete(temp: TempRef): Boolean

    /** 供 TempFileJanitor 使用（17.7）。 */
    fun listTemps(): List<TempInfo>
}

/**
 * “以 .txt 文件发送”的缓存目录（设计 C1.12、M4：Android `cacheDir/outgoing-text/`，Windows
 * `%LOCALAPPDATA%\LanTransfer\cache\outgoing-text\`）。设计 C5.2 未列出，此处补充。
 */
interface OutgoingTextCache {
    /**
     * 新建子目录 [dirName]，在其中写入文件 [fileName]，内容恰好为 [content]（不加任何字节）；
     * 返回的引用可直接交给 [SourceFiles] 读取（Android 为 file:// URI，Windows 为完整路径）。
     */
    fun write(dirName: String, fileName: String, content: ByteArray): WriteResult<SourceRef>

    /** 缓存中现有的全部文件（TempFileJanitor 启动清理用，M4）。 */
    fun list(): List<SourceRef>

    /** [ref] 是否是本缓存中的文件。 */
    fun owns(ref: SourceRef): Boolean

    /** 删除 [ref] 所在的整个子目录；不存在时返回 false。 */
    fun delete(ref: SourceRef): Boolean
}

interface SourceFiles {
    fun describe(ref: SourceRef): SourceMeta?

    /** 续传前比对大小和修改时间（17.5）；不存在或无法读取时为 null。 */
    fun stat(ref: SourceRef): SourceStat?

    /** 从 offset 起读取；打开失败返回 Failed（14.7）。 */
    fun open(ref: SourceRef, offset: Long): ReadResult<ChunkReader>
}
