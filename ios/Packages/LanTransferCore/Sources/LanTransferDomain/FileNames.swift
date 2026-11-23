import Foundation

private enum NameText {
    static let maxLength = 255
    static let reserved: Set<String> = {
        var set: Set<String> = ["CON", "PRN", "AUX", "NUL"]
        for i in 1...9 {
            set.insert("COM\(i)")
            set.insert("LPT\(i)")
        }
        return set
    }()

    static func length(_ s: String, profile: FsProfile) -> Int {
        profile == .windows ? s.utf16.count : s.utf8.count
    }

    static func fits(_ s: String, profile: FsProfile) -> Bool {
        length(s, profile: profile) <= maxLength
    }

    /// 扩展名 = 最后一个 "." 及其后的部分；"." 为首字符或不存在时为空。
    static func splitExtension(_ s: String) -> (stem: String, ext: String) {
        let scalars = Array(s.unicodeScalars)
        guard let lastDot = scalars.lastIndex(of: "."), lastDot > 0 else {
            return (s, "")
        }
        let stem = String(String.UnicodeScalarView(scalars[0..<lastDot]))
        let ext = String(String.UnicodeScalarView(scalars[lastDot..<scalars.count]))
        return (stem, ext)
    }

    static func dropLastScalar(_ s: String) -> String {
        var scalars = Array(s.unicodeScalars)
        if !scalars.isEmpty {
            scalars.removeLast()
        }
        return String(String.UnicodeScalarView(scalars))
    }

    static func isReserved(_ s: String) -> Bool {
        let headSub: Substring
        if let dot = s.firstIndex(of: ".") {
            headSub = s[..<dot]
        } else {
            headSub = s[...]
        }
        var head = String(headSub)
        while head.last == " " {
            head.removeLast()
        }
        guard head.unicodeScalars.allSatisfy({ $0.value < 0x80 }) else {
            return false
        }
        return reserved.contains(head.uppercased())
    }
}

/// 文件名清洗（需求 16.4–16.10，S0–S2 与 L1–L4）。
public enum FileNameSanitizer: Sendable {
    private static let illegalScalars: Set<UInt32> = Set("\\/:*?\"<>|".unicodeScalars.map(\.value))

    /// 接收 UTF-16 码元序列（支持测试向量中含孤立代理项的输入，执行 S0）。
    public static func sanitize(utf16Units: [UInt16], profile: FsProfile, now: LocalDateTime) -> String {
        var scalars = String.UnicodeScalarView()
        scalars.reserveCapacity(utf16Units.count)
        var i = 0
        while i < utf16Units.count {
            let u = utf16Units[i]
            if u >= 0xD800 && u <= 0xDBFF {
                if i + 1 < utf16Units.count {
                    let next = utf16Units[i + 1]
                    if next >= 0xDC00 && next <= 0xDFFF {
                        let cp = 0x10000 + ((UInt32(u) - 0xD800) << 10) + (UInt32(next) - 0xDC00)
                        if let scalar = UnicodeScalar(cp) {
                            scalars.append(scalar)
                        }
                        i += 2
                        continue
                    }
                }
                scalars.append("_")
                i += 1
            } else if u >= 0xDC00 && u <= 0xDFFF {
                scalars.append("_")
                i += 1
            } else {
                if let scalar = UnicodeScalar(u) {
                    scalars.append(scalar)
                }
                i += 1
            }
        }
        return sanitize(String(scalars), profile: profile, now: now)
    }

    public static func sanitize(_ raw: String, profile: FsProfile, now: LocalDateTime) -> String {
        // S1：只保留最后一个 "/" 或 "\" 之后的部分；"." 或 ".." 视为空
        let rawScalars = Array(raw.unicodeScalars)
        var cut = -1
        for idx in 0..<rawScalars.count {
            let v = rawScalars[idx].value
            if v == 0x2F || v == 0x5C { // '/' or '\'
                cut = idx
            }
        }
        var s1 = String(String.UnicodeScalarView(rawScalars[(cut + 1)..<rawScalars.count]))
        if s1 == "." || s1 == ".." {
            s1 = ""
        }

        // S2：非法字符与控制字符替换为 "_"；记下初始扩展名 ext0
        var s2Scalars = String.UnicodeScalarView()
        s2Scalars.reserveCapacity(s1.unicodeScalars.count)
        for sc in s1.unicodeScalars {
            let v = sc.value
            if illegalScalars.contains(v) || v <= 0x1F || v == 0x7F {
                s2Scalars.append("_")
            } else {
                s2Scalars.append(sc)
            }
        }
        let s2 = String(s2Scalars)
        let ext0 = NameText.splitExtension(s2).ext

        // L1–L4 循环至不动点
        var name = s2
        while true {
            let before = name
            if profile == .windows {
                var scalars = Array(name.unicodeScalars)
                while let last = scalars.last, last == " " || last == "." {
                    scalars.removeLast()
                }
                name = String(String.UnicodeScalarView(scalars))
            }
            if name.isEmpty || name == "." || name == ".." {
                name = fallback(now: now) + ext0
            }
            if profile == .windows && NameText.isReserved(name) {
                name = "_" + name
            }
            if !NameText.fits(name, profile: profile) {
                name = shortenOnce(name)
            }
            if name == before {
                return name
            }
        }
    }

    public static func fallback(now: LocalDateTime) -> String {
        "file_" + now.compactTimestamp
    }

    private static func shortenOnce(_ name: String) -> String {
        let (stem, ext) = NameText.splitExtension(name)
        if stem.unicodeScalars.count > 1 {
            return NameText.dropLastScalar(stem) + ext
        }
        return stem + NameText.dropLastScalar(ext)
    }
}

/// 重名冲突解析（需求 16.11）。
public enum NameCollisionResolver: Sendable {
    public static func resolve(
        _ name: String,
        profile: FsProfile,
        startAt: Int = 1,
        exists: (String) -> Bool
    ) -> String {
        precondition(startAt >= 1, "startAt must be >= 1")
        if !exists(name) {
            return name
        }
        let (stem, ext) = NameText.splitExtension(name)
        var n = startAt
        while true {
            let cand = candidate(stem: stem, ext: ext, n: n, profile: profile)
            if !exists(cand) {
                return cand
            }
            n += 1
        }
    }

    internal static func candidate(stem: String, ext: String, n: Int, profile: FsProfile) -> String {
        let suffix = " (\(n))"
        var s = stem
        while s.unicodeScalars.count > 1 && !NameText.fits(s + suffix + ext, profile: profile) {
            s = NameText.dropLastScalar(s)
        }
        var x = ext
        while !x.isEmpty && !NameText.fits(s + suffix + x, profile: profile) {
            x = NameText.dropLastScalar(x)
        }
        return s + suffix + x
    }
}
