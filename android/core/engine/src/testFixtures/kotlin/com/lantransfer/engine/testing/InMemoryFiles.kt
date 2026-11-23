package com.lantransfer.engine.testing

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.DirStatus
import com.lantransfer.domain.FsProfile
import com.lantransfer.domain.NameCollisionResolver
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.SourceStat
import com.lantransfer.domain.StoredFileRef
import com.lantransfer.domain.TempInfo
import com.lantransfer.domain.TempRef
import com.lantransfer.engine.ports.ChunkReader
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.CommitResult
import com.lantransfer.engine.ports.IoResult
import com.lantransfer.engine.ports.OutgoingTextCache
import com.lantransfer.engine.ports.ReadResult
import com.lantransfer.engine.ports.ReceiveStorage
import com.lantransfer.engine.ports.SourceFiles
import com.lantransfer.engine.ports.SourceMeta
import com.lantransfer.engine.ports.TempWriter
import com.lantransfer.engine.ports.WriteResult
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.TransferId
import java.io.ByteArrayOutputStream

// 内存版文件端口（设计 C5.2）。临时文件与最终文件分开存放，所以临时文件不会以最终文件名出现（16.12）；
// 提交时按 [FsProfile] 做重名处理（Windows 不区分大小写，16.11）。

/** 一个内存目录：是否存在、能否创建、是否可写、可用空间（null = 未知）与已提交的文件。 */
class MemoryDir {
    var exists = false
    var creatable = true
    var writable = true
    var freeSpace: Long? = null
    internal val files = LinkedHashMap<String, Bytes>()
}

/** 已登记的临时文件。 */
class MemoryTemp internal constructor(val dir: DirRef, val transferId: TransferId, val index: Int, var lastWriteUtcMs: Long) {
    internal val data = ByteArrayOutputStream()

    val length: Long get() = synchronized(data) { data.size().toLong() }

    fun bytes(): ByteArray = synchronized(data) { data.toByteArray() }
}

class InMemoryReceiveStorage(private val profile: FsProfile, private val clock: Clock) : ReceiveStorage {
    private val dirs = HashMap<DirRef, MemoryDir>()
    private val temps = LinkedHashMap<TempRef, MemoryTemp>()

    /** 返回非 null 时，在该临时文件当前长度处的写入失败，返回值为失败原因（17.6）。 */
    var writeFailure: (TempRef, Long) -> String? = { _, _ -> null }

    /** 返回非 null 时提交失败。 */
    var commitFailure: (TempRef) -> String? = { null }

    /** 目录的可配置状态（不存在时按“可创建、可写、空间未知”新建）。 */
    fun dir(ref: DirRef): MemoryDir = synchronized(this) { dirs.getOrPut(ref) { MemoryDir() } }

    /** 已提交的文件（名称 → 内容）。 */
    fun filesIn(ref: DirRef): Map<String, Bytes> = synchronized(this) { LinkedHashMap(dir(ref).files) }

    /** 预先放入一个已存在的文件（用于测试重名处理）。 */
    fun putFile(ref: DirRef, name: String, content: Bytes) = synchronized(this) {
        val d = dir(ref)
        d.exists = true
        d.files[name] = content
    }

    fun temp(ref: TempRef): MemoryTemp? = synchronized(this) { temps[ref] }

    val tempRefs: List<TempRef> get() = synchronized(this) { temps.keys.toList() }

    /** 模拟临时文件被外部删除或被清理（17.5）。 */
    fun removeTemp(ref: TempRef) = synchronized(this) { temps.remove(ref) }

    private fun pathOf(dir: DirRef): String = when (dir) {
        DirRef.Default -> "/default"
        is DirRef.Tree -> "/tree/${dir.uri}"
        is DirRef.Path -> dir.path.replace('\\', '/')
    }

    private fun sameName(a: String, b: String) = if (profile == FsProfile.WINDOWS) a.equals(b, ignoreCase = true) else a == b

    override fun probe(dir: DirRef): DirStatus = synchronized(this) {
        val d = dir(dir)
        when {
            d.exists && d.writable -> DirStatus.USABLE
            d.exists -> DirStatus.NOT_WRITABLE
            d.creatable -> DirStatus.USABLE
            else -> DirStatus.CANNOT_CREATE
        }
    }

