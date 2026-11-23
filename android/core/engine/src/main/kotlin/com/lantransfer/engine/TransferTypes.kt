package com.lantransfer.engine

import com.lantransfer.domain.Progress
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.TransferTask
import com.lantransfer.protocol.TransferId

// FileTransferEngine 的对外类型（设计 C4.5；TransferTaskView、SendOfferResult、ResumeResult 在设计中只有名称，此处确定）。

/** 一个传输任务的界面快照。 */
data class TransferTaskView(
    val task: TransferTask,
    /** 任务级进度：已进入终态的文件按完整大小计入（18.1）。 */
    val progress: Progress,
    /** 每个文件的进度，与 task.files 一一对应；速度与剩余时间只对当前文件给出。 */
    val files: List<Progress>,
    /** 续传时正在从磁盘重新计算已传输部分的哈希（M3.2：RESUMING 显示“正在校验已传输部分”）。 */
    val checkingPrefix: Boolean,
) {
    val id: TransferId get() = task.transferId

    /** OFFERING、AWAITING_USER、RUNNING、RESUMING、INTERRUPTED（20.11）。 */
    val unfinished: Boolean get() = !task.state.isTerminal
}

/** [FileTransferEngine.send] 的结果。 */
sealed interface SendOfferResult {
    /** 已发出 FILE_OFFER，任务处于 OFFERING。 */
    data class Sent(val id: TransferId) : SendOfferResult

    /** 待发送列表为空。 */
    data object Empty : SendOfferResult

    /** 超过 1000 个文件（14.8）；列表应已由 PendingListRules 拦截，这里兜底。 */
    data object TooMany : SendOfferResult

    /** 这些源文件无法读取元数据或是文件夹（14.9）；未发送 Offer，保留待发送列表。 */
    data class Unreadable(val refs: List<SourceRef>) : SendOfferResult

    /** 10 s 内无法建立 Session：不发送 Offer，保留待发送列表（14.10）。 */
    data class ConnectFailed(val failure: OpenFailure) : SendOfferResult
}

/** [FileTransferEngine.resume] 的结果。 */
sealed interface ResumeResult {
    /** 已建立 Session 并发出 RESUME_REQUEST / RESUME_STATE，任务处于 RESUMING。 */
    data object Started : ResumeResult

    /** 任务不存在或不是“已中断”。 */
    data object NotResumable : ResumeResult

    /** 10 s 内无法建立 Session：任务保持“已中断”，保留临时文件（17.10）。 */
    data class ConnectFailed(val failure: OpenFailure) : ResumeResult
}

/** 需要界面提示的事件。 */
sealed interface TransferNotice {
    /** 任务状态写入失败（20.8）；传输照常进行。 */
    data class StoreFailed(val detail: String) : TransferNotice

    /** pending_cancel 写入失败：本机仍显示“已取消”，提示取消通知可能无法送达对方（E5、18.9）。 */
    data class PendingCancelNotSaved(val id: TransferId) : TransferNotice
}

/** 传输相关的时限与大小（需求与设计规定的数值；只为测试注入而做成参数）。 */
data class TransferTimings(
    /** 接收方等待用户响应的时限（15.4）。 */
    val receiverOfferMs: Long = 60_000,
    /** 发送方等待 FILE_OFFER_REPLY 的保底时限（15.4）。 */
    val senderOfferMs: Long = 65_000,
    /** 点击“继续”后进入 RUNNING 的时限（17.10）。 */
    val resumeMs: Long = 10_000,
    /** FILE_CHUNK 的数据长度（C4.5）。 */
    val chunkBytes: Int = 64 * 1024,
    /** 每个任务未确认数据的上限（C1.13）。 */
    val windowBytes: Long = 4L * 1024 * 1024,
    /** 接收方每写入这么多字节回复一次 FILE_ACK（C1.13）。 */
    val ackBytes: Long = 256L * 1024,
    /** 接收方距上次 FILE_ACK 这么久仍有未确认数据时回复一次（C1.13）。 */
    val ackIntervalMs: Long = 200,
    /** 进度快照的最短发布间隔（并发模型：250 ms）。 */
    val publishIntervalMs: Long = 250,
    /** persisted / last_write_at 的最短写库间隔（M2）。 */
    val saveIntervalMs: Long = 1_000,
    /** 后台进度通知的最短更新间隔（21.1）。 */
    val progressNotifyMs: Long = 1_000,
    /** TempFileJanitor 的运行周期（17.7）。 */
    val janitorIntervalMs: Long = 60 * 60 * 1000L,
)
