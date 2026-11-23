package com.lantransfer.domain

import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.FileEntry
import com.lantransfer.protocol.FileOffer
import com.lantransfer.protocol.OfferDecision
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.protocol.ResumeAction
import com.lantransfer.protocol.ResumeFilePlan
import com.lantransfer.protocol.ResumeFileState
import com.lantransfer.protocol.ResumeFileStatus
import com.lantransfer.protocol.TransferId
import com.lantransfer.testing.Gens
import com.lantransfer.testing.Pbt
import com.lantransfer.testing.mime
import com.lantransfer.testing.transferId
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.arbitrary
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.boolean
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.constant
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.enum
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.arbitrary.map
import io.kotest.property.checkAll
import java.math.BigInteger
import java.time.Duration
import kotlin.random.Random

// ---- Property 30：令牌注册表 ----

private sealed interface TokenOp {
    data object Issue : TokenOp

    /** [which]：0 当前令牌，1 上一个令牌，2 随机 16 字节，3 当前令牌改 1 个字节，4 当前令牌截短，5 空串。 */
    data class Check(val which: Int, val flipAt: Int) : TokenOp

    data class MarkUsed(val k: Int) : TokenOp

    data class Release(val k: Int) : TokenOp

    data class Advance(val dt: Long) : TokenOp
}

private val tokenOp: Arb<TokenOp> = Arb.choose(
    2 to Arb.constant(TokenOp.Issue),
    6 to Arb.bind(Arb.choose(6 to Arb.constant(0), 1 to Arb.int(1..5)), Arb.int(0..15)) { w, f -> TokenOp.Check(w, f) },
    2 to Arb.int(0..7).map { TokenOp.MarkUsed(it) },
    2 to Arb.int(0..7).map { TokenOp.Release(it) },
    3 to Arb.choose(
        3 to Arb.long(0L..2_000L),
        2 to Arb.long(60_000L..120_000L),
        1 to Arb.element(1L, 299_999L, 300_000L, 300_001L),
        1 to Arb.long(300_000L..1_000_000L),
    ).map { TokenOp.Advance(it) },
)

/** 参考模型：直接按 8.6 的四个条件判定。 */
private class RefRegistry(seed: Long) {
    private val rnd = Random(seed)
    val issued = mutableListOf<ByteArray>()
    var issuedAt = 0L
    var failures = 0
    var used = false
    var activeLease: Int? = null
    var leaseCount = 0

    fun issue(now: Long): ByteArray {
        val v = rnd.nextBytes(16)
        issued += v
        issuedAt = now
        failures = 0
        used = false
        activeLease = null
        return v
    }

    private fun valid(now: Long) = issued.isNotEmpty() && !used && failures < 5 && now - issuedAt < 300_000

    /** 返回租约编号或 null（Invalid）。 */
    fun check(token: ByteArray, now: Long): Int? {
        val ok = issued.isNotEmpty() && token.contentEquals(issued.last()) && valid(now) && activeLease == null
        if (!ok) {
            failures++
            return null
        }
        activeLease = leaseCount
        return leaseCount++
    }

    fun markUsed(k: Int) {
        if (activeLease == k) {
            used = true
            activeLease = null
        }
    }

    fun release(k: Int) {
        if (activeLease == k) activeLease = null
    }

    fun needsRefresh(now: Long) = !valid(now)
}

// ---- Property 31：待发送列表 ----

private val refArb: Arb<SourceRef> = Arb.int(0..5000).map { SourceRef("content://files/$it") }

private val pickedArb: Arb<PickedItem> = Arb.bind(refArb, Arb.choose(4 to Arb.constant(false), 1 to Arb.constant(true))) { r, d -> PickedItem(r, d) }

private val currentListArb: Arb<List<SourceRef>> = Arb.choose(
    3 to Arb.list(refArb, 0..20),
    2 to Arb.int(980..1000).map { n -> List(n) { SourceRef("file:///c/$it") } },
    1 to Arb.list(refArb, 0..1000),
)

private val incomingArb: Arb<List<PickedItem>> = Arb.choose(
    4 to Arb.list(pickedArb, 0..30),
    1 to Arb.list(pickedArb, 990..1010),
)

// ---- Property 32：Offer 预检 ----

private val tib: ULong = ProtocolConstants.MAX_FILE_SIZE.toULong()

