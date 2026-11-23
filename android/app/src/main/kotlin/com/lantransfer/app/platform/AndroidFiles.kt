package com.lantransfer.app.platform

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.os.StatFs
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.provider.OpenableColumns
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
import com.lantransfer.engine.ports.CommitResult
import com.lantransfer.engine.ports.IoResult
import com.lantransfer.engine.ports.OutgoingTextCache
import com.lantransfer.engine.ports.ReadResult
import com.lantransfer.engine.ports.ReceiveStorage
import com.lantransfer.engine.ports.SourceFiles
import com.lantransfer.engine.ports.SourceMeta
import com.lantransfer.engine.ports.TempWriter
import com.lantransfer.engine.ports.WriteResult
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.TransferId
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.io.RandomAccessFile

/**
 * Android 接收存储适配（设计 C6.2、需求 16.1–16.14、17.7）：
 * - 默认目录：API 29+ 使用 MediaStore.Downloads（RelativePath = Download/LanTransfer/，IS_PENDING=1 -> 0）；
 *   API 26–28 使用公共 Downloads/LanTransfer/.lantransfer-tmp/ -> 原子重命名。
 * - 自定义目录（SAF DirRef.Tree）：在授权目录内使用 .lt-probe 探写与 .lt-<tid>-<index>.part 临时文件。
 */
