import Foundation

/// 文件大小格式化（1024 进制，B 以外截断到 1 位小数，区域设置无关）。
public enum SizeFormatter: Sendable {
    private static let units = ["B", "KB", "MB", "GB", "TB"]

    public static func format(_ bytes: Int64) -> String {
        precondition(bytes >= 0 && bytes <= (1 << 59), "bytes out of range")
        var k = 0
        while k < units.count - 1 && bytes >= (1 << (10 * (k + 1))) {
            k += 1
        }
        if k == 0 {
            return "\(bytes) B"
        }
        let divisor: Int64 = 1 << (10 * k)
        let tenths = (bytes / divisor) * 10 + ((bytes % divisor) * 10) / divisor
        return "\(tenths / 10).\(tenths % 10) \(units[k])"
    }
}

/// 进度快照（需求 18.1）。
public struct TransferProgress: Hashable, Sendable {
    public let percent: Int
    public let transferred: Int64
    public let total: Int64
    public let bytesPerSec: Int64
    public let etaSec: Int64?

    public init(percent: Int, transferred: Int64, total: Int64, bytesPerSec: Int64, etaSec: Int64?) {
        self.percent = percent
        self.transferred = transferred
        self.total = total
        self.bytesPerSec = bytesPerSec
        self.etaSec = etaSec
    }
}

/// 滑动窗口速率与 ETA 计算器（默认 3 秒窗口）。
public struct ProgressCalculator: Sendable {
    private let windowMs: Int64
    private var samples: [(time: Int64, value: Int64)] = []
    private var resetAt: Int64 = 0

    public init(windowMs: Int64 = 3_000) {
        self.windowMs = windowMs
    }

    public mutating func reset(nowMono: Int64, transferred: Int64) {
        samples = [(nowMono, transferred)]
        resetAt = nowMono
    }

    public mutating func sample(nowMono: Int64, transferred: Int64) {
        if samples.isEmpty {
            reset(nowMono: nowMono, transferred: transferred)
            return
        }
        samples.append((nowMono, transferred))
        let start = max(nowMono - windowMs, resetAt)
        var drop = 0
        while samples.count - drop >= 2 && samples[drop + 1].time <= start {
            drop += 1
        }
        if drop > 0 {
            samples.removeFirst(drop)
        }
    }

    public func snapshot(nowMono: Int64, total: Int64, completed: Bool) -> TransferProgress {
        let current = samples.last?.value ?? 0
        let start = max(nowMono - windowMs, resetAt)
        var base = samples.first?.value ?? current
        for s in samples where s.time <= start {
            base = s.value
        }
        let elapsed = nowMono - start
        let rate: Int64 = (elapsed > 0 && current > base) ? Self.mulDiv(current - base, 1000, elapsed) : 0
        let remaining = max(total - current, 0)
        let eta: Int64? = rate == 0 ? nil : (remaining / rate) + (remaining % rate == 0 ? 0 : 1)
        let clamped = max(0, min(current, total))
        let percent = total <= 0 ? (completed ? 100 : 0) : Int(Self.mulDiv(clamped, 100, total))
        return TransferProgress(percent: percent, transferred: current, total: total, bytesPerSec: rate, etaSec: eta)
    }

    private static func mulDiv(_ a: Int64, _ b: Int64, _ c: Int64) -> Int64 {
        let prod = UInt64(a).multipliedFullWidth(by: UInt64(b))
        let (quotient, _) = UInt64(c).dividingFullWidth(prod)
        return Int64(quotient)
    }
}
