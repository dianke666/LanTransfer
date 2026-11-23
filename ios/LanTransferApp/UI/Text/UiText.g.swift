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

    private static func get(_ key: String) -> String {
        zhHansBundle.localizedString(forKey: key, value: key, table: "Localizable")
    }

    private static func format(_ key: String, _ args: [CVarArg]) -> String {
        String(format: get(key), locale: zhHansLocale, arguments: args)
    }

    public static var appName: String { get("app_name") }
    public static var appSubtitle: String { get("app_subtitle") }
    public static var deviceTypeAndroid: String { get("device_type_android") }
    public static var deviceTypeWindows: String { get("device_type_windows") }
    public static var deviceTypeIos: String { get("device_type_ios") }
    public static var navDevices: String { get("nav_devices") }
    public static var navHistory: String { get("nav_history") }
    public static var navSettings: String { get("nav_settings") }
    public static var sectionLocalDevice: String { get("section_local_device") }
    public static var sectionNearbyDevices: String { get("section_nearby_devices") }
    public static var sectionTrustedDevices: String { get("section_trusted_devices") }
    public static var sectionAppearance: String { get("section_appearance") }
    public static var sectionTransferPrefs: String { get("section_transfer_prefs") }
    public static var themeSystem: String { get("theme_system") }
    public static var themeLight: String { get("theme_light") }
    public static var themeDark: String { get("theme_dark") }
    public static var labelIpAddress: String { get("label_ip_address") }
    public static var labelPort: String { get("label_port") }
    public static var labelPortOptional: String { get("label_port_optional") }
    public static var labelDeviceName: String { get("label_device_name") }
    public static var labelReceiveDir: String { get("label_receive_dir") }
    public static var labelDefaultReceiveDir: String { get("label_default_receive_dir") }
    public static var labelAutoCopy: String { get("label_auto_copy") }
    public static var labelAutoCopyDesc: String { get("label_auto_copy_desc") }
    public static var labelAutoAccept: String { get("label_auto_accept") }
    public static var labelTrustedBadge: String { get("label_trusted_badge") }
    public static var labelConnectedBadge: String { get("label_connected_badge") }
    public static var labelUntrustedBadge: String { get("label_untrusted_badge") }
    public static var labelEncryptedHint: String { get("label_encrypted_hint") }
    public static var labelDirectionSend: String { get("label_direction_send") }
    public static var labelDirectionReceive: String { get("label_direction_receive") }
    public static func labelFileCount(count: Any) -> String { format("label_file_count", [String(describing: count) as NSString]) }
    public static func labelTotalSize(size: Any) -> String { format("label_total_size", [String(describing: size) as NSString]) }
    public static func labelPendingFiles(count: Any) -> String { format("label_pending_files", [String(describing: count) as NSString]) }
    public static func labelSpeed(speed: Any) -> String { format("label_speed", [String(describing: speed) as NSString]) }
    public static func labelEta(eta: Any) -> String { format("label_eta", [String(describing: eta) as NSString]) }
    public static var labelEtaUnknown: String { get("label_eta_unknown") }
    public static func labelListenPort(port: Any) -> String { format("label_listen_port", [String(describing: port) as NSString]) }
    public static var labelNotListening: String { get("label_not_listening") }
    public static var labelSearchHistory: String { get("label_search_history") }
    public static var labelFilterAll: String { get("label_filter_all") }
    public static var labelFilterText: String { get("label_filter_text") }
    public static var labelFilterFile: String { get("label_filter_file") }
    public static var labelMessagePlaceholder: String { get("label_message_placeholder") }
    public static var labelWelcomeTitle: String { get("label_welcome_title") }
    public static var labelScanningDevices: String { get("label_scanning_devices") }
    public static var labelNoTrustedDevices: String { get("label_no_trusted_devices") }
    public static var labelNoHistory: String { get("label_no_history") }
    public static var labelNoMessages: String { get("label_no_messages") }
    public static var labelPairCodeHint: String { get("label_pair_code_hint") }
    public static var labelQrHint: String { get("label_qr_hint") }
    public static var labelQrAutoRefresh: String { get("label_qr_auto_refresh") }
    public static var labelCameraPermissionHint: String { get("label_camera_permission_hint") }
    public static func labelIncomingOfferFrom(peer: Any) -> String { format("label_incoming_offer_from", [String(describing: peer) as NSString]) }
    public static func labelPairRequestFrom(peer: Any) -> String { format("label_pair_request_from", [String(describing: peer) as NSString]) }
    public static func labelVersionDetail(local: Any, remote: Any) -> String { format("label_version_detail", [String(describing: local) as NSString, String(describing: remote) as NSString]) }
    public static var btnConnect: String { get("btn_connect") }
    public static var btnConnecting: String { get("btn_connecting") }
    public static var btnCancel: String { get("btn_cancel") }
    public static var btnAccept: String { get("btn_accept") }
    public static var btnReject: String { get("btn_reject") }
    public static var btnReceive: String { get("btn_receive") }
    public static var btnSend: String { get("btn_send") }
    public static var btnSendFiles: String { get("btn_send_files") }
    public static var btnSendPending: String { get("btn_send_pending") }
    public static var btnClearPending: String { get("btn_clear_pending") }
    public static var btnPaste: String { get("btn_paste") }
    public static var btnSendClipboard: String { get("btn_send_clipboard") }
    public static var btnCopy: String { get("btn_copy") }
    public static var btnCopyPath: String { get("btn_copy_path") }
    public static var btnCopyAddress: String { get("btn_copy_address") }
    public static var btnRetry: String { get("btn_retry") }
    public static var btnResume: String { get("btn_resume") }
    public static var btnExpand: String { get("btn_expand") }
    public static var btnCollapse: String { get("btn_collapse") }
    public static var btnSendAsTxt: String { get("btn_send_as_txt") }
    public static var btnReplaceAndSend: String { get("btn_replace_and_send") }
    public static var btnShowQr: String { get("btn_show_qr") }
    public static var btnScanQr: String { get("btn_scan_qr") }
    public static var btnQrPair: String { get("btn_qr_pair") }
    public static var btnManualConnect: String { get("btn_manual_connect") }
    public static var btnUnpair: String { get("btn_unpair") }
    public static var btnRepairPair: String { get("btn_repair_pair") }
    public static var btnOpenFile: String { get("btn_open_file") }
    public static var btnRevealInFolder: String { get("btn_reveal_in_folder") }
    public static var btnOpenReceiveDir: String { get("btn_open_receive_dir") }
    public static var btnChangeDir: String { get("btn_change_dir") }
    public static var btnSave: String { get("btn_save") }
    public static var btnDelete: String { get("btn_delete") }
    public static var btnDeleteRecordOnly: String { get("btn_delete_record_only") }
    public static var btnDeleteWithFile: String { get("btn_delete_with_file") }
    public static var btnClearHistory: String { get("btn_clear_history") }
    public static var btnClear: String { get("btn_clear") }
    public static var btnOpenNotificationSettings: String { get("btn_open_notification_settings") }
    public static var btnReceiveDirSettings: String { get("btn_receive_dir_settings") }
    public static var btnOpenAppSettings: String { get("btn_open_app_settings") }
    public static var btnConfirm: String { get("btn_confirm") }
    public static var btnClose: String { get("btn_close") }
    public static var btnRefreshQr: String { get("btn_refresh_qr") }
    public static var bannerNoNetwork: String { get("banner_no_network") }
    public static var bannerIdentityReset: String { get("banner_identity_reset") }
    public static var bannerIdentitySaveFailed: String { get("banner_identity_save_failed") }
    public static var bannerListenFailed: String { get("banner_listen_failed") }
    public static var bannerDiscoveryUnavailable: String { get("banner_discovery_unavailable") }
    public static var bannerHistoryStoreFailed: String { get("banner_history_store_failed") }
    public static var bannerNotificationsOff: String { get("banner_notifications_off") }
    public static var statusTextSending: String { get("status_text_sending") }
    public static var statusTextDelivered: String { get("status_text_delivered") }
    public static var statusTextFailed: String { get("status_text_failed") }
    public static var statusTextReceived: String { get("status_text_received") }
    public static var statusTaskOffering: String { get("status_task_offering") }
    public static var statusTaskAwaitingUser: String { get("status_task_awaiting_user") }
    public static var statusTaskRunning: String { get("status_task_running") }
    public static var statusTaskInterrupted: String { get("status_task_interrupted") }
    public static var statusTaskResuming: String { get("status_task_resuming") }
    public static var statusTaskVerifyingPrefix: String { get("status_task_verifying_prefix") }
    public static var statusTaskCompleted: String { get("status_task_completed") }
    public static var statusTaskRejectedSender: String { get("status_task_rejected_sender") }
    public static var statusTaskRejectedReceiver: String { get("status_task_rejected_receiver") }
    public static var statusTaskNoResponseSender: String { get("status_task_no_response_sender") }
    public static var statusTaskTimeoutReceiver: String { get("status_task_timeout_receiver") }
    public static var statusTaskNoSpace: String { get("status_task_no_space") }
    public static var statusTaskCannotSaveSender: String { get("status_task_cannot_save_sender") }
    public static var statusTaskCannotSaveReceiver: String { get("status_task_cannot_save_receiver") }
    public static var statusTaskConnectionLost: String { get("status_task_connection_lost") }
    public static var statusTaskCancelled: String { get("status_task_cancelled") }
    public static var statusTaskPeerCancelled: String { get("status_task_peer_cancelled") }
    public static var statusTaskPeerCancelledOffer: String { get("status_task_peer_cancelled_offer") }
    public static var statusTaskOfferInvalid: String { get("status_task_offer_invalid") }
    public static var statusInterruptPeerUnresponsive: String { get("status_interrupt_peer_unresponsive") }
    public static var statusInterruptPeerClosed: String { get("status_interrupt_peer_closed") }
    public static var statusInterruptLocalNetworkLost: String { get("status_interrupt_local_network_lost") }
    public static var statusInterruptIntegrityFailed: String { get("status_interrupt_integrity_failed") }
    public static var statusInterruptProtocolError: String { get("status_interrupt_protocol_error") }
    public static func statusInterruptWriteFailed(detail: Any) -> String { format("status_interrupt_write_failed", [String(describing: detail) as NSString]) }
    public static var statusInterruptAppExit: String { get("status_interrupt_app_exit") }
    public static var statusInterruptAppKilled: String { get("status_interrupt_app_killed") }
    public static var statusFileWaiting: String { get("status_file_waiting") }
    public static var statusFileTransferring: String { get("status_file_transferring") }
    public static var statusFileVerifying: String { get("status_file_verifying") }
    public static var statusFileWriteFailed: String { get("status_file_write_failed") }
    public static var statusFileCompleted: String { get("status_file_completed") }
    public static var statusFileReadFailed: String { get("status_file_read_failed") }
    public static var statusFileChecksumFailed: String { get("status_file_checksum_failed") }
    public static var statusPairWaiting: String { get("status_pair_waiting") }
    public static var statusPairCompleting: String { get("status_pair_completing") }
    public static var errorNotLocal: String { get("error_not_local") }
    public static var errorConnectFailed: String { get("error_connect_failed") }
    public static var errorConnectSendFile: String { get("error_connect_send_file") }
    public static var errorConnectResume: String { get("error_connect_resume") }
    public static var errorConnectSelf: String { get("error_connect_self") }
    public static var errorBindFailed: String { get("error_bind_failed") }
    public static var errorBindFailedVpn: String { get("error_bind_failed_vpn") }
    public static var errorIpInvalid: String { get("error_ip_invalid") }
    public static var errorPortInvalid: String { get("error_port_invalid") }
    public static var errorNoVersionResponse: String { get("error_no_version_response") }
    public static var errorVersionIncompatible: String { get("error_version_incompatible") }
    public static var errorNameInvalid: String { get("error_name_invalid") }
    public static var errorDirNotWritable: String { get("error_dir_not_writable") }
    public static var errorFileSizeUnknown: String { get("error_file_size_unknown") }
    public static var errorTooManyFiles: String { get("error_too_many_files") }
    public static var errorFoldersNotSupported: String { get("error_folders_not_supported") }
    public static var errorNoNetworkQr: String { get("error_no_network_qr") }
    public static var errorQrUnrecognized: String { get("error_qr_unrecognized") }
    public static var errorQrTokenInvalid: String { get("error_qr_token_invalid") }
    public static var errorNoFileHandler: String { get("error_no_file_handler") }
    public static var errorFileNotFound: String { get("error_file_not_found") }
    public static var errorReceiveDirInaccessible: String { get("error_receive_dir_inaccessible") }
    public static var errorPeerUnpaired: String { get("error_peer_unpaired") }
    public static var securityTitle: String { get("security_title") }
    public static func securityKeyMismatch(peer: Any) -> String { format("security_key_mismatch", [String(describing: peer) as NSString]) }
    public static var securityAuthFailed: String { get("security_auth_failed") }
    public static var securityQrMismatch: String { get("security_qr_mismatch") }
    public static var toastCopied: String { get("toast_copied") }
    public static var toastCopiedWithNul: String { get("toast_copied_with_nul") }
    public static var toastClipboardEmpty: String { get("toast_clipboard_empty") }
    public static var toastClipboardFailed: String { get("toast_clipboard_failed") }
    public static var toastNameCharsRemoved: String { get("toast_name_chars_removed") }
    public static var toastNameSaved: String { get("toast_name_saved") }
    public static var toastPairSuccess: String { get("toast_pair_success") }
    public static func toastPairSuccessPeer(peer: Any) -> String { format("toast_pair_success_peer", [String(describing: peer) as NSString]) }
    public static var toastPairRejected: String { get("toast_pair_rejected") }
    public static var toastPairTimeout: String { get("toast_pair_timeout") }
    public static var toastPairBusy: String { get("toast_pair_busy") }
    public static var toastPairFailed: String { get("toast_pair_failed") }
    public static var toastDeleteLocalFailed: String { get("toast_delete_local_failed") }
    public static var toastPendingCancelFailed: String { get("toast_pending_cancel_failed") }
    public static var dialogTextTooLong: String { get("dialog_text_too_long") }
    public static var dialogUnpairedSurrogate: String { get("dialog_unpaired_surrogate") }
    public static var dialogUnpairedSurrogateDesc: String { get("dialog_unpaired_surrogate_desc") }
    public static var dialogUnpairTitle: String { get("dialog_unpair_title") }
    public static func dialogUnpairDesc(peer: Any) -> String { format("dialog_unpair_desc", [String(describing: peer) as NSString]) }
    public static var dialogDeleteFileTitle: String { get("dialog_delete_file_title") }
    public static var dialogDeleteFileDesc: String { get("dialog_delete_file_desc") }
    public static var dialogDeleteInterrupted: String { get("dialog_delete_interrupted") }
    public static var dialogClearHistoryTitle: String { get("dialog_clear_history_title") }
    public static var dialogClearHistory: String { get("dialog_clear_history") }
    public static func dialogClearHistoryUnfinished(count: Any) -> String { format("dialog_clear_history_unfinished", [String(describing: count) as NSString]) }
    public static func notifyTextTitle(peer: Any) -> String { format("notify_text_title", [String(describing: peer) as NSString]) }
    public static func notifyFileDoneTitle(peer: Any) -> String { format("notify_file_done_title", [String(describing: peer) as NSString]) }
    public static var notifyPairReqTitle: String { get("notify_pair_req_title") }
    public static func notifyPairReqBody(peer: Any) -> String { format("notify_pair_req_body", [String(describing: peer) as NSString]) }
    public static var notifyFileReqTitle: String { get("notify_file_req_title") }
    public static func notifyFileReqBody(peer: Any, count: Any) -> String { format("notify_file_req_body", [String(describing: peer) as NSString, String(describing: count) as NSString]) }
    public static func notifyProblemTitle(peer: Any) -> String { format("notify_problem_title", [String(describing: peer) as NSString]) }
    public static var shareChooseDevice: String { get("share_choose_device") }
    public static func shareFilesSummary(count: Any) -> String { format("share_files_summary", [String(describing: count) as NSString]) }
    public static var shareTextSummary: String { get("share_text_summary") }
    public static var iosBannerLocalNetworkDenied: String { get("ios_banner_local_network_denied") }
    public static var iosLabelFilesAppDir: String { get("ios_label_files_app_dir") }
    public static var iosBtnOpenFilesApp: String { get("ios_btn_open_files_app") }
    public static var iosBtnPickPhotos: String { get("ios_btn_pick_photos") }
    public static var iosBtnPickFiles: String { get("ios_btn_pick_files") }
    public static var iosSectionBgPermissions: String { get("ios_section_bg_permissions") }
    public static var iosBgNoticeTitle: String { get("ios_bg_notice_title") }
    public static var iosBgNoticeDesc: String { get("ios_bg_notice_desc") }
    public static var iosBtnSystemSettings: String { get("ios_btn_system_settings") }
    public static var iosQrScreenshotWarning: String { get("ios_qr_screenshot_warning") }
    public static var iosBtnQuickLook: String { get("ios_btn_quick_look") }
    public static var iosNotifyBgPaused: String { get("ios_notify_bg_paused") }
}