class AndroidReceiveStorage(
    private val context: Context,
    private val knownDirs: () -> List<DirRef>,
) : ReceiveStorage {
    private val resolver = context.applicationContext.contentResolver
    private val tempMeta = HashMap<String, TempRecord>()

    private data class TempRecord(val dir: DirRef, val task: TransferId, val index: Int)

    override fun probe(dir: DirRef): DirStatus = when (dir) {
        DirRef.Default -> {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                DirStatus.USABLE
            } else {
                val target = legacyDefaultDir()
                if (target.exists()) {
                    if (target.isDirectory && target.canWrite()) DirStatus.USABLE else DirStatus.NOT_WRITABLE
                } else {
                    val parent = target.parentFile
                    if (parent != null && (parent.exists() || parent.mkdirs())) DirStatus.USABLE else DirStatus.CANNOT_CREATE
                }
            }
        }
        is DirRef.Tree -> if (isSafWritable(Uri.parse(dir.uri))) DirStatus.USABLE else DirStatus.NOT_WRITABLE
        is DirRef.Path -> {
            val f = File(dir.path)
            if (f.exists()) {
                if (f.isDirectory && f.canWrite()) DirStatus.USABLE else DirStatus.NOT_WRITABLE
            } else {
                DirStatus.USABLE
            }
        }
    }

    override fun ensureDirectory(dir: DirRef): Boolean = when (dir) {
        DirRef.Default -> {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                true
            } else {
                val d = legacyDefaultDir()
                val tmp = File(d, LEGACY_TMP_DIR)
                (d.exists() || d.mkdirs()) && (tmp.exists() || tmp.mkdirs())
            }
        }
        is DirRef.Tree -> isSafWritable(Uri.parse(dir.uri))
        is DirRef.Path -> {
            val d = File(dir.path)
            d.exists() || d.mkdirs()
        }
    }

    override fun isWritable(dir: DirRef): Boolean = when (dir) {
        DirRef.Default -> probe(dir) == DirStatus.USABLE
        is DirRef.Tree -> isSafWritable(Uri.parse(dir.uri))
        is DirRef.Path -> runCatching {
            val d = File(dir.path)
            if (!d.exists() && !d.mkdirs()) return@runCatching false
            val probeFile = File(d, ".lt-probe")
            probeFile.writeBytes(ByteArray(1))
            probeFile.delete()
            true
        }.getOrDefault(false)
    }

    override fun freeSpace(dir: DirRef): Long? = runCatching {
        val path = when (dir) {
            DirRef.Default -> Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
            is DirRef.Path -> File(dir.path)
            is DirRef.Tree -> Environment.getExternalStorageDirectory()
        }
        if (path.exists()) StatFs(path.absolutePath).availableBytes else null
    }.getOrNull()

    override fun exists(dir: DirRef, name: String): Boolean = when (dir) {
        DirRef.Default -> {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                queryMediaStoreItem(name) != null
            } else {
                File(legacyDefaultDir(), name).exists()
            }
        }
        is DirRef.Tree -> findSafChild(Uri.parse(dir.uri), name) != null
        is DirRef.Path -> File(dir.path, name).exists()
    }

    override fun createTemp(dir: DirRef, task: TransferId, index: Int): TempRef {
        val partName = ".lt-${task.value.toHex()}-$index.part"
        return when (dir) {
            DirRef.Default -> {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    val existing = queryMediaStorePending(partName)
                    val uri = existing ?: insertMediaStorePending(partName)
                    TempRef(uri.toString()).also { synchronized(tempMeta) { tempMeta[it.value] = TempRecord(dir, task, index) } }
                } else {
                    val tmpDir = File(legacyDefaultDir(), LEGACY_TMP_DIR).apply { mkdirs() }
                    val file = File(tmpDir, partName)
                    TempRef(file.absolutePath).also { synchronized(tempMeta) { tempMeta[it.value] = TempRecord(dir, task, index) } }
                }
            }
            is DirRef.Tree -> {
                val treeUri = Uri.parse(dir.uri)
                val existing = findSafChild(treeUri, partName)
                val docUri = existing ?: createSafDocument(treeUri, partName)
                TempRef(docUri.toString()).also { synchronized(tempMeta) { tempMeta[it.value] = TempRecord(dir, task, index) } }
            }
            is DirRef.Path -> {
                val tmpDir = File(dir.path, LEGACY_TMP_DIR).apply { mkdirs() }
                val file = File(tmpDir, partName)
                TempRef(file.absolutePath).also { synchronized(tempMeta) { tempMeta[it.value] = TempRecord(dir, task, index) } }
            }
        }
    }

    override fun openAppend(temp: TempRef, offset: Long): WriteResult<TempWriter> = try {
        if (temp.value.startsWith("content://")) {
            val uri = Uri.parse(temp.value)
            val pfd = resolver.openFileDescriptor(uri, "rw")
                ?: return WriteResult.Failed("无法打开临时文件")
            val fos = FileOutputStream(pfd.fileDescriptor)
            fos.channel.truncate(offset)
            fos.channel.position(offset)
            WriteResult.Ok(PfdTempWriter(pfd, fos))
        } else {
            val file = File(temp.value)
            file.parentFile?.mkdirs()
            val raf = RandomAccessFile(file, "rw")
            raf.setLength(offset)
            raf.seek(offset)
            WriteResult.Ok(RafTempWriter(raf))
        }
    } catch (e: Throwable) {
        WriteResult.Failed(e.message ?: "无法写入临时文件")
    }

    override fun openRead(temp: TempRef): ReadResult<ChunkReader> {
        return try {
            if (temp.value.startsWith("content://")) {
                val uri = Uri.parse(temp.value)
                val stream = resolver.openInputStream(uri)
                    ?: return ReadResult.Failed("无法读取临时文件")
                ReadResult.Ok(StreamChunkReader(stream))
            } else {
                val file = File(temp.value)
                if (!file.exists()) return ReadResult.Failed("临时文件不存在")
                ReadResult.Ok(StreamChunkReader(FileInputStream(file)))
            }
        } catch (e: Throwable) {
            ReadResult.Failed(e.message ?: "无法读取临时文件")
        }
    }

    override fun length(temp: TempRef): Long? = runCatching {
        if (temp.value.startsWith("content://")) {
            val uri = Uri.parse(temp.value)
            resolver.openFileDescriptor(uri, "r")?.use { it.statSize.takeIf { s -> s >= 0 } }
        } else {
            val f = File(temp.value)
            if (f.exists()) f.length() else null
        }
    }.getOrNull()

    override fun commit(temp: TempRef, sanitizedName: String): CommitResult {
        val meta = synchronized(tempMeta) { tempMeta[temp.value] }
        val dir = meta?.dir ?: DirRef.Default
        val finalName = NameCollisionResolver.resolve(sanitizedName, FsProfile.ANDROID, exists = { candidate ->
            exists(dir, candidate)
        })
        return try {
            when {
                dir is DirRef.Default && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && temp.value.startsWith("content://") -> {
                    val uri = Uri.parse(temp.value)
                    val values = ContentValues().apply {
                        put(MediaStore.Downloads.DISPLAY_NAME, finalName)
                        put(MediaStore.Downloads.IS_PENDING, 0)
                    }
                    resolver.update(uri, values, null, null)
                    val fullPath = "Download/$DEFAULT_SUBDIR/$finalName"
                    CommitResult.Ok(finalName, fullPath, StoredFileRef(uri.toString()))
                }
                dir is DirRef.Tree && temp.value.startsWith("content://") -> {
                    val srcUri = Uri.parse(temp.value)
                    val renamed = DocumentsContract.renameDocument(resolver, srcUri, finalName) ?: srcUri
                    val displayDir = formatTreeDisplayPath(dir.uri)
                    CommitResult.Ok(finalName, "$displayDir/$finalName", StoredFileRef(renamed.toString()))
                }
                else -> {
                    val srcFile = File(temp.value)
                    val targetDir = when (dir) {
                        is DirRef.Path -> File(dir.path)
                        else -> legacyDefaultDir()
                    }.apply { mkdirs() }
                    val dest = File(targetDir, finalName)
                    if (!srcFile.renameTo(dest)) {
                        srcFile.copyTo(dest, overwrite = false)
                        srcFile.delete()
                    }
                    CommitResult.Ok(finalName, dest.absolutePath, StoredFileRef(dest.absolutePath))
                }
            }
        } catch (e: Throwable) {
            CommitResult.Failed(e.message ?: "提交接收文件失败")
        }
    }

    override fun delete(temp: TempRef): Boolean = runCatching {
        synchronized(tempMeta) { tempMeta.remove(temp.value) }
        if (temp.value.startsWith("content://")) {
            val uri = Uri.parse(temp.value)
            runCatching { DocumentsContract.deleteDocument(resolver, uri) }.getOrElse {
                resolver.delete(uri, null, null) > 0
            }
        } else {
            File(temp.value).delete()
        }
    }.getOrDefault(false)

    override fun listTemps(): List<TempInfo> {
        val out = mutableListOf<TempInfo>()
        // 1. API 29+ MediaStore pending items
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            runCatching {
                val proj = arrayOf(
                    MediaStore.Downloads._ID,
                    MediaStore.Downloads.DISPLAY_NAME,
                    MediaStore.Downloads.DATE_MODIFIED,
                )
                val sel = "${MediaStore.Downloads.IS_PENDING} = 1 AND ${MediaStore.Downloads.RELATIVE_PATH} LIKE ?"
                resolver.query(MediaStore.Downloads.EXTERNAL_CONTENT_URI, proj, sel, arrayOf("%$DEFAULT_SUBDIR%"), null)?.use { c ->
                    while (c.moveToNext()) {
                        val id = c.getLong(0)
                        val name = c.getString(1) ?: continue
                        val modMs = c.getLong(2) * 1000L
                        val uri = Uri.withAppendedPath(MediaStore.Downloads.EXTERNAL_CONTENT_URI, id.toString())
                        out += TempInfo(TempRef(uri.toString()), parseTransferId(name), modMs)
                    }
                }
            }
        }
        // 2. Legacy / path directories
        for (dir in knownDirs().distinct()) {
            val tmpFolder = when (dir) {
                DirRef.Default -> if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) File(legacyDefaultDir(), LEGACY_TMP_DIR) else null
                is DirRef.Path -> File(dir.path, LEGACY_TMP_DIR)
                is DirRef.Tree -> null
            } ?: continue
            tmpFolder.listFiles()?.forEach { f ->
                if (f.isFile && f.name.endsWith(".part")) {
                    out += TempInfo(TempRef(f.absolutePath), parseTransferId(f.name), f.lastModified())
                }
            }
        }
        return out
    }

    fun formatDirDisplay(dir: DirRef): String = when (dir) {
        DirRef.Default -> "Download/$DEFAULT_SUBDIR"
        is DirRef.Tree -> formatTreeDisplayPath(dir.uri)
        is DirRef.Path -> dir.path
    }

    private fun legacyDefaultDir(): File =
        File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), DEFAULT_SUBDIR)

    private fun queryMediaStoreItem(displayName: String): Uri? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        val proj = arrayOf(MediaStore.Downloads._ID)
        val sel = "${MediaStore.Downloads.DISPLAY_NAME} = ? AND ${MediaStore.Downloads.RELATIVE_PATH} LIKE ?"
        return resolver.query(
            MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            proj,
            sel,
            arrayOf(displayName, "%$DEFAULT_SUBDIR%"),
            null,
        )?.use { c ->
            if (c.moveToFirst()) Uri.withAppendedPath(MediaStore.Downloads.EXTERNAL_CONTENT_URI, c.getLong(0).toString()) else null
        }
    }

    private fun queryMediaStorePending(partName: String): Uri? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return null
        val proj = arrayOf(MediaStore.Downloads._ID)
        val sel = "${MediaStore.Downloads.DISPLAY_NAME} = ? AND ${MediaStore.Downloads.IS_PENDING} = 1"
        return resolver.query(
            MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            proj,
            sel,
            arrayOf(partName),
            null,
        )?.use { c ->
            if (c.moveToFirst()) Uri.withAppendedPath(MediaStore.Downloads.EXTERNAL_CONTENT_URI, c.getLong(0).toString()) else null
        }
    }

    @androidx.annotation.RequiresApi(Build.VERSION_CODES.Q)
    private fun insertMediaStorePending(partName: String): Uri {
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, partName)
            put(MediaStore.Downloads.MIME_TYPE, SourceMeta.OCTET_STREAM)
            put(MediaStore.Downloads.RELATIVE_PATH, "${Environment.DIRECTORY_DOWNLOADS}/$DEFAULT_SUBDIR")
            put(MediaStore.Downloads.IS_PENDING, 1)
        }
        return resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw IOException("无法在下载目录创建临时文件")
    }

    private fun isSafWritable(treeUri: Uri): Boolean = runCatching {
        val docId = DocumentsContract.getTreeDocumentId(treeUri)
        val parentDoc = DocumentsContract.buildDocumentUriUsingTree(treeUri, docId)
        val probe = DocumentsContract.createDocument(resolver, parentDoc, "application/octet-stream", ".lt-probe")
            ?: return@runCatching false
        DocumentsContract.deleteDocument(resolver, probe)
        true
    }.getOrDefault(false)

    private fun findSafChild(treeUri: Uri, displayName: String): Uri? = runCatching {
        val parentId = DocumentsContract.getTreeDocumentId(treeUri)
        val childrenUri = DocumentsContract.buildChildDocumentsUriUsingTree(treeUri, parentId)
        val proj = arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME)
        resolver.query(childrenUri, proj, null, null, null)?.use { c ->
            while (c.moveToNext()) {
                if (c.getString(1) == displayName) {
                    return@runCatching DocumentsContract.buildDocumentUriUsingTree(treeUri, c.getString(0))
                }
            }
        }
        null
    }.getOrNull()

    private fun createSafDocument(treeUri: Uri, displayName: String): Uri {
        val parentId = DocumentsContract.getTreeDocumentId(treeUri)
        val parentDoc = DocumentsContract.buildDocumentUriUsingTree(treeUri, parentId)
        return DocumentsContract.createDocument(resolver, parentDoc, SourceMeta.OCTET_STREAM, displayName)
            ?: throw IOException("无法在所选目录创建临时文件")
    }

    private fun formatTreeDisplayPath(uriStr: String): String = runCatching {
        val docId = DocumentsContract.getTreeDocumentId(Uri.parse(uriStr))
        docId.replace("primary:", "内部存储/")
    }.getOrDefault(uriStr)

    private fun parseTransferId(name: String): TransferId? {
        if (!name.startsWith(".lt-") || !name.endsWith(".part")) return null
        val hex = name.removePrefix(".lt-").substringBefore('-')
        if (hex.length != 32) return null
        return runCatching {
            val bytes = ByteArray(16) { i -> hex.substring(i * 2, i * 2 + 2).toInt(16).toByte() }
            TransferId(Bytes16.of(bytes))
        }.getOrNull()
    }

    companion object {
        const val DEFAULT_SUBDIR = "LanTransfer"
        private const val LEGACY_TMP_DIR = ".lantransfer-tmp"
    }
}

