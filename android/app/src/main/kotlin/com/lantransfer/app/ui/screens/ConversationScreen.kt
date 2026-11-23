package com.lantransfer.app.ui.screens

import android.content.Context
import android.util.TypedValue
import android.widget.TextView
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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import com.lantransfer.app.ui.MainUiState
import com.lantransfer.app.ui.icons.AppIcons
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.text.UiTextMapper
import com.lantransfer.app.ui.theme.LocalLanColors
import com.lantransfer.app.ui.theme.MonoTextStyle
import com.lantransfer.domain.Collapse
import com.lantransfer.domain.Direction
import com.lantransfer.domain.FileState
import com.lantransfer.domain.SizeFormatter
import com.lantransfer.domain.SourceRef
import com.lantransfer.domain.StoredFileRef
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TextDisplayMapper
import com.lantransfer.domain.TextRules
import com.lantransfer.engine.TextItem
import com.lantransfer.engine.TransferTaskView
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.TransferId

@Composable
fun ConversationScreen(
    state: MainUiState,
    onBack: () -> Unit,
    onSendText: (DeviceId, String, () -> Unit) -> Unit,
    onRetryText: (DeviceId, TextItem) -> Unit,
    onCopyText: (String) -> Unit,
    onPasteToInput: ((String) -> Unit) -> Unit,
    onSendClipboard: (DeviceId) -> Unit,
    onAddUris: (DeviceId, List<android.net.Uri>) -> Unit,
    onRemovePending: (DeviceId, SourceRef) -> Unit,
    onClearPending: (DeviceId) -> Unit,
    onSendPending: (DeviceId) -> Unit,
    onResumeTask: (TransferId, DeviceId) -> Unit,
    onCancelTask: (TransferId) -> Unit,
    onOpenFile: (StoredFileRef, String, String) -> Unit,
    onRevealFile: (StoredFileRef, String) -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    val peerId = state.activePeerId
    var inputText by rememberSaveable { mutableStateOf("") }
    val expandedKeys = remember { mutableStateMapOf<String, Boolean>() }

    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (peerId != null && uris.isNotEmpty()) {
            onAddUris(peerId, uris)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(colors.canvas)
            .imePadding(),
    ) {
        // 顶部会话栏
        Surface(color = colors.surface, shadowElevation = 1.dp) {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 8.dp, vertical = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(onClick = onBack) {
                    Icon(AppIcons.Back, contentDescription = UiText.btnClose(ctx), tint = colors.textPrimary)
                }
                val devAccent = colors.deviceAccent(state.activePeerType)
                Box(
                    modifier = Modifier
                        .size(38.dp)
                        .clip(RoundedCornerShape(11.dp))
                        .background(devAccent.copy(alpha = 0.14f)),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        imageVector = AppIcons.forDevice(state.activePeerType),
                        contentDescription = null,
                        tint = devAccent,
                        modifier = Modifier.size(20.dp),
                    )
                }
                Spacer(Modifier.width(10.dp))
                Column(modifier = Modifier.weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = state.activePeerName,
                            style = MaterialTheme.typography.titleMedium,
                            color = colors.textPrimary,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f, fill = false),
                        )
                        Spacer(Modifier.width(6.dp))
                        Box(
                            modifier = Modifier
                                .size(8.dp)
                                .clip(CircleShape)
                                .background(if (state.activePeerConnected) colors.success else colors.textMuted),
                        )
                    }
                    if (state.activePeerAddress.isNotEmpty()) {
                        Text(
                            text = "${UiTextMapper.deviceTypeLabel(ctx, state.activePeerType)} · ${state.activePeerAddress}",
                            style = MonoTextStyle.copy(fontSize = MaterialTheme.typography.bodySmall.fontSize),
                            color = colors.textMuted,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
            }
        }
        HorizontalDivider(color = colors.border)

        // 会话时间线（文字气泡 + 文件任务卡片）
        LazyColumn(
            modifier = Modifier
                .weight(1f)
                .fillMaxWidth(),
            contentPadding = PaddingValues(horizontal = 14.dp, vertical = 14.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            if (state.activeMessages.isEmpty() && state.activeTasks.isEmpty()) {
                item {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(vertical = 48.dp),
                        contentAlignment = Alignment.Center,
                    ) {
                        Text(
                            text = UiText.labelNoMessages(ctx),
                            style = MaterialTheme.typography.bodyMedium,
                            color = colors.textMuted,
                        )
                    }
                }
            }

            items(state.activeMessages, key = { "m:${it.key.messageId.value.toHex()}" }) { msg ->
                val mapKey = msg.key.messageId.value.toHex()
                val expanded = expandedKeys[mapKey] == true
                TextBubbleCard(
                    item = msg,
                    expanded = expanded,
                    onToggleExpand = { expandedKeys[mapKey] = !expanded },
                    onCopy = onCopyText,
                    onRetry = { if (peerId != null) onRetryText(peerId, msg) },
                )
            }

            items(state.activeTasks, key = { "t:${it.id.value.toHex()}" }) { tv ->
                TransferTaskCard(
                    view = tv,
                    onResume = { if (peerId != null) onResumeTask(tv.id, peerId) },
                    onCancel = { onCancelTask(tv.id) },
                    onOpenFile = onOpenFile,
                    onRevealFile = onRevealFile,
                )
            }
        }

        // 待发送文件队列条
        if (peerId != null && state.pendingFiles.isNotEmpty()) {
            Surface(color = colors.surfaceSubtle, border = BorderStroke(1.dp, colors.border)) {
                Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = UiText.labelPendingFiles(ctx, state.pendingFiles.size),
                            style = MaterialTheme.typography.labelMedium,
                            color = colors.textPrimary,
                            modifier = Modifier.weight(1f),
                        )
                        TextButton(
                            onClick = { onClearPending(peerId) },
                            contentPadding = PaddingValues(horizontal = 8.dp, vertical = 2.dp),
                        ) {
                            Text(UiText.btnClearPending(ctx), style = MaterialTheme.typography.labelSmall)
                        }
                        Spacer(Modifier.width(4.dp))
                        Button(
                            onClick = { onSendPending(peerId) },
                            enabled = state.hasNetwork,
                            shape = RoundedCornerShape(10.dp),
                            contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp),
                        ) {
                            Text(UiText.btnSendPending(ctx), style = MaterialTheme.typography.labelMedium)
                        }
                    }
                    Spacer(Modifier.height(6.dp))
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        items(state.pendingFiles, key = { it.ref.value }) { pf ->
                            Surface(
                                shape = RoundedCornerShape(10.dp),
                                color = colors.surface,
                                border = BorderStroke(1.dp, colors.border),
                            ) {
                                Row(
                                    modifier = Modifier.padding(start = 10.dp, end = 4.dp, top = 5.dp, bottom = 5.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Icon(AppIcons.AttachFile, contentDescription = null, tint = colors.accent, modifier = Modifier.size(14.dp))
                                    Spacer(Modifier.width(6.dp))
                                    Text(
                                        text = if (pf.sizeText.isNotEmpty()) "${pf.name} (${pf.sizeText})" else pf.name,
                                        style = MaterialTheme.typography.bodySmall,
                                        color = colors.textPrimary,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                        modifier = Modifier.widthIn(max = 180.dp),
                                    )
                                    IconButton(
                                        onClick = { onRemovePending(peerId, pf.ref) },
                                        modifier = Modifier.size(24.dp),
                                    ) {
                                        Icon(AppIcons.Close, contentDescription = null, tint = colors.textMuted, modifier = Modifier.size(14.dp))
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // 底部输入与快捷工具栏
        Surface(color = colors.surface, shadowElevation = 4.dp) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 12.dp, vertical = 10.dp),
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    FilledTonalButton(
                        onClick = { filePicker.launch(arrayOf("*/*")) },
                        enabled = state.hasNetwork && peerId != null,
                        shape = RoundedCornerShape(10.dp),
                        contentPadding = PaddingValues(horizontal = 10.dp, vertical = 6.dp),
                    ) {
                        Icon(AppIcons.AttachFile, contentDescription = null, modifier = Modifier.size(15.dp))
                        Spacer(Modifier.width(5.dp))
                        Text(UiText.btnSendFiles(ctx), style = MaterialTheme.typography.labelSmall)
                    }
                    OutlinedButton(
                        onClick = { onPasteToInput { pasted -> inputText += pasted } },
                        shape = RoundedCornerShape(10.dp),
                        border = BorderStroke(1.dp, colors.border),
                        contentPadding = PaddingValues(horizontal = 10.dp, vertical = 6.dp),
                    ) {
                        Icon(AppIcons.Clipboard, contentDescription = null, modifier = Modifier.size(15.dp))
                        Spacer(Modifier.width(5.dp))
                        Text(UiText.btnPaste(ctx), style = MaterialTheme.typography.labelSmall)
                    }
                    OutlinedButton(
                        onClick = { if (peerId != null) onSendClipboard(peerId) },
                        enabled = state.hasNetwork && peerId != null,
                        shape = RoundedCornerShape(10.dp),
                        border = BorderStroke(1.dp, colors.border),
                        contentPadding = PaddingValues(horizontal = 10.dp, vertical = 6.dp),
                    ) {
                        Text(UiText.btnSendClipboard(ctx), style = MaterialTheme.typography.labelSmall)
                    }
                }

                Spacer(Modifier.height(8.dp))

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.Bottom,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    OutlinedTextField(
                        value = inputText,
                        onValueChange = { inputText = it },
                        placeholder = {
                            Text(
                                text = UiText.labelMessagePlaceholder(ctx),
                                style = MaterialTheme.typography.bodyMedium,
                                color = colors.textMuted,
                            )
                        },
                        maxLines = 4,
                        shape = RoundedCornerShape(14.dp),
                        modifier = Modifier.weight(1f),
                    )
                    Button(
                        onClick = {
                            if (peerId != null && inputText.isNotEmpty()) {
                                onSendText(peerId, inputText) { inputText = "" }
                            }
                        },
                        enabled = state.hasNetwork && peerId != null && inputText.isNotEmpty(),
                        shape = RoundedCornerShape(14.dp),
                        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 14.dp),
                    ) {
                        Icon(AppIcons.Send, contentDescription = UiText.btnSend(ctx), modifier = Modifier.size(18.dp))
                    }
                }
            }
        }
    }
}

