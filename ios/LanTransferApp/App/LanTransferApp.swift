import LanTransferDomain
import LanTransferEngine
import LanTransferProtocol
import SwiftUI

@main
struct LanTransferApp: App {
    @State private var controller = AppController()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootAppView(controller: controller)
                .preferredColorScheme(controller.snapshot.settings.themeMode.preferredColorScheme)
                .environment(\.locale, Locale(identifier: "zh-Hans-CN"))
                .onAppear {
                    controller.startIfNeeded()
                }
                .onChange(of: scenePhase) { _, phase in
                    controller.handleScenePhase(phase)
                }
                .onOpenURL { url in
                    controller.handleIncomingURL(url)
                }
        }
    }
}

private struct RootAppView: View {
    @Bindable var controller: AppController
    @Environment(\.colorScheme) private var colorScheme

    private var palette: LanPalette {
        LanPalette.resolve(colorScheme)
    }

    private var isPairingSheetPresented: Binding<Bool> {
        Binding(
            get: { controller.snapshot.pairingState != .idle },
            set: { if !$0 { controller.cancelPairing() } }
        )
    }

    private var currentIncomingOffer: PendingIncomingOffer? {
        controller.snapshot.pendingOffers.first
    }

    var body: some View {
        ZStack {
            TabView(selection: $controller.selectedTab) {
                NavigationStack(path: $controller.navigationPath) {
                    DevicesTabView(controller: controller)
                        .navigationDestination(for: DeviceId.self) { peerId in
                            ConversationView(peerId: peerId, controller: controller)
                        }
                }
                .tabItem {
                    Label(UiText.navDevices, systemImage: "wifi")
                }
                .tag(AppTab.devices)

                NavigationStack {
                    HistoryTabView(controller: controller)
                }
                .tabItem {
                    Label(UiText.navHistory, systemImage: "clock.arrow.circlepath")
                }
                .badge(controller.unfinishedTaskCount)
                .tag(AppTab.history)

                NavigationStack {
                    SettingsTabView(controller: controller)
                }
                .tabItem {
                    Label(UiText.navSettings, systemImage: "gearshape")
                }
                .tag(AppTab.settings)
            }
            .tint(palette.accent)

            ToastOverlayView(message: controller.toastMessage)
        }
        .sheet(isPresented: $controller.showQrSheet) {
            QrShowSheet(controller: controller)
        }
        .fullScreenCover(isPresented: $controller.showQrScanner) {
            QrScanView(controller: controller)
        }
        .sheet(isPresented: $controller.showManualConnect) {
            ManualConnectSheet(controller: controller)
        }
        .sheet(isPresented: isPairingSheetPresented) {
            PairingSheet(state: controller.snapshot.pairingState, controller: controller)
        }
        .sheet(item: Binding(
            get: { currentIncomingOffer },
            set: { _ in }
        )) { offer in
            IncomingOfferSheet(offer: offer, controller: controller)
        }
        .sheet(isPresented: Binding(
            get: { controller.quickLookURL != nil },
            set: { if !$0 { controller.quickLookURL = nil } }
        )) {
            if let url = controller.quickLookURL {
                QuickLookPreviewSheet(url: url)
                    .ignoresSafeArea()
            }
        }
        .alert(
            controller.alertPayload?.title ?? "",
            isPresented: Binding(
                get: { controller.alertPayload != nil },
                set: { if !$0 { controller.alertPayload = nil } }
            ),
            presenting: controller.alertPayload
        ) { _ in
            Button(UiText.btnConfirm, role: .cancel) {
                controller.alertPayload = nil
            }
        } message: { alert in
            Text(alert.message)
        }
        .sensoryFeedback(trigger: controller.hapticTrigger) { _, _ in
            switch controller.lastHapticKind {
            case .light: return .impact(weight: .light)
            case .success: return .success
            case .warning: return .warning
            case .error: return .error
            }
        }
    }
}