private class PfdTempWriter(
    private val pfd: android.os.ParcelFileDescriptor,
    private val fos: FileOutputStream,
) : TempWriter {
    override fun write(b: ByteArray, off: Int, len: Int): IoResult = try {
        fos.write(b, off, len)
        IoResult.Ok
    } catch (e: IOException) {
        IoResult.Failed(e.message ?: "写入失败")
    }

    override fun flushSync(): IoResult = try {
        fos.flush()
        fos.fd.sync()
        IoResult.Ok
    } catch (e: IOException) {
        IoResult.Failed(e.message ?: "落盘失败")
    }

    override fun close() {
        runCatching { fos.close() }
        runCatching { pfd.close() }
    }
}

private class RafTempWriter(private val raf: RandomAccessFile) : TempWriter {
    override fun write(b: ByteArray, off: Int, len: Int): IoResult = try {
        raf.write(b, off, len)
        IoResult.Ok
    } catch (e: IOException) {
        IoResult.Failed(e.message ?: "写入失败")
    }

    override fun flushSync(): IoResult = try {
        raf.fd.sync()
        IoResult.Ok
    } catch (e: IOException) {
        IoResult.Failed(e.message ?: "落盘失败")
    }

    override fun close() {
        runCatching { raf.close() }
    }
}