@Composable
private fun TextBubbleCard(
    item: TextItem,
    expanded: Boolean,
    onToggleExpand: () -> Unit,
    onCopy: (String) -> Unit,
    onRetry: () -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    val outgoing = item.direction == Direction.SEND
    val collapse = remember(item.text) { TextRules.collapse(item.text) }
    val isCollapsible = collapse is Collapse.Collapsed
    val shownRaw = when {
        collapse is Collapse.Collapsed && !expanded -> item.text.substring(0, collapse.prefixEnd)
        else -> item.text
    }
    val mapped = remember(shownRaw) { TextDisplayMapper.map(shownRaw) }

    Column(
        modifier = Modifier.fillMaxWidth(),
        horizontalAlignment = if (outgoing) Alignment.End else Alignment.Start,
    ) {
        Surface(
            shape = RoundedCornerShape(
                topStart = 16.dp,
                topEnd = 16.dp,
                bottomStart = if (outgoing) 16.dp else 4.dp,
                bottomEnd = if (outgoing) 4.dp else 16.dp,
            ),
            color = if (outgoing) colors.bubbleOut else colors.bubbleIn,
            border = if (outgoing) null else BorderStroke(1.dp, colors.border),
            modifier = Modifier.widthIn(max = 310.dp),
        ) {
            Column(modifier = Modifier.padding(horizontal = 14.dp, vertical = 10.dp)) {
                val fgArgb = (if (outgoing) colors.bubbleOutText else colors.bubbleInText).toArgb()
                // 使用原生可选择 TextView 拦截选区复制，确保按 TextDisplayMapper 还原无损原文（需求 12.1–12.5、13.1）
                AndroidView(
                    factory = { c ->
                        LosslessSelectableTextView(c).apply {
                            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14.5f)
                            setTextIsSelectable(true)
                        }
                    },
                    update = { tv ->
                        tv.setTextColor(fgArgb)
                        tv.bindLossless(
                            originalSlice = shownRaw,
                            displayText = mapped,
                            onCopyExact = onCopy,
                        )
                    },
                    modifier = Modifier.fillMaxWidth(),
                )

                if (isCollapsible) {
                    Spacer(Modifier.height(4.dp))
                    TextButton(
                        onClick = onToggleExpand,
                        contentPadding = PaddingValues(horizontal = 4.dp, vertical = 0.dp),
                        colors = ButtonDefaults.textButtonColors(
                            contentColor = if (outgoing) Color.White.copy(alpha = 0.9f) else colors.accent,
                        ),
                    ) {
                        Text(
                            text = if (expanded) UiText.btnCollapse(ctx) else UiText.btnExpand(ctx),
                            style = MaterialTheme.typography.labelSmall,
                        )
                    }
                }
            }
        }

        Row(
            modifier = Modifier.padding(top = 3.dp, start = 4.dp, end = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text(
                text = "${UiTextMapper.formatRecordTime(item.recordedAt)} · ${UiTextMapper.textStatusLabel(ctx, item.status)}",
                style = MaterialTheme.typography.labelSmall,
                color = colors.textMuted,
            )
            TextButton(
                onClick = { onCopy(item.text) },
                contentPadding = PaddingValues(horizontal = 6.dp, vertical = 0.dp),
            ) {
                Text(UiText.btnCopy(ctx), style = MaterialTheme.typography.labelSmall)
            }
            if (item.showRetry) {
                TextButton(
                    onClick = onRetry,
                    contentPadding = PaddingValues(horizontal = 6.dp, vertical = 0.dp),
                    colors = ButtonDefaults.textButtonColors(contentColor = colors.danger),
                ) {
                    Text(UiText.btnRetry(ctx), style = MaterialTheme.typography.labelSmall)
                }
            }
        }
    }
}

