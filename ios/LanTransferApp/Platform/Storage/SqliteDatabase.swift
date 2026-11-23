import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import SQLite3

private let sqliteTransient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

/// 原生 libsqlite3 M2 持久化引擎（零第三方依赖，WAL 模式，与 Android Room / Windows SQLite 共用相同 6 表 Schema）。
/// 使用 `NSLock` 保护连接（避免阻塞 Swift 并发协作线程池，并彻底规避 `OpaquePointer` 跨 actor 隔离域检查）。
public final class SqliteDatabase: TrustStore, HistoryStore, TaskStore, @unchecked Sendable {
    private static let currentSchemaVersion: Int32 = 1
    private static let nextHistorySeqKey = "next_history_seq"

    private let lock = NSLock()
    private let dbURL: URL
    private var dbHandle: OpaquePointer?
    private var openError: String?

    public init(url: URL = AppGroupConfig.databaseURL()) {
        self.dbURL = url
    }

    deinit {
        lock.withLock {
            if let db = dbHandle {
                sqlite3_close(db)
                dbHandle = nil
            }
        }
    }

    /// 供 Share Extension 只读查询已信任设备列表，避免与主 App 争用写锁。
    public static func readTrustedDevicesSnapshot(url: URL = AppGroupConfig.databaseURL()) -> [TrustedDevice] {
        guard FileManager.default.fileExists(atPath: url.path) else { return [] }
        var conn: OpaquePointer?
        guard sqlite3_open_v2(url.path, &conn, SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX, nil) == SQLITE_OK,
              let db = conn else {
            if let conn { sqlite3_close(conn) }
            return []
        }
        defer { sqlite3_close(db) }
        sqlite3_busy_timeout(db, 2000)
        return queryTrustedDevices(db: db)
    }

    private func ensureOpenLocked() -> OpaquePointer? {
        if let dbHandle { return dbHandle }
        try? FileManager.default.createDirectory(at: dbURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        var handle: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
        guard sqlite3_open_v2(dbURL.path, &handle, flags, nil) == SQLITE_OK, let conn = handle else {
            let msg = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "sqlite3_open_v2 failed"
            if let handle { sqlite3_close(handle) }
            openError = msg
            return nil
        }
        sqlite3_busy_timeout(conn, 5000)
        _ = exec(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;")

        let existingVersion = queryInt32(conn, "PRAGMA user_version;") ?? 0
        if existingVersion > Self.currentSchemaVersion {
            sqlite3_close(conn)
            openError = "数据库版本（\(existingVersion)）高于当前应用支持版本"
            return nil
        }

        let schemaSql = loadSchemaSql()
        guard exec(conn, schemaSql) else {
            let msg = String(cString: sqlite3_errmsg(conn))
            sqlite3_close(conn)
            openError = msg
            return nil
        }
        if existingVersion == 0 {
            _ = exec(conn, "PRAGMA user_version = \(Self.currentSchemaVersion);")
        }
        _ = exec(conn, "INSERT OR IGNORE INTO schema_meta (key, value) VALUES ('\(Self.nextHistorySeqKey)', 1);")
        self.dbHandle = conn
        self.openError = nil
        return conn
    }

    private func loadSchemaSql() -> String {
        if let url = Bundle.main.url(forResource: "Schema", withExtension: "sql"),
           let text = try? String(contentsOf: url, encoding: .utf8) {
            return text
        }
        return Self.embeddedFallbackSchema
    }

    // MARK: - TrustStore

    public func all() -> [TrustedDevice] {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return [] }
            return Self.queryTrustedDevices(db: db)
        }
    }

