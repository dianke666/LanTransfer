package com.lantransfer.app.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.lantransfer.app.BackgroundRunGuide
import com.lantransfer.app.ui.DeviceUiItem
import com.lantransfer.app.ui.MainUiState
import com.lantransfer.app.ui.icons.AppIcons
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.text.UiTextMapper
import com.lantransfer.app.ui.theme.LocalLanColors
import com.lantransfer.app.ui.theme.MonoTextStyle

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun DevicesScreen(
    state: MainUiState,
    onSelectDevice: (DeviceUiItem) -> Unit,
    onCopyAddress: (String) -> Unit,
    onOpenScanQr: () -> Unit,
    onOpenShowQr: () -> Unit,
    onOpenManualConnect: () -> Unit,
    onToggleTheme: () -> Unit,
    onRetryNetworking: () -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current

    LazyColumn(
        modifier = Modifier
            .fillMaxSize()
            .background(colors.canvas),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        // 顶部品牌栏 + 主题快速切换
        item {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(
                    modifier = Modifier
                        .size(40.dp)
                        .clip(RoundedCornerShape(12.dp))
                        .background(colors.accent),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        imageVector = AppIcons.Logo,
                        contentDescription = null,
                        tint = Color.White,
                        modifier = Modifier.size(22.dp),
                    )
                }
                Spacer(Modifier.width(12.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = UiText.appName(ctx),
                        style = MaterialTheme.typography.titleLarge,
                        color = colors.textPrimary,
                    )
                    Text(
                        text = UiText.appSubtitle(ctx),
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.textMuted,
                    )
                }
                IconButton(onClick = onToggleTheme) {
                    Icon(
                        imageVector = AppIcons.Sun,
                        contentDescription = UiText.sectionAppearance(ctx),
                        tint = colors.textSecondary,
                        modifier = Modifier.size(20.dp),
                    )
                }
            }
        }

        // 系统/网络/安全横幅区
        item {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (!state.hasNetwork) {
                    AlertBanner(
                        text = UiText.bannerNoNetwork(ctx),
                        bg = colors.warningSoft,
                        border = colors.warning,
                        textColor = colors.textPrimary,
                    )
                }
                if (state.discoveryUnavailable) {
                    AlertBanner(
                        text = UiText.bannerDiscoveryUnavailable(ctx),
                        bg = colors.warningSoft,
                        border = colors.warning,
                        textColor = colors.textPrimary,
                    )
                }
                if (state.listenFailed) {
                    AlertBanner(
                        text = UiText.bannerListenFailed(ctx),
                        bg = colors.dangerSoft,
                        border = colors.danger,
                        textColor = colors.danger,
                        actionLabel = UiText.btnRetry(ctx),
                        onAction = onRetryNetworking,
                    )
                }
                if (state.identityReset) {
                    AlertBanner(
                        text = UiText.bannerIdentityReset(ctx),
                        bg = colors.warningSoft,
                        border = colors.warning,
                        textColor = colors.textPrimary,
                    )
                }
                if (state.identitySaveFailed) {
                    AlertBanner(
                        text = UiText.bannerIdentitySaveFailed(ctx),
                        bg = colors.dangerSoft,
                        border = colors.danger,
                        textColor = colors.danger,
                        actionLabel = UiText.btnRetry(ctx),
                        onAction = onRetryNetworking,
                    )
                }
                if (state.notificationsOff) {
                    AlertBanner(
                        text = UiText.bannerNotificationsOff(ctx),
                        bg = colors.accentSoft,
                        border = colors.accent,
                        textColor = colors.textPrimary,
                        actionLabel = UiText.btnOpenNotificationSettings(ctx),
                        onAction = { BackgroundRunGuide.openNotificationSettings(ctx) },
                    )
                }
                if (state.historyStoreFailed) {
                    AlertBanner(
                        text = UiText.bannerHistoryStoreFailed(ctx),
                        bg = colors.dangerSoft,
                        border = colors.danger,
                        textColor = colors.danger,
                    )
                }
            }
        }

        // 本机状态英雄卡片
        item {
            Surface(
                shape = RoundedCornerShape(18.dp),
                color = colors.surface,
                border = BorderStroke(1.dp, colors.border),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(
                            modifier = Modifier
                                .size(9.dp)
                                .clip(CircleShape)
                                .background(if (state.hasNetwork && state.listenPort != null) colors.success else colors.warning),
                        )
                        Spacer(Modifier.width(8.dp))
                        Text(
                            text = state.localDeviceName,
                            style = MaterialTheme.typography.titleMedium,
                            color = colors.textPrimary,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f),
                        )
                        Surface(
                            shape = RoundedCornerShape(999.dp),
                            color = colors.successSoft,
                        ) {
                            Row(
                                modifier = Modifier.padding(horizontal = 9.dp, vertical = 4.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Icon(
                                    imageVector = AppIcons.Shield,
                                    contentDescription = null,
                                    tint = colors.success,
                                    modifier = Modifier.size(12.dp),
                                )
                                Spacer(Modifier.width(4.dp))
                                Text(
                                    text = "端到端加密",
                                    style = MaterialTheme.typography.labelSmall,
                                    color = colors.success,
                                )
                            }
                        }
                    }

                    Spacer(Modifier.height(10.dp))

                    if (state.localEndpoints.isNotEmpty()) {
                        FlowRow(
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            verticalArrangement = Arrangement.spacedBy(6.dp),
                        ) {
                            for (ep in state.localEndpoints) {
                                Surface(
                                    shape = RoundedCornerShape(10.dp),
                                    color = colors.surfaceSubtle,
                                    modifier = Modifier.clickable { onCopyAddress(ep) },
                                ) {
                                    Row(
                                        modifier = Modifier.padding(horizontal = 10.dp, vertical = 6.dp),
                                        verticalAlignment = Alignment.CenterVertically,
                                    ) {
                                        Text(
                                            text = ep,
                                            style = MonoTextStyle.copy(fontSize = MaterialTheme.typography.bodySmall.fontSize),
                                            color = colors.textSecondary,
                                        )
                                        Spacer(Modifier.width(6.dp))
                                        Icon(
                                            imageVector = AppIcons.Copy,
                                            contentDescription = UiText.btnCopyAddress(ctx),
                                            tint = colors.textMuted,
                                            modifier = Modifier.size(13.dp),
                                        )
                                    }
                                }
                            }
                        }
                    } else {
                        Text(
                            text = UiText.labelNotListening(ctx),
                            style = MaterialTheme.typography.bodySmall,
                            color = colors.textMuted,
                        )
                    }

                    Spacer(Modifier.height(14.dp))

                    // 三大高频配对/连接入口
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Button(
                            onClick = onOpenScanQr,
                            shape = RoundedCornerShape(12.dp),
                            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 10.dp),
                            modifier = Modifier.weight(1.1f),
                        ) {
                            Icon(AppIcons.ScanQr, contentDescription = null, modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(6.dp))
                            Text(UiText.btnScanQr(ctx), maxLines = 1, style = MaterialTheme.typography.labelMedium)
                        }
                        FilledTonalButton(
                            onClick = onOpenShowQr,
                            shape = RoundedCornerShape(12.dp),
                            contentPadding = PaddingValues(horizontal = 10.dp, vertical = 10.dp),
                            modifier = Modifier.weight(1f),
                        ) {
                            Icon(AppIcons.QrCode, contentDescription = null, modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(5.dp))
                            Text(UiText.btnQrPair(ctx), maxLines = 1, style = MaterialTheme.typography.labelMedium)
                        }
                        OutlinedButton(
                            onClick = onOpenManualConnect,
                            shape = RoundedCornerShape(12.dp),
                            border = BorderStroke(1.dp, colors.border),
                            contentPadding = PaddingValues(horizontal = 10.dp, vertical = 10.dp),
                            modifier = Modifier.weight(1f),
                        ) {
                            Icon(AppIcons.ManualLink, contentDescription = null, modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(5.dp))
                            Text(UiText.btnManualConnect(ctx), maxLines = 1, style = MaterialTheme.typography.labelMedium)
                        }
                    }
                }
            }
        }

        // 附近与已信任设备标题
        item {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 4.dp, start = 2.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = UiText.sectionNearbyDevices(ctx),
                    style = MaterialTheme.typography.labelLarge,
                    color = colors.textSecondary,
                )
                Spacer(Modifier.width(8.dp))
                if (state.devices.isNotEmpty()) {
                    Surface(
                        shape = RoundedCornerShape(999.dp),
                        color = colors.accentSoft,
                    ) {
                        Text(
                            text = state.devices.size.toString(),
                            style = MaterialTheme.typography.labelSmall,
                            color = colors.accent,
                            modifier = Modifier.padding(horizontal = 7.dp, vertical = 2.dp),
                        )
                    }
                }
            }
        }

        // 设备卡片列表或扫描空状态
        if (state.devices.isEmpty()) {
            item {
                Surface(
                    shape = RoundedCornerShape(18.dp),
                    color = colors.surface,
                    border = BorderStroke(1.dp, colors.border),
                    modifier = Modifier.fillMaxWidth(),
                ) {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 24.dp, vertical = 34.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                    ) {
                        Box(
                            modifier = Modifier
                                .size(52.dp)
                                .clip(RoundedCornerShape(16.dp))
                                .background(colors.accentSoft),
                            contentAlignment = Alignment.Center,
                        ) {
                            Icon(
                                imageVector = AppIcons.ManualLink,
                                contentDescription = null,
                                tint = colors.accent,
                                modifier = Modifier.size(26.dp),
                            )
                        }
                        Spacer(Modifier.height(14.dp))
                        Text(
                            text = UiText.labelScanningDevices(ctx),
                            style = MaterialTheme.typography.bodyMedium,
                            color = colors.textSecondary,
                        )
                        Spacer(Modifier.height(4.dp))
                        Text(
                            text = UiText.labelEncryptedHint(ctx),
                            style = MaterialTheme.typography.bodySmall,
                            color = colors.textMuted,
                        )
                    }
                }
            }
        } else {
            items(state.devices, key = { it.deviceId.value.toHex() }) { item ->
                DeviceCard(item = item, onClick = { onSelectDevice(item) })
            }
        }
    }
}

