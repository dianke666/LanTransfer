import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import PhotosUI
import SwiftUI
import UIKit
import UniformTypeIdentifiers

/// 支持流式零内存拷贝导出系统相册原图/原视频的 Transferable 包装。
private struct StagedPhotoFile: Transferable {
    let url: URL

    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .item) { received in
            let dst = FileManager.default.temporaryDirectory
                .appendingPathComponent("\(UUID().uuidString)-\(received.file.lastPathComponent)")
            try FileManager.default.copyItem(at: received.file, to: dst)
            return Self(url: dst)
        }
    }
}

private enum ConversationTimelineItem: Identifiable {
    case text(TextRecord)
    case task(TransferTask)

    var id: String {
        switch self {
        case .text(let r): return "t-\(r.seq)-\(r.messageId.hexString)"
        case .task(let t): return "f-\(t.transferId.hexString)"
        }
    }

    var sortUtcMs: Int64 {
        switch self {
        case .text(let r): return r.recordedAt.utcMs
        case .task(let t): return t.recordedAt.utcMs
        }
    }

    var sortSeq: Int64 {
        switch self {
        case .text(let r): return r.seq
        case .task(let t): return t.files.first?.seq ?? 0
        }
    }
}

/// 对端会话与传输页：无损文字气泡（`LosslessTextView` + 折叠展开）、多文件进度卡、待发送芯片列与毛玻璃底部工具栏。
public struct ConversationView: View {
    public let peerId: DeviceId
    @Bindable public var controller: AppController

    @State private var expandedTextIds = Set<MessageId>()
    @State private var showFileImporter = false
    @State private var selectedPhotoItems: [PhotosPickerItem] = []
    @State private var overflowTextForDialog: String?
    @Environment(\.colorScheme) private var colorScheme

    public init(peerId: DeviceId, controller: AppController) {
        self.peerId = peerId
        self.controller = controller
    }

    private var peerInfo: DeviceListItem {
        controller.deviceInfo(for: peerId)
    }

    private var draftBinding: Binding<String> {
        Binding(
            get: { controller.draftTextByPeer[peerId] ?? "" },
            set: { controller.draftTextByPeer[peerId] = $0 }
        )
    }

    private var pendingFiles: [PendingLocalFile] {
        controller.pendingFilesByPeer[peerId] ?? []
    }

