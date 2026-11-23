import LanTransferDomain
import QuickLook
import SwiftUI
import UIKit

/// 无损划词复制文本视图：包装只读 `UITextView` 并重写 `copy(_:)`，通过 `TextDisplayMapper.originalRange` 将显示选区还原为原始 UTF-8/CRLF/控制字符写入系统剪贴板。
public struct LosslessTextView: UIViewRepresentable {
    public let originalText: String
    public let displayText: DisplayText
    public let textColor: UIColor
    public let font: UIFont
    public let onCopied: (Bool) -> Void

    public init(
        originalText: String,
        displayText: DisplayText,
        textColor: UIColor,
        font: UIFont = .preferredFont(forTextStyle: .body),
        onCopied: @escaping (Bool) -> Void
    ) {
        self.originalText = originalText
        self.displayText = displayText
        self.textColor = textColor
        self.font = font
        self.onCopied = onCopied
    }

    public func makeUIView(context: Context) -> LosslessCopyTextView {
        let tv = LosslessCopyTextView()
        tv.isEditable = false
        tv.isSelectable = true
        tv.isScrollEnabled = false
        tv.backgroundColor = .clear
        tv.textContainerInset = .zero
        tv.textContainer.lineFragmentPadding = 0
        tv.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        tv.setContentHuggingPriority(.required, for: .vertical)
        return tv
    }

    public func updateUIView(_ uiView: LosslessCopyTextView, context: Context) {
        uiView.originalSource = originalText
        uiView.mappedDisplay = displayText
        uiView.onCopied = onCopied
        if uiView.text != displayText.display {
            uiView.text = displayText.display
        }
        uiView.textColor = textColor
        uiView.font = font
        uiView.invalidateIntrinsicContentSize()
    }

    public func sizeThatFits(_ proposal: ProposedViewSize, uiView: LosslessCopyTextView, context: Context) -> CGSize? {
        let fallbackWidth = (UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first?.screen.bounds.width ?? 390) * 0.72
        let maxWidth = proposal.width ?? fallbackWidth
        let fitted = uiView.sizeThatFits(CGSize(width: maxWidth, height: .greatestFiniteMagnitude))
        return CGSize(width: min(fitted.width, maxWidth), height: fitted.height)
    }
}

public final class LosslessCopyTextView: UITextView {
    var originalSource: String = ""
    var mappedDisplay: DisplayText?
    var onCopied: ((Bool) -> Void)?

    public override func copy(_ sender: Any?) {
        let range = selectedRange
        let extracted: String
        if let mapped = mappedDisplay, range.length > 0 {
            extracted = mapped.extractOriginal(
                from: originalSource,
                selStart: range.location,
                selEnd: range.location + range.length
            )
        } else {
            extracted = originalSource
        }
        UIPasteboard.general.string = extracted
        onCopied?(TextRules.containsNul(extracted))
    }
}

/// 原生 QuickLook 预览包装（用于“打开文件”直接预览 PDF、照片、视频、Office 等并原生支持共享/存储）。
public struct QuickLookPreviewSheet: UIViewControllerRepresentable {
    public let url: URL

    public init(url: URL) {
        self.url = url
    }

    public func makeCoordinator() -> Coordinator {
        Coordinator(url: url)
    }

    public func makeUIViewController(context: Context) -> UINavigationController {
        let controller = QLPreviewController()
        controller.dataSource = context.coordinator
        return UINavigationController(rootViewController: controller)
    }

    public func updateUIViewController(_ uiViewController: UINavigationController, context: Context) {
        context.coordinator.url = url
    }

    public final class Coordinator: NSObject, QLPreviewControllerDataSource {
        var url: URL

        init(url: URL) {
            self.url = url
        }

        public func numberOfPreviewItems(in controller: QLPreviewController) -> Int { 1 }

        public func previewController(_ controller: QLPreviewController, previewItemAt index: Int) -> any QLPreviewItem {
            url as NSURL
        }
    }
}