@Composable
private fun DeviceCard(item: DeviceUiItem, onClick: () -> Unit) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    val devAccent = colors.deviceAccent(item.deviceType)

    Surface(
        shape = RoundedCornerShape(16.dp),
        color = colors.surface,
        border = BorderStroke(1.dp, if (item.isConnected) colors.success.copy(alpha = 0.45f) else colors.border),
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
    ) {
        Row(
            modifier = Modifier.padding(14.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                modifier = Modifier
                    .size(44.dp)
                    .clip(RoundedCornerShape(13.dp))
                    .background(devAccent.copy(alpha = 0.12f)),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    imageVector = AppIcons.forDevice(item.deviceType),
                    contentDescription = UiTextMapper.deviceTypeLabel(ctx, item.deviceType),
                    tint = devAccent,
                    modifier = Modifier.size(22.dp),
                )
            }
            Spacer(Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        text = item.displayName,
                        style = MaterialTheme.typography.titleSmall,
                        color = colors.textPrimary,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                    Spacer(Modifier.width(8.dp))
                    val (badgeBg, badgeFg, badgeText) = when {
                        item.isConnected -> Triple(colors.successSoft, colors.success, UiText.labelConnectedBadge(ctx))
                        item.isTrusted -> Triple(colors.accentSoft, colors.accent, UiText.labelTrustedBadge(ctx))
                        else -> Triple(colors.surfaceSubtle, colors.textMuted, UiText.labelUntrustedBadge(ctx))
                    }
                    Surface(shape = RoundedCornerShape(6.dp), color = badgeBg) {
                        Text(
                            text = badgeText,
                            style = MaterialTheme.typography.labelSmall,
                            fontWeight = FontWeight.SemiBold,
                            color = badgeFg,
                            modifier = Modifier.padding(horizontal = 7.dp, vertical = 2.dp),
                        )
                    }
                }
                Spacer(Modifier.height(3.dp))
                Text(
                    text = "${UiTextMapper.deviceTypeLabel(ctx, item.deviceType)} · ${item.addressText}",
                    style = MonoTextStyle.copy(fontSize = MaterialTheme.typography.bodySmall.fontSize),
                    color = colors.textMuted,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
fun AlertBanner(
    text: String,
    bg: Color,
    border: Color,
    textColor: Color,
    actionLabel: String? = null,
    onAction: (() -> Unit)? = null,
) {
    Surface(
        shape = RoundedCornerShape(12.dp),
        color = bg,
        border = BorderStroke(1.dp, border.copy(alpha = 0.55f)),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                imageVector = AppIcons.Warning,
                contentDescription = null,
                tint = border,
                modifier = Modifier.size(16.dp),
            )
            Spacer(Modifier.width(9.dp))
            Text(
                text = text,
                style = MaterialTheme.typography.bodySmall,
                color = textColor,
                modifier = Modifier.weight(1f),
            )
            if (actionLabel != null && onAction != null) {
                TextButton(
                    onClick = onAction,
                    contentPadding = PaddingValues(horizontal = 8.dp, vertical = 2.dp),
                    colors = ButtonDefaults.textButtonColors(contentColor = border),
                ) {
                    Text(actionLabel, style = MaterialTheme.typography.labelMedium)
                }
            }
        }
    }
}