/** 拦截选区复制并按 DisplayText.originalRange 映射回无损原文的原生 TextView（设计 C3.3、C6.4）。 */
private class LosslessSelectableTextView(context: Context) : TextView(context) {
    private var originalSlice: String = ""
    private var mapped: com.lantransfer.domain.DisplayText? = null
    private var onCopyExact: ((String) -> Unit)? = null

    fun bindLossless(
        originalSlice: String,
        displayText: com.lantransfer.domain.DisplayText,
        onCopyExact: (String) -> Unit,
    ) {
        this.originalSlice = originalSlice
        this.mapped = displayText
        this.onCopyExact = onCopyExact
        if (text?.toString() != displayText.display) {
            text = displayText.display
        }
    }

    override fun onTextContextMenuItem(id: Int): Boolean {
        if (id == android.R.id.copy) {
            val m = mapped
            val cb = onCopyExact
            if (m != null && cb != null) {
                val start = selectionStart.coerceAtLeast(0)
                val end = selectionEnd.coerceAtLeast(start)
                val range = m.originalRange(start.coerceAtMost(m.display.length), end.coerceAtMost(m.display.length))
                val slice = if (range.isEmpty()) originalSlice else originalSlice.substring(range.first, range.last + 1)
                cb(slice)
                clearFocus()
                return true
            }
        }
        return super.onTextContextMenuItem(id)
    }
}

