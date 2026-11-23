import AVFoundation
import CoreImage
import CoreImage.CIFilterBuiltins
import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import SwiftUI
import UIKit

// MARK: - 1. 本机二维码展示 Sheet

public struct QrShowSheet: View {
    public let controller: AppController
    @State private var qrText: String?
    @State private var screenshotTaken = false
    @State private var savedBrightness: CGFloat?
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var colorScheme

    public init(controller: AppController) {
        self.controller = controller
    }

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)
        let interfaces = controller.snapshot.localInterfaces
        let port = controller.snapshot.listenPort ?? ProtocolConstants.defaultPort

        NavigationStack {
            ScrollView {
                VStack(spacing: LanSpacing.lg) {
                    if let qrText, let uiImage = Self.renderQrImage(from: qrText) {
                        Image(uiImage: uiImage)
                            .interpolation(.none)
                            .resizable()
                            .scaledToFit()
                            .frame(width: 210, height: 210)
                            .padding(18)
                            .background(
                                RoundedRectangle(cornerRadius: 20, style: .continuous)
                                    .fill(Color.white)
                            )
                            .shadow(color: Color.black.opacity(0.08), radius: 14, y: 6)

                        Text(UiText.labelQrHint)
                            .font(.footnote)
                            .multilineTextAlignment(.center)
                            .foregroundStyle(palette.textSecondary)

                        HStack(spacing: 6) {
                            ForEach(AddressRules.qrAddressList(sortedInterfaces: interfaces), id: \.self) { ip in
                                Text("\(ip.description):\(port)")
                                    .font(.caption.monospaced())
                                    .foregroundStyle(palette.textSecondary)
                                    .padding(.horizontal, 10)
                                    .padding(.vertical, 5)
                                    .background(RoundedRectangle(cornerRadius: LanRadii.chip, style: .continuous).fill(palette.surface))
                                    .overlay(RoundedRectangle(cornerRadius: LanRadii.chip, style: .continuous).strokeBorder(palette.border, lineWidth: 1))
                            }
                        }

                        Text(UiText.labelQrAutoRefresh)
                            .font(.caption)
                            .foregroundStyle(palette.textMuted)

                        if screenshotTaken {
                            HStack(spacing: 8) {
                                Image(systemName: "exclamationmark.shield.fill")
                                Text(UiText.iosQrScreenshotWarning)
                            }
                            .font(.caption)
                            .foregroundStyle(palette.warning)
                            .padding(10)
                            .frame(maxWidth: .infinity)
                            .background(RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous).fill(palette.warningSoft))
                        }

                        Button {
                            Task { qrText = await controller.engine.currentQrText(forceRefresh: true) }
                        } label: {
                            Label(UiText.btnRefreshQr, systemImage: "arrow.clockwise")
                                .font(.subheadline.weight(.semibold))
                                .frame(maxWidth: .infinity)
                                .padding(.vertical, 12)
                                .foregroundStyle(palette.accent)
                                .background(RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous).fill(palette.accentSoft))
                        }
                        .buttonStyle(.plain)
                    } else {
                        ContentUnavailableView(
                            UiText.errorNoNetworkQr,
                            systemImage: "wifi.slash",
                            description: Text(UiText.bannerNoNetwork)
                        )
                        .padding(.top, 40)
                    }
                }
                .padding(LanSpacing.xl)
            }
            .background(palette.canvas.ignoresSafeArea())
            .navigationTitle(UiText.btnQrPair)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button(UiText.btnClose) { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
        .task {
            await refreshLoop()
        }
        .onAppear {
            if let screen = Self.activeScreen() {
                let current = screen.brightness
                savedBrightness = current
                if current < 0.78 {
                    screen.brightness = 0.78
                }
            }
        }
        .onDisappear {
            if let saved = savedBrightness, let screen = Self.activeScreen() {
                screen.brightness = saved
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: UIApplication.userDidTakeScreenshotNotification)) { _ in
            screenshotTaken = true
        }
    }

    private func refreshLoop() async {
        while !Task.isCancelled {
            qrText = await controller.engine.currentQrText(forceRefresh: false)
            do {
                try await Task.sleep(nanoseconds: 2_000_000_000)
            } catch {
                break
            }
        }
    }

    @MainActor
    private static func activeScreen() -> UIScreen? {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .first?.screen
    }

    private static func renderQrImage(from text: String) -> UIImage? {
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        guard let output = filter.outputImage else { return nil }
        let scaled = output.transformed(by: CGAffineTransform(scaleX: 12, y: 12))
        let context = CIContext()
        guard let cgImage = context.createCGImage(scaled, from: scaled.extent) else { return nil }
        return UIImage(cgImage: cgImage)
    }
}

