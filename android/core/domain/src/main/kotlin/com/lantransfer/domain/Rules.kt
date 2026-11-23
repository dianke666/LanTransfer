package com.lantransfer.domain

import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.FileOffer
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.ResumeAction
import com.lantransfer.protocol.ResumeFilePlan
import com.lantransfer.protocol.ResumeFileState
import com.lantransfer.protocol.ResumeFileStatus
import com.lantransfer.protocol.TransferId
import java.security.MessageDigest

// C3.5 规则（需求 8、14、15、17）。

/** 随机数端口（C5 的 Rng）：生产环境为 SecureRandom，测试时注入确定的序列。领域层需要它，所以定义在这里，引擎层直接复用。 */
fun interface Rng {
    fun nextBytes(n: Int): ByteArray
}

/** 一次 issue 产生的 Pairing_Token。 */
data class PairingToken(val value: Bytes16, val issuedAtMono: Long)

/** 校验通过后得到的租约；只有最近一次校验通过时得到的租约能 markUsed / release。 */
class TokenLease internal constructor(internal val id: Long) {
    override fun toString(): String = "TokenLease($id)"
}

sealed interface TokenCheck {
    data class Ok(val lease: TokenLease) : TokenCheck

    data object Invalid : TokenCheck
}

/**
 * Pairing_Token 注册表（需求 8.6、8.7、8.9，设计 C3.5）。令牌有效的条件：是最近一次 issue 的令牌、发出不足 5 min、
 * 未被使用、失败次数少于 5。校验通过的令牌进入“使用中”，直到 markUsed（配对完成）或 release（配对中断）；
 * 使用中的令牌再次被校验时返回 Invalid。每次 Invalid 使失败计数加 1。令牌比较使用常数时间比较。时间为单调时钟毫秒。
 */
class PairingTokenRegistry(private val rng: Rng) {
    private var current: ByteArray? = null
    private var issuedAt = 0L
    private var failures = 0
    private var used = false
    private var activeLease: Long? = null
    private var nextLeaseId = 1L

    /** 生成新令牌，替换掉之前的令牌（失败计数、使用状态一并重置）。 */
    fun issue(nowMono: Long): PairingToken {
        val v = rng.nextBytes(TOKEN_SIZE)
        kotlin.check(v.size == TOKEN_SIZE) { "Rng returned ${v.size} bytes" }
        current = v.copyOf()
        issuedAt = nowMono
        failures = 0
        used = false
        activeLease = null
        return PairingToken(Bytes16.of(v), nowMono)
    }

    fun check(token: ByteArray, nowMono: Long): TokenCheck {
        val cur = current
        // MessageDigest.isEqual 在 Java 7u6 起为常数时间比较；长度不同时直接判定不相等
        val matches = cur != null && token.size == TOKEN_SIZE && MessageDigest.isEqual(cur, token)
        if (matches && activeLease == null && isUsable(nowMono)) {
            val lease = TokenLease(nextLeaseId++)
            activeLease = lease.id
            return TokenCheck.Ok(lease)
        }
        failures++
        return TokenCheck.Invalid
    }

    /** 配对完成：令牌作废。 */
    fun markUsed(lease: TokenLease) {
        if (activeLease == lease.id) {
            used = true
            activeLease = null
        }
    }

    /** 配对中断：令牌回到可用状态（仍受 5 min 与失败次数约束）。 */
    fun release(lease: TokenLease) {
        if (activeLease == lease.id) activeLease = null
    }

    /** 当前令牌已失效（或从未 issue）→ 调用方在 2 s 内重新 issue（8.7）。使用中不算失效。 */
    fun needsRefresh(nowMono: Long): Boolean = current == null || !isUsable(nowMono)

    private fun isUsable(nowMono: Long): Boolean = !used && failures < MAX_FAILURES && nowMono - issuedAt < LIFETIME_MS

    companion object {
        const val TOKEN_SIZE = 16
        const val LIFETIME_MS = 5 * 60_000L
        const val MAX_FAILURES = 5
    }
}

/** 选入待发送列表的一项（文件选择器、拖放或分享）。 */
data class PickedItem(val ref: SourceRef, val isDirectory: Boolean)

/**
 * [PendingListRules.add] 的结果。[list] 为加入后的列表（[tooMany] 时等于原列表）；
 * [foldersSkipped] 为 true 时提示“暂不支持发送文件夹”（14.9）；[tooMany] 为 true 时提示单次最多发送 1000 个文件（14.8）。
 */
data class AddResult(val list: List<SourceRef>, val foldersSkipped: Boolean, val tooMany: Boolean)

/** 待发送列表规则（需求 14.1、14.2、14.8、14.9）：先剔除文件夹，再检查总数；超过 1000 时整批拒绝、列表不变。 */
object PendingListRules {
    const val MAX_FILES = ProtocolConstants.MAX_FILES_PER_OFFER

    fun add(current: List<SourceRef>, incoming: List<PickedItem>): AddResult {
        val files = incoming.filterNot { it.isDirectory }.map { it.ref }
        val foldersSkipped = files.size != incoming.size
        return if (current.size + files.size > MAX_FILES) {
            AddResult(current, foldersSkipped, tooMany = true)
        } else {
            AddResult(current + files, foldersSkipped, tooMany = false)
        }
    }
}

/** 接收目录状态（ReceiveStorage.probe，15.7）。 */
enum class DirStatus { USABLE, CANNOT_CREATE, NOT_WRITABLE }

sealed interface PrecheckDecision {
    /** 回复 FILE_OFFER_REPLY 的拒绝码：INVALID_OFFER、CANNOT_SAVE 或 NO_SPACE。 */
    data class Reject(val reply: OfferDecision) : PrecheckDecision

