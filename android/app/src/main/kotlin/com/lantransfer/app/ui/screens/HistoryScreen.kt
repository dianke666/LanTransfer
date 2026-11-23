package com.lantransfer.app.ui.screens

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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.lantransfer.app.ui.HistoryFilter
import com.lantransfer.app.ui.HistoryUiItem
import com.lantransfer.app.ui.MainUiState
import com.lantransfer.app.ui.icons.AppIcons
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.theme.LocalLanColors
import com.lantransfer.app.ui.theme.MonoTextStyle
import com.lantransfer.domain.Direction
import com.lantransfer.domain.StoredFileRef

@Composable
fun HistoryScreen(
    state: MainUiState,
    onFilterChange: (HistoryFilter) -> Unit,
    onQueryChange: (String) -> Unit,
    onCopyText: (String) -> Unit,
    onOpenFile: (StoredFileRef, String, String) -> Unit,
    onRevealFile: (StoredFileRef, String) -> Unit,
    onDeleteItem: (HistoryUiItem) -> Unit,
    onClearAll: () -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(colors.canvas),
    ) {
        // 顶部标题与清空按钮
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 18.dp, end = 12.dp, top = 14.dp, bottom = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = UiText.navHistory(ctx),
                style = MaterialTheme.typography.headlineSmall,
                color = colors.textPrimary,
                modifier = Modifier.weight(1f),
            )
            TextButton(
                onClick = onClearAll,
                colors = ButtonDefaults.textButtonColors(contentColor = colors.danger),
            ) {
                Text(UiText.btnClearHistory(ctx), style = MaterialTheme.typography.labelLarge)
            }
        }

        // 搜索框与类型筛选芯片
        Column(modifier = Modifier.padding(horizontal = 16.dp)) {
            OutlinedTextField(
                value = state.historyQuery,
                onValueChange = onQueryChange,
                placeholder = { Text(UiText.labelSearchHistory(ctx), style = MaterialTheme.typography.bodyMedium) },
                leadingIcon = { Icon(AppIcons.Search, contentDescription = null, modifier = Modifier.size(18.dp)) },
                singleLine = true,
                shape = RoundedCornerShape(14.dp),
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                FilterChip(
                    selected = state.historyFilter == HistoryFilter.ALL,
                    onClick = { onFilterChange(HistoryFilter.ALL) },
                    label = { Text(UiText.labelFilterAll(ctx)) },
                )
                FilterChip(
                    selected = state.historyFilter == HistoryFilter.TEXT,
                    onClick = { onFilterChange(HistoryFilter.TEXT) },
                    label = { Text(UiText.labelFilterText(ctx)) },
                )
                FilterChip(
                    selected = state.historyFilter == HistoryFilter.FILE,
                    onClick = { onFilterChange(HistoryFilter.FILE) },
                    label = { Text(UiText.labelFilterFile(ctx)) },
                )
            }
        }

        Spacer(Modifier.height(6.dp))

        if (state.historyItems.isEmpty()) {
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Icon(AppIcons.History, contentDescription = null, tint = colors.textMuted, modifier = Modifier.size(36.dp))
                    Spacer(Modifier.height(10.dp))
                    Text(UiText.labelNoHistory(ctx), style = MaterialTheme.typography.bodyMedium, color = colors.textMuted)
                }
            }
        } else {
            LazyColumn(
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                items(state.historyItems, key = { "${if (it.isText) "t" else "f"}:${it.seq}" }) { item ->
                    HistoryCard(
                        item = item,
                        onCopyText = onCopyText,
                        onOpenFile = onOpenFile,
                        onRevealFile = onRevealFile,
                        onDelete = { onDeleteItem(item) },
                    )
                }
            }
        }
    }
}

@Composable
private fun HistoryCard(
    item: HistoryUiItem,
    onCopyText: (String) -> Unit,
    onOpenFile: (StoredFileRef, String, String) -> Unit,
    onRevealFile: (StoredFileRef, String) -> Unit,
    onDelete: () -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    val isSend = item.direction == Direction.SEND

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
                    text = item.peerName,
                    style = MaterialTheme.typography.titleSmall,
                    color = colors.textPrimary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Text(
                    text = item.timeText,
                    style = MonoTextStyle.copy(fontSize = MaterialTheme.typography.labelSmall.fontSize),
                    color = colors.textMuted,
                )
                IconButton(onClick = onDelete, modifier = Modifier.size(30.dp)) {
                    Icon(AppIcons.Trash, contentDescription = UiText.btnDelete(ctx), tint = colors.textMuted, modifier = Modifier.size(16.dp))
                }
            }

            Spacer(Modifier.height(6.dp))

            Text(
                text = item.titleText,
                style = MaterialTheme.typography.bodyMedium,
                color = colors.textPrimary,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
            )

            if (!item.fullPath.isNullOrEmpty()) {
                Spacer(Modifier.height(4.dp))
                Text(
                    text = item.fullPath,
                    style = MonoTextStyle.copy(fontSize = MaterialTheme.typography.labelSmall.fontSize),
                    color = colors.textMuted,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }

            Spacer(Modifier.height(8.dp))

            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = item.statusText,
                    style = MaterialTheme.typography.labelSmall,
                    fontWeight = FontWeight.SemiBold,
                    color = if (item.isInterrupted) colors.danger else colors.textSecondary,
                    modifier = Modifier.weight(1f),
                )
                if (item.isText && item.fullText != null) {
                    TextButton(
                        onClick = { onCopyText(item.fullText) },
                        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 2.dp),
                    ) {
                        Text(UiText.btnCopy(ctx), style = MaterialTheme.typography.labelSmall)
                    }
                }
                if (item.isCompletedReceived && item.storedRef != null) {
                    TextButton(
                        onClick = { onOpenFile(item.storedRef, item.mime, item.fullPath ?: item.titleText) },
                        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 2.dp),
                    ) {
                        Text(UiText.btnOpenFile(ctx), style = MaterialTheme.typography.labelSmall)
                    }
                    TextButton(
                        onClick = { onRevealFile(item.storedRef, item.fullPath ?: item.titleText) },
                        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 2.dp),
                    ) {
                        Text(UiText.btnRevealInFolder(ctx), style = MaterialTheme.typography.labelSmall)
                    }
                    if (!item.fullPath.isNullOrEmpty()) {
                        TextButton(
                            onClick = { onCopyText(item.fullPath) },
                            contentPadding = PaddingValues(horizontal = 8.dp, vertical = 2.dp),
                        ) {
                            Text(UiText.btnCopyPath(ctx), style = MaterialTheme.typography.labelSmall)
                        }
                    }
                }
            }
        }
    }
}
