import LanTransferDomain
import LanTransferProtocol
import SwiftUI

/// Tab 3：设置页（原生 Inset Grouped 分组：外观主题、本机信息、传输与存储、后台与权限、已信任设备）。
public struct SettingsTabView: View {
    public let controller: AppController

    @State private var nameDraft: String = ""
    @State private var nameError: String?
    @State private var peerToUnpair: TrustedDevice?
    @Environment(\.colorScheme) private var colorScheme

    public init(controller: AppController) {
        self.controller = controller
    }

    private var themeBinding: Binding<ThemeMode> {
        Binding(
            get: { controller.snapshot.settings.themeMode },
            set: { controller.setThemeMode($0) }
        )
    }

    private var autoCopyBinding: Binding<Bool> {
        Binding(
            get: { controller.snapshot.settings.autoCopyText },
            set: { controller.setAutoCopy($0) }
        )
    }

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)

        List {
            Section(UiText.sectionAppearance) {
                Picker(UiText.sectionAppearance, selection: themeBinding) {
                    ForEach(ThemeMode.allCases, id: \.self) { mode in
                        Text(mode.displayLabel).tag(mode)
                    }
                }
                .pickerStyle(.segmented)
            }

            Section(UiText.sectionLocalDevice) {
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        TextField(UiText.labelDeviceName, text: $nameDraft)
                            .textInputAutocapitalization(.never)
                        Button(UiText.btnSave) {
                            Task {
                                let res = await controller.saveDisplayName(nameDraft)
                                switch res {
                                case .ok(let cleaned, _):
                                    nameDraft = cleaned
                                    nameError = nil
                                case .empty, .tooLong, .hasControl:
                                    nameError = UiText.errorNameInvalid
                                }
                            }
                        }
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(palette.accent)
                    }
                    if let nameError {
                        Text(nameError)
                            .font(.caption)
                            .foregroundStyle(palette.danger)
                    }
                }

                HStack {
                    Text("设备标识摘要")
                        .foregroundStyle(palette.textSecondary)
                    Spacer()
                    Text(controller.localDeviceIdSummary)
                        .font(.footnote.monospaced())
                        .foregroundStyle(palette.textMuted)
                }
            }

            Section(UiText.sectionTransferPrefs) {
                VStack(alignment: .leading, spacing: 6) {
                    Text(UiText.labelReceiveDir)
                        .font(.subheadline.weight(.medium))
                    Text(UiText.iosLabelFilesAppDir)
                        .font(.caption)
                        .foregroundStyle(palette.textSecondary)
                    Button(UiText.iosBtnOpenFilesApp) {
                        controller.openReceiveDirectory()
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(palette.accent)
                    .padding(.top, 2)
                }

                Toggle(isOn: autoCopyBinding) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(UiText.labelAutoCopy)
                        Text(UiText.labelAutoCopyDesc)
                            .font(.caption)
                            .foregroundStyle(palette.textMuted)
                    }
                }
                .tint(palette.success)
            }

            Section(UiText.iosSectionBgPermissions) {
                VStack(alignment: .leading, spacing: 5) {
                    Text(UiText.iosBgNoticeTitle)
                        .font(.subheadline.weight(.semibold))
                    Text(UiText.iosBgNoticeDesc)
                        .font(.caption)
                        .foregroundStyle(palette.textSecondary)
                }

                Button {
                    controller.openSystemAppSettings()
                } label: {
                    HStack {
                        Text(UiText.iosBtnSystemSettings)
                            .foregroundStyle(palette.accent)
                        Spacer()
                        Image(systemName: "arrow.up.forward.app")
                            .font(.caption)
                            .foregroundStyle(palette.textMuted)
                    }
                }
            }

            Section(UiText.sectionTrustedDevices) {
                if controller.snapshot.trustedDevices.isEmpty {
                    Text(UiText.labelNoTrustedDevices)
                        .font(.footnote)
                        .foregroundStyle(palette.textMuted)
                } else {
                    ForEach(controller.snapshot.trustedDevices, id: \.deviceId) { device in
                        HStack(spacing: LanSpacing.md) {
                            PlatformBadgeView(deviceType: device.deviceType, size: 36)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(device.displayName)
                                    .font(.subheadline.weight(.semibold))
                                Text("\(device.deviceType.displayLabel) · \(UiText.labelAutoAccept)")
                                    .font(.caption)
                                    .foregroundStyle(palette.textMuted)
                            }
                            Spacer()
                            Toggle("", isOn: Binding(
                                get: { device.autoAccept },
                                set: { controller.setAutoAccept(peer: device.deviceId, enabled: $0) }
                            ))
                            .labelsHidden()
                            .tint(palette.success)
                        }
                        .swipeActions(edge: .trailing) {
                            Button(UiText.btnUnpair, role: .destructive) {
                                peerToUnpair = device
                            }
                        }
                        .contextMenu {
                            Button(UiText.btnUnpair, role: .destructive) {
                                peerToUnpair = device
                            }
                        }
                    }
                }
            }
        }
        .listStyle(.insetGrouped)
        .scrollContentBackground(.hidden)
        .background(palette.canvas.ignoresSafeArea())
        .navigationTitle(UiText.navSettings)
        .navigationBarTitleDisplayMode(.large)
        .onAppear {
            nameDraft = controller.localDisplayName
        }
        .onChange(of: controller.localDisplayName) { _, updated in
            if nameDraft.isEmpty {
                nameDraft = updated
            }
        }
        .alert(
            UiText.dialogUnpairTitle,
            isPresented: Binding(
                get: { peerToUnpair != nil },
                set: { if !$0 { peerToUnpair = nil } }
            )
        ) {
            Button(UiText.btnUnpair, role: .destructive) {
                if let target = peerToUnpair {
                    controller.unpair(target.deviceId)
                }
                peerToUnpair = nil
            }
            Button(UiText.btnCancel, role: .cancel) {
                peerToUnpair = nil
            }
        } message: {
            if let target = peerToUnpair {
                Text(UiText.dialogUnpairDesc(peer: target.displayName))
            }
        }
    }
}