private val sizeArb: Arb<ULong> = Arb.choose(
    4 to Arb.long(0L..(1L shl 20)).map { it.toULong() },
    2 to Arb.element(0uL, tib, tib + 1uL, ULong.MAX_VALUE),
    1 to Arb.long(0L..ProtocolConstants.MAX_FILE_SIZE).map { it.toULong() },
)

private val badMimes = listOf("a".repeat(256), "text/plain\u0000", "图片/jpeg", "\u007F", "text\tplain", "é")
private val mimeArb: Arb<String> = Arb.choose(6 to Gens.mime(), 1 to Arb.element(badMimes + listOf("a".repeat(255), "~ !")))

private val entryArb: Arb<FileEntry> = Arb.bind(sizeArb, mimeArb, Arb.choose(2 to Arb.long(), 1 to Arb.constant(-1L))) { s, m, t ->
    FileEntry("f", s, m, t.takeIf { it != -1L })
}

private data class OfferCase(val offer: FileOffer, val dir: DirStatus, val freeMode: Int, val freeRandom: Long, val autoAccept: Boolean)

/** 小 Offer 随机取每个字段；大 Offer（0、999–1001 个文件）大多全部有效，偶尔把其中 1 个换成无效项。 */
private val offerArb: Arb<FileOffer> = Arb.choose(
    3 to Arb.bind(Gens.transferId(), Arb.list(entryArb, 1..6)) { id, fs -> FileOffer(id, fs) },
    2 to arbitrary { rs ->
        val r = rs.random
        val n = listOf(0, 1, 999, 1000, 1001)[r.nextInt(5)]
        val files = MutableList(n) { FileEntry("f$it", r.nextLong(0, 1L shl 30).toULong(), "text/plain") }
        if (n > 0 && r.nextInt(3) == 0) {
            val bad = if (r.nextBoolean()) FileEntry("x", tib + 1uL, "text/plain") else FileEntry("x", 1uL, badMimes[r.nextInt(badMimes.size)])
            files[r.nextInt(n)] = bad
        }
        FileOffer(Gens.transferId().bind(), files)
    },
)

private val offerCase: Arb<OfferCase> =
    Arb.bind(offerArb, Arb.enum<DirStatus>(), Arb.int(0..5), Arb.long(0L..(1L shl 51)), Arb.boolean()) { o, d, m, f, a -> OfferCase(o, d, m, f, a) }

private val mimePattern = Regex("^[\\x20-\\x7E]{0,255}$")

private fun refDecide(offer: FileOffer, dir: DirStatus, free: Long?, autoAccept: Boolean): PrecheckDecision {
    val valid = offer.files.size in 1..1000 && offer.files.all { it.size <= tib && mimePattern.matches(it.mime) }
    val total = offer.files.fold(BigInteger.ZERO) { acc, f -> acc + BigInteger(f.size.toString()) }
    return when {
        !valid -> PrecheckDecision.Reject(OfferDecision.INVALID_OFFER)
        dir != DirStatus.USABLE -> PrecheckDecision.Reject(OfferDecision.CANNOT_SAVE)
        free != null && BigInteger.valueOf(free) < total -> PrecheckDecision.Reject(OfferDecision.NO_SPACE)
        autoAccept -> PrecheckDecision.AutoAccept
        else -> PrecheckDecision.AskUser
    }
}

// ---- Property 33：续传计划 ----

private val sampleTransferId = TransferId(Bytes16.ofHex("0f".repeat(16)))
private val samplePeer = DeviceId(Bytes16.ofHex("a0".repeat(16)))

private fun task(files: List<TransferFile>, state: TaskState = TaskState.RESUMING) = TransferTask(
    sampleTransferId, samplePeer, "对方", Direction.SEND, state, null, null, RecordTime(1_700_000_000_000, 480), 1_700_000_000_000, files,
)

private fun file(index: Int, size: Long, mtime: Long?, state: FileState) =
    TransferFile(sampleTransferId, index, index.toLong(), "f$index", size, "application/octet-stream", mtime, state)

private data class ResumeCase(val task: TransferTask, val state: List<ResumeFileState>, val stats: List<SourceStat?>)

