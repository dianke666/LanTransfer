import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import SwiftUI

/// 三端设备平台连续圆角彩色徽章（Android 翡翠绿 / Windows 皇家蓝 / iOS 靛紫）。
public struct PlatformBadgeView: View {
    public let deviceType: DeviceType
    public var size: CGFloat = 44

    public init(deviceType: DeviceType, size: CGFloat = 44) {
        self.deviceType = deviceType
        self.size = size
    }

    public var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.27, style: .continuous)
                .fill(deviceType.badgeColor.opacity(0.14))
                .frame(width: size, height: size)
            Image(systemName: deviceType.sfSymbolName)
                .font(.system(size: size * 0.44, weight: .semibold))
                .foregroundStyle(deviceType.badgeColor)
        }
    }
}

/// 语义状态胶囊。
public struct StatusPill: View {
    public let text: String
    public var systemImage: String?
    public let foreground: Color
    public let background: Color

    public init(text: String, systemImage: String? = nil, foreground: Color, background: Color) {
        self.text = text
        self.systemImage = systemImage
        self.foreground = foreground
        self.background = background
    }

    public var body: some View {
        HStack(spacing: 4) {
            if let systemImage {
                Image(systemName: systemImage)
                    .font(.caption2.weight(.bold))
            }
            Text(text)
                .font(.caption2.weight(.semibold))
        }
        .foregroundStyle(foreground)
        .padding(.horizontal, 9)
        .padding(.vertical, 4)
        .background(Capsule().fill(background))
    }
}

/// 顶部柔和 Toast 提示胶囊。
public struct ToastOverlayView: View {
    public let message: String?
    @Environment(\.colorScheme) private var colorScheme

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)
        VStack {
            if let message {
                Text(message)
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(palette.canvas)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 9)
                    .background(Capsule().fill(palette.textPrimary))
                    .shadow(color: Color.black.opacity(0.18), radius: 10, y: 4)
                    .padding(.top, 8)
                    .transition(.move(edge: .top).combined(with: .opacity))
            }
            Spacer()
        }
        .animation(.spring(response: 0.28, dampingFraction: 0.82), value: message)
        .allowsHitTesting(false)
    }
}

/// 条件式状态横幅栈（无网 / 身份异常 / 监听失败 / 自动发现不可用 / 通知关闭）。
public struct BannerStackView: View {
    public let controller: AppController
    @Environment(\.colorScheme) private var colorScheme

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)
        let snap = controller.snapshot

        VStack(spacing: LanSpacing.sm) {
            if case .saveFailed = snap.identityLoad {
                bannerRow(
                    text: UiText.bannerIdentitySaveFailed,
                    foreground: palette.danger,
                    background: palette.dangerSoft,
                    actionTitle: UiText.btnRetry
                ) {
                    Task { await controller.engine.start() }
                }
            } else if case .resetCorrupted = snap.identityLoad {
                bannerRow(
                    text: UiText.bannerIdentityReset,
                    foreground: palette.warning,
                    background: palette.warningSoft,
                    actionTitle: nil,
                    action: nil
                )
            }

            if snap.localInterfaces.isEmpty {
                bannerRow(
                    text: UiText.bannerNoNetwork,
                    foreground: palette.warning,
                    background: palette.warningSoft,
                    actionTitle: nil,
                    action: nil
                )
            } else if snap.listenFailed {
                bannerRow(
                    text: UiText.bannerListenFailed,
                    foreground: palette.danger,
                    background: palette.dangerSoft,
                    actionTitle: UiText.btnRetry
                ) {
                    Task { await controller.engine.startNetworking() }
                }
            } else if case .unavailable = snap.discoveryStatus {
                bannerRow(
                    text: UiText.bannerDiscoveryUnavailable,
                    foreground: palette.warning,
                    background: palette.warningSoft,
                    actionTitle: UiText.btnOpenAppSettings
                ) {
                    controller.openSystemAppSettings()
                }
            }

            if !snap.notificationsEnabled {
                bannerRow(
                    text: UiText.bannerNotificationsOff,
                    foreground: palette.textSecondary,
                    background: palette.surfaceSubtle,
                    actionTitle: UiText.btnOpenNotificationSettings
                ) {
                    controller.openSystemAppSettings()
                }
            }
        }
    }

    @ViewBuilder
    private func bannerRow(
        text: String,
        foreground: Color,
        background: Color,
        actionTitle: String?,
        action: (() -> Void)?
    ) -> some View {
        HStack(spacing: LanSpacing.sm) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.footnote)
                .foregroundStyle(foreground)
            Text(text)
                .font(.caption)
                .foregroundStyle(foreground)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
            if let actionTitle, let action {
                Button(actionTitle, action: action)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(foreground)
            }
        }
        .padding(.horizontal, LanSpacing.md)
        .padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                .fill(background)
        )
    }
}