private class StreamChunkReader(private val input: java.io.InputStream) : ChunkReader {
    override fun read(buf: ByteArray, off: Int, len: Int): ReadResult<Int> = try {
        val n = input.read(buf, off, len)
        ReadResult.Ok(n)
    } catch (e: IOException) {
        ReadResult.Failed(e.message ?: "读取失败")
    }

    override fun close() {
        runCatching { input.close() }
    }
}

/** Android 源文件读取适配（支持 content:// 与 file://，设计 C5.2、需求 14.4、14.9）。 */
class ContentResolverSourceFiles(private val context: Context) : SourceFiles {
    private val resolver = context.applicationContext.contentResolver

    override fun describe(ref: SourceRef): SourceMeta? {
        if (ref.value.startsWith("file://")) {
            val file = File(Uri.parse(ref.value).path ?: return null)
            if (!file.exists()) return null
            val mime = guessMime(file.name)
            return SourceMeta(
                name = file.name,
                size = if (file.isDirectory) 0L else file.length(),
                mtime = file.lastModified(),
                mime = mime,
                isDirectory = file.isDirectory,
            )
        }
        val uri = Uri.parse(ref.value)
        return runCatching {
            val mime = resolver.getType(uri)?.takeIf { it.isNotBlank() } ?: SourceMeta.OCTET_STREAM
            val isDir = mime == DocumentsContract.Document.MIME_TYPE_DIR
            var name: String? = null
            var size: Long? = null
            var mtime: Long? = null
            resolver.query(uri, null, null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    val nameIdx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (nameIdx >= 0) name = c.getString(nameIdx)
                    val sizeIdx = c.getColumnIndex(OpenableColumns.SIZE)
                    if (sizeIdx >= 0 && !c.isNull(sizeIdx)) size = c.getLong(sizeIdx)
                    val modIdx = c.getColumnIndex(DocumentsContract.Document.COLUMN_LAST_MODIFIED)
                    if (modIdx >= 0 && !c.isNull(modIdx)) mtime = c.getLong(modIdx)
                }
            }
            val resolvedSize = size ?: resolver.openFileDescriptor(uri, "r")?.use { it.statSize.takeIf { s -> s >= 0 } }
                ?: return@runCatching null
            SourceMeta(
                name = name?.takeIf { it.isNotBlank() } ?: uri.lastPathSegment ?: "file",
                size = resolvedSize,
                mtime = mtime,
                mime = sanitizeMime(mime),
                isDirectory = isDir,
            )
        }.getOrNull()
    }

    override fun stat(ref: SourceRef): SourceStat? = describe(ref)?.let { SourceStat(it.size, it.mtime) }

    override fun open(ref: SourceRef, offset: Long): ReadResult<ChunkReader> {
        return try {
            val stream = if (ref.value.startsWith("file://")) {
                val file = File(Uri.parse(ref.value).path ?: return ReadResult.Failed("无效路径"))
                FileInputStream(file)
            } else {
                resolver.openInputStream(Uri.parse(ref.value)) ?: return ReadResult.Failed("无法打开源文件")
            }
            if (offset > 0) {
                var remaining = offset
                val skipBuf = ByteArray(8192)
                while (remaining > 0) {
                    val skipped = stream.skip(remaining)
                    if (skipped > 0) {
                        remaining -= skipped
                    } else {
                        val n = stream.read(skipBuf, 0, minOf(skipBuf.size.toLong(), remaining).toInt())
                        if (n < 0) {
                            stream.close()
                            return ReadResult.Failed("源文件偏移超出长度")
                        }
                        remaining -= n
                    }
                }
            }
            ReadResult.Ok(StreamChunkReader(stream))
        } catch (e: Throwable) {
            ReadResult.Failed(e.message ?: "无法读取源文件")
        }
    }

    private fun guessMime(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
        "txt" -> "text/plain"
        "jpg", "jpeg" -> "image/jpeg"
        "png" -> "image/png"
        "pdf" -> "application/pdf"
        "mp4" -> "video/mp4"
        "mp3" -> "audio/mpeg"
        "zip" -> "application/zip"
        else -> SourceMeta.OCTET_STREAM
    }

    private fun sanitizeMime(mime: String): String =
        if (mime.length <= 255 && mime.all { it in ' '..'~' }) mime else SourceMeta.OCTET_STREAM
}

