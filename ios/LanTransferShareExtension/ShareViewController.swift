import Foundation
import LanTransferDomain
import LanTransferProtocol
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// iOS 系统分享扩展入口：从 App Group 只读加载已信任设备列表，复制附件至共享暂存目录并通过 `lantransfer://share` 唤起主应用。
@MainActor
final class ShareViewController: UIViewController {
    private var extractedText: String?
    private var extractedFileURLs: [URL] = []

    override func viewDidLoad() {
        super.viewDidLoad()

        let trusted = SqliteDatabase.readTrustedDevicesSnapshot()
        let hosting = UIHostingController(
            rootView: ShareExtensionPickerView(
                trustedDevices: trusted,
                loadSummary: { [weak self] in
                    await self?.extractSharedItems() ?? UiText.shareTextSummary
                },
                onSelectDevice: { [weak self] device in
                    await self?.handoverAndOpenHost(to: device)
                },
                onCancel: { [weak self] in
                    self?.extensionContext?.completeRequest(returningItems: nil)
                }
            )
        )
        addChild(hosting)
        hosting.view.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(hosting.view)
        NSLayoutConstraint.activate([
            hosting.view.topAnchor.constraint(equalTo: view.topAnchor),
            hosting.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            hosting.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            hosting.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
        ])
        hosting.didMove(toParent: self)
    }

    private func extractSharedItems() async -> String {
        guard let inputItems = extensionContext?.inputItems as? [NSExtensionItem] else {
            return UiText.shareTextSummary
        }
        var files: [URL] = []
        var textParts: [String] = []

        for item in inputItems {
            for provider in item.attachments ?? [] {
                if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
                    if let url = await loadURL(provider: provider, typeId: UTType.fileURL.identifier) {
                        files.append(url)
                    }
                } else if provider.hasItemConformingToTypeIdentifier(UTType.movie.identifier) {
                    if let url = await loadFileRepresentation(provider: provider, typeId: UTType.movie.identifier) {
                        files.append(url)
                    }
                } else if provider.hasItemConformingToTypeIdentifier(UTType.image.identifier) {
                    if let url = await loadFileRepresentation(provider: provider, typeId: UTType.image.identifier) {
                        files.append(url)
                    }
                } else if provider.hasItemConformingToTypeIdentifier(UTType.plainText.identifier) {
                    if let str = await loadText(provider: provider) {
                        textParts.append(str)
                    }
                } else if provider.hasItemConformingToTypeIdentifier(UTType.url.identifier) {
                    if let url = await loadURL(provider: provider, typeId: UTType.url.identifier) {
                        textParts.append(url.absoluteString)
                    }
                }
            }
        }

        self.extractedFileURLs = files
        if !textParts.isEmpty {
            self.extractedText = textParts.joined(separator: "\n")
        }
        if !files.isEmpty {
            return UiText.shareFilesSummary(count: files.count)
        }
        return UiText.shareTextSummary
    }

    private func handoverAndOpenHost(to device: TrustedDevice) async {
        let batchId = UUID().uuidString
        let batchDir = AppGroupConfig.shareInboxDirectoryURL().appendingPathComponent(batchId, isDirectory: true)
        try? FileManager.default.createDirectory(at: batchDir, withIntermediateDirectories: true)

        var stagedPaths: [String] = []
        for src in extractedFileURLs {
            let dst = batchDir.appendingPathComponent(src.lastPathComponent)
            try? FileManager.default.removeItem(at: dst)
            if (try? FileManager.default.copyItem(at: src, to: dst)) != nil {
                stagedPaths.append(dst.path)
            }
        }

        let manifest = ShareHandoverManifest(
            batchId: batchId,
            peerHex: device.deviceId.hexString,
            text: stagedPaths.isEmpty ? extractedText : nil,
            filePaths: stagedPaths,
            createdAtUtcMs: Int64(Date().timeIntervalSince1970 * 1000.0)
        )
        let manifestURL = batchDir.appendingPathComponent("manifest.json")
        if let encoded = try? JSONEncoder().encode(manifest) {
            try? encoded.write(to: manifestURL, options: .atomic)
        }

        if let openURL = URL(string: "\(AppGroupConfig.urlScheme)://share?batch=\(batchId)&peer=\(device.deviceId.hexString)") {
            openHostApp(url: openURL)
        }
        extensionContext?.completeRequest(returningItems: nil)
    }

    private func openHostApp(url: URL) {
        // 遵循 App Extension 规范，沿响应链查找支持 open(_:options:completionHandler:) 的 UIResponder
        var responder: UIResponder? = self
        let selector = NSSelectorFromString("openURL:")
        while let current = responder {
            if current.responds(to: selector) {
                current.perform(selector, with: url)
                return
            }
            responder = current.next
        }
    }

    private func loadURL(provider: NSItemProvider, typeId: String) async -> URL? {
        await withCheckedContinuation { cont in
            provider.loadItem(forTypeIdentifier: typeId, options: nil) { item, _ in
                cont.resume(returning: item as? URL)
            }
        }
    }

    private func loadText(provider: NSItemProvider) async -> String? {
        await withCheckedContinuation { cont in
            provider.loadItem(forTypeIdentifier: UTType.plainText.identifier, options: nil) { item, _ in
                cont.resume(returning: item as? String)
            }
        }
    }

    private func loadFileRepresentation(provider: NSItemProvider, typeId: String) async -> URL? {
        await withCheckedContinuation { cont in
            provider.loadFileRepresentation(forTypeIdentifier: typeId) { url, _ in
                guard let url else {
                    cont.resume(returning: nil)
                    return
                }
                let tempDst = FileManager.default.temporaryDirectory
                    .appendingPathComponent("\(UUID().uuidString)-\(url.lastPathComponent)")
                do {
                    try FileManager.default.copyItem(at: url, to: tempDst)
                    cont.resume(returning: tempDst)
                } catch {
                    cont.resume(returning: nil)
                }
            }
        }
    }
}

