package com.lantransfer.engine.testing

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.Rng
import com.lantransfer.domain.StoredFileRef
import com.lantransfer.engine.ports.AppLifecycle
import com.lantransfer.engine.ports.ClipRead
import com.lantransfer.engine.ports.ClipWrite
import com.lantransfer.engine.ports.Clipboard
import com.lantransfer.engine.ports.FileLocator
import com.lantransfer.engine.ports.LocateResult
import com.lantransfer.engine.ports.Notifier
import com.lantransfer.engine.ports.NotifyKey
import com.lantransfer.engine.ports.RequestKind
import com.lantransfer.protocol.TransferId
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlin.random.Random

// 系统服务端口的测试替身：记录全部调用，可注入失败。

/** 由种子决定的随机数（测试用）。 */
class SeededRng(seed: Long) : Rng {
    private val random = Random(seed)

    override fun nextBytes(n: Int): ByteArray = random.nextBytes(n)
}

/** 剪贴板替身：[writes] 记录每次写入的原文；[failReads] / [failWrites] 模拟剪贴板被占用（13.9）。 */
class RecordingClipboard(initial: String? = null) : Clipboard {
    var content: String? = initial
        private set
    val writes = mutableListOf<String>()
    var failReads = false
    var failWrites = false

    override suspend fun readText(): ClipRead = when {
        failReads -> ClipRead.Failed
        else -> content?.let { ClipRead.Text(it) } ?: ClipRead.NoText
    }

    override suspend fun writeText(s: String): ClipWrite {
        writes += s
        if (failWrites) return ClipWrite.Failed
        content = s
        return ClipWrite.Ok
    }

    /** 模拟其他应用改动了剪贴板。 */
    fun setExternally(s: String?) {
        content = s
    }
}

sealed interface LocateCall {
    data class OpenFile(val file: StoredFileRef, val mime: String) : LocateCall

    data class Reveal(val file: StoredFileRef) : LocateCall

    data class OpenDirectory(val dir: DirRef) : LocateCall
}

/** FileLocator 替身：[calls] 记录调用，[result] 决定返回值。 */
class RecordingFileLocator(var result: (LocateCall) -> LocateResult = { LocateResult.OK }) : FileLocator {
    val calls = mutableListOf<LocateCall>()

    private fun record(c: LocateCall): LocateResult {
        calls += c
        return result(c)
    }

    override fun openFile(f: StoredFileRef, mime: String): LocateResult = record(LocateCall.OpenFile(f, mime))

    override fun revealInFolder(f: StoredFileRef): LocateResult = record(LocateCall.Reveal(f))

    override fun openDirectory(dir: DirRef): LocateResult = record(LocateCall.OpenDirectory(dir))
}

sealed interface NotifyEvent {
    data class Text(val key: NotifyKey, val peerName: String, val preview: String) : NotifyEvent

    data class FileCompleted(val key: NotifyKey, val peerName: String, val fileName: String, val file: StoredFileRef) : NotifyEvent

    data class Request(val key: NotifyKey, val peerName: String, val kind: RequestKind, val fileCount: Int?) : NotifyEvent

    data class Problem(val key: NotifyKey, val peerName: String, val fileName: String?, val status: String) : NotifyEvent

    data class Progress(val task: TransferId, val peerName: String, val fileName: String, val percent: Int) : NotifyEvent

    data class Cancel(val key: NotifyKey) : NotifyEvent
}

/** Notifier 替身：[events] 按顺序记录全部调用，[active] 为当前仍显示的通知（cancel 后移除）。 */
class RecordingNotifier(enabled: Boolean = true) : Notifier {
    private val enabledFlow = MutableStateFlow(enabled)
    override val enabled: StateFlow<Boolean> get() = enabledFlow
    val events = mutableListOf<NotifyEvent>()
    val active = LinkedHashMap<NotifyKey, NotifyEvent>()

    fun setEnabled(on: Boolean) {
        enabledFlow.value = on
    }

    private fun show(key: NotifyKey, e: NotifyEvent) {
        events += e
        active[key] = e
    }

    override fun text(key: NotifyKey, peerName: String, preview: String) = show(key, NotifyEvent.Text(key, peerName, preview))

    override fun fileCompleted(key: NotifyKey, peerName: String, fileName: String, file: StoredFileRef) =
        show(key, NotifyEvent.FileCompleted(key, peerName, fileName, file))

    override fun request(key: NotifyKey, peerName: String, kind: RequestKind, fileCount: Int?) =
        show(key, NotifyEvent.Request(key, peerName, kind, fileCount))

    override fun problem(key: NotifyKey, peerName: String, fileName: String?, status: String) =
        show(key, NotifyEvent.Problem(key, peerName, fileName, status))

    override fun progress(task: TransferId, peerName: String, fileName: String, percent: Int) {
        val e = NotifyEvent.Progress(task, peerName, fileName, percent)
        events += e
        active[NotifyKey.Transfer(task)] = e
    }

    override fun cancel(key: NotifyKey) {
        events += NotifyEvent.Cancel(key)
        active.remove(key)
    }
}

/** 前后台状态替身。 */
class FakeAppLifecycle(background: Boolean = false) : AppLifecycle {
    private val flow = MutableStateFlow(background)
    override val background: StateFlow<Boolean> get() = flow

    fun setBackground(on: Boolean) {
        flow.value = on
    }
}