/** “以 .txt 文件发送”的缓存目录（cacheDir/outgoing-text/，设计 M4）。 */
class AndroidOutgoingTextCache(context: Context) : OutgoingTextCache {
    private val root = File(context.applicationContext.cacheDir, "outgoing-text")

    override fun write(dirName: String, fileName: String, content: ByteArray): WriteResult<SourceRef> = try {
        val dir = File(root, dirName).apply { mkdirs() }
        val file = File(dir, fileName)
        file.writeBytes(content)
        WriteResult.Ok(SourceRef(Uri.fromFile(file).toString()))
    } catch (e: Throwable) {
        WriteResult.Failed(e.message ?: "写入临时文本文件失败")
    }

    override fun list(): List<SourceRef> =
        root.listFiles()?.flatMap { sub ->
            sub.listFiles()?.filter { it.isFile }?.map { SourceRef(Uri.fromFile(it).toString()) }.orEmpty()
        }.orEmpty()

    override fun owns(ref: SourceRef): Boolean {
        if (!ref.value.startsWith("file://")) return false
        val path = Uri.parse(ref.value).path ?: return false
        return runCatching { File(path).canonicalPath.startsWith(root.canonicalPath + File.separator) }.getOrDefault(false)
    }

    override fun delete(ref: SourceRef): Boolean {
        if (!owns(ref)) return false
        val path = Uri.parse(ref.value).path ?: return false
        val parent = File(path).parentFile ?: return false
        return parent.deleteRecursively()
    }
}
