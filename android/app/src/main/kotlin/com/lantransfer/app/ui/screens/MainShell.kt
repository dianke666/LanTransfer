package com.lantransfer.app.ui.screens

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.lantransfer.app.BackgroundRunGuide
import com.lantransfer.app.ui.AppAlert
import com.lantransfer.app.ui.MainViewModel
import com.lantransfer.app.ui.icons.AppIcons
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.theme.LanTransferTheme
import com.lantransfer.app.ui.theme.LocalLanColors
import com.lantransfer.engine.ports.LocateResult
import kotlinx.coroutines.flow.collectLatest

object Routes {
    const val DEVICES = "devices"
    const val HISTORY = "history"
    const val SETTINGS = "settings"
    const val CONVERSATION = "conversation"
    const val QR_SHOW = "qr_show"
    const val QR_SCAN = "qr_scan"
    const val MANUAL_CONNECT = "manual_connect"
}

@Composable
fun MainShell(
    viewModel: MainViewModel,
    initialTabRoute: String? = null,
) {
    val ctx = LocalContext.current
    val state by viewModel.state.collectAsState()
    val navController = rememberNavController()
    val snackbarHostState = remember { SnackbarHostState() }

    LaunchedEffect(Unit) {
        viewModel.toasts.collect { msg ->
            snackbarHostState.currentSnackbarData?.dismiss()
            snackbarHostState.showSnackbar(msg)
        }
    }

    LaunchedEffect(Unit) {
        viewModel.openConversationEvents.collectLatest {
            navController.navigate(Routes.CONVERSATION) {
                launchSingleTop = true
            }
        }
    }

    LaunchedEffect(initialTabRoute) {
        if (initialTabRoute != null) {
            navController.navigate(initialTabRoute) {
                launchSingleTop = true
            }
        }
    }

    val backStack by navController.currentBackStackEntryAsState()
    val currentRoute = backStack?.destination?.route ?: Routes.DEVICES
    val showBottomBar = currentRoute in setOf(Routes.DEVICES, Routes.HISTORY, Routes.SETTINGS)

    LanTransferTheme(themeMode = state.themeMode) {
        val colors = LocalLanColors.current
        Scaffold(
            containerColor = colors.canvas,
            snackbarHost = { SnackbarHost(snackbarHostState) },
            bottomBar = {
                if (showBottomBar) {
                    NavigationBar(containerColor = colors.surface) {
                        NavigationBarItem(
                            selected = currentRoute == Routes.DEVICES,
                            onClick = {
                                navController.navigate(Routes.DEVICES) {
                                    popUpTo(navController.graph.findStartDestination().id) { saveState = true }
                                    launchSingleTop = true
                                    restoreState = true
                                }
                            },
                            icon = { Icon(AppIcons.Devices, contentDescription = null, modifier = Modifier.size(22.dp)) },
                            label = { Text(UiText.navDevices(ctx)) },
                        )
                        NavigationBarItem(
                            selected = currentRoute == Routes.HISTORY,
                            onClick = {
                                viewModel.reloadHistory()
                                navController.navigate(Routes.HISTORY) {
                                    popUpTo(navController.graph.findStartDestination().id) { saveState = true }
                                    launchSingleTop = true
                                    restoreState = true
                                }
                            },
                            icon = {
                                BadgedBox(
                                    badge = {
                                        if (state.unfinishedCount > 0) {
                                            Badge { Text(state.unfinishedCount.toString()) }
                                        }
                                    },
                                ) {
                                    Icon(AppIcons.History, contentDescription = null, modifier = Modifier.size(22.dp))
                                }
                            },
                            label = { Text(UiText.navHistory(ctx)) },
                        )
                        NavigationBarItem(
                            selected = currentRoute == Routes.SETTINGS,
                            onClick = {
                                navController.navigate(Routes.SETTINGS) {
                                    popUpTo(navController.graph.findStartDestination().id) { saveState = true }
                                    launchSingleTop = true
                                    restoreState = true
                                }
                            },
                            icon = { Icon(AppIcons.Settings, contentDescription = null, modifier = Modifier.size(22.dp)) },
                            label = { Text(UiText.navSettings(ctx)) },
                        )
                    }
                }
            },
        ) { innerPadding ->
            NavHost(
                navController = navController,
                startDestination = Routes.DEVICES,
                modifier = Modifier
                    .fillMaxSize()
                    .padding(innerPadding),
            ) {
                composable(Routes.DEVICES) {
                    DevicesScreen(
                        state = state,
                        onSelectDevice = viewModel::selectDevice,
                        onCopyAddress = viewModel::copyText,
                        onOpenScanQr = { navController.navigate(Routes.QR_SCAN) },
                        onOpenShowQr = { navController.navigate(Routes.QR_SHOW) },
                        onOpenManualConnect = { navController.navigate(Routes.MANUAL_CONNECT) },
                        onToggleTheme = viewModel::toggleQuickTheme,
                        onRetryNetworking = viewModel::retryNetworking,
                    )
                }
                composable(Routes.HISTORY) {
                    HistoryScreen(
                        state = state,
                        onFilterChange = viewModel::setHistoryFilter,
                        onQueryChange = viewModel::setHistoryQuery,
                        onCopyText = viewModel::copyText,
                        onOpenFile = viewModel::openStoredFile,
                        onRevealFile = viewModel::revealStoredFile,
                        onDeleteItem = viewModel::requestDeleteHistoryItem,
                        onClearAll = viewModel::requestClearHistory,
                    )
                }
                composable(Routes.SETTINGS) {
                    SettingsScreen(
                        state = state,
                        onSetTheme = viewModel::setThemeMode,
                        onSaveDeviceName = viewModel::saveDeviceName,
                        onSelectReceiveTreeUri = viewModel::setReceiveTreeUri,
                        onOpenReceiveDir = viewModel::openReceiveDirectory,
                        onSetAutoCopy = viewModel::setAutoCopy,
                        onSetBackgroundReceive = viewModel::setBackgroundReceive,
                        onOpenBackgroundGuide = viewModel::showBackgroundGuide,
                        onSetAutoAccept = viewModel::setAutoAccept,
                        onRequestUnpair = viewModel::requestUnpair,
                    )
                }
                composable(Routes.CONVERSATION) {
                    ConversationScreen(
                        state = state,
                        onBack = { navController.popBackStack() },
                        onSendText = viewModel::sendText,
                        onRetryText = viewModel::retryText,
                        onCopyText = viewModel::copyText,
                        onPasteToInput = viewModel::readClipboard,
                        onSendClipboard = viewModel::sendClipboardNow,
                        onAddUris = viewModel::addPickedUris,
                        onRemovePending = viewModel::removePendingFile,
                        onClearPending = viewModel::clearPendingFiles,
                        onSendPending = viewModel::sendPendingFiles,
                        onResumeTask = viewModel::resumeTransfer,
                        onCancelTask = viewModel::cancelTransfer,
                        onOpenFile = viewModel::openStoredFile,
                        onRevealFile = viewModel::revealStoredFile,
                    )
                }
                composable(Routes.QR_SHOW) {
                    QrShowScreen(
                        pairing = viewModel.app.services?.pairing,
                        onBack = { navController.popBackStack() },
                    )
                }
                composable(Routes.QR_SCAN) {
                    QrScanScreen(
                        onQrScanned = viewModel::pairByQrText,
                        onOpenManualConnect = {
                            navController.popBackStack()
                            navController.navigate(Routes.MANUAL_CONNECT)
                        },
                        onShowToast = viewModel::showToast,
                        onBack = { navController.popBackStack() },
                    )
                }
                composable(Routes.MANUAL_CONNECT) {
                    ManualConnectScreen(
                        onConnect = viewModel::pairByManual,
                        onBack = { navController.popBackStack() },
                    )
                }
            }

            // 全局配对状态弹窗
            PairingOverlayDialog(
                state = state.pairingUi,
                onRespond = viewModel::respondPairing,
                onCancel = viewModel::cancelPairing,
            )

            // 全局文件接收确认弹窗（队首展示，支持多个排队确认）
            state.incomingOffers.firstOrNull()?.let { offer ->
                IncomingOfferDialog(
                    view = offer,
                    onAccept = { viewModel.acceptOffer(offer.id) },
                    onReject = { viewModel.rejectOffer(offer.id) },
                )
            }

            // 全局安全、确认与定位回退对话框
            state.activeAlert?.let { alert ->
                GlobalAlertDialog(alert = alert, viewModel = viewModel)
            }
        }
    }
}