private struct ShareExtensionPickerView: View {
    let trustedDevices: [TrustedDevice]
    let loadSummary: @MainActor () async -> String
    let onSelectDevice: @MainActor (TrustedDevice) async -> Void
    let onCancel: @MainActor () -> Void

    @State private var summary: String = UiText.shareTextSummary
    @State private var sending = false
    @Environment(\.colorScheme) private var colorScheme

    private var palette: LanPalette {
        LanPalette.resolve(colorScheme)
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: LanSpacing.md) {
                    HStack(spacing: LanSpacing.md) {
                        Image(systemName: "paperplane.circle.fill")
                            .font(.title2)
                            .foregroundStyle(palette.accent)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(summary)
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(palette.textPrimary)
                            Text(UiText.labelEncryptedHint)
                                .font(.caption)
                                .foregroundStyle(palette.textSecondary)
                        }
                        Spacer()
                    }
                    .padding(LanSpacing.md)
                    .background(
                        RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                            .fill(palette.accentSoft)
                    )

                    if trustedDevices.isEmpty {
                        VStack(spacing: LanSpacing.sm) {
                            Image(systemName: "laptopcomputer.and.iphone")
                                .font(.largeTitle)
                                .foregroundStyle(palette.textMuted)
                            Text(UiText.labelNoTrustedDevices)
                                .font(.footnote)
                                .multilineTextAlignment(.center)
                                .foregroundStyle(palette.textSecondary)
                        }
                        .frame(maxWidth: .infinity)
                        .lanCard(padding: LanSpacing.xl)
                    } else {
                        ForEach(trustedDevices, id: \.deviceId) { device in
                            Button {
                                guard !sending else { return }
                                sending = true
                                Task { await onSelectDevice(device) }
                            } label: {
                                HStack(spacing: LanSpacing.md) {
                                    ZStack {
                                        RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                                            .fill(device.deviceType.badgeColor.opacity(0.14))
                                            .frame(width: 40, height: 40)
                                        Image(systemName: device.deviceType.sfSymbolName)
                                            .foregroundStyle(device.deviceType.badgeColor)
                                    }
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(device.displayName)
                                            .font(.body.weight(.semibold))
                                            .foregroundStyle(palette.textPrimary)
                                        Text(device.deviceType.displayLabel)
                                            .font(.caption)
                                            .foregroundStyle(palette.textMuted)
                                    }
                                    Spacer()
                                    if sending {
                                        ProgressView()
                                    } else {
                                        Image(systemName: "chevron.right")
                                            .font(.footnote.weight(.semibold))
                                            .foregroundStyle(palette.textMuted)
                                    }
                                }
                                .lanCard(padding: LanSpacing.md)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
                .padding(LanSpacing.lg)
            }
            .background(palette.canvas.ignoresSafeArea())
            .navigationTitle(UiText.shareChooseDevice)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(UiText.btnCancel, action: onCancel)
                }
            }
            .task {
                summary = await loadSummary()
            }
        }
        .environment(\.locale, Locale(identifier: "zh-Hans-CN"))
    }
}