    override fun ensureDirectory(dir: DirRef): Boolean = synchronized(this) {
        val d = dir(dir)
        if (!d.exists && d.creatable) d.exists = true
        d.exists && d.writable
    }

    override fun isWritable(dir: DirRef): Boolean = probe(dir) == DirStatus.USABLE

    override fun freeSpace(dir: DirRef): Long? = synchronized(this) { dir(dir).freeSpace }

    override fun exists(dir: DirRef, name: String): Boolean = synchronized(this) { dir(dir).files.keys.any { sameName(it, name) } }

    override fun createTemp(dir: DirRef, task: TransferId, index: Int): TempRef = synchronized(this) {
        val ref = TempRef("mem:${pathOf(dir)}/.lantransfer-tmp/${task.value.toHex()}-$index.part")
        temps.getOrPut(ref) { MemoryTemp(dir, task, index, clock.wallMs()) }
        ref
    }

    override fun openAppend(temp: TempRef, offset: Long): WriteResult<TempWriter> = synchronized(this) {
        val t = temps[temp] ?: return WriteResult.Failed("临时文件不存在")
        val d = dir(t.dir)
        if (!d.exists || !d.writable) return WriteResult.Failed("接收目录不可写入")
        val current = t.bytes()
        if (offset < 0 || offset > current.size) return WriteResult.Failed("offset $offset 超出临时文件长度 ${current.size}")
        synchronized(t.data) {
            t.data.reset()
            t.data.write(current, 0, offset.toInt())
        }
        WriteResult.Ok(MemoryTempWriter(temp, t, d))
    }

    private inner class MemoryTempWriter(private val ref: TempRef, private val t: MemoryTemp, private val d: MemoryDir) : TempWriter {
        private var closed = false

        override fun write(b: ByteArray, off: Int, len: Int): IoResult = synchronized(this@InMemoryReceiveStorage) {
            if (closed) return IoResult.Failed("写入器已关闭")
            writeFailure(ref, t.length)?.let { return IoResult.Failed(it) }
            val free = d.freeSpace
            if (free != null && len > free) return IoResult.Failed("存储空间不足")
            if (free != null) d.freeSpace = free - len
            synchronized(t.data) { t.data.write(b, off, len) }
            t.lastWriteUtcMs = clock.wallMs()
            IoResult.Ok
        }

        override fun flushSync(): IoResult = if (closed) IoResult.Failed("写入器已关闭") else IoResult.Ok

        override fun close() {
            closed = true
        }
    }

    override fun openRead(temp: TempRef): ReadResult<ChunkReader> = synchronized(this) {
        val t = temps[temp] ?: return ReadResult.Failed("临时文件不存在")
        ReadResult.Ok(BytesReader(t.bytes(), 0, null))
    }

    override fun length(temp: TempRef): Long? = synchronized(this) { temps[temp]?.length }

    override fun commit(temp: TempRef, sanitizedName: String): CommitResult = synchronized(this) {
        val t = temps[temp] ?: return CommitResult.Failed("临时文件不存在")
        commitFailure(temp)?.let { return CommitResult.Failed(it) }
        val d = dir(t.dir)
        if (!d.exists || !d.writable) return CommitResult.Failed("接收目录不可写入")
        val final = NameCollisionResolver.resolve(sanitizedName, profile, { n -> d.files.keys.any { sameName(it, n) } })
        d.files[final] = Bytes.of(t.bytes())
        temps.remove(temp)
        val fullPath = "${pathOf(t.dir)}/$final"
        CommitResult.Ok(final, fullPath, StoredFileRef("mem:$fullPath"))
    }

    override fun delete(temp: TempRef): Boolean = synchronized(this) { temps.remove(temp) != null }

    override fun listTemps(): List<TempInfo> = synchronized(this) {
        temps.map { (ref, t) -> TempInfo(ref, t.transferId, t.lastWriteUtcMs) }
    }
}

/** 从字节数组读取；[failAt] 非 null 时，读到该位置（绝对偏移）时返回 Failed（模拟读取过程中文件被删除，14.7）。 */
internal class BytesReader(private val data: ByteArray, start: Int, private val failAt: Long?) : ChunkReader {
    private var pos = start
    private var closed = false

