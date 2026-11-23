import Foundation
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import UniformTypeIdentifiers

/// iOS 文件接收存储：最终文件落盘至 `Documents/LanTransfer`（自动在系统“文件”App 可见），未完成的 `.part` 临时文件严格隔离在 `Library/Caches/lantransfer-tmp`。
public final class IosReceiveStorage: ReceiveStorage, @unchecked Sendable {
    public let fsProfile: FsProfile = .ios

    public init() {}

    public static func defaultReceiveDirectoryURL() -> URL {
        let docs = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return docs.appendingPathComponent("LanTransfer", isDirectory: true)
    }

    public static func tempDirectoryURL() -> URL {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return caches.appendingPathComponent("lantransfer-tmp", isDirectory: true)
    }

    private func resolveDirectory(_ dir: DirRef) -> URL {
        switch dir {
        case .default:
            return Self.defaultReceiveDirectoryURL()
        case .path(let path):
            return URL(fileURLWithPath: path, isDirectory: true)
        case .tree(let uri):
            return URL(string: uri) ?? Self.defaultReceiveDirectoryURL()
        }
    }

    public func probe(_ dir: DirRef) -> DirStatus {
        let url = resolveDirectory(dir)
        let fm = FileManager.default
        var isDir: ObjCBool = false
        if fm.fileExists(atPath: url.path, isDirectory: &isDir) {
            return (isDir.boolValue && fm.isWritableFile(atPath: url.path)) ? .usable : .notWritable
        }
        return ensureDirectory(dir) ? .usable : .cannotCreate
    }

    public func ensureDirectory(_ dir: DirRef) -> Bool {
        let url = resolveDirectory(dir)
        do {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            try FileManager.default.createDirectory(at: Self.tempDirectoryURL(), withIntermediateDirectories: true)
            return true
        } catch {
            return false
        }
    }

    public func isWritable(_ dir: DirRef) -> Bool {
        probe(dir) == .usable
    }

    public func freeSpace(_ dir: DirRef) -> Int64? {
        let url = resolveDirectory(dir)
        let target = FileManager.default.fileExists(atPath: url.path) ? url : url.deletingLastPathComponent()
        if let values = try? target.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]),
           let cap = values.volumeAvailableCapacityForImportantUsage {
            return cap
        }
        if let attrs = try? FileManager.default.attributesOfFileSystem(forPath: target.path),
           let free = attrs[.systemFreeSize] as? NSNumber {
            return free.int64Value
        }
        return nil
    }

    public func exists(_ dir: DirRef, name: String) -> Bool {
        let fileURL = resolveDirectory(dir).appendingPathComponent(name)
        return FileManager.default.fileExists(atPath: fileURL.path)
    }

    public func createTemp(dir: DirRef, task: TransferId, index: Int) -> TempRef {
        let tempDir = Self.tempDirectoryURL()
        try? FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        let fileName = ".lt-\(task.hexString)-\(index).part"
        let url = tempDir.appendingPathComponent(fileName)
        if !FileManager.default.fileExists(atPath: url.path) {
            FileManager.default.createFile(atPath: url.path, contents: nil)
        }
        return TempRef(url.path)
    }

    public func openTempForWrite(_ ref: TempRef, offset: Int64) -> Result<any TempWriter, IoError> {
        let url = URL(fileURLWithPath: ref.value)
        do {
            if !FileManager.default.fileExists(atPath: url.path) {
                FileManager.default.createFile(atPath: url.path, contents: nil)
            }
            let writer = try FileHandleTempWriter(url: url, offset: offset)
            return .success(writer)
        } catch {
            return .failure(IoError(error.localizedDescription))
        }
    }

    public func openTempForRead(_ ref: TempRef) -> Result<any ChunkReader, IoError> {
        let url = URL(fileURLWithPath: ref.value)
        do {
            let reader = try FileHandleChunkReader(url: url, offset: 0, securityScoped: false)
            return .success(reader)
        } catch {
            return .failure(IoError(error.localizedDescription))
        }
    }

    public func tempSize(_ ref: TempRef) -> Int64? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: ref.value),
              let size = attrs[.size] as? NSNumber else {
            return nil
        }
        return size.int64Value
    }

    public func deleteTemp(_ ref: TempRef) {
        try? FileManager.default.removeItem(atPath: ref.value)
    }

    public func listTemps() -> [TempInfo] {
        let tempDir = Self.tempDirectoryURL()
        guard let urls = try? FileManager.default.contentsOfDirectory(at: tempDir, includingPropertiesForKeys: [.contentModificationDateKey]) else {
            return []
        }
        return urls.compactMap { url in
            let name = url.lastPathComponent
            guard name.hasPrefix(".lt-"), name.hasSuffix(".part") else { return nil }
            let modDate = (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? Date.distantPast
            let ms = Int64(modDate.timeIntervalSince1970 * 1000.0)
            let parts = name.dropFirst(4).dropLast(5).split(separator: "-")
            let tid = parts.first.flatMap { Self.parseHex16(String($0)) }.flatMap(TransferId.init(bytes:))
            return TempInfo(ref: TempRef(url.path), transferId: tid, lastWriteUtcMs: ms)
        }
    }

    public func commitTemp(_ ref: TempRef, dir: DirRef, finalName: String, mtime: Int64?) -> CommitResult {
        guard ensureDirectory(dir) else {
            return .failed(detail: "无法创建接收目录")
        }
        let srcURL = URL(fileURLWithPath: ref.value)
        let dstDir = resolveDirectory(dir)
        let dstURL = dstDir.appendingPathComponent(finalName)
        do {
            try FileManager.default.moveItem(at: srcURL, to: dstURL)
            if let mtime, mtime > 0 {
                let modDate = Date(timeIntervalSince1970: TimeInterval(mtime) / 1000.0)
                try? FileManager.default.setAttributes([.modificationDate: modDate], ofItemAtPath: dstURL.path)
            }
            let displayPath = "我的 iPhone / 局域网传输 / \(finalName)"
            return .ok(finalName: finalName, fullPath: displayPath, storedRef: StoredFileRef(dstURL.path))
        } catch {
            return .failed(detail: error.localizedDescription)
        }
    }

    public func deleteStoredFile(_ ref: StoredFileRef) -> Bool {
        do {
            try FileManager.default.removeItem(atPath: ref.value)
            return true
        } catch {
            return false
        }
    }

    private static func parseHex16(_ s: String) -> Data? {
        let chars = Array(s.utf8)
        guard chars.count == 32 else { return nil }
        func nib(_ c: UInt8) -> UInt8? {
            switch c {
            case 48...57: return c - 48
            case 97...102: return c - 97 + 10
            case 65...70: return c - 65 + 10
            default: return nil
            }
        }
        var bytes: [UInt8] = []
        bytes.reserveCapacity(16)
        var i = 0
        while i < 32 {
            guard let hi = nib(chars[i]), let lo = nib(chars[i + 1]) else { return nil }
            bytes.append((hi << 4) | lo)
            i += 2
        }
        return Data(bytes)
    }
}

