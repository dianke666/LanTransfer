package com.lantransfer.protocol

/** 握手 msg3 中的连接意图（规范第 6 节）。 */
enum class Intent(override val code: Int, override val since: ProtocolVersion = ProtocolVersion.V1_0) : ProtocolEnum {
    SESSION(1), PAIR(2), QR_PAIR(3)
}

/** msg2 / msg3 的载荷。[intent] 只在 msg3 中出现。显示名称由接收方按显示名称规则另行检查。 */
data class HandshakeIdentity(
    val deviceId: DeviceId,
    val displayName: String,
    val deviceType: DeviceType,
    val intent: Intent? = null,
)

/** HandshakeIdentity（Struct：1 device_id、2 display_name、3 device_type、4 intent）编解码。 */
object HandshakeIdentityCodec {
    fun encode(id: HandshakeIdentity): ByteArray {
        val w = TlvWriter()
        w.bytes16(1, id.deviceId.value)
        w.string(2, id.displayName)
        w.u8(3, id.deviceType.code)
        id.intent?.let { w.enum(4, it) }
        return w.toByteArray()
    }

    /**
     * 解码；Struct 不合法、缺少字段、设备类型或 intent 未定义，或 intent 的出现与 [expectIntent] 不符（msg3 必须有、msg2 不得有）时返回 null。
     */
    fun decode(b: ByteArray, expectIntent: Boolean, version: ProtocolVersion = ProtocolVersion.CURRENT): HandshakeIdentity? = try {
        val s = TlvStruct(b, 0, b.size)
        val type = DeviceType.fromCode(s.u8(3, "device_type"))
        when {
            type == null -> null
            s.has(4) != expectIntent -> null
            else -> HandshakeIdentity(
                DeviceId(s.bytes16(1, "device_id")),
                s.string(2, "display_name"),
                type,
                if (expectIntent) s.enum(4, "intent", Intent.entries, version) else null,
            )
        }
    } catch (_: MalformedException) {
        null
    }
}
