import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import SwiftUI

private enum HistoryFilterKind: String, CaseIterable {
    case all
    case text
    case file

    var label: String {
        switch self {
        case .all: return UiText.labelFilterAll
        case .text: return UiText.labelFilterText
        case .file: return UiText.labelFilterFile
        }
    }
}

/// Tab 2：传输记录页（原生 `.searchable` 搜索、三段类型筛选、清空确认与三选一文件删除确认）。
public struct HistoryTabView: View {
    public let controller: AppController

    @State private var searchText: String = ""
    @State private var filter: HistoryFilterKind = .all
    @State private var showClearAlert = false
    @State private var fileRecordPendingDelete: FileRecord?
    @Environment(\.colorScheme) private var colorScheme

    public init(controller: AppController) {
        self.controller = controller
    }

    private var filteredRecords: [HistoryRecord] {
        let q = searchText.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return controller.snapshot.history.filter { record in
            switch (filter, record) {
            case (.text, .file), (.file, .text):
                return false
            default:
                break
            }
            guard !q.isEmpty else { return true }
            switch record {
            case .text(let t):
                let body = StrictUtf8.decode(t.textUtf8)?.lowercased() ?? ""
                return t.peerName.lowercased().contains(q) || body.contains(q)
            case .file(let f):
                let name = (f.file.finalName ?? f.file.originalName).lowercased()
                return f.peerName.lowercased().contains(q) || name.contains(q)
            }
        }
    }

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)

        VStack(spacing: 0) {
            Picker("", selection: $filter) {
                ForEach(HistoryFilterKind.allCases, id: \.self) { kind in
                    Text(kind.label).tag(kind)
                }
            }
            .pickerStyle(.segmented)
            .padding(.horizontal, LanSpacing.lg)
            .padding(.vertical, LanSpacing.sm)
            .background(palette.canvas)

            if filteredRecords.isEmpty {
                ContentUnavailableView(
                    UiText.labelNoHistory,
                    systemImage: "clock.arrow.circlepath",
                    description: Text(UiText.labelEncryptedHint)
                )
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(palette.canvas)
            } else {
                List {
                    ForEach(filteredRecords, id: \.seq) { record in
                        Group {
                            switch record {
                            case .text(let t):
                                textHistoryCard(t, palette: palette)
                                    .swipeActions(edge: .trailing) {
                                        Button(UiText.btnDelete, role: .destructive) {
                                            controller.deleteTextRecord(t)
                                        }
                                    }
                            case .file(let f):
                                fileHistoryCard(f, palette: palette)
                                    .swipeActions(edge: .trailing) {
                                        Button(UiText.btnDelete, role: .destructive) {
                                            fileRecordPendingDelete = f
                                        }
                                    }
                            }
                        }
                        .listRowSeparator(.hidden)
                        .listRowBackground(Color.clear)
                        .listRowInsets(EdgeInsets(top: 6, leading: 16, bottom: 6, trailing: 16))
                    }
                }
                .listStyle(.plain)
                .scrollContentBackground(.hidden)
                .background(palette.canvas)
            }
        }
        .background(palette.canvas.ignoresSafeArea())
        .navigationTitle(UiText.navHistory)
        .navigationBarTitleDisplayMode(.large)
        .searchable(text: $searchText, prompt: UiText.labelSearchHistory)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                if !controller.snapshot.history.isEmpty {
                    Button(UiText.btnClearHistory, role: .destructive) {
                        showClearAlert = true
                    }
                    .foregroundStyle(palette.danger)
                }
            }
        }
        .alert(UiText.dialogClearHistoryTitle, isPresented: $showClearAlert) {
            Button(UiText.btnClear, role: .destructive) {
                controller.clearAllHistory()
            }
            Button(UiText.btnCancel, role: .cancel) {}
        } message: {
            let unfinished = controller.unfinishedTaskCount
            if unfinished > 0 {
                Text("\(UiText.dialogClearHistory)\n\(UiText.dialogClearHistoryUnfinished(count: unfinished))")
            } else {
                Text(UiText.dialogClearHistory)
            }
        }
        .confirmationDialog(
            UiText.dialogDeleteFileTitle,
            isPresented: Binding(
                get: { fileRecordPendingDelete != nil },
                set: { if !$0 { fileRecordPendingDelete = nil } }
            ),
            titleVisibility: .visible
        ) {
            if let f = fileRecordPendingDelete {
                if f.direction == .receive && f.file.state == .completed && f.file.storedRef != nil {
                    Button(UiText.btnDeleteWithFile, role: .destructive) {
                        controller.deleteFileRecord(f, deleteLocal: true)
                        fileRecordPendingDelete = nil
                    }
                }
                Button(UiText.btnDeleteRecordOnly, role: .destructive) {
                    controller.deleteFileRecord(f, deleteLocal: false)
                    fileRecordPendingDelete = nil
                }
                Button(UiText.btnCancel, role: .cancel) {
                    fileRecordPendingDelete = nil
                }
            }
        } message: {
            if let f = fileRecordPendingDelete {
                if !f.taskState.isTerminal {
                    Text(UiText.dialogDeleteInterrupted)
                } else if f.direction == .receive && f.file.state == .completed {
                    Text(UiText.dialogDeleteFileDesc)
                }
            }
        }
    }

    private func textHistoryCard(_ record: TextRecord, palette: LanPalette) -> some View {
        let original = StrictUtf8.decode(record.textUtf8) ?? ""
        let mapped = TextDisplayMapper.map(TextRules.notificationPreview(original)).display

        return VStack(alignment: .leading, spacing: 8) {
            HStack {
                StatusPill(
                    text: record.direction == .send ? UiText.labelDirectionSend : UiText.labelDirectionReceive,
                    foreground: record.direction == .send ? palette.accent : palette.success,
                    background: record.direction == .send ? palette.accentSoft : palette.successSoft
                )
                Text(record.peerName)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(palette.textPrimary)
                Spacer()
                Text(UiTextMapper.formatRecordTime(record.recordedAt))
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(palette.textMuted)
            }

            Text(mapped)
                .font(.footnote)
                .foregroundStyle(palette.textSecondary)
                .lineLimit(3)

            HStack(spacing: 14) {
                Button(UiText.btnCopy) {
                    controller.copyTextLossless(original)
                }
                .font(.caption.weight(.semibold))
                .foregroundStyle(palette.accent)
                .buttonStyle(.plain)

                Spacer()

                Text(UiTextMapper.textStatus(record.status))
                    .font(.caption2)
                    .foregroundStyle(palette.textMuted)
            }
        }
        .lanCard(padding: 14)
    }

    private func fileHistoryCard(_ record: FileRecord, palette: LanPalette) -> some View {
        let f = record.file
        let name = f.finalName ?? f.originalName

        return VStack(alignment: .leading, spacing: 8) {
            HStack {
                StatusPill(
                    text: record.direction == .send ? UiText.labelDirectionSend : UiText.labelDirectionReceive,
                    foreground: record.direction == .send ? palette.accent : palette.success,
                    background: record.direction == .send ? palette.accentSoft : palette.successSoft
                )
                Text(record.peerName)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(palette.textPrimary)
                Spacer()
                Text(UiTextMapper.formatRecordTime(record.recordedAt))
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(palette.textMuted)
            }

            HStack(spacing: 6) {
                Text(name)
                    .font(.subheadline.weight(.semibold))
                    .foregroundStyle(palette.textPrimary)
                    .lineLimit(1)
                Text("· \(SizeFormatter.format(f.size))")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(palette.textMuted)
            }

            if let path = f.fullPath {
                Text(path)
                    .font(.caption2.monospaced())
                    .foregroundStyle(palette.textMuted)
                    .lineLimit(2)
            }

            HStack(spacing: 14) {
                if record.direction == .receive && f.state == .completed, let stored = f.storedRef {
                    Button(UiText.btnOpenFile) {
                        controller.openStoredFile(stored, mime: f.mime)
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(palette.accent)
                    .buttonStyle(.plain)

                    Button(UiText.btnRevealInFolder) {
                        controller.revealStoredFile(stored)
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(palette.accent)
                    .buttonStyle(.plain)

                    if let path = f.fullPath {
                        Button(UiText.btnCopyPath) {
                            controller.copyAddress(path)
                        }
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(palette.textSecondary)
                        .buttonStyle(.plain)
                    }
                } else if record.taskState == .interrupted {
                    Button(UiText.btnResume) {
                        controller.resumeTask(f.transferId)
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(palette.accent)
                    .buttonStyle(.plain)

                    Button(UiText.btnCancel) {
                        controller.cancelTask(f.transferId)
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(palette.danger)
                    .buttonStyle(.plain)
                }

                Spacer()

                Text(UiTextMapper.fileState(f.state))
                    .font(.caption2)
                    .foregroundStyle(f.state == .completed ? palette.success : palette.textMuted)
            }
        }
        .lanCard(padding: 14)
    }
}
