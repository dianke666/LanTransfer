// 自动生成自 shared/ui-text/zh-CN.tsv，请勿手工修改。
import Foundation

public enum UiText: Sendable {
    private static let zhHansBundle: Bundle = {
        if let path = Bundle.main.path(forResource: "zh-Hans", ofType: "lproj"),
           let bundle = Bundle(path: path) {
            return bundle
        }
        return Bundle.main
    }()

    private static let zhHansLocale = Locale(identifier: "zh-Hans-CN")

    private static func lookup(_ key: String) -> String {
        zhHansBundle.localizedString(forKey: key, value: key, table: "Localizable")
    }

    private static func format(_ key: String, _ args: [CVarArg]) -> String {
        String(format: lookup(key), locale: zhHansLocale, arguments: args)
    }

    public static var appName: String { lookup("app_name") }
    public static var appSubtitle: String { lookup("app_subtitle") }
    public static var deviceTypeAndroid: String { lookup("device_type_android") }
    public static var deviceTypeWindows: String { lookup("device_type_windows") }
    public static var deviceTypeIos: String { lookup("device_type_ios") }
    public static var navDevices: String { lookup("nav_devices") }
    public static var navHistory: String { lookup("nav_history") }
    public static var navSettings: String { lookup("nav_settings") }
    public static var sectionLocalDevice: String { lookup("section_local_device") }
    public static var sectionNearbyDevices: String { lookup("section_nearby_devices") }
    public static var sectionTrustedDevices: String { lookup("section_trusted_devices") }
    public static var sectionAppearance: String { lookup("section_appearance") }
    public static var sectionTransferPrefs: String { lookup("section_transfer_prefs") }
    public static var themeSystem: String { lookup("theme_system") }
    public static var themeLight: String { lookup("theme_light") }
    public static var themeDark: String { lookup("theme_dark") }
    public static var labelIpAddress: String { lookup("label_ip_address") }
    public static var labelPort: String { lookup("label_port") }
    public static var labelPortOptional: String { lookup("label_port_optional") }
    public static var labelDeviceName: String { lookup("label_device_name") }
    public static var labelReceiveDir: String { lookup("label_receive_dir") }
    public static var labelDefaultReceiveDir: String { lookup("label_default_receive_dir") }
    public static var labelAutoCopy: String { lookup("label_auto_copy") }
    public static var labelAutoCopyDesc: String { lookup("label_auto_copy_desc") }
    public static var labelAutoAccept: String { lookup("label_auto_accept") }
    public static var labelTrustedBadge: String { lookup("label_trusted_badge") }
    public static var labelConnectedBadge: String { lookup("label_connected_badge") }
    public static var labelUntrustedBadge: String { lookup("label_untrusted_badge") }
    public static var labelEncryptedHint: String { lookup("label_encrypted_hint") }
    public static var labelDirectionSend: String { lookup("label_direction_send") }
    public static var labelDirectionReceive: String { lookup("label_direction_receive") }
    public static func labelFileCount(count: Any) -> String { format("label_file_count", [String(describing: count) as NSString]) }
    public static func labelTotalSize(size: Any) -> String { format("label_total_size", [String(describing: size) as NSString]) }
    public static func labelPendingFiles(count: Any) -> String { format("label_pending_files", [String(describing: count) as NSString]) }
    public static func labelSpeed(speed: Any) -> String { format("label_speed", [String(describing: speed) as NSString]) }
    public static func labelEta(eta: Any) -> String { format("label_eta", [String(describing: eta) as NSString]) }
    public static var labelEtaUnknown: String { lookup("label_eta_unknown") }
    public static func labelListenPort(port: Any) -> String { format("label_listen_port", [String(describing: port) as NSString]) }
    public static var labelNotListening: String { lookup("label_not_listening") }
    public static var labelSearchHistory: String { lookup("label_search_history") }
    public static var labelFilterAll: String { lookup("label_filter_all") }
    public static var labelFilterText: String { lookup("label_filter_text") }
    public static var labelFilterFile: String { lookup("label_filter_file") }
    public static var labelMessagePlaceholder: String { lookup("label_message_placeholder") }
    public static var labelWelcomeTitle: String { lookup("label_welcome_title") }
    public static var labelScanningDevices: String { lookup("label_scanning_devices") }
    public static var labelNoTrustedDevices: String { lookup("label_no_trusted_devices") }
    public static var labelNoHistory: String { lookup("label_no_history") }
    public static var labelNoMessages: String { lookup("label_no_messages") }
    public static var labelPairCodeHint: String { lookup("label_pair_code_hint") }
    public static var labelQrHint: String { lookup("label_qr_hint") }
    public static var labelQrAutoRefresh: String { lookup("label_qr_auto_refresh") }
    public static var labelCameraPermissionHint: String { lookup("label_camera_permission_hint") }
    public static func labelIncomingOfferFrom(peer: Any) -> String { format("label_incoming_offer_from", [String(describing: peer) as NSString]) }
    public static func labelPairRequestFrom(peer: Any) -> String { format("label_pair_request_from", [String(describing: peer) as NSString]) }
    public static func labelVersionDetail(local: Any, remote: Any) -> String { format("label_version_detail", [String(describing: local) as NSString, String(describing: remote) as NSString]) }
    public static var btnConnect: String { lookup("btn_connect") }
    public static var btnConnecting: String { lookup("btn_connecting") }
    public static var btnCancel: String { lookup("btn_cancel") }
    public static var btnAccept: String { lookup("btn_accept") }
    public static var btnReject: String { lookup("btn_reject") }
    public static var btnReceive: String { lookup("btn_receive") }
    public static var btnSend: String { lookup("btn_send") }
    public static var btnSendFiles: String { lookup("btn_send_files") }
    public static var btnSendPending: String { lookup("btn_send_pending") }
    public static var btnClearPending: String { lookup("btn_clear_pending") }
    public static var btnPaste: String { lookup("btn_paste") }
    public static var btnSendClipboard: String { lookup("btn_send_clipboard") }
    public static var btnCopy: String { lookup("btn_copy") }
    public static var btnCopyPath: String { lookup("btn_copy_path") }
    public static var btnCopyAddress: String { lookup("btn_copy_address") }
    public static var btnRetry: String { lookup("btn_retry") }
    public static var btnResume: String { lookup("btn_resume") }
    public static var btnExpand: String { lookup("btn_expand") }
    public static var btnCollapse: String { lookup("btn_collapse") }
    public static var btnSendAsTxt: String { lookup("btn_send_as_txt") }
    public static var btnReplaceAndSend: String { lookup("btn_replace_and_send") }
    public static var btnShowQr: String { lookup("btn_show_qr") }
    public static var btnScanQr: String { lookup("btn_scan_qr") }
    public static var btnQrPair: String { lookup("btn_qr_pair") }
    public static var btnManualConnect: String { lookup("btn_manual_connect") }
    public static var btnUnpair: String { lookup("btn_unpair") }
    public static var btnRepairPair: String { lookup("btn_repair_pair") }
    public static var btnOpenFile: String { lookup("btn_open_file") }
    public static var btnRevealInFolder: String { lookup("btn_reveal_in_folder") }
    public static var btnOpenReceiveDir: String { lookup("btn_open_receive_dir") }
    public static var btnChangeDir: String { lookup("btn_change_dir") }
    public static var btnSave: String { lookup("btn_save") }
    public static var btnDelete: String { lookup("btn_delete") }
    public static var btnDeleteRecordOnly: String { lookup("btn_delete_record_only") }
    public static var btnDeleteWithFile: String { lookup("btn_delete_with_file") }
    public static var btnClearHistory: String { lookup("btn_clear_history") }
    public static var btnClear: String { lookup("btn_clear") }
    public static var btnOpenNotificationSettings: String { lookup("btn_open_notification_settings") }
    public static var btnReceiveDirSettings: String { lookup("btn_receive_dir_settings") }
    public static var btnOpenAppSettings: String { lookup("btn_open_app_settings") }
    public static var btnConfirm: String { lookup("btn_confirm") }
    public static var btnClose: String { lookup("btn_close") }
    public static var btnRefreshQr: String { lookup("btn_refresh_qr") }
    public static var bannerNoNetwork: String { lookup("banner_no_network") }
    public static var bannerIdentityReset: String { lookup("banner_identity_reset") }
    public static var bannerIdentitySaveFailed: String { lookup("banner_identity_save_failed") }
    public static var bannerListenFailed: String { lookup("banner_listen_failed") }
    public static var bannerDiscoveryUnavailable: String { lookup("banner_discovery_unavailable") }
    public static var bannerHistoryStoreFailed: String { lookup("banner_history_store_failed") }
    public static var bannerNotificationsOff: String { lookup("banner_notifications_off") }
    public static var statusTextSending: String { lookup("status_text_sending") }
    public static var statusTextDelivered: String { lookup("status_text_delivered") }
    public static var statusTextFailed: String { lookup("status_text_failed") }
    public static var statusTextReceived: String { lookup("status_text_received") }
    public static var statusTaskOffering: String { lookup("status_task_offering") }
    public static var statusTaskAwaitingUser: String { lookup("status_task_awaiting_user") }
    public static var statusTaskRunning: String { lookup("status_task_running") }
    public static var statusTaskInterrupted: String { lookup("status_task_interrupted") }
    public static var statusTaskResuming: String { lookup("status_task_resuming") }
    public static var statusTaskVerifyingPrefix: String { lookup("status_task_verifying_prefix") }
    public static var statusTaskCompleted: String { lookup("status_task_completed") }
    public static var statusTaskRejectedSender: String { lookup("status_task_rejected_sender") }
    public static var statusTaskRejectedReceiver: String { lookup("status_task_rejected_receiver") }
    public static var statusTaskNoResponseSender: String { lookup("status_task_no_response_sender") }
    public static var statusTaskTimeoutReceiver: String { lookup("status_task_timeout_receiver") }
    public static var statusTaskNoSpace: String { lookup("status_task_no_space") }
    public static var statusTaskCannotSaveSender: String { lookup("status_task_cannot_save_sender") }
    public static var statusTaskCannotSaveReceiver: String { lookup("status_task_cannot_save_receiver") }
    public static var statusTaskConnectionLost: String { lookup("status_task_connection_lost") }
    public static var statusTaskCancelled: String { lookup("status_task_cancelled") }
    public static var statusTaskPeerCancelled: String { lookup("status_task_peer_cancelled") }
    public static var statusTaskPeerCancelledOffer: String { lookup("status_task_peer_cancelled_offer") }
    public static var statusTaskOfferInvalid: String { lookup("status_task_offer_invalid") }
    public static var statusInterruptPeerUnresponsive: String { lookup("status_interrupt_peer_unresponsive") }
    public static var statusInterruptPeerClosed: String { lookup("status_interrupt_peer_closed") }
    public static var statusInterruptLocalNetworkLost: String { lookup("status_interrupt_local_network_lost") }
    public static var statusInterruptIntegrityFailed: String { lookup("status_interrupt_integrity_failed") }
    public static var statusInterruptProtocolError: String { lookup("status_interrupt_protocol_error") }
    public static func statusInterruptWriteFailed(detail: Any) -> String { format("status_interrupt_write_failed", [String(describing: detail) as NSString]) }
    public static var statusInterruptAppExit: String { lookup("status_interrupt_app_exit") }
    public static var statusInterruptAppKilled: String { lookup("status_interrupt_app_killed") }
    public static var statusFileWaiting: String { lookup("status_file_waiting") }
    public static var statusFileTransferring: String { lookup("status_file_transferring") }
    public static var statusFileVerifying: String { lookup("status_file_verifying") }
    public static var statusFileWriteFailed: String { lookup("status_file_write_failed") }
    public static var statusFileCompleted: String { lookup("status_file_completed") }
    public static var statusFileReadFailed: String { lookup("status_file_read_failed") }
    public static var statusFileChecksumFailed: String { lookup("status_file_checksum_failed") }
    public static var statusPairWaiting: String { lookup("status_pair_waiting") }
    public static var statusPairCompleting: String { lookup("status_pair_completing") }
    public static var errorNotLocal: String { lookup("error_not_local") }
    public static var errorConnectFailed: String { lookup("error_connect_failed") }
    public static var errorConnectSendFile: String { lookup("error_connect_send_file") }
    public static var errorConnectResume: String { lookup("error_connect_resume") }
    public static var errorConnectSelf: String { lookup("error_connect_self") }
    public static var errorBindFailed: String { lookup("error_bind_failed") }
    public static var errorBindFailedVpn: String { lookup("error_bind_failed_vpn") }
    public static var errorIpInvalid: String { lookup("error_ip_invalid") }
    public static var errorPortInvalid: String { lookup("error_port_invalid") }
    public static var errorNoVersionResponse: String { lookup("error_no_version_response") }
    public static var errorVersionIncompatible: String { lookup("error_version_incompatible") }
    public static var errorNameInvalid: String { lookup("error_name_invalid") }
    public static var errorDirNotWritable: String { lookup("error_dir_not_writable") }
    public static var errorFileSizeUnknown: String { lookup("error_file_size_unknown") }
    public static var errorTooManyFiles: String { lookup("error_too_many_files") }
    public static var errorFoldersNotSupported: String { lookup("error_folders_not_supported") }
    public static var errorNoNetworkQr: String { lookup("error_no_network_qr") }
    public static var errorQrUnrecognized: String { lookup("error_qr_unrecognized") }
    public static var errorQrTokenInvalid: String { lookup("error_qr_token_invalid") }
    public static var errorNoFileHandler: String { lookup("error_no_file_handler") }
    public static var errorFileNotFound: String { lookup("error_file_not_found") }
    public static var errorReceiveDirInaccessible: String { lookup("error_receive_dir_inaccessible") }
    public static var errorPeerUnpaired: String { lookup("error_peer_unpaired") }
    public static var securityTitle: String { lookup("security_title") }
    public static func securityKeyMismatch(peer: Any) -> String { format("security_key_mismatch", [String(describing: peer) as NSString]) }
    public static var securityAuthFailed: String { lookup("security_auth_failed") }
    public static var securityQrMismatch: String { lookup("security_qr_mismatch") }
    public static var toastCopied: String { lookup("toast_copied") }
    public static var toastCopiedWithNul: String { lookup("toast_copied_with_nul") }
    public static var toastClipboardEmpty: String { lookup("toast_clipboard_empty") }
    public static var toastClipboardFailed: String { lookup("toast_clipboard_failed") }
    public static var toastNameCharsRemoved: String { lookup("toast_name_chars_removed") }
    public static var toastNameSaved: String { lookup("toast_name_saved") }
    public static var toastPairSuccess: String { lookup("toast_pair_success") }
    public static func toastPairSuccessPeer(peer: Any) -> String { format("toast_pair_success_peer", [String(describing: peer) as NSString]) }
    public static var toastPairRejected: String { lookup("toast_pair_rejected") }
    public static var toastPairTimeout: String { lookup("toast_pair_timeout") }
    public static var toastPairBusy: String { lookup("toast_pair_busy") }
    public static var toastPairFailed: String { lookup("toast_pair_failed") }
    public static var toastDeleteLocalFailed: String { lookup("toast_delete_local_failed") }
    public static var toastPendingCancelFailed: String { lookup("toast_pending_cancel_failed") }
    public static var dialogTextTooLong: String { lookup("dialog_text_too_long") }
    public static var dialogUnpairedSurrogate: String { lookup("dialog_unpaired_surrogate") }
    public static var dialogUnpairedSurrogateDesc: String { lookup("dialog_unpaired_surrogate_desc") }
    public static var dialogUnpairTitle: String { lookup("dialog_unpair_title") }
    public static func dialogUnpairDesc(peer: Any) -> String { format("dialog_unpair_desc", [String(describing: peer) as NSString]) }
    public static var dialogDeleteFileTitle: String { lookup("dialog_delete_file_title") }
    public static var dialogDeleteFileDesc: String { lookup("dialog_delete_file_desc") }
    public static var dialogDeleteInterrupted: String { lookup("dialog_delete_interrupted") }
    public static var dialogClearHistoryTitle: String { lookup("dialog_clear_history_title") }
    public static var dialogClearHistory: String { lookup("dialog_clear_history") }
    public static func dialogClearHistoryUnfinished(count: Any) -> String { format("dialog_clear_history_unfinished", [String(describing: count) as NSString]) }
    public static func notifyTextTitle(peer: Any) -> String { format("notify_text_title", [String(describing: peer) as NSString]) }
    public static func notifyFileDoneTitle(peer: Any) -> String { format("notify_file_done_title", [String(describing: peer) as NSString]) }
    public static var notifyPairReqTitle: String { lookup("notify_pair_req_title") }
    public static func notifyPairReqBody(peer: Any) -> String { format("notify_pair_req_body", [String(describing: peer) as NSString]) }
    public static var notifyFileReqTitle: String { lookup("notify_file_req_title") }
    public static func notifyFileReqBody(peer: Any, count: Any) -> String { format("notify_file_req_body", [String(describing: peer) as NSString, String(describing: count) as NSString]) }
    public static func notifyProblemTitle(peer: Any) -> String { format("notify_problem_title", [String(describing: peer) as NSString]) }
    public static var shareChooseDevice: String { lookup("share_choose_device") }
    public static func shareFilesSummary(count: Any) -> String { format("share_files_summary", [String(describing: count) as NSString]) }
    public static var shareTextSummary: String { lookup("share_text_summary") }
    public static var iosBannerLocalNetworkDenied: String { lookup("ios_banner_local_network_denied") }
    public static var iosLabelFilesAppDir: String { lookup("ios_label_files_app_dir") }
    public static var iosBtnOpenFilesApp: String { lookup("ios_btn_open_files_app") }
    public static var iosBtnPickPhotos: String { lookup("ios_btn_pick_photos") }
    public static var iosBtnPickFiles: String { lookup("ios_btn_pick_files") }
    public static var iosSectionBgPermissions: String { lookup("ios_section_bg_permissions") }
    public static var iosBgNoticeTitle: String { lookup("ios_bg_notice_title") }
    public static var iosBgNoticeDesc: String { lookup("ios_bg_notice_desc") }
    public static var iosBtnSystemSettings: String { lookup("ios_btn_system_settings") }
    public static var iosQrScreenshotWarning: String { lookup("ios_qr_screenshot_warning") }
    public static var iosBtnQuickLook: String { lookup("ios_btn_quick_look") }
    public static var iosNotifyBgPaused: String { lookup("ios_notify_bg_paused") }
}