@Composable
private fun TransferTaskCard(
    view: TransferTaskView,
    onResume: () -> Unit,
    onCancel: () -> Unit,
    onOpenFile: (StoredFileRef, String, String) -> Unit,
    onRevealFile: (StoredFileRef, String) -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    val task = view.task
    val isSend = task.direction == Direction.SEND
    val progress = view.progress
    val stateLabel = UiTextMapper.taskStateLabel(ctx, task.state, task.direction, task.interrupt, view.checkingPrefix)

    Surface(
        shape = RoundedCornerShape(16.dp),
        color = colors.surface,
        border = BorderStroke(1.dp, colors.border),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(modifier = Modifier.padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Surface(
                    shape = RoundedCornerShape(6.dp),
                    color = if (isSend) colors.accentSoft else colors.successSoft,
                ) {
                    Text(
                        text = if (isSend) UiText.labelDirectionSend(ctx) else UiText.labelDirectionReceive(ctx),
                        style = MaterialTheme.typography.labelSmall,
                        color = if (isSend) colors.accent else colors.success,
                        modifier = Modifier.padding(horizontal = 7.dp, vertical = 2.dp),
                    )
                }
                Spacer(Modifier.width(8.dp))
                Text(
                    text = "${UiText.labelFileCount(ctx, task.files.size)} · ${SizeFormatter.format(progress.total)}",
                    style = MaterialTheme.typography.titleSmall,
                    color = colors.textPrimary,
                    modifier = Modifier.weight(1f),
                )
                Text(
                    text = stateLabel,
                    style = MaterialTheme.typography.labelSmall,
                    fontWeight = FontWeight.SemiBold,
                    color = when (task.state) {
                        TaskState.COMPLETED -> colors.success
                        TaskState.INTERRUPTED, TaskState.NO_SPACE, TaskState.CANNOT_SAVE, TaskState.CONNECTION_LOST -> colors.danger
                        else -> colors.accent
                    },
                )
            }

            if (task.state == TaskState.RUNNING || task.state == TaskState.RESUMING || task.state == TaskState.INTERRUPTED) {
                Spacer(Modifier.height(10.dp))
                LinearProgressIndicator(
                    progress = { (progress.percent / 100f).coerceIn(0f, 1f) },
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(6.dp)
                        .clip(RoundedCornerShape(3.dp)),
                    color = colors.accent,
                    trackColor = colors.surfaceSubtle,
                )
                Spacer(Modifier.height(6.dp))
                Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(
                        text = "${progress.percent}% · ${SizeFormatter.format(progress.transferred)} / ${SizeFormatter.format(progress.total)}",
                        style = MonoTextStyle.copy(fontSize = MaterialTheme.typography.labelSmall.fontSize),
                        color = colors.textSecondary,
                    )
                    if (task.state == TaskState.RUNNING) {
                        Text(
                            text = "${UiTextMapper.formatSpeed(progress.bytesPerSec)} · ${UiTextMapper.formatEta(ctx, progress.etaSec)}",
                            style = MonoTextStyle.copy(fontSize = MaterialTheme.typography.labelSmall.fontSize),
                            color = colors.textMuted,
                        )
                    }
                }
            }

            Spacer(Modifier.height(10.dp))

            // 最多展示前 5 个文件，超出给出摘要
            for (f in task.files.take(5)) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(AppIcons.AttachFile, contentDescription = null, tint = colors.textMuted, modifier = Modifier.size(15.dp))
                    Spacer(Modifier.width(7.dp))
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = f.finalName ?: f.originalName,
                            style = MaterialTheme.typography.bodySmall,
                            color = colors.textPrimary,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            text = "${SizeFormatter.format(f.size)} · ${UiTextMapper.fileDisplayStatus(ctx, f.state, task.state, task.direction, task.interrupt)}",
                            style = MaterialTheme.typography.labelSmall,
                            color = colors.textMuted,
                        )
                    }
                    if (!isSend && f.state == FileState.COMPLETED && f.storedRef != null) {
                        TextButton(
                            onClick = { onOpenFile(f.storedRef!!, f.mime, f.fullPath ?: f.originalName) },
                            contentPadding = PaddingValues(horizontal = 6.dp, vertical = 2.dp),
                        ) {
                            Text(UiText.btnOpenFile(ctx), style = MaterialTheme.typography.labelSmall)
                        }
                        TextButton(
                            onClick = { onRevealFile(f.storedRef!!, f.fullPath ?: f.originalName) },
                            contentPadding = PaddingValues(horizontal = 6.dp, vertical = 2.dp),
                        ) {
                            Text(UiText.btnRevealInFolder(ctx), style = MaterialTheme.typography.labelSmall)
                        }
                    }
                }
            }
            if (task.files.size > 5) {
                Text(
                    text = "… 以及其余 ${task.files.size - 5} 个文件",
                    style = MaterialTheme.typography.labelSmall,
                    color = colors.textMuted,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }

            // 任务级动作按钮（继续 / 取消）
            if (task.state == TaskState.INTERRUPTED || task.state == TaskState.RUNNING || task.state == TaskState.RESUMING || task.state == TaskState.OFFERING) {
                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (task.state == TaskState.INTERRUPTED) {
                        Button(
                            onClick = onResume,
                            shape = RoundedCornerShape(10.dp),
                            contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp),
                        ) {
                            Text(UiText.btnResume(ctx), style = MaterialTheme.typography.labelMedium)
                        }
                    }
                    OutlinedButton(
                        onClick = onCancel,
                        shape = RoundedCornerShape(10.dp),
                        border = BorderStroke(1.dp, colors.border),
                        contentPadding = PaddingValues(horizontal = 14.dp, vertical = 6.dp),
                    ) {
                        Text(UiText.btnCancel(ctx), style = MaterialTheme.typography.labelMedium)
                    }
                }
            }
        }
    }
}