    private var timeline: [ConversationTimelineItem] {
        var items: [ConversationTimelineItem] = []
        for rec in controller.snapshot.history {
            if case .text(let t) = rec, t.peer == peerId {
                items.append(.text(t))
            }
        }
        for task in controller.snapshot.tasks where task.peer == peerId {
            items.append(.task(task))
        }
        return items.sorted {
            if $0.sortUtcMs != $1.sortUtcMs { return $0.sortUtcMs < $1.sortUtcMs }
            return $0.sortSeq < $1.sortSeq
        }
    }

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)

        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView {
                    if timeline.isEmpty {
                        VStack(spacing: LanSpacing.md) {
                            Image(systemName: "bubble.left.and.bubble.right")
                                .font(.system(size: 36))
                                .foregroundStyle(palette.textMuted)
                            Text(UiText.labelNoMessages)
                                .font(.subheadline)
                                .multilineTextAlignment(.center)
                                .foregroundStyle(palette.textSecondary)
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.top, 90)
                        .padding(.horizontal, LanSpacing.xl)
                    } else {
                        LazyVStack(spacing: LanSpacing.md) {
                            ForEach(timeline) { item in
                                switch item {
                                case .text(let record):
                                    textBubbleRow(record, palette: palette)
                                        .id(item.id)
                                case .task(let task):
                                    taskCard(task, palette: palette)
                                        .id(item.id)
                                }
                            }
                        }
                        .padding(LanSpacing.lg)
                    }
                }
                .background(palette.canvas)
                .onChange(of: timeline.count) { _, _ in
                    if let last = timeline.last {
                        withAnimation(.easeOut(duration: 0.2)) {
                            proxy.scrollTo(last.id, anchor: .bottom)
                        }
                    }
                }
            }

            if !pendingFiles.isEmpty {
                pendingFilesBar(palette: palette)
            }

            composerBar(palette: palette)
        }
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .principal) {
                HStack(spacing: 8) {
                    PlatformBadgeView(deviceType: peerInfo.deviceType, size: 28)
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 5) {
                            Text(peerInfo.displayName)
                                .font(.subheadline.weight(.semibold))
                                .lineLimit(1)
                            if peerInfo.isOnline || peerInfo.isConnected {
                                Circle()
                                    .fill(palette.success)
                                    .frame(width: 7, height: 7)
                            }
                        }
                        if let ip = peerInfo.ipText {
                            let portSuffix = peerInfo.port.map { ":\($0)" } ?? ""
                            Text("\(peerInfo.deviceType.displayLabel) · \(ip)\(portSuffix)")
                                .font(.caption2.monospaced())
                                .foregroundStyle(palette.textMuted)
                                .lineLimit(1)
                        }
                    }
                }
            }
        }
        .fileImporter(
            isPresented: $showFileImporter,
            allowedContentTypes: [.item],
            allowsMultipleSelection: true
        ) { result in
            if case .success(let urls) = result {
                controller.addPickedFiles(peer: peerId, urls: urls)
            }
        }
        .onChange(of: selectedPhotoItems) { _, items in
            guard !items.isEmpty else { return }
            let picked = items
            selectedPhotoItems = []
            Task {
                var urls: [URL] = []
                for item in picked {
                    if let staged = try? await item.loadTransferable(type: StagedPhotoFile.self) {
                        urls.append(staged.url)
                    }
                }
                if !urls.isEmpty {
                    controller.addPickedFiles(peer: peerId, urls: urls)
                }
            }
        }
        .alert(UiText.dialogTextTooLong, isPresented: Binding(
            get: { overflowTextForDialog != nil },
            set: { if !$0 { overflowTextForDialog = nil } }
        )) {
            Button(UiText.btnSendAsTxt) {
                if let text = overflowTextForDialog {
                    controller.sendTextAsTxtFile(peer: peerId, text: text)
                }
                overflowTextForDialog = nil
            }
            Button(UiText.btnCancel, role: .cancel) {
                overflowTextForDialog = nil
            }
        }
    }

    // MARK: - Text Bubble

    private func textBubbleRow(_ record: TextRecord, palette: LanPalette) -> some View {
        let isOut = record.direction == .send
        let original = StrictUtf8.decode(record.textUtf8) ?? ""
        let collapse = TextRules.collapse(original)
        let isExpanded = expandedTextIds.contains(record.messageId)
        let visibleOriginal: String
        let canToggleCollapse: Bool
        switch collapse {
        case .full:
            visibleOriginal = original
            canToggleCollapse = false
        case .collapsed(let utf16End):
            canToggleCollapse = true
            if isExpanded {
                visibleOriginal = original
            } else {
                let ns = original as NSString
                visibleOriginal = ns.substring(to: min(utf16End, ns.length))
            }
        }
        let mapped = TextDisplayMapper.map(visibleOriginal)
        let uiColor: UIColor = isOut ? .white : (colorScheme == .dark ? UIColor(red: 0.945, green: 0.961, blue: 0.976, alpha: 1) : UIColor(red: 0.059, green: 0.090, blue: 0.165, alpha: 1))

        return HStack {
            if isOut { Spacer(minLength: 44) }

            VStack(alignment: isOut ? .trailing : .leading, spacing: 5) {
                VStack(alignment: .leading, spacing: 6) {
                    LosslessTextView(
                        originalText: visibleOriginal,
                        displayText: mapped,
                        textColor: uiColor
                    ) { hasNul in
                        controller.triggerHaptic(.light)
                        controller.showToast(hasNul ? UiText.toastCopiedWithNul : UiText.toastCopied)
                    }

                    if canToggleCollapse {
                        Button(isExpanded ? UiText.btnCollapse : UiText.btnExpand) {
                            if isExpanded {
                                expandedTextIds.remove(record.messageId)
                            } else {
                                expandedTextIds.insert(record.messageId)
                            }
                        }
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(isOut ? Color.white.opacity(0.92) : palette.accent)
                    }
                }
                .padding(.horizontal, 13)
                .padding(.vertical, 10)
                .background(
                    RoundedRectangle(cornerRadius: LanRadii.card, style: .continuous)
                        .fill(isOut ? palette.bubbleOutgoing : palette.bubbleIncoming)
                )
                .overlay(
                    RoundedRectangle(cornerRadius: LanRadii.card, style: .continuous)
                        .strokeBorder(isOut ? Color.clear : palette.border, lineWidth: 1)
                )

                HStack(spacing: 10) {
                    Text(UiTextMapper.formatClockTime(record.recordedAt))
                        .font(.caption2.monospacedDigit())
                        .foregroundStyle(palette.textMuted)
                    if isOut {
                        Text(UiTextMapper.textStatus(record.status))
                            .font(.caption2)
                            .foregroundStyle(record.status == .failed ? palette.danger : palette.textMuted)
                    }
                    Button(UiText.btnCopy) {
                        controller.copyTextLossless(original)
                    }
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(palette.accent)

                    if isOut && record.status == .failed {
                        Button(UiText.btnRetry) {
                            controller.retryText(peer: peerId, messageId: record.messageId)
                        }
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(palette.danger)
                    }
                }
            }

            if !isOut { Spacer(minLength: 44) }
        }
    }

    // MARK: - Transfer Task Card

    private func taskCard(_ task: TransferTask, palette: LanPalette) -> some View {
        let totalBytes = task.files.reduce(Int64(0)) { $0 + $1.size }
        let transferredBytes = task.files.reduce(Int64(0)) { $0 + ($1.state == .completed ? $1.size : $1.persisted) }
        let prog = controller.snapshot.progress[task.transferId]
        let fraction: Double = totalBytes > 0 ? min(max(Double(transferredBytes) / Double(totalBytes), 0), 1) : (task.state == .completed ? 1 : 0)

        return VStack(alignment: .leading, spacing: LanSpacing.md) {
            HStack(spacing: 8) {
                StatusPill(
                    text: task.direction == .send ? UiText.labelDirectionSend : UiText.labelDirectionReceive,
                    foreground: task.direction == .send ? palette.accent : palette.success,
                    background: task.direction == .send ? palette.accentSoft : palette.successSoft
                )
                Text("\(UiText.labelFileCount(count: task.files.count)) · \(SizeFormatter.format(totalBytes))")
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(palette.textPrimary)
                    .monospacedDigit()
                Spacer()
                Text(UiTextMapper.taskState(task.state, direction: task.direction, interrupt: task.interrupt))
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(taskStatusColor(task.state, palette: palette))
            }

            if task.state == .running || task.state == .interrupted || task.state == .resuming {
                VStack(spacing: 5) {
                    ProgressView(value: fraction)
                        .tint(palette.accent)
                    HStack {
                        let pct = prog?.percent ?? Int(fraction * 100)
                        Text("\(pct)% · \(SizeFormatter.format(transferredBytes)) / \(SizeFormatter.format(totalBytes))")
                            .font(.caption2.monospacedDigit())
                            .foregroundStyle(palette.textSecondary)
                        Spacer()
                        if let prog, task.state == .running {
                            Text("\(SizeFormatter.format(prog.bytesPerSec))/s · \(UiText.labelEta(eta: UiTextMapper.formatEta(prog.etaSec)))")
                                .font(.caption2.monospacedDigit())
                                .foregroundStyle(palette.textSecondary)
                        }
                    }
                }
            }

            VStack(spacing: 6) {
                ForEach(task.files, id: \.index) { file in
                    HStack(spacing: 8) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(file.finalName ?? file.originalName)
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(palette.textPrimary)
                                .lineLimit(1)
                            Text("\(SizeFormatter.format(file.size)) · \(UiTextMapper.fileState(file.state))")
                                .font(.caption2.monospacedDigit())
                                .foregroundStyle(palette.textMuted)
                        }
                        Spacer()
                        if task.direction == .receive && file.state == .completed, let stored = file.storedRef {
                            Button(UiText.iosBtnQuickLook) {
                                controller.openStoredFile(stored, mime: file.mime)
                            }
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(palette.accent)

                            Button(UiText.btnRevealInFolder) {
                                controller.revealStoredFile(stored)
                            }
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(palette.textSecondary)
                        }
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 8)
                    .background(
                        RoundedRectangle(cornerRadius: LanRadii.control, style: .continuous)
                            .fill(palette.surfaceSubtle)
                    )
                }
            }

            if task.state == .interrupted {
                HStack(spacing: LanSpacing.sm) {
                    Button {
                        controller.resumeTask(task.transferId)
                    } label: {
                        Text(UiText.btnResume)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(palette.accentOn)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 7)
                            .background(RoundedRectangle(cornerRadius: LanRadii.chip, style: .continuous).fill(palette.accent))
                    }
                    .buttonStyle(.plain)

                    Button {
                        controller.cancelTask(task.transferId)
                    } label: {
                        Text(UiText.btnCancel)
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(palette.danger)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 7)
                            .background(RoundedRectangle(cornerRadius: LanRadii.chip, style: .continuous).fill(palette.dangerSoft))
                    }
                    .buttonStyle(.plain)
                }
            } else if task.state == .offering || task.state == .running || task.state == .resuming {
                Button(UiText.btnCancel) {
                    controller.cancelTask(task.transferId)
                }
                .font(.caption.weight(.semibold))
                .foregroundStyle(palette.danger)
            }
        }
        .lanCard(padding: 14)
    }

    private func taskStatusColor(_ state: TaskState, palette: LanPalette) -> Color {
        switch state {
        case .completed: return palette.success
        case .running, .offering, .resuming, .awaitingUser: return palette.accent
        case .interrupted: return palette.warning
        default: return palette.danger
        }
    }

    // MARK: - Pending Strip & Glass Composer

    private func pendingFilesBar(palette: LanPalette) -> some View {
        HStack(spacing: LanSpacing.sm) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(pendingFiles) { item in
                        HStack(spacing: 5) {
                            Text("\(item.name) · \(SizeFormatter.format(item.size))")
                                .font(.caption2.weight(.semibold))
                                .lineLimit(1)
                            Button {
                                controller.removePendingFile(peer: peerId, id: item.id)
                            } label: {
                                Image(systemName: "xmark.circle.fill")
                                    .font(.caption2)
                            }
                        }
                        .foregroundStyle(palette.accent)
                        .padding(.horizontal, 9)
                        .padding(.vertical, 5)
                        .background(RoundedRectangle(cornerRadius: LanRadii.chip, style: .continuous).fill(palette.accentSoft))
                    }
                }
            }

            Button(UiText.btnClearPending) {
                controller.clearPendingFiles(peer: peerId)
            }
            .font(.caption2)
            .foregroundStyle(palette.textSecondary)

            Button {
                controller.sendPendingFiles(peer: peerId)
            } label: {
                Text(UiText.btnSendPending)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(palette.accentOn)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 6)
                    .background(RoundedRectangle(cornerRadius: LanRadii.chip, style: .continuous).fill(palette.accent))
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, LanSpacing.md)
        .padding(.vertical, 8)
        .background(palette.surface)
        .overlay(alignment: .top) { Divider() }
    }

    private func composerBar(palette: LanPalette) -> some View {
        let canSend = !draftBinding.wrappedValue.isEmpty && !controller.snapshot.localInterfaces.isEmpty

        return VStack(spacing: LanSpacing.sm) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: LanSpacing.sm) {
                    Button {
                        showFileImporter = true
                    } label: {
                        toolPillLabel(UiText.iosBtnPickFiles, systemImage: "doc.badge.plus", palette: palette)
                    }
                    .buttonStyle(.plain)

                    PhotosPicker(selection: $selectedPhotoItems, maxSelectionCount: 50, matching: .any(of: [.images, .videos])) {
                        toolPillLabel(UiText.iosBtnPickPhotos, systemImage: "photo.on.rectangle", palette: palette)
                    }
                    .buttonStyle(.plain)

                    Button {
                        controller.pasteToDraft(peer: peerId)
                    } label: {
                        toolPillLabel(UiText.btnPaste, systemImage: "doc.on.clipboard", palette: palette)
                    }
                    .buttonStyle(.plain)

                    Button {
                        controller.sendClipboardDirectly(peer: peerId)
                    } label: {
                        toolPillLabel(UiText.btnSendClipboard, systemImage: "bolt.fill", palette: palette)
                    }
                    .buttonStyle(.plain)
                }
            }

            HStack(alignment: .bottom, spacing: LanSpacing.sm) {
                TextField(UiText.labelMessagePlaceholder, text: draftBinding, axis: .vertical)
                    .lineLimit(1...5)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 9)
                    .background(
                        RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                            .fill(palette.surface)
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                            .strokeBorder(palette.border, lineWidth: 1)
                    )

                Button {
                    let current = draftBinding.wrappedValue
                    Task {
                        let outcome = await controller.sendText(peer: peerId, text: current)
                        if case .tooLong = outcome {
                            overflowTextForDialog = current
                        }
                    }
                } label: {
                    Image(systemName: "paperplane.fill")
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(palette.accentOn)
                        .frame(width: 40, height: 40)
                        .background(
                            RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous)
                                .fill(canSend ? palette.accent : palette.borderStrong)
                        )
                }
                .disabled(!canSend)
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, LanSpacing.md)
        .padding(.vertical, 10)
        .background(.ultraThinMaterial)
        .overlay(alignment: .top) { Divider() }
    }

    private func toolPillLabel(_ title: String, systemImage: String, palette: LanPalette) -> some View {
        Label(title, systemImage: systemImage)
            .font(.caption.weight(.semibold))
            .foregroundStyle(palette.textSecondary)
            .padding(.horizontal, 11)
            .padding(.vertical, 6)
            .background(Capsule().fill(palette.surface))
            .overlay(Capsule().strokeBorder(palette.border, lineWidth: 1))
    }
}
