package com.lantransfer.app.ui.screens

import android.content.Intent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.lantransfer.app.platform.ThemeMode
import com.lantransfer.app.ui.MainUiState
import com.lantransfer.app.ui.icons.AppIcons
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.text.UiTextMapper
import com.lantransfer.app.ui.theme.LocalLanColors
import com.lantransfer.app.ui.theme.MonoTextStyle
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.protocol.DeviceId

@Composable
fun SettingsScreen(
    state: MainUiState,
    onSetTheme: (ThemeMode) -> Unit,
    onSaveDeviceName: (String) -> String?,
    onSelectReceiveTreeUri: (android.net.Uri) -> String?,
    onOpenReceiveDir: () -> Unit,
    onSetAutoCopy: (Boolean) -> Unit,
    onSetBackgroundReceive: (Boolean) -> Unit,
    onOpenBackgroundGuide: () -> Unit,
    onSetAutoAccept: (DeviceId, Boolean) -> Unit,
    onRequestUnpair: (TrustedDevice) -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    var nameInput by remember { mutableStateOf(state.localDeviceName) }
    var nameError by remember { mutableStateOf<String?>(null) }
    var dirError by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(state.localDeviceName) {
        if (nameError == null) {
            nameInput = state.localDeviceName
        }
    }

    val dirPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            runCatching {
                val flags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                ctx.contentResolver.takePersistableUriPermission(uri, flags)
            }
            dirError = onSelectReceiveTreeUri(uri)
        }
    }

    LazyColumn(
        modifier = Modifier
            .fillMaxSize()
            .background(colors.canvas),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        item {
            Text(
                text = UiText.navSettings(ctx),
                style = MaterialTheme.typography.headlineSmall,
                color = colors.textPrimary,
                modifier = Modifier.padding(start = 2.dp),
            )
        }

        // 外观主题卡片
        item {
            SettingsCard(title = UiText.sectionAppearance(ctx)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    ThemeOptionButton(
                        label = UiText.themeSystem(ctx),
                        selected = state.themeMode == ThemeMode.SYSTEM,
                        onClick = { onSetTheme(ThemeMode.SYSTEM) },
                        modifier = Modifier.weight(1f),
                    )
                    ThemeOptionButton(
                        label = UiText.themeLight(ctx),
                        selected = state.themeMode == ThemeMode.LIGHT,
                        onClick = { onSetTheme(ThemeMode.LIGHT) },
                        modifier = Modifier.weight(1f),
                    )
                    ThemeOptionButton(
                        label = UiText.themeDark(ctx),
                        selected = state.themeMode == ThemeMode.DARK,
                        onClick = { onSetTheme(ThemeMode.DARK) },
                        modifier = Modifier.weight(1f),
                    )
                }
            }
        }

        // 本机设备名称卡片（需求 4.4、4.5、4.8）
        item {
            SettingsCard(title = UiText.sectionLocalDevice(ctx)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    OutlinedTextField(
                        value = nameInput,
                        onValueChange = { nameInput = it; nameError = null },
                        label = { Text(UiText.labelDeviceName(ctx)) },
                        singleLine = true,
                        isError = nameError != null,
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.weight(1f),
                    )
                    Button(
                        onClick = {
                            val err = onSaveDeviceName(nameInput)
                            nameError = err
                            if (err == null) {
                                nameInput = state.localDeviceName
                            }
                        },
                        shape = RoundedCornerShape(12.dp),
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 14.dp),
                    ) {
                        Text(UiText.btnSave(ctx))
                    }
                }
                if (nameError != null) {
                    Spacer(Modifier.height(6.dp))
                    Text(
                        text = nameError!!,
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.danger,
                    )
                }
            }
        }

        // 传输与存储偏好卡片
        item {
            SettingsCard(title = UiText.sectionTransferPrefs(ctx)) {
                Text(
                    text = UiText.labelReceiveDir(ctx),
                    style = MaterialTheme.typography.labelMedium,
                    color = colors.textSecondary,
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    text = state.receiveDirDisplay.ifEmpty { UiText.labelDefaultReceiveDir(ctx) },
                    style = MonoTextStyle.copy(fontSize = MaterialTheme.typography.bodySmall.fontSize),
                    color = colors.textPrimary,
                )
                if (dirError != null) {
                    Spacer(Modifier.height(4.dp))
                    Text(text = dirError!!, style = MaterialTheme.typography.bodySmall, color = colors.danger)
                }
                Spacer(Modifier.height(10.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    FilledTonalButton(
                        onClick = { dirPicker.launch(null) },
                        shape = RoundedCornerShape(10.dp),
                    ) {
                        Icon(AppIcons.Folder, contentDescription = null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(6.dp))
                        Text(UiText.btnChangeDir(ctx), style = MaterialTheme.typography.labelMedium)
                    }
                    OutlinedButton(
                        onClick = onOpenReceiveDir,
                        shape = RoundedCornerShape(10.dp),
                        border = BorderStroke(1.dp, colors.border),
                    ) {
                        Text(UiText.btnOpenReceiveDir(ctx), style = MaterialTheme.typography.labelMedium)
                    }
                }

                Spacer(Modifier.height(14.dp))
                HorizontalDivider(color = colors.border)
                Spacer(Modifier.height(10.dp))

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = UiText.labelAutoCopy(ctx),
                            style = MaterialTheme.typography.titleSmall,
                            color = colors.textPrimary,
                        )
                        Text(
                            text = UiText.labelAutoCopyDesc(ctx),
                            style = MaterialTheme.typography.bodySmall,
                            color = colors.textMuted,
                        )
                    }
                    Spacer(Modifier.width(12.dp))
                    Switch(checked = state.autoCopy, onCheckedChange = onSetAutoCopy)
                }
            }
        }

        // 后台运行卡片（Android 专用，需求 21.2）
        item {
            SettingsCard(title = UiText.sectionBackgroundPrefs(ctx)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = UiText.labelBackgroundReceive(ctx),
                            style = MaterialTheme.typography.titleSmall,
                            color = colors.textPrimary,
                        )
                        Text(
                            text = UiText.labelBackgroundReceiveDesc(ctx),
                            style = MaterialTheme.typography.bodySmall,
                            color = colors.textMuted,
                        )
                    }
                    Spacer(Modifier.width(12.dp))
                    Switch(checked = state.backgroundReceive, onCheckedChange = onSetBackgroundReceive)
                }
                Spacer(Modifier.height(8.dp))
                OutlinedButton(
                    onClick = onOpenBackgroundGuide,
                    shape = RoundedCornerShape(10.dp),
                    border = BorderStroke(1.dp, colors.border),
                ) {
                    Text(UiText.settingsBackgroundRun(ctx), style = MaterialTheme.typography.labelMedium)
                }
            }
        }

        // 已信任设备管理卡片（需求 9.3、15.2）
        item {
            SettingsCard(title = UiText.sectionTrustedDevices(ctx)) {
                if (state.trustedDevices.isEmpty()) {
                    Text(
                        text = UiText.labelNoTrustedDevices(ctx),
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.textMuted,
                    )
                } else {
                    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        state.trustedDevices.forEachIndexed { idx, dev ->
                            if (idx > 0) HorizontalDivider(color = colors.border)
                            val devAccent = colors.deviceAccent(dev.deviceType)
                            Column {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Box(
                                        modifier = Modifier
                                            .size(36.dp)
                                            .clip(RoundedCornerShape(10.dp))
                                            .background(devAccent.copy(alpha = 0.14f)),
                                        contentAlignment = Alignment.Center,
                                    ) {
                                        Icon(
                                            imageVector = AppIcons.forDevice(dev.deviceType),
                                            contentDescription = null,
                                            tint = devAccent,
                                            modifier = Modifier.size(18.dp),
                                        )
                                    }
                                    Spacer(Modifier.width(10.dp))
                                    Column(modifier = Modifier.weight(1f)) {
                                        Text(
                                            text = dev.displayName,
                                            style = MaterialTheme.typography.titleSmall,
                                            color = colors.textPrimary,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                        Text(
                                            text = UiTextMapper.deviceTypeLabel(ctx, dev.deviceType),
                                            style = MaterialTheme.typography.bodySmall,
                                            color = colors.textMuted,
                                        )
                                    }
                                    TextButton(
                                        onClick = { onRequestUnpair(dev) },
                                        colors = ButtonDefaults.textButtonColors(contentColor = colors.danger),
                                    ) {
                                        Text(UiText.btnUnpair(ctx), style = MaterialTheme.typography.labelMedium)
                                    }
                                }
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .padding(top = 6.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Text(
                                        text = UiText.labelAutoAccept(ctx),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = colors.textSecondary,
                                        modifier = Modifier.weight(1f),
                                    )
                                    Switch(
                                        checked = dev.autoAccept,
                                        onCheckedChange = { onSetAutoAccept(dev.deviceId, it) },
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SettingsCard(
    title: String,
    content: @Composable () -> Unit,
) {
    val colors = LocalLanColors.current
    Surface(
        shape = RoundedCornerShape(18.dp),
        color = colors.surface,
        border = BorderStroke(1.dp, colors.border),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            Text(
                text = title,
                style = MaterialTheme.typography.labelLarge,
                color = colors.accent,
            )
            Spacer(Modifier.height(12.dp))
            content()
        }
    }
}

@Composable
private fun ThemeOptionButton(
    label: String,
    selected: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = LocalLanColors.current
    if (selected) {
        Button(
            onClick = onClick,
            shape = RoundedCornerShape(10.dp),
            contentPadding = PaddingValues(vertical = 10.dp, horizontal = 8.dp),
            modifier = modifier,
        ) {
            Text(label, maxLines = 1, style = MaterialTheme.typography.labelMedium)
        }
    } else {
        OutlinedButton(
            onClick = onClick,
            shape = RoundedCornerShape(10.dp),
            border = BorderStroke(1.dp, colors.border),
            contentPadding = PaddingValues(vertical = 10.dp, horizontal = 8.dp),
            modifier = modifier,
        ) {
            Text(label, maxLines = 1, color = colors.textSecondary, style = MaterialTheme.typography.labelMedium)
        }
    }
}
