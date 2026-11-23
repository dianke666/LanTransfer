import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import SwiftUI

/// Tab 1：附近设备主页（英雄状态卡、可点击复制 IP 芯片、三联快捷按钮、局域网与已信任设备列表）。
public struct DevicesTabView: View {
    @Bindable public var controller: AppController
    @Environment(\.colorScheme) private var colorScheme

    public init(controller: AppController) {
        self.controller = controller
    }

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)
        let snap = controller.snapshot
        let devices = controller.mergedDevices

        ScrollView {
            VStack(alignment: .leading, spacing: LanSpacing.lg) {
                BannerStackView(controller: controller)

                heroCard(palette: palette, snap: snap)

                HStack {
                    Text(UiText.sectionNearbyDevices)
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(palette.textSecondary)
                    Spacer()
                    if !devices.isEmpty {
                        Text("\(devices.count) 台")
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(palette.textMuted)
                    }
                }
                .padding(.horizontal, 4)

                if devices.isEmpty {
                    emptyRadarCard(palette: palette)
                } else {
                    LazyVStack(spacing: LanSpacing.md) {
                        ForEach(devices) { device in
                            deviceCard(device, palette: palette)
                        }
                    }
                }
            }
            .padding(LanSpacing.lg)
        }
        .background(palette.canvas.ignoresSafeArea())
        .navigationTitle(UiText.appName)
        .navigationBarTitleDisplayMode(.large)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button {
                    controller.toggleQuickTheme(currentScheme: colorScheme)
                } label: {
                    Image(systemName: colorScheme == .dark ? "sun.max.fill" : "moon.stars.fill")
                        .foregroundStyle(palette.textSecondary)
                }
                .accessibilityLabel(UiText.sectionAppearance)
            }
        }
    }

    private func heroCard(palette: LanPalette, snap: EngineSnapshot) -> some View {
        let isOnline = !snap.localInterfaces.isEmpty && snap.listenPort != nil
        let port = snap.listenPort ?? ProtocolConstants.defaultPort

        return VStack(alignment: .leading, spacing: LanSpacing.md) {
            HStack(spacing: LanSpacing.sm) {
                Circle()
                    .fill(isOnline ? palette.success : palette.warning)
                    .frame(width: 9, height: 9)
                Text(controller.localDisplayName)
                    .font(.headline)
                    .foregroundStyle(palette.textPrimary)
                    .lineLimit(1)
                Spacer(minLength: 6)
                StatusPill(
                    text: "端到端加密",
                    systemImage: "checkmark.shield.fill",
                    foreground: palette.success,
                    background: palette.successSoft
                )
            }

            if snap.localInterfaces.isEmpty {
                Text(UiText.labelNotListening)
                    .font(.caption.monospaced())
                    .foregroundStyle(palette.textMuted)
            } else {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: LanSpacing.sm) {
                        ForEach(snap.localInterfaces, id: \.address) { iface in
                            let addrText = "\(iface.address.description):\(port)"
                            Button {
                                controller.copyAddress(addrText)
                            } label: {
                                HStack(spacing: 5) {
                                    Text(addrText)
                                        .font(.caption.monospaced())
                                    Image(systemName: "doc.on.doc")
                                        .font(.caption2)
                                }
                                .foregroundStyle(palette.textSecondary)
                                .padding(.horizontal, 10)
                                .padding(.vertical, 6)
                                .background(
                                    RoundedRectangle(cornerRadius: LanRadii.chip, style: .continuous)
                                        .fill(palette.surfaceSubtle)
                                )
                                .overlay(
                                    RoundedRectangle(cornerRadius: LanRadii.chip, style: .continuous)
                                        .strokeBorder(palette.border, lineWidth: 1)
                                )
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }

            HStack(spacing: LanSpacing.sm) {
                Button {
                    controller.showQrScanner = true
                } label: {
                    Label(UiText.btnScanQr, systemImage: "qrcode.viewfinder")
                        .font(.caption.weight(.semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                        .foregroundStyle(palette.accentOn)
                        .background(
                            RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                                .fill(palette.accent)
                        )
                }
                .buttonStyle(.plain)

                Button {
                    controller.showQrSheet = true
                } label: {
                    Label(UiText.btnQrPair, systemImage: "qrcode")
                        .font(.caption.weight(.semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                        .foregroundStyle(palette.accent)
                        .background(
                            RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                                .fill(palette.accentSoft)
                        )
                }
                .buttonStyle(.plain)

                Button {
                    controller.showManualConnect = true
                } label: {
                    Text(UiText.btnManualConnect)
                        .font(.caption.weight(.semibold))
                        .padding(.horizontal, 12)
                        .padding(.vertical, 10)
                        .foregroundStyle(palette.textPrimary)
                        .background(
                            RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                                .fill(palette.surface)
                        )
                        .overlay(
                            RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                                .strokeBorder(palette.border, lineWidth: 1)
                        )
                }
                .buttonStyle(.plain)
            }
        }
        .lanCard()
    }

    private func deviceCard(_ device: DeviceListItem, palette: LanPalette) -> some View {
        Button {
            controller.tapDevice(device)
        } label: {
            HStack(spacing: LanSpacing.md) {
                PlatformBadgeView(deviceType: device.deviceType, size: 44)

                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 6) {
                        Text(device.displayName)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(palette.textPrimary)
                            .lineLimit(1)
                        Spacer(minLength: 4)
                        deviceStatusPill(device, palette: palette)
                    }

                    let addrSuffix: String = {
                        if let ip = device.ipText, let port = device.port {
                            return " · \(ip):\(port)"
                        }
                        if let ip = device.ipText {
                            return " · \(ip)"
                        }
                        return ""
                    }()
                    Text("\(device.deviceType.displayLabel)\(addrSuffix)")
                        .font(.caption.monospaced())
                        .foregroundStyle(palette.textMuted)
                        .lineLimit(1)
                }
            }
            .lanCard(padding: 14)
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private func deviceStatusPill(_ device: DeviceListItem, palette: LanPalette) -> some View {
        if device.isConnected {
            StatusPill(text: UiText.labelConnectedBadge, foreground: palette.success, background: palette.successSoft)
        } else if device.isTrusted {
            StatusPill(text: UiText.labelTrustedBadge, foreground: palette.accent, background: palette.accentSoft)
        } else {
            StatusPill(text: UiText.labelUntrustedBadge, foreground: palette.textSecondary, background: palette.surfaceSubtle)
        }
    }

    private func emptyRadarCard(palette: LanPalette) -> some View {
        VStack(spacing: LanSpacing.md) {
            ZStack {
                Circle()
                    .fill(palette.accentSoft)
                    .frame(width: 64, height: 64)
                Image(systemName: "dot.radiowaves.left.and.right")
                    .font(.system(size: 28))
                    .foregroundStyle(palette.accent)
            }
            Text(UiText.labelScanningDevices)
                .font(.subheadline.weight(.medium))
                .foregroundStyle(palette.textPrimary)
            Text(UiText.labelEncryptedHint)
                .font(.caption)
                .foregroundStyle(palette.textMuted)
        }
        .frame(maxWidth: .infinity)
        .lanCard(padding: LanSpacing.xl)
    }
}
