package com.lantransfer.app

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.lantransfer.app.ui.icons.AppIcons
import com.lantransfer.app.ui.screens.ScreenTopBar
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.text.UiTextMapper
import com.lantransfer.app.ui.theme.LanTransferTheme
import com.lantransfer.app.ui.theme.LocalLanColors
import com.lantransfer.domain.TrustedDevice
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking

/**
 * Android 系统分享目标选择页（设计 C6.3、需求 14.3）：
 * 接收 ACTION_SEND / ACTION_SEND_MULTIPLE，将 ClipData URI 读权限随 Intent 传递到 MainActivity。
 */
class ShareActivity : ComponentActivity() {
    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(LocaleHelper.wrap(newBase))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val app = application as LanTransferApp
        app.ensureNetworkingStarted()

        val sharedUris = extractSharedUris(intent)
        val sharedText = if (sharedUris.isEmpty()) intent.getStringExtra(Intent.EXTRA_TEXT) else null
        val trusted = runBlocking(Dispatchers.IO) { app.trustStore.all() }

        setContent {
            val themeMode by app.settingsStore.theme.collectAsState()
            LanTransferTheme(themeMode = themeMode) {
                val ctx = LocalContext.current
                val colors = LocalLanColors.current
                Scaffold(
                    containerColor = colors.canvas,
                    topBar = {
                        ScreenTopBar(title = UiText.shareChooseDevice(ctx), onBack = { finish() })
                    },
                ) { pad ->
                    Column(
                        modifier = Modifier
                            .fillMaxSize()
                            .padding(pad)
                            .padding(horizontal = 16.dp, vertical = 12.dp),
                    ) {
                        Surface(
                            shape = RoundedCornerShape(14.dp),
                            color = colors.accentSoft,
                            modifier = Modifier.fillMaxWidth(),
                        ) {
                            Text(
                                text = if (sharedUris.isNotEmpty()) {
                                    UiText.shareFilesSummary(ctx, sharedUris.size)
                                } else {
                                    UiText.shareTextSummary(ctx)
                                },
                                style = MaterialTheme.typography.labelLarge,
                                color = colors.accent,
                                modifier = Modifier.padding(horizontal = 14.dp, vertical = 10.dp),
                            )
                        }
                        Spacer(Modifier.height(14.dp))
                        if (trusted.isEmpty()) {
                            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                                Text(UiText.labelNoTrustedDevices(ctx), color = colors.textMuted)
                            }
                        } else {
                            LazyColumn(
                                contentPadding = PaddingValues(bottom = 16.dp),
                                verticalArrangement = Arrangement.spacedBy(10.dp),
                            ) {
                                items(trusted, key = { it.deviceId.value.toHex() }) { dev ->
                                    ShareTargetRow(device = dev) {
                                        forwardToMain(dev, sharedUris, sharedText)
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    @androidx.compose.runtime.Composable
    private fun ShareTargetRow(device: TrustedDevice, onClick: () -> Unit) {
        val ctx = LocalContext.current
        val colors = LocalLanColors.current
        val devAccent = colors.deviceAccent(device.deviceType)
        Surface(
            shape = RoundedCornerShape(16.dp),
            color = colors.surface,
            border = BorderStroke(1.dp, colors.border),
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
                        .size(40.dp)
                        .clip(RoundedCornerShape(12.dp))
                        .background(devAccent.copy(alpha = 0.14f)),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(AppIcons.forDevice(device.deviceType), contentDescription = null, tint = devAccent, modifier = Modifier.size(20.dp))
                }
                Spacer(Modifier.width(12.dp))
                Column {
                    Text(device.displayName, style = MaterialTheme.typography.titleSmall, color = colors.textPrimary)
                    Text(UiTextMapper.deviceTypeLabel(ctx, device.deviceType), style = MaterialTheme.typography.bodySmall, color = colors.textMuted)
                }
            }
        }
    }

    private fun forwardToMain(device: TrustedDevice, uris: List<Uri>, text: String?) {
        val target = Intent(this, MainActivity::class.java).apply {
            action = ACTION_HANDLE_SHARE
            putExtra(EXTRA_PEER_HEX, device.deviceId.value.toHex())
            if (!text.isNullOrEmpty()) putExtra(EXTRA_SHARED_TEXT, text)
            if (uris.isNotEmpty()) {
                val clip = ClipData.newRawUri("shared", uris.first())
                uris.drop(1).forEach { clip.addItem(ClipData.Item(it)) }
                clipData = clip
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        }
        startActivity(target)
        finish()
    }

    private fun extractSharedUris(intent: Intent?): List<Uri> {
        if (intent == null) return emptyList()
        val out = LinkedHashSet<Uri>()
        when (intent.action) {
            Intent.ACTION_SEND -> {
                getParcelableExtraCompat(intent, Intent.EXTRA_STREAM)?.let { out += it }
            }
            Intent.ACTION_SEND_MULTIPLE -> {
                getParcelableArrayListExtraCompat(intent, Intent.EXTRA_STREAM)?.forEach { out += it }
            }
        }
        intent.clipData?.let { cd ->
            for (i in 0 until cd.itemCount) {
                cd.getItemAt(i).uri?.let { out += it }
            }
        }
        return out.toList()
    }

    private fun getParcelableExtraCompat(intent: Intent, key: String): Uri? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableExtra(key, Uri::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableExtra(key)
        }

    private fun getParcelableArrayListExtraCompat(intent: Intent, key: String): ArrayList<Uri>? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableArrayListExtra(key, Uri::class.java)
        } else {
            @Suppress("DEPRECATION")
            intent.getParcelableArrayListExtra(key)
        }

    companion object {
        const val ACTION_HANDLE_SHARE = "com.lantransfer.app.HANDLE_SHARE"
        const val EXTRA_PEER_HEX = "share_peer_hex"
        const val EXTRA_SHARED_TEXT = "share_text"
    }
}