    public func get(_ id: DeviceId) -> TrustedDevice? {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return nil }
            let sql = "SELECT device_id, display_name, device_type, public_key, auto_accept, paired_at, last_ip, last_port FROM trusted_device WHERE device_id = ?;"
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { return nil }
            defer { sqlite3_finalize(stmt) }
            bindBlob(stmt, 1, id.bytes)
            guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
            return Self.readTrustedRow(stmt)
        }
    }

    public func upsert(_ device: TrustedDevice) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            let sql = """
            INSERT OR REPLACE INTO trusted_device
              (device_id, display_name, device_type, public_key, auto_accept, paired_at, last_ip, last_port)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?);
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            bindBlob(stmt, 1, device.deviceId.bytes)
            bindText(stmt, 2, device.displayName)
            sqlite3_bind_int(stmt, 3, Int32(device.deviceType.rawValue))
            bindBlob(stmt, 4, device.publicKey)
            sqlite3_bind_int(stmt, 5, device.autoAccept ? 1 : 0)
            sqlite3_bind_int64(stmt, 6, device.pairedAtUtcMs)
            if let ip = device.lastAddress {
                sqlite3_bind_int64(stmt, 7, Int64(ip.rawValue))
            } else {
                sqlite3_bind_null(stmt, 7)
            }
            if let port = device.lastPort {
                sqlite3_bind_int(stmt, 8, Int32(port))
            } else {
                sqlite3_bind_null(stmt, 8)
            }
            return sqlite3_step(stmt) == SQLITE_DONE ? .ok : .failed(detail: lastError(db))
        }
    }

    public func delete(_ id: DeviceId) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, "DELETE FROM trusted_device WHERE device_id = ?;", -1, &stmt, nil) == SQLITE_OK, let stmt else {
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            bindBlob(stmt, 1, id.bytes)
            return sqlite3_step(stmt) == SQLITE_DONE ? .ok : .failed(detail: lastError(db))
        }
    }

    public func setAutoAccept(_ id: DeviceId, enabled: Bool) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, "UPDATE trusted_device SET auto_accept = ? WHERE device_id = ?;", -1, &stmt, nil) == SQLITE_OK, let stmt else {
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int(stmt, 1, enabled ? 1 : 0)
            bindBlob(stmt, 2, id.bytes)
            return sqlite3_step(stmt) == SQLITE_DONE ? .ok : .failed(detail: lastError(db))
        }
    }

    public func updateName(_ id: DeviceId, name: String) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, "UPDATE trusted_device SET display_name = ? WHERE device_id = ?;", -1, &stmt, nil) == SQLITE_OK, let stmt else {
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            bindText(stmt, 1, name)
            bindBlob(stmt, 2, id.bytes)
            return sqlite3_step(stmt) == SQLITE_DONE ? .ok : .failed(detail: lastError(db))
        }
    }

    public func recordAddress(_ id: DeviceId, ip: Ipv4, port: Int) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, "UPDATE trusted_device SET last_ip = ?, last_port = ? WHERE device_id = ?;", -1, &stmt, nil) == SQLITE_OK, let stmt else {
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int64(stmt, 1, Int64(ip.rawValue))
            sqlite3_bind_int(stmt, 2, Int32(port))
            bindBlob(stmt, 3, id.bytes)
            return sqlite3_step(stmt) == SQLITE_DONE ? .ok : .failed(detail: lastError(db))
        }
    }

    // MARK: - HistoryStore

    public func findText(peer: DeviceId, id: MessageId) -> TextRecord? {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return nil }
            let sql = "SELECT seq, peer_id, message_id, direction, peer_name, text_utf8, sent_at, recorded_at, recorded_tz, status FROM text_message WHERE peer_id = ? AND message_id = ?;"
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { return nil }
            defer { sqlite3_finalize(stmt) }
            bindBlob(stmt, 1, peer.bytes)
            bindBlob(stmt, 2, id.bytes)
            guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
            return readTextRow(stmt)
        }
    }

    public func appendText(_ record: TextRecord) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            guard exec(db, "BEGIN IMMEDIATE;") else { return .failed(detail: lastError(db)) }
            let seq = allocateSeqLocked(db)
            let sql = """
            INSERT INTO text_message
              (seq, peer_id, message_id, direction, peer_name, text_utf8, sent_at, recorded_at, recorded_tz, status)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
                _ = exec(db, "ROLLBACK;")
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int64(stmt, 1, seq)
            bindBlob(stmt, 2, record.peer.bytes)
            bindBlob(stmt, 3, record.messageId.bytes)
            sqlite3_bind_int(stmt, 4, Int32(record.direction.rawValue))
            bindText(stmt, 5, record.peerName)
            bindBlob(stmt, 6, record.textUtf8)
            sqlite3_bind_int64(stmt, 7, record.sentAtUtcMs)
            sqlite3_bind_int64(stmt, 8, record.recordedAt.utcMs)
            sqlite3_bind_int(stmt, 9, Int32(record.recordedAt.offsetMin))
            sqlite3_bind_int(stmt, 10, Int32(record.status.rawValue))

            let rc = sqlite3_step(stmt)
            if (rc & 0xFF) == SQLITE_CONSTRAINT {
                _ = exec(db, "ROLLBACK;")
                return .duplicate
            }
            guard rc == SQLITE_DONE else {
                let err = lastError(db)
                _ = exec(db, "ROLLBACK;")
                return .failed(detail: err)
            }
            _ = exec(db, "COMMIT;")
            return .ok
        }
    }

    public func updateTextStatus(id: MessageId, peer: DeviceId, status: TextStatus) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, "UPDATE text_message SET status = ? WHERE peer_id = ? AND message_id = ?;", -1, &stmt, nil) == SQLITE_OK, let stmt else {
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int(stmt, 1, Int32(status.rawValue))
            bindBlob(stmt, 2, peer.bytes)
            bindBlob(stmt, 3, id.bytes)
            return sqlite3_step(stmt) == SQLITE_DONE ? .ok : .failed(detail: lastError(db))
        }
    }

    public func upsertFile(_ record: FileRecord) -> StoreResult {
        .ok
    }

    public func list(limit: Int) -> Result<[HistoryRecord], IoError> {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failure(IoError(openError ?? "db unavailable")) }
            var items: [HistoryRecord] = []

            var tStmt: OpaquePointer?
            if sqlite3_prepare_v2(db, "SELECT seq, peer_id, message_id, direction, peer_name, text_utf8, sent_at, recorded_at, recorded_tz, status FROM text_message ORDER BY recorded_at DESC, seq DESC LIMIT ?;", -1, &tStmt, nil) == SQLITE_OK, let tStmt {
                defer { sqlite3_finalize(tStmt) }
                sqlite3_bind_int(tStmt, 1, Int32(limit))
                while sqlite3_step(tStmt) == SQLITE_ROW {
                    if let rec = readTextRow(tStmt) {
                        items.append(.text(rec))
                    }
                }
            }

            let fSql = """
            SELECT f.transfer_id, f.file_index, f.seq, f.original_name, f.size, f.mime, f.mtime, f.state, f.persisted,
                   f.sha256, f.source_ref, f.temp_ref, f.started_at, f.last_write_at, f.final_name, f.full_path, f.stored_ref,
                   t.peer_id, t.peer_name, t.direction, t.recorded_at, t.recorded_tz, t.state, t.interrupt_reason, t.interrupt_detail
            FROM transfer_file f
            JOIN transfer_task t ON f.transfer_id = t.transfer_id
            ORDER BY t.recorded_at DESC, f.seq DESC
            LIMIT ?;
            """
            var fStmt: OpaquePointer?
            if sqlite3_prepare_v2(db, fSql, -1, &fStmt, nil) == SQLITE_OK, let fStmt {
                defer { sqlite3_finalize(fStmt) }
                sqlite3_bind_int(fStmt, 1, Int32(limit))
                while sqlite3_step(fStmt) == SQLITE_ROW {
                    if let file = readTransferFileRow(fStmt),
                       let peer = DeviceId(bytes: colBlob(fStmt, 17)),
                       let dir = Direction(rawValue: Int(sqlite3_column_int(fStmt, 19))),
                       let tState = TaskState(rawValue: Int(sqlite3_column_int(fStmt, 22))) {
                        let peerName = colText(fStmt, 18) ?? ""
                        let recTime = RecordTime(utcMs: sqlite3_column_int64(fStmt, 20), offsetMin: Int(sqlite3_column_int(fStmt, 21)))
                        let interrupt: InterruptInfo?
                        if sqlite3_column_type(fStmt, 23) != SQLITE_NULL,
                           let reason = InterruptReason(rawValue: Int(sqlite3_column_int(fStmt, 23))) {
                            interrupt = InterruptInfo(reason: reason, detail: colText(fStmt, 24))
                        } else {
                            interrupt = nil
                        }
                        items.append(.file(FileRecord(
                            file: file,
                            peer: peer,
                            peerName: peerName,
                            direction: dir,
                            recordedAt: recTime,
                            taskState: tState,
                            interrupt: interrupt
                        )))
                    }
                }
            }

            items.sort {
                if $0.recordedAt.utcMs != $1.recordedAt.utcMs { return $0.recordedAt.utcMs > $1.recordedAt.utcMs }
                return $0.seq > $1.seq
            }
            return .success(Array(items.prefix(limit)))
        }
    }

    public func delete(_ id: RecordId) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, "DELETE FROM text_message WHERE seq = ?;", -1, &stmt, nil) == SQLITE_OK, let stmt else {
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            sqlite3_bind_int64(stmt, 1, id.seq)
            return sqlite3_step(stmt) == SQLITE_DONE ? .ok : .failed(detail: lastError(db))
        }
    }

    public func deleteFileRecord(transferId: TransferId, index: Int) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            guard exec(db, "BEGIN IMMEDIATE;") else { return .failed(detail: lastError(db)) }
            var delStmt: OpaquePointer?
            if sqlite3_prepare_v2(db, "DELETE FROM transfer_file WHERE transfer_id = ? AND file_index = ?;", -1, &delStmt, nil) == SQLITE_OK, let delStmt {
                bindBlob(delStmt, 1, transferId.bytes)
                sqlite3_bind_int(delStmt, 2, Int32(index))
                _ = sqlite3_step(delStmt)
                sqlite3_finalize(delStmt)
            }
            var cntStmt: OpaquePointer?
            var remaining: Int32 = 1
            if sqlite3_prepare_v2(db, "SELECT COUNT(*) FROM transfer_file WHERE transfer_id = ?;", -1, &cntStmt, nil) == SQLITE_OK, let cntStmt {
                bindBlob(cntStmt, 1, transferId.bytes)
                if sqlite3_step(cntStmt) == SQLITE_ROW {
                    remaining = sqlite3_column_int(cntStmt, 0)
                }
                sqlite3_finalize(cntStmt)
            }
            if remaining == 0 {
                var taskDel: OpaquePointer?
                if sqlite3_prepare_v2(db, "DELETE FROM transfer_task WHERE transfer_id = ?;", -1, &taskDel, nil) == SQLITE_OK, let taskDel {
                    bindBlob(taskDel, 1, transferId.bytes)
                    _ = sqlite3_step(taskDel)
                    sqlite3_finalize(taskDel)
                }
            }
            _ = exec(db, "COMMIT;")
            return .ok
        }
    }

    public func clearAll() -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            let ok = exec(db, "BEGIN IMMEDIATE; DELETE FROM text_message; DELETE FROM transfer_file; DELETE FROM transfer_task; COMMIT;")
            return ok ? .ok : .failed(detail: lastError(db))
        }
    }

    // MARK: - TaskStore

    public func save(_ task: TransferTask) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            guard exec(db, "BEGIN IMMEDIATE;") else { return .failed(detail: lastError(db)) }

            let taskSql = """
            INSERT OR REPLACE INTO transfer_task
              (transfer_id, peer_id, peer_name, direction, state, interrupt_reason, interrupt_detail, receive_dir, recorded_at, recorded_tz, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, taskSql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
                _ = exec(db, "ROLLBACK;")
                return .failed(detail: lastError(db))
            }
            bindBlob(stmt, 1, task.transferId.bytes)
            bindBlob(stmt, 2, task.peer.bytes)
            bindText(stmt, 3, task.peerName)
            sqlite3_bind_int(stmt, 4, Int32(task.direction.rawValue))
            sqlite3_bind_int(stmt, 5, Int32(task.state.rawValue))
            if let interrupt = task.interrupt {
                sqlite3_bind_int(stmt, 6, Int32(interrupt.reason.rawValue))
                bindOptText(stmt, 7, interrupt.detail)
            } else {
                sqlite3_bind_null(stmt, 6)
                sqlite3_bind_null(stmt, 7)
            }
            bindOptText(stmt, 8, task.receiveDir?.serialized)
            sqlite3_bind_int64(stmt, 9, task.recordedAt.utcMs)
            sqlite3_bind_int(stmt, 10, Int32(task.recordedAt.offsetMin))
            sqlite3_bind_int64(stmt, 11, task.updatedAtUtcMs)
            let rc = sqlite3_step(stmt)
            sqlite3_finalize(stmt)
            guard rc == SQLITE_DONE else {
                let err = lastError(db)
                _ = exec(db, "ROLLBACK;")
                return .failed(detail: err)
            }

            // 查询已有文件序号，保证更新不改变原始 seq
            var existingSeq: [Int: Int64] = [:]
            var qStmt: OpaquePointer?
            if sqlite3_prepare_v2(db, "SELECT file_index, seq FROM transfer_file WHERE transfer_id = ?;", -1, &qStmt, nil) == SQLITE_OK, let qStmt {
                bindBlob(qStmt, 1, task.transferId.bytes)
                while sqlite3_step(qStmt) == SQLITE_ROW {
                    existingSeq[Int(sqlite3_column_int(qStmt, 0))] = sqlite3_column_int64(qStmt, 1)
                }
                sqlite3_finalize(qStmt)
            }

            let fileSql = """
            INSERT OR REPLACE INTO transfer_file
              (transfer_id, file_index, seq, original_name, size, mime, mtime, state, persisted, sha256,
               source_ref, temp_ref, started_at, last_write_at, final_name, full_path, stored_ref)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
            """
            for f in task.files {
                let seq = existingSeq[f.index] ?? (f.seq > 0 ? f.seq : allocateSeqLocked(db))
                var fStmt: OpaquePointer?
                guard sqlite3_prepare_v2(db, fileSql, -1, &fStmt, nil) == SQLITE_OK, let fStmt else {
                    _ = exec(db, "ROLLBACK;")
                    return .failed(detail: lastError(db))
                }
                bindBlob(fStmt, 1, f.transferId.bytes)
                sqlite3_bind_int(fStmt, 2, Int32(f.index))
                sqlite3_bind_int64(fStmt, 3, seq)
                bindText(fStmt, 4, f.originalName)
                sqlite3_bind_int64(fStmt, 5, f.size)
                bindText(fStmt, 6, f.mime)
                bindOptInt64(fStmt, 7, f.mtime)
                sqlite3_bind_int(fStmt, 8, Int32(f.state.rawValue))
                sqlite3_bind_int64(fStmt, 9, f.persisted)
                bindOptBlob(fStmt, 10, f.sha256)
                bindOptText(fStmt, 11, f.sourceRef?.value)
                bindOptText(fStmt, 12, f.tempRef?.value)
                bindOptText(fStmt, 13, f.startedAt?.isoString)
                bindOptInt64(fStmt, 14, f.lastWriteAtUtcMs)
                bindOptText(fStmt, 15, f.finalName)
                bindOptText(fStmt, 16, f.fullPath)
                bindOptText(fStmt, 17, f.storedRef?.value)
                let frc = sqlite3_step(fStmt)
                sqlite3_finalize(fStmt)
                if frc != SQLITE_DONE {
                    let err = lastError(db)
                    _ = exec(db, "ROLLBACK;")
                    return .failed(detail: err)
                }
            }
            _ = exec(db, "COMMIT;")
            return .ok
        }
    }

    public func get(_ id: TransferId) -> TransferTask? {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return nil }
            let sql = "SELECT transfer_id, peer_id, peer_name, direction, state, interrupt_reason, interrupt_detail, receive_dir, recorded_at, recorded_tz, updated_at FROM transfer_task WHERE transfer_id = ?;"
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { return nil }
            defer { sqlite3_finalize(stmt) }
            bindBlob(stmt, 1, id.bytes)
            guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
            return readTaskRow(db: db, stmt: stmt)
        }
    }

    public func unfinished() -> [TransferTask] {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return [] }
            let sql = "SELECT transfer_id, peer_id, peer_name, direction, state, interrupt_reason, interrupt_detail, receive_dir, recorded_at, recorded_tz, updated_at FROM transfer_task WHERE state < 10 ORDER BY recorded_at DESC;"
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { return [] }
            defer { sqlite3_finalize(stmt) }
            var out: [TransferTask] = []
            while sqlite3_step(stmt) == SQLITE_ROW {
                if let t = readTaskRow(db: db, stmt: stmt) {
                    out.append(t)
                }
            }
            return out
        }
    }

    public func addPendingCancel(peer: DeviceId, transferId: TransferId) -> StoreResult {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return .failed(detail: openError ?? "db unavailable") }
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, "INSERT OR REPLACE INTO pending_cancel (peer_id, transfer_id, created_at) VALUES (?, ?, ?);", -1, &stmt, nil) == SQLITE_OK, let stmt else {
                return .failed(detail: lastError(db))
            }
            defer { sqlite3_finalize(stmt) }
            bindBlob(stmt, 1, peer.bytes)
            bindBlob(stmt, 2, transferId.bytes)
            sqlite3_bind_int64(stmt, 3, Int64(Date().timeIntervalSince1970 * 1000.0))
            return sqlite3_step(stmt) == SQLITE_DONE ? .ok : .failed(detail: lastError(db))
        }
    }

    public func takePendingCancels(peer: DeviceId) -> [TransferId] {
        lock.withLock {
            guard let db = ensureOpenLocked() else { return [] }
            var result: [TransferId] = []
            var stmt: OpaquePointer?
            if sqlite3_prepare_v2(db, "SELECT transfer_id FROM pending_cancel WHERE peer_id = ? ORDER BY created_at ASC;", -1, &stmt, nil) == SQLITE_OK, let stmt {
                bindBlob(stmt, 1, peer.bytes)
                while sqlite3_step(stmt) == SQLITE_ROW {
                    if let tid = TransferId(bytes: colBlob(stmt, 0)) {
                        result.append(tid)
                    }
                }
                sqlite3_finalize(stmt)
            }
            if !result.isEmpty {
                var delStmt: OpaquePointer?
                if sqlite3_prepare_v2(db, "DELETE FROM pending_cancel WHERE peer_id = ?;", -1, &delStmt, nil) == SQLITE_OK, let delStmt {
                    bindBlob(delStmt, 1, peer.bytes)
                    _ = sqlite3_step(delStmt)
                    sqlite3_finalize(delStmt)
                }
            }
            return result
        }
    }

    // MARK: - Helpers

    private func allocateSeqLocked(_ db: OpaquePointer) -> Int64 {
        var seq: Int64 = 1
        var q: OpaquePointer?
        if sqlite3_prepare_v2(db, "SELECT value FROM schema_meta WHERE key = '\(Self.nextHistorySeqKey)';", -1, &q, nil) == SQLITE_OK, let q {
            if sqlite3_step(q) == SQLITE_ROW {
                seq = sqlite3_column_int64(q, 0)
            }
            sqlite3_finalize(q)
        }
        _ = exec(db, "UPDATE schema_meta SET value = \(seq + 1) WHERE key = '\(Self.nextHistorySeqKey)';")
        return seq
    }

    private static func queryTrustedDevices(db: OpaquePointer) -> [TrustedDevice] {
        let sql = "SELECT device_id, display_name, device_type, public_key, auto_accept, paired_at, last_ip, last_port FROM trusted_device ORDER BY paired_at DESC;"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { return [] }
        defer { sqlite3_finalize(stmt) }
        var list: [TrustedDevice] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            if let d = readTrustedRow(stmt) {
                list.append(d)
            }
        }
        return list
    }

    private static func readTrustedRow(_ stmt: OpaquePointer) -> TrustedDevice? {
        guard let id = DeviceId(bytes: colBlobStatic(stmt, 0)),
              let type = DeviceType.fromCode(Int(sqlite3_column_int(stmt, 2))) else { return nil }
        let name = colTextStatic(stmt, 1) ?? ""
        let pubKey = colBlobStatic(stmt, 3)
        let autoAccept = sqlite3_column_int(stmt, 4) != 0
        let pairedAt = sqlite3_column_int64(stmt, 5)
        let lastIp: Ipv4? = sqlite3_column_type(stmt, 6) == SQLITE_NULL ? nil : Ipv4(rawValue: UInt32(truncatingIfNeeded: sqlite3_column_int64(stmt, 6)))
        let lastPort: Int? = sqlite3_column_type(stmt, 7) == SQLITE_NULL ? nil : Int(sqlite3_column_int(stmt, 7))
        return TrustedDevice(
            deviceId: id,
            displayName: name,
            deviceType: type,
            publicKey: pubKey,
            autoAccept: autoAccept,
            pairedAtUtcMs: pairedAt,
            lastAddress: lastIp,
            lastPort: lastPort
        )
    }

    private func readTextRow(_ stmt: OpaquePointer) -> TextRecord? {
        guard let peer = DeviceId(bytes: colBlob(stmt, 1)),
              let mid = MessageId(bytes: colBlob(stmt, 2)),
              let dir = Direction(rawValue: Int(sqlite3_column_int(stmt, 3))),
              let status = TextStatus(rawValue: Int(sqlite3_column_int(stmt, 9))) else {
            return nil
        }
        return TextRecord(
            seq: sqlite3_column_int64(stmt, 0),
            peer: peer,
            peerName: colText(stmt, 4) ?? "",
            messageId: mid,
            direction: dir,
            textUtf8: colBlob(stmt, 5),
            sentAtUtcMs: sqlite3_column_int64(stmt, 6),
            recordedAt: RecordTime(utcMs: sqlite3_column_int64(stmt, 7), offsetMin: Int(sqlite3_column_int(stmt, 8))),
            status: status
        )
    }

    private func readTaskRow(db: OpaquePointer, stmt: OpaquePointer) -> TransferTask? {
        guard let tid = TransferId(bytes: colBlob(stmt, 0)),
              let peer = DeviceId(bytes: colBlob(stmt, 1)),
              let dir = Direction(rawValue: Int(sqlite3_column_int(stmt, 3))),
              let state = TaskState(rawValue: Int(sqlite3_column_int(stmt, 4))) else {
            return nil
        }
        let interrupt: InterruptInfo?
        if sqlite3_column_type(stmt, 5) != SQLITE_NULL,
           let reason = InterruptReason(rawValue: Int(sqlite3_column_int(stmt, 5))) {
            interrupt = InterruptInfo(reason: reason, detail: colText(stmt, 6))
        } else {
            interrupt = nil
        }
        let receiveDir = colText(stmt, 7).flatMap(DirRef.parse)
        let files = loadFilesForTask(db: db, transferId: tid)
        return TransferTask(
            transferId: tid,
            peer: peer,
            peerName: colText(stmt, 2) ?? "",
            direction: dir,
            state: state,
            interrupt: interrupt,
            receiveDir: receiveDir,
            recordedAt: RecordTime(utcMs: sqlite3_column_int64(stmt, 8), offsetMin: Int(sqlite3_column_int(stmt, 9))),
            updatedAtUtcMs: sqlite3_column_int64(stmt, 10),
            files: files
        )
    }

    private func loadFilesForTask(db: OpaquePointer, transferId: TransferId) -> [TransferFile] {
        let sql = "SELECT transfer_id, file_index, seq, original_name, size, mime, mtime, state, persisted, sha256, source_ref, temp_ref, started_at, last_write_at, final_name, full_path, stored_ref FROM transfer_file WHERE transfer_id = ? ORDER BY file_index ASC;"
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { return [] }
        defer { sqlite3_finalize(stmt) }
        bindBlob(stmt, 1, transferId.bytes)
        var out: [TransferFile] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            if let f = readTransferFileRow(stmt) {
                out.append(f)
            }
        }
        return out
    }

    private func readTransferFileRow(_ stmt: OpaquePointer) -> TransferFile? {
        guard let tid = TransferId(bytes: colBlob(stmt, 0)),
              let state = FileState(rawValue: Int(sqlite3_column_int(stmt, 7))) else {
            return nil
        }
        return TransferFile(
            transferId: tid,
            index: Int(sqlite3_column_int(stmt, 1)),
            seq: sqlite3_column_int64(stmt, 2),
            originalName: colText(stmt, 3) ?? "",
            size: sqlite3_column_int64(stmt, 4),
            mime: colText(stmt, 5) ?? SourceMeta.octetStream,
            mtime: sqlite3_column_type(stmt, 6) == SQLITE_NULL ? nil : sqlite3_column_int64(stmt, 6),
            state: state,
            persisted: sqlite3_column_int64(stmt, 8),
            sha256: sqlite3_column_type(stmt, 9) == SQLITE_NULL ? nil : colBlob(stmt, 9),
            sourceRef: colText(stmt, 10).map(SourceRef.init),
            tempRef: colText(stmt, 11).map(TempRef.init),
            startedAt: colText(stmt, 12).flatMap(LocalDateTime.parseIso),
            lastWriteAtUtcMs: sqlite3_column_type(stmt, 13) == SQLITE_NULL ? nil : sqlite3_column_int64(stmt, 13),
            finalName: colText(stmt, 14),
            fullPath: colText(stmt, 15),
            storedRef: colText(stmt, 16).map(StoredFileRef.init)
        )
    }

    private func exec(_ db: OpaquePointer, _ sql: String) -> Bool {
        sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK
    }

    private func queryInt32(_ db: OpaquePointer, _ sql: String) -> Int32? {
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else { return nil }
        defer { sqlite3_finalize(stmt) }
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }
        return sqlite3_column_int(stmt, 0)
    }

    private func lastError(_ db: OpaquePointer) -> String {
        String(cString: sqlite3_errmsg(db))
    }

    private func bindText(_ stmt: OpaquePointer, _ index: Int32, _ text: String) {
        text.withCString { sqlite3_bind_text(stmt, index, $0, -1, sqliteTransient) }
    }

    private func bindOptText(_ stmt: OpaquePointer, _ index: Int32, _ text: String?) {
        if let text { bindText(stmt, index, text) } else { sqlite3_bind_null(stmt, index) }
    }

    private func bindOptInt64(_ stmt: OpaquePointer, _ index: Int32, _ value: Int64?) {
        if let value { sqlite3_bind_int64(stmt, index, value) } else { sqlite3_bind_null(stmt, index) }
    }

    private func bindBlob(_ stmt: OpaquePointer, _ index: Int32, _ data: Data) {
        data.withUnsafeBytes { raw in
            sqlite3_bind_blob(stmt, index, raw.baseAddress, Int32(data.count), sqliteTransient)
        }
    }

    private func bindOptBlob(_ stmt: OpaquePointer, _ index: Int32, _ data: Data?) {
        if let data { bindBlob(stmt, index, data) } else { sqlite3_bind_null(stmt, index) }
    }

    private func colText(_ stmt: OpaquePointer, _ index: Int32) -> String? {
        Self.colTextStatic(stmt, index)
    }

    private func colBlob(_ stmt: OpaquePointer, _ index: Int32) -> Data {
        Self.colBlobStatic(stmt, index)
    }

    private static func colTextStatic(_ stmt: OpaquePointer, _ index: Int32) -> String? {
        guard let cStr = sqlite3_column_text(stmt, index) else { return nil }
        return String(cString: cStr)
    }

    private static func colBlobStatic(_ stmt: OpaquePointer, _ index: Int32) -> Data {
        let len = Int(sqlite3_column_bytes(stmt, index))
        guard len > 0, let ptr = sqlite3_column_blob(stmt, index) else { return Data() }
        return Data(bytes: ptr, count: len)
    }

    private static let embeddedFallbackSchema = """
    CREATE TABLE IF NOT EXISTS schema_meta (key TEXT NOT NULL PRIMARY KEY, value INTEGER NOT NULL);
    CREATE TABLE IF NOT EXISTS trusted_device (device_id BLOB NOT NULL PRIMARY KEY, display_name TEXT NOT NULL, device_type INTEGER NOT NULL, public_key BLOB NOT NULL, auto_accept INTEGER NOT NULL DEFAULT 0, paired_at INTEGER NOT NULL, last_ip INTEGER, last_port INTEGER);
    CREATE TABLE IF NOT EXISTS text_message (seq INTEGER NOT NULL PRIMARY KEY, peer_id BLOB NOT NULL, message_id BLOB NOT NULL, direction INTEGER NOT NULL, peer_name TEXT NOT NULL, text_utf8 BLOB NOT NULL, sent_at INTEGER NOT NULL, recorded_at INTEGER NOT NULL, recorded_tz INTEGER NOT NULL, status INTEGER NOT NULL, UNIQUE (peer_id, message_id));
    CREATE INDEX IF NOT EXISTS ix_text_message_order ON text_message (recorded_at, seq);
    CREATE TABLE IF NOT EXISTS transfer_task (transfer_id BLOB NOT NULL PRIMARY KEY, peer_id BLOB NOT NULL, peer_name TEXT NOT NULL, direction INTEGER NOT NULL, state INTEGER NOT NULL, interrupt_reason INTEGER, interrupt_detail TEXT, receive_dir TEXT, recorded_at INTEGER NOT NULL, recorded_tz INTEGER NOT NULL, updated_at INTEGER NOT NULL);
    CREATE INDEX IF NOT EXISTS ix_transfer_task_order ON transfer_task (recorded_at);
    CREATE INDEX IF NOT EXISTS ix_transfer_task_peer_state ON transfer_task (peer_id, state);
    CREATE TABLE IF NOT EXISTS transfer_file (transfer_id BLOB NOT NULL, file_index INTEGER NOT NULL, seq INTEGER NOT NULL, original_name TEXT NOT NULL, size INTEGER NOT NULL, mime TEXT NOT NULL, mtime INTEGER, state INTEGER NOT NULL, persisted INTEGER NOT NULL DEFAULT 0, sha256 BLOB, source_ref TEXT, temp_ref TEXT, started_at TEXT, last_write_at INTEGER, final_name TEXT, full_path TEXT, stored_ref TEXT, PRIMARY KEY (transfer_id, file_index));
    CREATE UNIQUE INDEX IF NOT EXISTS ux_transfer_file_seq ON transfer_file (seq);
    CREATE TABLE IF NOT EXISTS pending_cancel (peer_id BLOB NOT NULL, transfer_id BLOB NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (peer_id, transfer_id));
    """
}