private val resumeCase: Arb<ResumeCase> = arbitrary { rs ->
    val r = rs.random
    val n = r.nextInt(1, 9)
    val files = List(n) { i ->
        val size = listOf(0L, 1L, 65_536L, r.nextLong(0, 1L shl 40))[r.nextInt(4)]
        val mtime = if (r.nextInt(4) == 0) null else r.nextLong(1_600_000_000_000, 2_000_000_000_000)
        file(i, size, mtime, FileState.entries[r.nextInt(FileState.entries.size)])
    }
    val state = mutableListOf<ResumeFileState>()
    for (f in files) {
        if (r.nextInt(8) == 0) continue // RESUME_STATE 中缺少该文件
        val persisted = when (r.nextInt(6)) {
            0 -> 0L
            1 -> f.size
            2 -> maxOf(f.size - 1, 0)
            3 -> f.size + 1
            4 -> if (f.size == 0L) 0L else r.nextLong(0, f.size + 1)
            else -> r.nextLong(0, Long.MAX_VALUE)
        }
        state += ResumeFileState(f.index.toUInt(), ResumeFileStatus.entries[r.nextInt(4)], persisted.toULong())
    }
    if (r.nextInt(5) == 0) state += ResumeFileState((n + r.nextInt(5)).toUInt(), ResumeFileStatus.PARTIAL, 1uL) // 不存在的文件序号
    state.shuffle(r)
    val stats = files.map { f ->
        when (r.nextInt(7)) {
            0 -> null
            1 -> SourceStat(f.size + 1 + r.nextLong(0, 1000), f.mtime)
            2 -> SourceStat(f.size, (f.mtime ?: 0L) + 1)
            3 -> SourceStat(f.size, if (f.mtime == null) 1_700_000_000_000 else null)
            else -> SourceStat(f.size, f.mtime)
        }
    }
    ResumeCase(task(files), state, stats)
}

private val terminalFileStates = setOf(FileState.COMPLETED, FileState.READ_FAILED, FileState.CHECKSUM_FAILED)

private fun refPlan(c: ResumeCase): List<ResumePlanEntry> = c.task.files.mapIndexed { k, f ->
    val r = c.state.firstOrNull { it.fileIndex == f.index.toUInt() }
    val stat = c.stats[k]
    when {
        f.state in terminalFileStates || r?.state == ResumeFileStatus.COMPLETED || r?.state == ResumeFileStatus.TERMINAL ->
            ResumePlanEntry(f.index, ResumeAction.SKIP, 0)
        stat != null && (stat.size != f.size || stat.mtime != f.mtime) -> ResumePlanEntry(f.index, ResumeAction.RESTART, 0, stat.size, stat.mtime)
        r == null || r.state == ResumeFileStatus.NONE -> ResumePlanEntry(f.index, ResumeAction.CONTINUE, 0)
        r.persisted <= f.size.toULong() -> ResumePlanEntry(f.index, ResumeAction.CONTINUE, r.persisted.toLong())
        else -> ResumePlanEntry(f.index, ResumeAction.RESTART, 0)
    }
}

// ---- Property 34：临时文件选择 ----

private data class TempCase(val temps: List<TempInfo>, val active: Set<TransferId>, val now: Long)

private val tempCase: Arb<TempCase> = arbitrary { rs ->
    val r = rs.random
    val pool = List(4) { Gens.transferId().bind() }
    val now = r.nextLong(1_600_000_000_000, 2_000_000_000_000)
    val day = TempFileSelector.MAX_AGE_MS
    val temps = List(r.nextInt(0, 12)) { k ->
        val age = when (r.nextInt(5)) {
            0 -> listOf(day - 1, day, day + 1)[r.nextInt(3)]
            1 -> -r.nextLong(0, 10_000) // 写入时间在“未来”（时钟回拨）
            else -> r.nextLong(0, 3 * day)
        }
        TempInfo(TempRef("tmp-$k"), if (r.nextInt(6) == 0) null else pool[r.nextInt(4)], now - age)
    }
    TempCase(temps, pool.filter { r.nextBoolean() }.toSet(), now)
}

