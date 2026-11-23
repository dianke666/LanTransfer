import LanTransferDomain
import LanTransferProtocol
import SwiftUI

public extension Color {
    init(hex: UInt32, alpha: Double = 1.0) {
        let r = Double((hex >> 16) & 0xFF) / 255.0
        let g = Double((hex >> 8) & 0xFF) / 255.0
        let b = Double(hex & 0xFF) / 255.0
        self.init(.sRGB, red: r, green: g, blue: b, opacity: alpha)
    }
}

/// 跨端统一设计令牌（严格同步自 shared/design-tokens.json）。
public struct LanPalette: Sendable {
    public let canvas: Color
    public let sidebar: Color
    public let surface: Color
    public let surfaceElevated: Color
    public let surfaceSubtle: Color
    public let surfaceHover: Color
    public let surfaceSelected: Color
    public let border: Color
    public let borderStrong: Color
    public let textPrimary: Color
    public let textSecondary: Color
    public let textMuted: Color
    public let accent: Color
    public let accentHover: Color
    public let accentPressed: Color
    public let accentSoft: Color
    public let accentOn: Color
    public let teal: Color
    public let success: Color
    public let successSoft: Color
    public let warning: Color
    public let warningSoft: Color
    public let danger: Color
    public let dangerSoft: Color
    public let bubbleOutgoing: Color
    public let bubbleOutgoingText: Color
    public let bubbleIncoming: Color
    public let bubbleIncomingText: Color

    public static let light = LanPalette(
        canvas: Color(hex: 0xF4F6FA),
        sidebar: Color(hex: 0xEDF1F7),
        surface: Color(hex: 0xFFFFFF),
        surfaceElevated: Color(hex: 0xFFFFFF),
        surfaceSubtle: Color(hex: 0xF8FAFC),
        surfaceHover: Color(hex: 0xE8EEF7),
        surfaceSelected: Color(hex: 0xDBEAFE),
        border: Color(hex: 0xE2E8F0),
        borderStrong: Color(hex: 0xCBD5E1),
        textPrimary: Color(hex: 0x0F172A),
        textSecondary: Color(hex: 0x475569),
        textMuted: Color(hex: 0x64748B),
        accent: Color(hex: 0x2563EB),
        accentHover: Color(hex: 0x1D4ED8),
        accentPressed: Color(hex: 0x1E40AF),
        accentSoft: Color(hex: 0xEFF6FF),
        accentOn: Color(hex: 0xFFFFFF),
        teal: Color(hex: 0x0EA5E9),
        success: Color(hex: 0x059669),
        successSoft: Color(hex: 0xECFDF5),
        warning: Color(hex: 0xD97706),
        warningSoft: Color(hex: 0xFFFBEB),
        danger: Color(hex: 0xDC2626),
        dangerSoft: Color(hex: 0xFEF2F2),
        bubbleOutgoing: Color(hex: 0x2563EB),
        bubbleOutgoingText: Color(hex: 0xFFFFFF),
        bubbleIncoming: Color(hex: 0xFFFFFF),
        bubbleIncomingText: Color(hex: 0x0F172A)
    )

    public static let dark = LanPalette(
        canvas: Color(hex: 0x0B1120),
        sidebar: Color(hex: 0x0F172A),
        surface: Color(hex: 0x162032),
        surfaceElevated: Color(hex: 0x1E293B),
        surfaceSubtle: Color(hex: 0x111A2B),
        surfaceHover: Color(hex: 0x243248),
        surfaceSelected: Color(hex: 0x1E3A5F),
        border: Color(hex: 0x263449),
        borderStrong: Color(hex: 0x334155),
        textPrimary: Color(hex: 0xF1F5F9),
        textSecondary: Color(hex: 0x94A3B8),
        textMuted: Color(hex: 0x64748B),
        accent: Color(hex: 0x3B82F6),
        accentHover: Color(hex: 0x60A5FA),
        accentPressed: Color(hex: 0x2563EB),
        accentSoft: Color(hex: 0x172554),
        accentOn: Color(hex: 0xFFFFFF),
        teal: Color(hex: 0x38BDF8),
        success: Color(hex: 0x10B981),
        successSoft: Color(hex: 0x064E3B),
        warning: Color(hex: 0xF59E0B),
        warningSoft: Color(hex: 0x451A03),
        danger: Color(hex: 0xEF4444),
        dangerSoft: Color(hex: 0x450A0A),
        bubbleOutgoing: Color(hex: 0x2563EB),
        bubbleOutgoingText: Color(hex: 0xFFFFFF),
        bubbleIncoming: Color(hex: 0x1E293B),
        bubbleIncomingText: Color(hex: 0xF1F5F9)
    )

    public static func resolve(_ colorScheme: ColorScheme) -> LanPalette {
        colorScheme == .dark ? .dark : .light
    }
}

public enum LanRadii: Sendable {
    public static let card: CGFloat = 16
    public static let subCard: CGFloat = 12
    public static let control: CGFloat = 10
    public static let chip: CGFloat = 8
    public static let pill: CGFloat = 999
}

public enum LanSpacing: Sendable {
    public static let xs: CGFloat = 4
    public static let sm: CGFloat = 8
    public static let md: CGFloat = 12
    public static let lg: CGFloat = 16
    public static let xl: CGFloat = 24
}

public extension ThemeMode {
    var preferredColorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }

    var displayLabel: String {
        switch self {
        case .system: return UiText.themeSystem
        case .light: return UiText.themeLight
        case .dark: return UiText.themeDark
        }
    }
}

public extension DeviceType {
    var sfSymbolName: String {
        switch self {
        case .android: return "smartphone"
        case .windows: return "desktopcomputer"
        case .ios: return "iphone"
        }
    }

    var badgeColor: Color {
        switch self {
        case .android: return Color(hex: 0x10B981)
        case .windows: return Color(hex: 0x2563EB)
        case .ios: return Color(hex: 0x6366F1)
        }
    }

    var displayLabel: String {
        switch self {
        case .android: return UiText.deviceTypeAndroid
        case .windows: return UiText.deviceTypeWindows
        case .ios: return UiText.deviceTypeIos
        }
    }
}

public struct LanCardModifier: ViewModifier {
    @Environment(\.colorScheme) private var colorScheme
    private let padding: CGFloat

    public init(padding: CGFloat = LanSpacing.lg) {
        self.padding = padding
    }

    public func body(content: Content) -> some View {
        let palette = LanPalette.resolve(colorScheme)
        content
            .padding(padding)
            .background(
                RoundedRectangle(cornerRadius: LanRadii.card, style: .continuous)
                    .fill(palette.surface)
            )
            .overlay(
                RoundedRectangle(cornerRadius: LanRadii.card, style: .continuous)
                    .strokeBorder(palette.border, lineWidth: 1)
            )
    }
}

public extension View {
    func lanCard(padding: CGFloat = LanSpacing.lg) -> some View {
        modifier(LanCardModifier(padding: padding))
    }
}