// MARK: - 2. 相机全屏扫码页

public struct QrScanView: View {
    public let controller: AppController
    @State private var permissionStatus: AVAuthorizationStatus = AVCaptureDevice.authorizationStatus(for: .video)
    @State private var torchOn = false
    @State private var errorHint: String?
    @State private var lastInvalidAt: Date = .distantPast
    @State private var handling = false
    @Environment(\.dismiss) private var dismiss

    public init(controller: AppController) {
        self.controller = controller
    }

    public var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            if permissionStatus == .authorized {
                QrCameraPreview(torchOn: torchOn) { code in
                    handleDetectedCode(code)
                }
                .ignoresSafeArea()

                // 取景框遮罩与引导
                VStack {
                    HStack {
                        Button(UiText.btnCancel) { dismiss() }
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 8)
                            .background(Capsule().fill(Color.black.opacity(0.48)))
                        Spacer()
                        Text(UiText.btnScanQr)
                            .font(.headline)
                            .foregroundStyle(.white)
                        Spacer()
                        Button {
                            torchOn.toggle()
                        } label: {
                            Image(systemName: torchOn ? "flashlight.on.fill" : "flashlight.off.fill")
                                .font(.body.weight(.semibold))
                                .foregroundStyle(torchOn ? Color.yellow : Color.white)
                                .padding(10)
                                .background(Circle().fill(Color.black.opacity(0.48)))
                        }
                    }
                    .padding(.horizontal, 20)
                    .padding(.top, 12)

                    Spacer()

                    RoundedRectangle(cornerRadius: 24, style: .continuous)
                        .strokeBorder(Color(hex: 0x3B82F6), lineWidth: 3)
                        .frame(width: 240, height: 240)
                        .background(Color.clear)

                    if let errorHint {
                        Text(errorHint)
                            .font(.footnote.weight(.semibold))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 14)
                            .padding(.vertical, 8)
                            .background(Capsule().fill(Color(hex: 0xDC2626).opacity(0.9)))
                            .padding(.top, 16)
                    } else {
                        Text(UiText.labelQrHint)
                            .font(.footnote)
                            .foregroundStyle(Color.white.opacity(0.88))
                            .padding(.top, 16)
                    }

                    Spacer()

                    Button {
                        dismiss()
                        controller.showManualConnect = true
                    } label: {
                        Text(UiText.btnManualConnect)
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 24)
                            .padding(.vertical, 12)
                            .background(Capsule().fill(.ultraThinMaterial))
                    }
                    .padding(.bottom, 28)
                }
            } else {
                VStack(spacing: LanSpacing.lg) {
                    Image(systemName: "camera.viewfinder")
                        .font(.system(size: 48))
                        .foregroundStyle(Color(hex: 0x3B82F6))
                    Text(UiText.labelCameraPermissionHint)
                        .font(.body)
                        .multilineTextAlignment(.center)
                        .foregroundStyle(.white)
                    HStack(spacing: LanSpacing.md) {
                        Button(UiText.btnOpenAppSettings) {
                            controller.openSystemAppSettings()
                        }
                        .buttonStyle(.borderedProminent)
                        Button(UiText.btnManualConnect) {
                            dismiss()
                            controller.showManualConnect = true
                        }
                        .buttonStyle(.bordered)
                        .tint(.white)
                    }
                    Button(UiText.btnCancel) { dismiss() }
                        .foregroundStyle(Color.white.opacity(0.75))
                        .padding(.top, 8)
                }
                .padding(LanSpacing.xl)
            }
        }
        .task {
            if permissionStatus == .notDetermined {
                let granted = await AVCaptureDevice.requestAccess(for: .video)
                permissionStatus = granted ? .authorized : .denied
            }
        }
    }

    private func handleDetectedCode(_ code: String) {
        guard !handling else { return }
        handling = true
        Task {
            let ok = await controller.handleScannedQr(code)
            if ok {
                dismiss()
            } else {
                let now = Date()
                if now.timeIntervalSince(lastInvalidAt) >= 3.0 {
                    lastInvalidAt = now
                    errorHint = UiText.errorQrUnrecognized
                    controller.triggerHaptic(.warning)
                }
                try? await Task.sleep(nanoseconds: 600_000_000)
                handling = false
            }
        }
    }
}