    override fun read(buf: ByteArray, off: Int, len: Int): ReadResult<Int> {
        if (closed) return ReadResult.Failed("读取器已关闭")
        if (failAt != null && pos >= failAt) return ReadResult.Failed("读取失败")
        if (pos >= data.size) return ReadResult.Ok(-1)
        val limit = if (failAt != null) minOf(data.size.toLong(), failAt).toInt() else data.size
        val n = minOf(len, limit - pos)
        System.arraycopy(data, pos, buf, off, n)
        pos += n
        return ReadResult.Ok(n)
    }

    override fun close() {
        closed = true
    }
}

/** 内存版“以 .txt 文件发送”缓存：写入的文件同时登记到 [sources]，可直接走文件发送流程；[failWrites] 模拟缓存目录不可写。 */
class InMemoryOutgoingTextCache(private val sources: InMemorySourceFiles) : OutgoingTextCache {
    private val dirs = LinkedHashMap<String, SourceRef>()

    var failWrites = false

    /** 已创建的子目录名（按创建顺序）。 */
    val dirNames: List<String> get() = synchronized(this) { dirs.keys.toList() }

    override fun write(dirName: String, fileName: String, content: ByteArray): WriteResult<SourceRef> = synchronized(this) {
        if (failWrites) return WriteResult.Failed("injected failure")
        if (dirName in dirs) return WriteResult.Failed("目录已存在")
        val ref = SourceRef("mem:outgoing-text/$dirName/$fileName")
        dirs[dirName] = ref
        sources.put(ref, fileName, Bytes.of(content), mime = "text/plain")
        WriteResult.Ok(ref)
    }

    override fun list(): List<SourceRef> = synchronized(this) { dirs.values.toList() }

    override fun owns(ref: SourceRef): Boolean = synchronized(this) { ref in dirs.values }

    override fun delete(ref: SourceRef): Boolean = synchronized(this) {
        val dir = dirs.entries.firstOrNull { it.value == ref }?.key ?: return false
        dirs.remove(dir)
        sources.remove(ref)
        true
    }
}

/** 内存版源文件。 */
class InMemorySourceFiles : SourceFiles {
    class Entry internal constructor(
        var name: String,
        var data: Bytes,
        var mtime: Long?,
        var mime: String,
        val isDirectory: Boolean,
    ) {
        /** false：打开即失败（已删除、无权限，14.7）。 */
        var readable = true

        /** 非 null：读到该偏移时失败。 */
        var failAt: Long? = null
    }

    private val entries = LinkedHashMap<SourceRef, Entry>()

    fun put(ref: SourceRef, name: String, data: Bytes, mtime: Long? = 1_700_000_000_000, mime: String = SourceMeta.OCTET_STREAM): Entry =
        synchronized(this) { Entry(name, data, mtime, mime, false).also { entries[ref] = it } }

    fun putDirectory(ref: SourceRef, name: String): Entry = synchronized(this) {
        Entry(name, Bytes.EMPTY, null, SourceMeta.OCTET_STREAM, true).also { entries[ref] = it }
    }

    fun entry(ref: SourceRef): Entry? = synchronized(this) { entries[ref] }

    /** 模拟源文件被修改（17.5）。 */
    fun modify(ref: SourceRef, data: Bytes, mtime: Long?) = synchronized(this) {
        entries[ref]?.let {
            it.data = data
            it.mtime = mtime
        }
    }

    fun remove(ref: SourceRef) = synchronized(this) { entries.remove(ref) }

    override fun describe(ref: SourceRef): SourceMeta? = synchronized(this) {
        entries[ref]?.let { SourceMeta(it.name, it.data.size.toLong(), it.mtime, it.mime, it.isDirectory) }
    }

    override fun stat(ref: SourceRef): SourceStat? = synchronized(this) {
        entries[ref]?.takeIf { !it.isDirectory && it.readable }?.let { SourceStat(it.data.size.toLong(), it.mtime) }
    }

    override fun open(ref: SourceRef, offset: Long): ReadResult<ChunkReader> = synchronized(this) {
        val e = entries[ref] ?: return ReadResult.Failed("文件不存在")
        if (e.isDirectory || !e.readable) return ReadResult.Failed("无法读取")
        val bytes = e.data.toByteArray()
        if (offset < 0 || offset > bytes.size) return ReadResult.Failed("offset $offset 超出文件长度 ${bytes.size}")
        ReadResult.Ok(BytesReader(bytes, offset.toInt(), e.failAt))
    }
}