@Composable
private fun GlobalAlertDialog(alert: AppAlert, viewModel: MainViewModel) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current

    when (alert) {
        is AppAlert.Security -> AlertDialog(
            onDismissRequest = viewModel::dismissAlert,
            shape = RoundedCornerShape(20.dp),
            title = { Text(alert.title, color = colors.danger) },
            text = { Text(alert.message) },
            confirmButton = {
                Button(
                    onClick = viewModel::dismissAlert,
                    colors = ButtonDefaults.buttonColors(containerColor = colors.danger),
                ) { Text(UiText.btnConfirm(ctx)) }
            },
        )

        is AppAlert.VersionIncompatible -> AlertDialog(
            onDismissRequest = viewModel::dismissAlert,
            shape = RoundedCornerShape(20.dp),
            title = { Text(UiText.appName(ctx)) },
            text = { Text("${UiText.errorVersionIncompatible(ctx)}\n${UiText.labelVersionDetail(ctx, alert.local, alert.remote)}") },
            confirmButton = { Button(onClick = viewModel::dismissAlert) { Text(UiText.btnConfirm(ctx)) } },
        )

        is AppAlert.PeerUnpaired -> AlertDialog(
            onDismissRequest = viewModel::dismissAlert,
            shape = RoundedCornerShape(20.dp),
            title = { Text(UiText.appName(ctx)) },
            text = { Text(UiText.errorPeerUnpaired(ctx)) },
            confirmButton = {
                Button(
                    onClick = {
                        viewModel.dismissAlert()
                        val pid = alert.peerId
                        if (pid != null) {
                            viewModel.selectDevice(
                                com.lantransfer.app.ui.DeviceUiItem(
                                    deviceId = pid,
                                    displayName = "",
                                    deviceType = com.lantransfer.protocol.DeviceType.ANDROID,
                                    addressText = "",
                                    isTrusted = false,
                                    isConnected = false,
                                    isOnline = true,
                                ),
                            )
                        }
                    },
                ) { Text(UiText.btnRepairPair(ctx)) }
            },
            dismissButton = { TextButton(onClick = viewModel::dismissAlert) { Text(UiText.btnCancel(ctx)) } },
        )

        is AppAlert.SendTextAsFile -> AlertDialog(
            onDismissRequest = viewModel::dismissAlert,
            shape = RoundedCornerShape(20.dp),
            title = { Text(UiText.appName(ctx)) },
            text = { Text(UiText.dialogTextTooLong(ctx)) },
            confirmButton = {
                Button(onClick = { viewModel.confirmSendTextAsFile(alert.peerId, alert.text) }) {
                    Text(UiText.btnSendAsTxt(ctx))
                }
            },
            dismissButton = { TextButton(onClick = viewModel::dismissAlert) { Text(UiText.btnCancel(ctx)) } },
        )

        is AppAlert.ReplaceSurrogate -> AlertDialog(
            onDismissRequest = viewModel::dismissAlert,
            shape = RoundedCornerShape(20.dp),
            title = { Text(UiText.dialogUnpairedSurrogate(ctx)) },
            text = { Text(UiText.dialogUnpairedSurrogateDesc(ctx)) },
            confirmButton = {
                Button(onClick = { viewModel.confirmReplaceSurrogateAndSend(alert.peerId, alert.text) }) {
                    Text(UiText.btnReplaceAndSend(ctx))
                }
            },
            dismissButton = { TextButton(onClick = viewModel::dismissAlert) { Text(UiText.btnCancel(ctx)) } },
        )

        is AppAlert.ConfirmDeleteFile -> AlertDialog(
            onDismissRequest = viewModel::dismissAlert,
            shape = RoundedCornerShape(20.dp),
            title = { Text(UiText.dialogDeleteFileTitle(ctx)) },
            text = {
                Text(
                    if (alert.item.isInterrupted) UiText.dialogDeleteInterrupted(ctx) else UiText.dialogDeleteFileDesc(ctx),
                )
            },
            confirmButton = {
                if (alert.item.isInterrupted) {
                    Button(
                        onClick = { viewModel.deleteFileRecord(alert.item, deleteLocalFile = false) },
                        colors = ButtonDefaults.buttonColors(containerColor = colors.danger),
                    ) { Text(UiText.btnDelete(ctx)) }
                } else {
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        OutlinedButton(onClick = { viewModel.deleteFileRecord(alert.item, deleteLocalFile = false) }) {
                            Text(UiText.btnDeleteRecordOnly(ctx), style = MaterialTheme.typography.labelSmall)
                        }
                        Button(
                            onClick = { viewModel.deleteFileRecord(alert.item, deleteLocalFile = true) },
                            colors = ButtonDefaults.buttonColors(containerColor = colors.danger),
                        ) { Text(UiText.btnDeleteWithFile(ctx), style = MaterialTheme.typography.labelSmall) }
                    }
                }
            },
            dismissButton = { TextButton(onClick = viewModel::dismissAlert) { Text(UiText.btnCancel(ctx)) } },
        )

        is AppAlert.ConfirmClearHistory -> AlertDialog(
            onDismissRequest = viewModel::dismissAlert,
            shape = RoundedCornerShape(20.dp),
            title = { Text(UiText.dialogClearHistoryTitle(ctx)) },
            text = {
                Text(
                    if (alert.unfinishedCount > 0) {
                        UiText.dialogClearHistoryUnfinished(ctx, alert.unfinishedCount)
                    } else {
                        UiText.dialogClearHistory(ctx)
                    },
                )
            },
            confirmButton = {
                Button(
                    onClick = viewModel::confirmClearHistory,
                    colors = ButtonDefaults.buttonColors(containerColor = colors.danger),
                ) { Text(UiText.btnClear(ctx)) }
            },
            dismissButton = { TextButton(onClick = viewModel::dismissAlert) { Text(UiText.btnCancel(ctx)) } },
        )

        is AppAlert.ConfirmUnpair -> AlertDialog(
            onDismissRequest = viewModel::dismissAlert,
            shape = RoundedCornerShape(20.dp),
            title = { Text(UiText.dialogUnpairTitle(ctx)) },
            text = { Text(UiText.dialogUnpairDesc(ctx, alert.device.displayName)) },
            confirmButton = {
                Button(
                    onClick = { viewModel.confirmUnpair(alert.device) },
                    colors = ButtonDefaults.buttonColors(containerColor = colors.danger),
                ) { Text(UiText.btnUnpair(ctx)) }
            },
            dismissButton = { TextButton(onClick = viewModel::dismissAlert) { Text(UiText.btnCancel(ctx)) } },
        )

        is AppAlert.LocateFallback -> {
            val (msg, actionText, action) = when (alert.result) {
                LocateResult.NO_HANDLER -> Triple(
                    UiText.errorNoFileHandler(ctx),
                    UiText.btnRevealInFolder(ctx),
                    {
                        viewModel.dismissAlert()
                        alert.file?.let { viewModel.revealStoredFile(it, alert.pathHint) }
                    },
                )
                LocateResult.NOT_FOUND -> Triple(
                    UiText.errorFileNotFound(ctx),
                    UiText.btnOpenReceiveDir(ctx),
                    {
                        viewModel.dismissAlert()
                        viewModel.openReceiveDirectory()
                    },
                )
                LocateResult.NO_FILE_MANAGER -> Triple(
                    UiText.dialogNoFileManager(ctx, alert.pathHint),
                    UiText.btnCopyPath(ctx),
                    {
                        viewModel.dismissAlert()
                        viewModel.copyText(alert.pathHint)
                    },
                )
                else -> Triple(
                    UiText.errorReceiveDirInaccessible(ctx),
                    UiText.btnCopyPath(ctx),
                    {
                        viewModel.dismissAlert()
                        viewModel.copyText(alert.pathHint)
                    },
                )
            }
            AlertDialog(
                onDismissRequest = viewModel::dismissAlert,
                shape = RoundedCornerShape(20.dp),
                title = { Text(UiText.appName(ctx)) },
                text = { Text(msg) },
                confirmButton = { Button(onClick = { action() }) { Text(actionText) } },
                dismissButton = { TextButton(onClick = viewModel::dismissAlert) { Text(UiText.btnClose(ctx)) } },
            )
        }

        AppAlert.BackgroundGuide -> {
            val xiaomi = BackgroundRunGuide.isXiaomiFamily()
            AlertDialog(
                onDismissRequest = viewModel::dismissAlert,
                shape = RoundedCornerShape(20.dp),
                title = { Text(UiText.guideBgTitle(ctx)) },
                text = {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(if (xiaomi) UiText.guideBgIntroXiaomi(ctx) else UiText.guideBgIntroGeneric(ctx))
                        Text(UiText.guideBgStepBattery(ctx), style = MaterialTheme.typography.bodySmall)
                        if (xiaomi) {
                            Text(UiText.guideBgStepAutostart(ctx), style = MaterialTheme.typography.bodySmall)
                            Text(UiText.guideBgStepLock(ctx), style = MaterialTheme.typography.bodySmall)
                        }
                        Spacer(Modifier.height(4.dp))
                        Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            OutlinedButton(
                                onClick = { BackgroundRunGuide.openBatterySettings(ctx) },
                                modifier = Modifier.weight(1f),
                            ) { Text(UiText.btnGuideBattery(ctx), style = MaterialTheme.typography.labelSmall) }
                            if (xiaomi) {
                                OutlinedButton(
                                    onClick = { BackgroundRunGuide.openAutostartSettings(ctx) },
                                    modifier = Modifier.weight(1f),
                                ) { Text(UiText.btnGuideAutostart(ctx), style = MaterialTheme.typography.labelSmall) }
                            }
                        }
                    }
                },
                confirmButton = {
                    Button(onClick = viewModel::dismissAlert) { Text(UiText.btnGuideDone(ctx)) }
                },
            )
        }
    }
}
