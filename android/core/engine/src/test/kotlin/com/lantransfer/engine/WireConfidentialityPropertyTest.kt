@file:OptIn(ExperimentalCoroutinesApi::class)

package com.lantransfer.engine

import com.lantransfer.domain.DirRef
import com.lantransfer.domain.FsProfile
import com.lantransfer.domain.PairingResult
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.TaskState
import com.lantransfer.engine.testing.FakeClock
import com.lantransfer.engine.testing.InMemoryNetwork
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.StrictUtf8
import com.lantransfer.testing.Pbt
import io.kotest.assertions.withClue
import io.kotest.core.spec.style.FunSpec
import io.kotest.matchers.shouldBe
import io.kotest.matchers.types.shouldBeInstanceOf
import io.kotest.property.Arb
import io.kotest.property.arbitrary.long
import io.kotest.property.checkAll
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlin.random.Random

/** 至少 16 字节的秘密片段的全部 16 字节窗口；按前 8 字节排序后二分查找，命中时再逐字节确认。 */
private class SecretWindows(secrets: Map<String, ByteArray>) {
    private val windows = ArrayList<Pair<String, ByteArray>>()

    init {
        for ((label, s) in secrets) {
            for (i in 0..s.size - WINDOW) windows += label to s.copyOfRange(i, i + WINDOW)
        }
    }

    private val heads: LongArray = windows.map { head(it.second, 0) }.toLongArray().also { it.sort() }

    /** 在 [captured] 中找到的第一个秘密窗口的标签；没有时为 null。 */
    fun firstLeak(captured: ByteArray): String? {
        for (i in 0..captured.size - WINDOW) {
            if (java.util.Arrays.binarySearch(heads, head(captured, i)) < 0) continue
            val hit = windows.firstOrNull { (_, w) -> (0 until WINDOW).all { captured[i + it] == w[it] } }
            if (hit != null) return "${hit.first} @ $i"
        }
        return null
    }

    companion object {
        const val WINDOW = 16

        fun head(b: ByteArray, off: Int): Long {
            var v = 0L
            for (k in 0 until 8) v = (v shl 8) or (b[off + k].toLong() and 0xFF)
            return v
        }
    }
}

/** 随机文本：ASCII 字母数字与常用汉字混合，UTF‑8 至少 16 字节。 */
private fun Random.text(minChars: Int, maxChars: Int): String {
    val sb = StringBuilder()
    repeat(nextInt(minChars, maxChars + 1)) {
        sb.append(if (nextInt(3) == 0) (0x4E00 + nextInt(0x51A5)).toChar() else "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"[nextInt(62)])
    }
    while (StrictUtf8.encode(sb.toString()).size < 16) sb.append('x')
    return sb.toString()
}

class WireConfidentialityPropertyTest : FunSpec({
    test("Property 16: 线路机密性") {
        // A（Android）与 B（Windows）经点选或扫码配对，然后双向发送随机文字与随机文件名、内容的文件。捕获的全部 TCP 字节
        // 与 UDP 数据报中不出现任一方的身份私钥、Pairing_Token，也不出现任何文字、文件名或文件内容的 16 字节片段。
        checkAll(Pbt.iterations(100), Arb.long()) { seed ->
            runTest {
                val rnd = Random(seed)
                val clock = FakeClock(testScheduler)
                val net = InMemoryNetwork()
                val a = HostDevice("A", 1, setOf(iface("wlan0", 192, 168, 1, 2)), net, clock, backgroundScope)
                val b = HostDevice("B", 2, setOf(iface("wlan0", 192, 168, 1, 3)), net, clock, backgroundScope, type = DeviceType.WINDOWS, profile = FsProfile.WINDOWS)
                a.host.startNetworking().shouldBeInstanceOf<StartResult.Running>()
                val bPort = b.host.startNetworking().shouldBeInstanceOf<StartResult.Running>().port
                runCurrent()
                val secrets = LinkedHashMap<String, ByteArray>()
                secrets["A 私钥"] = a.identity.keyPair.privateKeyBytes()
                secrets["B 私钥"] = b.identity.keyPair.privateKeyBytes()
                val byQr = rnd.nextBoolean()
                if (byQr) {
                    val shown = b.services.pairing.qrScreen().first { it is QrScreenState.Showing } as QrScreenState.Showing
                    secrets["令牌"] = shown.payload.token.toByteArray()
                    a.services.pairing.pairByQr(shown.text) shouldBe PairingOutcome.Done(PairingResult.SUCCESS)
                } else {
                    val outcome = async { a.services.pairing.pair(Target.Address(b.transport.monitor.interfaces.value.first().address, bPort)) }
                    val req = b.services.pairing.state.first { it is PairingUiState.Request && it.sas != null } as PairingUiState.Request
                    b.services.pairing.respond(req.id, accept = true)
                    outcome.await() shouldBe PairingOutcome.Done(PairingResult.SUCCESS)
                }
                runCurrent()
                a.trust.setAutoAccept(b.id, true)
                b.trust.setAutoAccept(a.id, true)
                // 文字
                repeat(rnd.nextInt(1, 4)) { k ->
                    val (from, to) = if (rnd.nextBoolean()) a to b else b to a
                    val t = rnd.text(6, 300)
                    secrets["文字 $k"] = StrictUtf8.encode(t)
                    from.services.text.send(to.id, t).shouldBeInstanceOf<SendCheck.Sent>()
                    to.services.text.conversation(from.id).first { l -> l.any { it.text == t } }
                }
                // 文件
                repeat(rnd.nextInt(1, 3)) { k ->
                    val (from, to) = if (rnd.nextBoolean()) a to b else b to a
                    val name = rnd.text(8, 40) + ".bin"
                    val content = rnd.nextBytes(rnd.nextInt(16, 20_000))
                    secrets["文件名 $k"] = StrictUtf8.encode(name)
                    secrets["文件内容 $k"] = content
                    val ref = SourceRef("src:${from.name}/$k")
                    from.sources.put(ref, name, Bytes.of(content))
                    val id = from.services.files.send(to.id, listOf(ref)).shouldBeInstanceOf<SendOfferResult.Sent>().id
                    to.awaitTask(id, TaskState.COMPLETED)
                    to.storage.filesIn(DirRef.Default).values.any { it == Bytes.of(content) } shouldBe true
                }
                val windows = SecretWindows(secrets)
                val captured = net.connections.flatMap { listOf("TCP ${it.clientHost}→${it.serverHost}" to it.bytesFromClient(), "TCP ${it.serverHost}→${it.clientHost}" to it.bytesFromServer()) } +
                    net.datagrams.map { "UDP ${it.from}" to it.data.toByteArray() }
                withClue("seed=$seed byQr=$byQr") {
                    for ((where, bytes) in captured) {
                        withClue(where) { windows.firstLeak(bytes) shouldBe null }
                    }
                }
            }
        }
    }
})
