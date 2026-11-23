import Foundation
import LanTransferProtocol

public enum OutgoingCheck: Hashable, Sendable {
    case empty
    case tooLong(utf8Bytes: Int)
    case ok(utf8: Data)
}

public enum TextCollapse: Hashable, Sendable {
    case full
    /// `prefixUtf16End` 为 UTF-16 码元结束位置，直接兼容 UITextView / NSString。
    case collapsed(prefixUtf16End: Int)
}

/// 文字规则（需求 11、12、21.6，设计 C3.3）。
public enum TextRules: Sendable {
    public static let collapseLines = 20
    public static let collapseCodePoints = 1000
    public static let previewCodePoints = 100

    public static func checkOutgoing(_ text: String) -> OutgoingCheck {
        if text.isEmpty {
            return .empty
        }
        let utf8 = StrictUtf8.encode(text)
        if utf8.count > ProtocolConstants.maxTextBytes {
            return .tooLong(utf8Bytes: utf8.count)
        }
        return .ok(utf8: utf8)
    }

    /// LF 数 + 其后非 LF 的 CR 数 + 1（11.10）。
    public static func lineCount(_ text: String) -> Int {
        let units = Array(text.utf16)
        var lines = 1
        for i in 0..<units.count {
            let c = units[i]
            if c == 0x0A || (c == 0x0D && (i + 1 >= units.count || units[i + 1] != 0x0A)) {
                lines += 1
            }
        }
        return lines
    }

    public static func collapse(_ text: String) -> TextCollapse {
        let units = Array(text.utf16)
        if lineCount(text) <= collapseLines && text.unicodeScalars.count <= collapseCodePoints {
            return .full
        }
        var best = 0
        var lines = 1
        var cps = 0
        var i = 0
        while i < units.count {
            let c = units[i]
            let isPair = (c >= 0xD800 && c <= 0xDBFF) && (i + 1 < units.count) && (units[i + 1] >= 0xDC00 && units[i + 1] <= 0xDFFF)
            let width = isPair ? 2 : 1
            let addLine: Int
            if c == 0x0A && i > 0 && units[i - 1] == 0x0D {
                addLine = 0
            } else if c == 0x0A || c == 0x0D {
                addLine = 1
            } else {
                addLine = 0
            }
            let newLines = lines + addLine
            let newCps = cps + 1
            if newLines > collapseLines || newCps > collapseCodePoints {
                break
            }
            lines = newLines
            cps = newCps
            i += width
            best = i
        }
        // 不切开 CRLF
        if best > 0 && best < units.count && units[best - 1] == 0x0D && units[best] == 0x0A {
            best -= 1
        }
        return .collapsed(prefixUtf16End: best)
    }

    public static func notificationPreview(_ text: String) -> String {
        let scalars = text.unicodeScalars
        if scalars.count <= previewCodePoints {
            return text
        }
        return String(String.UnicodeScalarView(scalars.prefix(previewCodePoints))) + "…"
    }

    public static func containsNul(_ text: String) -> Bool {
        text.unicodeScalars.contains(where: { $0.value == 0 })
    }
}

/// 显示文本与“显示 UTF-16 码元 → 原文 UTF-16 码元”映射（直接兼容 iOS UITextView `NSRange` 无损选区复制）。
public struct DisplayText: Hashable, Sendable {
    public let display: String
    private let unitOfDisplay: [Int]
    private let unitOrigStart: [Int]
    private let unitOrigEnd: [Int]
    private let originalUtf16Length: Int

    internal init(
        display: String,
        unitOfDisplay: [Int],
        unitOrigStart: [Int],
        unitOrigEnd: [Int],
        originalUtf16Length: Int
    ) {
        self.display = display
        self.unitOfDisplay = unitOfDisplay
        self.unitOrigStart = unitOrigStart
        self.unitOrigEnd = unitOrigEnd
        self.originalUtf16Length = originalUtf16Length
    }

    /// 显示选区 UTF-16 范围 `[selStart, selEnd)` 对应的原文 UTF-16 范围 `(start, end)`。
    public func originalRange(selStart: Int, selEnd: Int) -> (start: Int, end: Int) {
        let displayLen = unitOfDisplay.count
        let clampedStart = max(0, min(selStart, displayLen))
        let clampedEnd = max(clampedStart, min(selEnd, displayLen))
        if clampedStart == clampedEnd {
            let p = clampedStart == displayLen ? originalUtf16Length : unitOrigStart[unitOfDisplay[clampedStart]]
            return (p, p)
        }
        return (unitOrigStart[unitOfDisplay[clampedStart]], unitOrigEnd[unitOfDisplay[clampedEnd - 1]])
    }

    /// 根据显示选区（UTF-16 偏移）直接截取原始字符串片段。
    public func extractOriginal(from original: String, selStart: Int, selEnd: Int) -> String {
        let (start, end) = originalRange(selStart: selStart, selEnd: selEnd)
        let ns = original as NSString
        guard start >= 0, end >= start, end <= ns.length else { return "" }
        return ns.substring(with: NSRange(location: start, length: end - start))
    }
}

/// 文字显示映射（需求 12.8、13.3、13.4）：CRLF → LF，C0 控制符 → U+2400+c，DEL → ␡，C1 → ▯。
public enum TextDisplayMapper: Sendable {
    public static func map(_ original: String) -> DisplayText {
        let origUnits = Array(original.utf16)
        var displayUnits: [UInt16] = []
        displayUnits.reserveCapacity(origUnits.count)
        var unitOfDisplay: [Int] = []
        unitOfDisplay.reserveCapacity(origUnits.count)
        var starts: [Int] = []
        var ends: [Int] = []

        func appendUnit(origStart: Int, origEnd: Int, shown: [UInt16]) {
            let u = starts.count
            starts.append(origStart)
            ends.append(origEnd)
            displayUnits.append(contentsOf: shown)
            for _ in 0..<shown.count {
                unitOfDisplay.append(u)
            }
        }

        var i = 0
        while i < origUnits.count {
            let c = origUnits[i]
            if c == 0x0D && i + 1 < origUnits.count && origUnits[i + 1] == 0x0A {
                appendUnit(origStart: i, origEnd: i + 2, shown: [0x0A])
                i += 2
            } else if c >= 0xD800 && c <= 0xDBFF && i + 1 < origUnits.count && origUnits[i + 1] >= 0xDC00 && origUnits[i + 1] <= 0xDFFF {
                appendUnit(origStart: i, origEnd: i + 2, shown: [c, origUnits[i + 1]])
                i += 2
            } else {
                let shownUnit: UInt16
                switch c {
                case 0x0D, 0x0A:
                    shownUnit = 0x0A
                case 0x09:
                    shownUnit = 0x09
                case 0x00...0x1F:
                    shownUnit = 0x2400 + c
                case 0x7F:
                    shownUnit = 0x2421
                case 0x80...0x9F:
                    shownUnit = 0x25AF // ▯
                case 0xD800...0xDFFF:
                    shownUnit = 0xFFFD
                default:
                    shownUnit = c
                }
                appendUnit(origStart: i, origEnd: i + 1, shown: [shownUnit])
                i += 1
            }
        }

        let display = String(decoding: displayUnits, as: UTF16.self)
        return DisplayText(
            display: display,
            unitOfDisplay: unitOfDisplay,
            unitOrigStart: starts,
            unitOrigEnd: ends,
            originalUtf16Length: origUnits.count
        )
    }
}