class RulesPropertiesTest : FunSpec({
    // Feature: lan-file-transfer, Property 30: 配对令牌注册表
    // Validates: Requirements 8.6, 8.7, 8.9
    test("Property 30") {
        checkAll(Pbt.iterations(1_000), Arb.long(), Arb.list(tokenOp, 1..60)) { seed, ops ->
            val rnd = Random(seed)
            val reg = PairingTokenRegistry { n -> rnd.nextBytes(n) }
            val model = RefRegistry(seed)
            val leases = mutableListOf<TokenLease>()
            var now = 1_000_000L
            reg.needsRefresh(now) shouldBe true
            for (op in ops) {
                when (op) {
                    TokenOp.Issue -> {
                        val t = reg.issue(now)
                        val expected = model.issue(now)
                        t.value.toByteArray().contentEquals(expected) shouldBe true
                        t.issuedAtMono shouldBe now
                    }
                    is TokenOp.Check -> {
                        val cur = model.issued.lastOrNull() ?: ByteArray(16)
                        val token = when (op.which) {
                            0 -> cur
                            1 -> model.issued.getOrNull(model.issued.size - 2) ?: ByteArray(16) { 7 }
                            2 -> Random(op.flipAt + seed).nextBytes(16)
                            3 -> cur.copyOf().also { it[op.flipAt] = (it[op.flipAt].toInt() xor 0x01).toByte() }
                            4 -> cur.copyOf(op.flipAt)
                            else -> ByteArray(0)
                        }
                        val expected = model.check(token, now)
                        when (val got = reg.check(token, now)) {
                            is TokenCheck.Ok -> {
                                expected shouldBe leases.size
                                leases += got.lease
                            }
                            TokenCheck.Invalid -> expected shouldBe null
                        }
                    }
                    is TokenOp.MarkUsed -> if (leases.isNotEmpty()) {
                        val k = op.k % leases.size
                        reg.markUsed(leases[k])
                        model.markUsed(k)
                    }
                    is TokenOp.Release -> if (leases.isNotEmpty()) {
                        val k = op.k % leases.size
                        reg.release(leases[k])
                        model.release(k)
                    }
                    is TokenOp.Advance -> now += op.dt
                }
                reg.needsRefresh(now) shouldBe model.needsRefresh(now)
            }
        }
    }

    // Feature: lan-file-transfer, Property 31: 待发送列表规则
    // Validates: Requirements 14.1, 14.2, 14.8, 14.9
    test("Property 31") {
        checkAll(Pbt.iterations(1_000), currentListArb, incomingArb) { current, incoming ->
            val files = incoming.filter { !it.isDirectory }.map { it.ref }
            val hasFolder = incoming.any { it.isDirectory }
            val expected = if (current.size + files.size > 1000) AddResult(current, hasFolder, true) else AddResult(current + files, hasFolder, false)
            PendingListRules.add(current, incoming) shouldBe expected
        }
    }

    // Feature: lan-file-transfer, Property 32: File_Offer 预检判定表
    // Validates: Requirements 15.1, 15.2, 15.5, 15.7
    test("Property 32") {
        checkAll(Pbt.iterations(1_000), offerCase) { c ->
            val total = c.offer.files.fold(BigInteger.ZERO) { acc, f -> acc + BigInteger(f.size.toString()) }
            val clamp = { v: BigInteger -> v.max(BigInteger.ZERO).min(BigInteger.valueOf(Long.MAX_VALUE)).toLong() }
            val free = when (c.freeMode) {
                0 -> null
                1 -> clamp(total)
                2 -> clamp(total - BigInteger.ONE)
                3 -> clamp(total + BigInteger.ONE)
                4 -> c.freeRandom
                else -> 0L
            }
            OfferPrecheck.decide(c.offer, c.dir, free, c.autoAccept) shouldBe refDecide(c.offer, c.dir, free, c.autoAccept)
        }
    }

    // Feature: lan-file-transfer, Property 33: 续传计划
    // Validates: Requirements 17.4, 17.5
    test("Property 33") {
        checkAll(Pbt.iterations(1_000), resumeCase) { c ->
            val plan = ResumePlanner.plan(c.task, c.state, c.stats)
            plan shouldBe refPlan(c)
            plan.map { it.index } shouldBe c.task.files.map { it.index }
            plan.forEach { e ->
                e.toWire() shouldBe ResumeFilePlan(e.index.toUInt(), e.action, e.start.toULong(), e.size?.toULong(), e.mtime)
                if (e.action != ResumeAction.RESTART) (e.size == null && e.mtime == null) shouldBe true
            }
        }
    }

    // Feature: lan-file-transfer, Property 34: 过期临时文件选择
    // Validates: Requirements 17.7
    test("Property 34") {
        checkAll(Pbt.iterations(1_000), tempCase) { c ->
            val expected = c.temps.filter { t ->
                val inActive = t.transferId?.let { it in c.active } ?: false
                !inActive && Duration.ofMillis(c.now - t.lastWriteUtcMs) > Duration.ofHours(24)
            }
            TempFileSelector.select(c.temps, c.active, c.now) shouldBe expected
        }
    }

    test("示例：令牌有效期、使用中、失败次数与刷新") {
        var counter = 0
        val reg = PairingTokenRegistry { n -> ByteArray(n) { (counter + it).toByte() }.also { counter++ } }
        val t1 = reg.issue(0).value.toByteArray()
        reg.needsRefresh(0) shouldBe false
        val lease = (reg.check(t1, 1_000) as TokenCheck.Ok).lease
        reg.check(t1, 1_001) shouldBe TokenCheck.Invalid // 使用中
        reg.needsRefresh(1_001) shouldBe false
        reg.release(lease)
        val lease2 = (reg.check(t1, 2_000) as TokenCheck.Ok).lease
        reg.markUsed(lease) // 旧租约不起作用
        reg.needsRefresh(2_000) shouldBe false
        reg.markUsed(lease2)
        reg.needsRefresh(2_000) shouldBe true
        reg.check(t1, 2_001) shouldBe TokenCheck.Invalid

        val t2 = reg.issue(10_000).value.toByteArray()
        reg.check(t1, 10_001) shouldBe TokenCheck.Invalid // 已被取代
        reg.check(t2, 10_000 + 299_999).shouldBeInstanceOf<TokenCheck.Ok>()

        val t3 = reg.issue(0).value.toByteArray()
        reg.check(t3, 300_000) shouldBe TokenCheck.Invalid // 恰好 5 min 时已失效
        reg.needsRefresh(299_999) shouldBe false // 失败 1 次、未满 5 min
        reg.needsRefresh(300_000) shouldBe true
        val t4 = reg.issue(0).value.toByteArray()
        repeat(4) { reg.check(ByteArray(16), 1) shouldBe TokenCheck.Invalid }
        reg.needsRefresh(1) shouldBe false
        reg.check(t4, 1).shouldBeInstanceOf<TokenCheck.Ok>() // 失败 4 次时仍有效
        val t5 = reg.issue(0).value.toByteArray()
        repeat(5) { reg.check(ByteArray(16), 1) }
        reg.needsRefresh(1) shouldBe true
        reg.check(t5, 1) shouldBe TokenCheck.Invalid
    }

    test("示例：待发送列表、Offer 预检、续传计划与临时文件") {
        val thousand = List(1000) { SourceRef("p$it") }
        PendingListRules.add(thousand.take(999), listOf(PickedItem(SourceRef("x"), false))).list.size shouldBe 1000
        PendingListRules.add(thousand, listOf(PickedItem(SourceRef("x"), false))) shouldBe AddResult(thousand, false, true)
        PendingListRules.add(emptyList(), listOf(PickedItem(SourceRef("d"), true), PickedItem(SourceRef("a"), false))) shouldBe
            AddResult(listOf(SourceRef("a")), true, false)

        val id = TransferId(Bytes16.ofHex("01".repeat(16)))
        // 不用 vararg ULong：它会生成实验性的 ULongArray
        fun offer(sizes: List<ULong>) = FileOffer(id, sizes.map { FileEntry("f", it, "text/plain") })
        OfferPrecheck.decide(offer(listOf(tib)), DirStatus.USABLE, null, false) shouldBe PrecheckDecision.AskUser
        OfferPrecheck.decide(offer(listOf(tib + 1uL)), DirStatus.USABLE, null, false) shouldBe PrecheckDecision.Reject(OfferDecision.INVALID_OFFER)
        OfferPrecheck.decide(FileOffer(id, emptyList()), DirStatus.USABLE, null, false) shouldBe PrecheckDecision.Reject(OfferDecision.INVALID_OFFER)
        OfferPrecheck.decide(FileOffer(id, List(1000) { FileEntry("f", 0uL, "") }), DirStatus.USABLE, 0, true) shouldBe PrecheckDecision.AutoAccept
        OfferPrecheck.decide(FileOffer(id, List(1001) { FileEntry("f", 0uL, "") }), DirStatus.USABLE, 0, true) shouldBe PrecheckDecision.Reject(OfferDecision.INVALID_OFFER)
        OfferPrecheck.decide(offer(listOf(10uL, 5uL)), DirStatus.NOT_WRITABLE, 0, true) shouldBe PrecheckDecision.Reject(OfferDecision.CANNOT_SAVE)
        OfferPrecheck.decide(offer(listOf(10uL, 5uL)), DirStatus.USABLE, 14, true) shouldBe PrecheckDecision.Reject(OfferDecision.NO_SPACE)
        OfferPrecheck.decide(offer(listOf(10uL, 5uL)), DirStatus.USABLE, 15, false) shouldBe PrecheckDecision.AskUser

        val files = listOf(
            file(0, 100, 5, FileState.COMPLETED),
            file(1, 100, 5, FileState.TRANSFERRING),
            file(2, 100, 5, FileState.WRITE_FAILED),
            file(3, 100, 5, FileState.WAITING),
            file(4, 100, 5, FileState.TRANSFERRING),
        )
        val state = listOf(
            ResumeFileState(1u, ResumeFileStatus.PARTIAL, 40uL),
            ResumeFileState(2u, ResumeFileStatus.PARTIAL, 100uL),
            ResumeFileState(4u, ResumeFileStatus.PARTIAL, 101uL),
        )
        val stats = listOf(SourceStat(100, 5), SourceStat(100, 5), SourceStat(100, 5), SourceStat(100, 6), SourceStat(100, 5))
        ResumePlanner.plan(task(files), state, stats) shouldBe listOf(
            ResumePlanEntry(0, ResumeAction.SKIP, 0),
            ResumePlanEntry(1, ResumeAction.CONTINUE, 40),
            ResumePlanEntry(2, ResumeAction.CONTINUE, 100), // 写入失败的文件：临时文件已完整，start = size，重发 FILE_END
            ResumePlanEntry(3, ResumeAction.RESTART, 0, 100, 6),
            ResumePlanEntry(4, ResumeAction.RESTART, 0),
        )

        val day = TempFileSelector.MAX_AGE_MS
        val a = TempInfo(TempRef("a"), id, 0)
        val b = TempInfo(TempRef("b"), null, 1)
        TempFileSelector.select(listOf(a, b), emptySet(), day) shouldBe emptyList()
        TempFileSelector.select(listOf(a, b), emptySet(), day + 1) shouldBe listOf(a)
        TempFileSelector.select(listOf(a, b), setOf(id), day + 2) shouldBe listOf(b)
    }

    test("示例：枚举码值、DirRef 序列化与设置默认值") {
        TaskState.entries.filter { it.isTerminal }.map { it.code } shouldBe (10..18).toList()
        TaskState.entries.filter { !it.isTerminal }.map { it.code } shouldBe (0..4).toList()
        FileState.entries.map { it.code } shouldBe listOf(0, 1, 2, 3, 10, 11, 12)
        FileState.WRITE_FAILED.isTerminal shouldBe false
        InterruptReason.entries.map { it.code } shouldBe (1..8).toList()
        TextStatus.entries.map { it.code } shouldBe (0..3).toList()
        TaskState.fromCode(18) shouldBe TaskState.OFFER_INVALID
        TaskState.fromCode(5) shouldBe null
        Direction.fromCode(1) shouldBe Direction.RECEIVE

        for (d in listOf(DirRef.Default, DirRef.Tree("content://com.android.externalstorage.documents/tree/primary%3ADownload"), DirRef.Path("D:\\接收\\LanTransfer"))) {
            DirRef.parse(d.serialize()) shouldBe d
        }
        DirRef.Default.serialize() shouldBe "default"
        DirRef.Path("C:\\x").serialize() shouldBe "path:C:\\x"
        DirRef.parse("tree:") shouldBe null
        DirRef.parse("Default") shouldBe null
        DirRef.parse("") shouldBe null
        Settings() shouldBe Settings(DirRef.Default, autoCopy = false, backgroundReceive = false)
    }
})