/// iOS 源文件读取端口（支持 `fileImporter` 安全作用域 URL 与 `PhotosPicker` 导出文件）。
public final class IosSourceFiles: SourceFiles, @unchecked Sendable {
    public init() {}

    public func stat(_ ref: SourceRef) async -> Result<SourceMeta, IoError> {
        let url = resolveURL(ref.value)
        let accessed = url.startAccessingSecurityScopedResource()
        defer { if accessed { url.stopAccessingSecurityScopedResource() } }

        do {
            let values = try url.resourceValues(forKeys: [.isDirectoryKey, .fileSizeKey, .contentModificationDateKey, .contentTypeKey, .nameKey])
            let isDir = values.isDirectory ?? false
            let size = Int64(values.fileSize ?? 0)
            let mtime = values.contentModificationDate.map { Int64($0.timeIntervalSince1970 * 1000.0) }
            let mime = values.contentType?.preferredMIMEType ?? SourceMeta.octetStream
            let name = values.name ?? url.lastPathComponent
            return .success(SourceMeta(name: name, size: size, mtime: mtime, mime: mime, isDirectory: isDir))
        } catch {
            return .failure(IoError(error.localizedDescription))
        }
    }

    public func openForRead(_ ref: SourceRef, offset: Int64) async -> Result<any ChunkReader, IoError> {
        let url = resolveURL(ref.value)
        do {
            let reader = try FileHandleChunkReader(url: url, offset: offset, securityScoped: true)
            return .success(reader)
        } catch {
            return .failure(IoError(error.localizedDescription))
        }
    }

    private func resolveURL(_ raw: String) -> URL {
        if raw.hasPrefix("file://"), let url = URL(string: raw) {
            return url
        }
        return URL(fileURLWithPath: raw)
    }
}

private final class FileHandleTempWriter: TempWriter, @unchecked Sendable {
    private let lock = NSLock()
    private let handle: FileHandle
    private var closed = false

    init(url: URL, offset: Int64) throws {
        let h = try FileHandle(forWritingTo: url)
        try h.truncate(atOffset: UInt64(max(offset, 0)))
        try h.seekToEnd()
        self.handle = h
    }

    deinit {
        closeSync()
    }

    func write(_ data: Data) async -> IoResult {
        lock.withLock {
            guard !closed else { return .failed(detail: "文件句柄已关闭") }
            do {
                try handle.write(contentsOf: data)
                return .ok
            } catch {
                return .failed(detail: error.localizedDescription)
            }
        }
    }

    func flushSync() async -> IoResult {
        lock.withLock {
            guard !closed else { return .ok }
            do {
                try handle.synchronize()
                return .ok
            } catch {
                return .failed(detail: error.localizedDescription)
            }
        }
    }

    func close() async {
        closeSync()
    }

    private func closeSync() {
        lock.withLock {
            guard !closed else { return }
            closed = true
            try? handle.close()
        }
    }
}

private final class FileHandleChunkReader: ChunkReader, @unchecked Sendable {
    private let lock = NSLock()
    private let handle: FileHandle
    private let scopedURL: URL?
    private var closed = false

    init(url: URL, offset: Int64, securityScoped: Bool) throws {
        let accessed = securityScoped ? url.startAccessingSecurityScopedResource() : false
        do {
            let h = try FileHandle(forReadingFrom: url)
            if offset > 0 {
                try h.seek(toOffset: UInt64(offset))
            }
            self.handle = h
            self.scopedURL = accessed ? url : nil
        } catch {
            if accessed { url.stopAccessingSecurityScopedResource() }
            throw error
        }
    }

    deinit {
        closeSync()
    }

    func read(maxBytes: Int) async -> Result<Data?, IoError> {
        lock.withLock {
            guard !closed else { return .success(nil) }
            do {
                if let data = try handle.read(upToCount: maxBytes), !data.isEmpty {
                    return .success(data)
                }
                return .success(nil)
            } catch {
                return .failure(IoError(error.localizedDescription))
            }
        }
    }

    func close() async {
        closeSync()
    }

    private func closeSync() {
        lock.withLock {
            guard !closed else { return }
            closed = true
            try? handle.close()
            scopedURL?.stopAccessingSecurityScopedResource()
        }
    }
}