private struct QrCameraPreview: UIViewRepresentable {
    let torchOn: Bool
    let onDetected: @MainActor (String) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(onDetected: onDetected)
    }

    func makeUIView(context: Context) -> CameraContainerView {
        let view = CameraContainerView()
        context.coordinator.startSession(in: view)
        return view
    }

    func updateUIView(_ uiView: CameraContainerView, context: Context) {
        context.coordinator.setTorch(torchOn)
    }

    static func dismantleUIView(_ uiView: CameraContainerView, coordinator: Coordinator) {
        coordinator.stopSession()
    }

    final class CameraContainerView: UIView {
        var previewLayer: AVCaptureVideoPreviewLayer?
        override func layoutSubviews() {
            super.layoutSubviews()
            previewLayer?.frame = bounds
        }
    }

    final class Coordinator: NSObject, AVCaptureMetadataOutputObjectsDelegate, @unchecked Sendable {
        private let onDetected: @MainActor (String) -> Void
        private let session = AVCaptureSession()
        private let sessionQueue = DispatchQueue(label: "com.lantransfer.qr.camera")

        init(onDetected: @escaping @MainActor (String) -> Void) {
            self.onDetected = onDetected
        }

        @MainActor
        func startSession(in view: CameraContainerView) {
            let layer = AVCaptureVideoPreviewLayer(session: session)
            layer.videoGravity = .resizeAspectFill
            layer.frame = view.bounds
            view.layer.addSublayer(layer)
            view.previewLayer = layer

            sessionQueue.async {
                guard let device = AVCaptureDevice.default(for: .video),
                      let input = try? AVCaptureDeviceInput(device: device),
                      self.session.canAddInput(input) else {
                    return
                }
                self.session.beginConfiguration()
                self.session.addInput(input)
                let output = AVCaptureMetadataOutput()
                if self.session.canAddOutput(output) {
                    self.session.addOutput(output)
                    output.setMetadataObjectsDelegate(self, queue: DispatchQueue.main)
                    output.metadataObjectTypes = [.qr]
                }
                self.session.commitConfiguration()
                self.session.startRunning()
            }
        }

        func setTorch(_ enabled: Bool) {
            sessionQueue.async {
                guard let device = AVCaptureDevice.default(for: .video), device.hasTorch else { return }
                try? device.lockForConfiguration()
                device.torchMode = enabled ? .on : .off
                device.unlockForConfiguration()
            }
        }

        func stopSession() {
            setTorch(false)
            sessionQueue.async {
                if self.session.isRunning {
                    self.session.stopRunning()
                }
            }
        }

        func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
            guard let readable = metadataObjects.first as? AVMetadataMachineReadableCodeObject,
                  let text = readable.stringValue else {
                return
            }
            Task { @MainActor in
                self.onDetected(text)
            }
        }
    }
}

// MARK: - 3. 手动连接 Sheet

public struct ManualConnectSheet: View {
    public let controller: AppController
    @State private var ipInput: String = ""
    @State private var portInput: String = ""
    @State private var ipError: String?
    @State private var portError: String?
    @State private var connecting = false
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var colorScheme

    public init(controller: AppController) {
        self.controller = controller
    }

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)

        NavigationStack {
            Form {
                Section {
                    TextField("例如 192.168.1.100", text: $ipInput)
                        .keyboardType(.decimalPad)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.body.monospaced())
                    if let ipError {
                        Text(ipError)
                            .font(.caption)
                            .foregroundStyle(palette.danger)
                    }
                } header: {
                    Text(UiText.labelIpAddress)
                }

                Section {
                    TextField(UiText.labelPortOptional, text: $portInput)
                        .keyboardType(.numberPad)
                        .font(.body.monospaced())
                    if let portError {
                        Text(portError)
                            .font(.caption)
                            .foregroundStyle(palette.danger)
                    }
                } header: {
                    Text(UiText.labelPort)
                }

                Section {
                    Button {
                        submit()
                    } label: {
                        HStack {
                            Spacer()
                            if connecting {
                                ProgressView()
                                    .padding(.trailing, 6)
                                Text(UiText.btnConnecting)
                            } else {
                                Text(UiText.btnConnect)
                            }
                            Spacer()
                        }
                        .font(.headline)
                    }
                    .disabled(connecting)
                }
            }
            .scrollContentBackground(.hidden)
            .background(palette.canvas.ignoresSafeArea())
            .navigationTitle(UiText.btnManualConnect)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(UiText.btnCancel) { dismiss() }
                }
            }
        }
        .presentationDetents([.medium])
        .presentationDragIndicator(.visible)
    }

    private func submit() {
        ipError = nil
        portError = nil
        guard let ip = AddressRules.parseIpv4(ipInput) else {
            ipError = UiText.errorIpInvalid
            return
        }
        let port: Int
        switch AddressRules.parsePort(portInput) {
        case .default(let p), .ok(let p):
            port = p
        case .invalid:
            portError = UiText.errorPortInvalid
            return
        }
        connecting = true
        Task {
            let ok = await controller.connectManually(ip: ip, port: port)
            connecting = false
            if ok {
                dismiss()
            }
        }
    }
}

