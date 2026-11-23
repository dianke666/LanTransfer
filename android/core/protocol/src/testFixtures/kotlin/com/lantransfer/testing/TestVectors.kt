package com.lantransfer.testing

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * 测试向量加载器：读取类路径 `test-vectors/` 下的 JSON（来自仓库唯一一份 `protocol/test-vectors`，构建时作为测试资源复制）。
 * 文件格式见 `protocol/test-vectors/README.md`。C# 侧同名加载器：`LanTransfer.Testing.TestVectors`。
 */
object TestVectors {
    const val FORMAT_VERSION = 1

    /** 读取向量文件的原始字节。 */
    fun bytes(file: String): ByteArray {
        val stream = requireNotNull(TestVectors::class.java.getResourceAsStream("/test-vectors/$file")) {
            "找不到测试向量文件 test-vectors/$file（检查 protocol/test-vectors 是否已加入测试资源）"
        }
        return stream.use { it.readBytes() }
    }

    /** 读取并解析向量文件。 */
    fun load(file: String): JsonObject = Json.parseToJsonElement(bytes(file).decodeToString(throwOnInvalidSequence = true)).jsonObject

    /** manifest.json：当前已提交的向量文件列表。 */
    fun manifest(): JsonObject = load("manifest.json")

    /** manifest.json 中列出的文件名。 */
    fun files(): List<String> = manifest().getValue("files").jsonArray.map { it.jsonPrimitive.content }

    /** 向量文件中的 `vectors` 数组。 */
    fun vectors(file: String): List<JsonObject> = load(file).getValue("vectors").jsonArray.map { it.jsonObject }

    /** 小写十六进制字符串 → 字节（向量中 Bytes 字段的写法）。 */
    fun hex(text: String): ByteArray {
        require(text.length % 2 == 0) { "十六进制字符串长度必须为偶数：$text" }
        return ByteArray(text.length / 2) { i -> text.substring(2 * i, 2 * i + 2).toInt(16).toByte() }
    }

    /** 字节 → 小写十六进制字符串。 */
    fun toHex(bytes: ByteArray): String = bytes.joinToString("") { "%02x".format(it) }
}

/** 读取 JSON 对象中的字符串字段。 */
fun JsonObject.string(key: String): String = getValue(key).jsonPrimitive.content

/** 读取 JSON 对象中的十六进制字段。 */
fun JsonObject.hexBytes(key: String): ByteArray = TestVectors.hex(string(key))

/** 读取 JSON 对象中的数组字段。 */
fun JsonObject.array(key: String): JsonArray = getValue(key).jsonArray

/** 可选字段：缺省时为 null。 */
fun JsonObject.optional(key: String): JsonElement? = this[key]
