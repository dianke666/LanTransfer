package com.lantransfer.engine.testing

import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.ProtocolConstants
import com.lantransfer.testing.Gens
import com.lantransfer.testing.rawFileName
import io.kotest.property.Arb
import io.kotest.property.arbitrary.arbitrary
import io.kotest.property.arbitrary.bind
import io.kotest.property.arbitrary.boolean
import io.kotest.property.arbitrary.choose
import io.kotest.property.arbitrary.element
import io.kotest.property.arbitrary.int
import io.kotest.property.arbitrary.list
import io.kotest.property.arbitrary.long
import io.kotest.property.arbitrary.map
import io.kotest.property.arbitrary.orNull
import kotlin.random.Random

// 引擎事件序列生成器（任务 14.1）：文件集合与内容、中断位置、存储失败计划、前后台切换。
// C# 侧同名生成器在 LanTransfer.Testing/EngineGens.cs。

private const val CHUNK = ProtocolConstants.MAX_CHUNK

/** 文件长度：偏向 0 字节、64 KiB 块边界两侧和小文件。 */
fun Gens.fileSize(max: Int = 5 * CHUNK): Arb<Int> = Arb.choose(
    2 to Arb.element(0, 1, CHUNK - 1, CHUNK, CHUNK + 1, 2 * CHUNK).map { minOf(it, max) },
    3 to Arb.int(0..minOf(4_096, max)),
    1 to Arb.int(0..max),
)

/** 文件内容：长度取 [fileSize]，内容由种子决定（生成大文件时比逐字节生成快得多）。 */
fun Gens.fileContent(max: Int = 5 * CHUNK): Arb<Bytes> =
    Arb.bind(fileSize(max), Arb.long()) { n, seed -> Bytes.of(Random(seed).nextBytes(n)) }

/** 生成的源文件：名称取自文件名生成器（含需要清理的名称）。 */
data class GeneratedFile(val name: String, val content: Bytes, val mtime: Long?, val mime: String) {
    override fun toString(): String = "GeneratedFile(name=$name, size=${content.size}, mtime=$mtime, mime=$mime)"
}

fun Gens.generatedFile(max: Int = 5 * CHUNK): Arb<GeneratedFile> = Arb.bind(
    rawFileName(),
    fileContent(max),
    Arb.long(1_600_000_000_000L..2_000_000_000_000L).orNull(0.2),
    Arb.element("application/octet-stream", "text/plain", "image/jpeg", "application/pdf"),
) { name, content, mtime, mime -> GeneratedFile(name.ifEmpty { "f" }, content, mtime, mime) }

/** 1–[maxFiles] 个源文件。 */
fun Gens.sourceFileSet(maxFiles: Int = 5, max: Int = 5 * CHUNK): Arb<List<GeneratedFile>> = Arb.list(generatedFile(max), 1..maxFiles)

/** 中断位置：[0, total] 内 0–[maxCount] 个字节偏移，升序（可重复，表示同一位置多次中断）。 */
fun Gens.interruptionPoints(total: Long, maxCount: Int = 3): Arb<List<Long>> =
    Arb.list(Arb.choose(3 to Arb.long(0L..total), 1 to Arb.element(0L, total)), 0..maxCount).map { it.sorted() }

/** 存储写入失败计划：第几次写操作失败（从 0 计）。 */
fun Gens.failureSchedule(maxOps: Int = 30): Arb<Set<Int>> =
    Arb.choose(3 to arbitrary { emptySet() }, 2 to Arb.list(Arb.int(0 until maxOps), 1..4).map { it.toSet() })

/** 前后台切换：（距上次切换的毫秒数, 切换后是否在后台）。 */
fun Gens.backgroundToggles(maxCount: Int = 6): Arb<List<Pair<Long, Boolean>>> =
    Arb.list(Arb.bind(Arb.long(0L..40_000L), Arb.boolean()) { dt, bg -> dt to bg }, 0..maxCount)