// MARK: - 4. 全局配对 SAS 确认 Sheet

public struct PairingSheet: View {
    public let state: PairingUiState
    public let controller: AppController
    @Environment(\.colorScheme) private var colorScheme

    public init(state: PairingUiState, controller: AppController) {
        self.state = state
        self.controller = controller
    }

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)

        NavigationStack {
            VStack(spacing: LanSpacing.lg) {
                switch state {
                case .idle:
                    EmptyView()
                case .connecting:
                    ProgressView()
                        .scaleEffect(1.3)
                        .padding(.top, 18)
                    Text(UiText.btnConnecting)
                        .font(.headline)
                    Button(UiText.btnCancel) { controller.cancelPairing() }
                        .buttonStyle(.bordered)
                case .waitingForPeer(let peer):
                    peerHeader(peer, palette: palette)
                    ProgressView()
                    Text(UiText.statusPairWaiting)
                        .font(.subheadline)
                        .foregroundStyle(palette.textSecondary)
                    Button(UiText.btnCancel) { controller.cancelPairing() }
                        .buttonStyle(.bordered)
                case .showingCode(let peer, let sas):
                    peerHeader(peer, palette: palette)
                    sasBox(sas: sas, palette: palette)
                    Text(UiText.statusPairWaiting)
                        .font(.footnote)
                        .foregroundStyle(palette.textSecondary)
                    Button(UiText.btnCancel) { controller.cancelPairing() }
                        .buttonStyle(.bordered)
                case .incomingRequest(let peer, let sas):
                    peerHeader(peer, palette: palette)
                    if let sas {
                        sasBox(sas: sas, palette: palette)
                    } else {
                        ProgressView()
                        Text(UiText.statusPairCompleting)
                            .font(.caption)
                            .foregroundStyle(palette.textSecondary)
                    }
                    HStack(spacing: LanSpacing.md) {
                        Button {
                            controller.respondPairing(accept: false)
                        } label: {
                            Text(UiText.btnReject)
                                .font(.headline)
                                .frame(maxWidth: .infinity)
                                .padding(.vertical, 12)
                                .foregroundStyle(palette.textPrimary)
                                .background(RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous).fill(palette.surface))
                                .overlay(RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous).strokeBorder(palette.border, lineWidth: 1))
                        }
                        .buttonStyle(.plain)

                        Button {
                            controller.respondPairing(accept: true)
                        } label: {
                            Text(UiText.btnAccept)
                                .font(.headline)
                                .frame(maxWidth: .infinity)
                                .padding(.vertical, 12)
                                .foregroundStyle(palette.accentOn)
                                .background(RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous).fill(sas != nil ? palette.accent : palette.borderStrong))
                        }
                        .disabled(sas == nil)
                        .buttonStyle(.plain)
                    }
                case .completing(let peer):
                    peerHeader(peer, palette: palette)
                    ProgressView()
                    Text(UiText.statusPairCompleting)
                        .font(.subheadline)
                        .foregroundStyle(palette.textSecondary)
                }
                Spacer()
            }
            .padding(LanSpacing.xl)
            .background(palette.canvas.ignoresSafeArea())
            .navigationTitle(UiText.appName)
            .navigationBarTitleDisplayMode(.inline)
        }
        .interactiveDismissDisabled(true)
        .presentationDetents([.medium])
    }

    private func peerHeader(_ peer: PeerInfo, palette: LanPalette) -> some View {
        HStack(spacing: LanSpacing.md) {
            PlatformBadgeView(deviceType: peer.deviceType, size: 42)
            VStack(alignment: .leading, spacing: 3) {
                Text(peer.displayName)
                    .font(.headline)
                    .foregroundStyle(palette.textPrimary)
                let addr = peer.ip.map { " · \($0.description)" } ?? ""
                Text("\(peer.deviceType.displayLabel)\(addr)")
                    .font(.caption.monospaced())
                    .foregroundStyle(palette.textMuted)
            }
            Spacer()
        }
        .lanCard(padding: 12)
    }

    private func sasBox(sas: String, palette: LanPalette) -> some View {
        VStack(spacing: 8) {
            Text(UiText.labelPairCodeHint)
                .font(.caption)
                .multilineTextAlignment(.center)
                .foregroundStyle(palette.textSecondary)
            Text(UiTextMapper.formatGroupedSas(sas))
                .font(.system(size: 34, weight: .bold, design: .monospaced))
                .tracking(4)
                .foregroundStyle(palette.accent)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 18)
        .padding(.horizontal, 12)
        .background(
            RoundedRectangle(cornerRadius: LanRadii.card, style: .continuous)
                .fill(palette.accentSoft)
        )
        .overlay(
            RoundedRectangle(cornerRadius: LanRadii.card, style: .continuous)
                .strokeBorder(palette.accent, lineWidth: 1.5)
        )
    }
}