    data object AutoAccept : PrecheckDecision

    data object AskUser : PrecheckDecision
}

/** File_Offer 预检（需求 15.1、15.2、15.5、15.7，设计 C3.5）。 */
object OfferPrecheck {
    /**
     * 判定顺序：语义无效 → INVALID_OFFER；目录不可用 → CANNOT_SAVE；可用空间已知且小于文件大小之和 → NO_SPACE；
     * 已开启自动接收 → AutoAccept；其余 → AskUser。[freeBytes] 为 null 表示无法获知可用空间，此时不做空间检查。
     */
    fun decide(offer: FileOffer, dir: DirStatus, freeBytes: Long?, autoAccept: Boolean): PrecheckDecision = when {
        !isSemanticallyValid(offer) -> PrecheckDecision.Reject(OfferDecision.INVALID_OFFER)
        dir != DirStatus.USABLE -> PrecheckDecision.Reject(OfferDecision.CANNOT_SAVE)
        freeBytes != null && freeBytes < totalSize(offer) -> PrecheckDecision.Reject(OfferDecision.NO_SPACE)
        autoAccept -> PrecheckDecision.AutoAccept
        else -> PrecheckDecision.AskUser
    }

    /** 语义校验（规范 FILE_OFFER）：文件数 1–1000、每个文件 size ≤ 1 TiB、mime 为 ≤ 255 字节的可打印 ASCII。 */
    fun isSemanticallyValid(offer: FileOffer): Boolean =
        offer.files.size in 1..ProtocolConstants.MAX_FILES_PER_OFFER &&
            offer.files.all { it.size <= ProtocolConstants.MAX_FILE_SIZE.toULong() && isValidMime(it.mime) }

    /** 可打印 ASCII（20–7E），1 个字符即 1 个字节，最多 255 个。 */
    fun isValidMime(mime: String): Boolean = mime.length <= ProtocolConstants.MAX_MIME_BYTES && mime.all { it in ' '..'~' }

    /** 文件大小之和；前置条件：语义有效（因此不会溢出：1000 × 1 TiB < 2^50）。 */
    fun totalSize(offer: FileOffer): Long = offer.files.sumOf { it.size.toLong() }
}

/** 源文件的当前大小与修改时间（SourceFiles.stat，17.5）。 */
data class SourceStat(val size: Long, val mtime: Long?)

/** 续传计划中的一项；[size] / [mtime] 只在因源文件变化而 RESTART 时给出。 */
data class ResumePlanEntry(val index: Int, val action: ResumeAction, val start: Long, val size: Long? = null, val mtime: Long? = null) {
    fun toWire(): ResumeFilePlan = ResumeFilePlan(index.toUInt(), action, start.toULong(), size?.toULong(), mtime)
}

/** 续传计划（需求 17.4、17.5，设计 C1.13、C3.5），由发送方根据接收方的 RESUME_STATE 生成。 */
object ResumePlanner {
    /**
     * 按 [task] 中文件的顺序，为每个文件恰好给出 1 项；[stats] 与 task.files 一一对应，null 表示无法获取 stat
     * （按未变化处理，之后打开失败时按 14.7 报告读取失败）。RESUME_STATE 中缺少某个文件时按 NONE 处理。
     *
     * - 发送方记录为终态（已完成、读取失败、校验失败），或接收方报告 COMPLETED / TERMINAL → SKIP；
     * - 源文件大小或修改时间与记录不同 → RESTART(0, 新 size, 新 mtime)；
     * - NONE → CONTINUE(0)；PARTIAL 且 persisted ≤ size → CONTINUE(persisted)；persisted > size → RESTART(0)。
     */
    fun plan(task: TaskRecord, state: List<ResumeFileState>, stats: List<SourceStat?>): List<ResumePlanEntry> {
        require(stats.size == task.files.size) { "stats must match task files" }
        val byIndex = HashMap<Int, ResumeFileState>()
        for (s in state) byIndex.putIfAbsent(s.fileIndex.toInt(), s)
        return task.files.mapIndexed { k, f ->
            val r = byIndex[f.index]
            val stat = stats[k]
            when {
                f.state.isTerminal || r?.state == ResumeFileStatus.COMPLETED || r?.state == ResumeFileStatus.TERMINAL ->
                    ResumePlanEntry(f.index, ResumeAction.SKIP, 0)
                stat != null && (stat.size != f.size || stat.mtime != f.mtime) ->
                    ResumePlanEntry(f.index, ResumeAction.RESTART, 0, stat.size, stat.mtime)
                r == null || r.state == ResumeFileStatus.NONE -> ResumePlanEntry(f.index, ResumeAction.CONTINUE, 0)
                r.persisted <= f.size.toULong() -> ResumePlanEntry(f.index, ResumeAction.CONTINUE, r.persisted.toLong())
                else -> ResumePlanEntry(f.index, ResumeAction.RESTART, 0)
            }
        }
    }
}

/** 一个临时文件；[transferId] 为 null 表示无法从名称或数据库确定所属任务。[lastWriteUtcMs] 的取法见设计 M4。 */
data class TempInfo(val ref: TempRef, val transferId: TransferId?, val lastWriteUtcMs: Long)

/** 过期临时文件选择（需求 17.7）。 */
object TempFileSelector {
    const val MAX_AGE_MS = 24 * 60 * 60 * 1000L

    /** 不属于进行中任务（[active]）且最后写入时间距 [nowWall] 超过 24 h 的临时文件，保持输入顺序。 */
    fun select(temps: List<TempInfo>, active: Set<TransferId>, nowWall: Long): List<TempInfo> =
        temps.filter { (it.transferId == null || it.transferId !in active) && nowWall - it.lastWriteUtcMs > MAX_AGE_MS }
}
