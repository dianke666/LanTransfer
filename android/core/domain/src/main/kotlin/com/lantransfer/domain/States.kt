package com.lantransfer.domain

// M3 状态枚举（设计 Data Models → M3）。码值会写入数据库，只能追加新值，不能重新编号；终态码值都 ≥ 10。

/** 文字消息状态（M3.1）。 */
enum class TextStatus(val code: Int) {
    SENDING(0),
    DELIVERED(1),
    FAILED(2),
    RECEIVED(3),
    ;

    companion object {
        fun fromCode(code: Int): TextStatus? = entries.firstOrNull { it.code == code }
    }
}

/** 传输任务状态（M3.2）。 */
enum class TaskState(val code: Int) {
    OFFERING(0),
    AWAITING_USER(1),
    RUNNING(2),
    INTERRUPTED(3),
    RESUMING(4),
    COMPLETED(10),
    REJECTED(11),
    NO_RESPONSE(12),
    NO_SPACE(13),
    CANNOT_SAVE(14),
    CONNECTION_LOST(15),
    CANCELLED(16),
    PEER_CANCELLED(17),
    OFFER_INVALID(18),
    ;

    val isTerminal: Boolean get() = code >= 10

    /** “正在进行”：RUNNING 或 RESUMING（临时文件清理据此排除，M4）。 */
    val isActive: Boolean get() = this == RUNNING || this == RESUMING

    companion object {
        fun fromCode(code: Int): TaskState? = entries.firstOrNull { it.code == code }
    }
}

/** 任务中断原因（M3.2）。 */
enum class InterruptReason(val code: Int) {
    PEER_UNRESPONSIVE(1),
    PEER_CLOSED(2),
    LOCAL_NETWORK_LOST(3),
    INTEGRITY_FAILED(4),
    PROTOCOL_ERROR(5),
    WRITE_FAILED(6),
    APP_EXIT(7),
    APP_KILLED(8),
    ;

    companion object {
        fun fromCode(code: Int): InterruptReason? = entries.firstOrNull { it.code == code }
    }
}

/** 文件状态（M3.3）。WRITE_FAILED 不是终态：临时文件保留，继续时按 PARTIAL 续传。 */
enum class FileState(val code: Int) {
    WAITING(0),
    TRANSFERRING(1),
    VERIFYING(2),
    WRITE_FAILED(3),
    COMPLETED(10),
    READ_FAILED(11),
    CHECKSUM_FAILED(12),
    ;

    val isTerminal: Boolean get() = code >= 10

    companion object {
        fun fromCode(code: Int): FileState? = entries.firstOrNull { it.code == code }
    }
}

/**
 * 配对结局（M3.4 中 Done 的结果）。CONNECT_FAILED 的具体原因（OpenFailure）由引擎层附带，不在领域枚举中。
 * 只在内存中使用，不写入数据库。
 */
enum class PairingResult {
    SUCCESS,
    REJECTED,
    AUTO_REJECTED,
    BUSY,
    TIMEOUT,
    CANCELLED,
    CODE_MISMATCH,
    TOKEN_INVALID,
    DISCONNECTED,
    CONNECT_FAILED,

    /** 保存或提交信任失败（设计 E 表“配对时写入信任”），界面同样提示“配对失败，请重试”。M3.4 未列出，此处补充。 */
    STORE_FAILED,
}