// MARK: - 5. 全局接收文件确认 Sheet

public struct IncomingOfferSheet: View {
    public let offer: PendingIncomingOffer
    public let controller: AppController
    @Environment(\.colorScheme) private var colorScheme

    public init(offer: PendingIncomingOffer, controller: AppController) {
        self.offer = offer
        self.controller = controller
    }

    public var body: some View {
        let palette = LanPalette.resolve(colorScheme)

        NavigationStack {
            VStack(alignment: .leading, spacing: LanSpacing.md) {
                Text(UiText.labelIncomingOfferFrom(peer: offer.peerName))
                    .font(.headline)
                    .foregroundStyle(palette.textPrimary)

                Text("\(UiText.labelFileCount(count: offer.files.count)) · \(UiText.labelTotalSize(size: SizeFormatter.format(offer.totalSize)))")
                    .font(.subheadline.monospacedDigit())
                    .foregroundStyle(palette.textSecondary)

                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(Array(offer.files.enumerated()), id: \.offset) { idx, f in
                            HStack {
                                Text(f.name)
                                    .font(.subheadline.weight(.medium))
                                    .foregroundStyle(palette.textPrimary)
                                    .lineLimit(1)
                                Spacer()
                                Text(SizeFormatter.format(Int64(f.size)))
                                    .font(.caption.monospacedDigit())
                                    .foregroundStyle(palette.textMuted)
                            }
                            .padding(.horizontal, 14)
                            .padding(.vertical, 11)
                            if idx < offer.files.count - 1 {
                                Divider()
                            }
                        }
                    }
                    .background(RoundedRectangle(cornerRadius: LanRadii.card, style: .continuous).fill(palette.surface))
                    .overlay(RoundedRectangle(cornerRadius: LanRadii.card, style: .continuous).strokeBorder(palette.border, lineWidth: 1))
                }

                Text(UiText.iosLabelFilesAppDir)
                    .font(.caption)
                    .foregroundStyle(palette.textMuted)

                HStack(spacing: LanSpacing.md) {
                    Button {
                        controller.respondOffer(transferId: offer.transferId, accept: false)
                    } label: {
                        Text(UiText.btnReject)
                            .font(.headline)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 12)
                            .foregroundStyle(palette.textPrimary)
                            .background(RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous).fill(palette.surface))
                            .overlay(RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous).strokeBorder(palette.border, lineWidth: 1))
                    }
                    .buttonStyle(.plain)

                    Button {
                        controller.respondOffer(transferId: offer.transferId, accept: true)
                    } label: {
                        Text(UiText.btnReceive)
                            .font(.headline)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 12)
                            .foregroundStyle(palette.accentOn)
                            .background(RoundedRectangle(cornerRadius: LanRadii.subCard, style: .continuous).fill(palette.accent))
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(LanSpacing.lg)
            .background(palette.canvas.ignoresSafeArea())
            .navigationTitle(UiText.notifyFileReqTitle)
            .navigationBarTitleDisplayMode(.inline)
        }
        .interactiveDismissDisabled(true)
        .presentationDetents([.medium, .large])
    }
}
