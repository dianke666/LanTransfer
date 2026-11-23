package com.lantransfer.app

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.core.content.ContextCompat
import com.lantransfer.app.platform.AndroidNotifier
import com.lantransfer.app.ui.MainViewModel
import com.lantransfer.app.ui.screens.MainShell
import com.lantransfer.app.ui.screens.Routes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId

/** Android 主入口 Activity（设计 C6.4、任务 27.1）。 */
class MainActivity : ComponentActivity() {
    private val viewModel: MainViewModel by viewModels()
    private var initialTabRoute: String? = null

    private val notifPermLauncher = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        (application as LanTransferApp).notifier.refreshEnabled()
    }

    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(LocaleHelper.wrap(newBase))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        requestNotificationPermissionIfNeeded()
        handleIncomingIntent(intent)

        setContent {
            MainShell(viewModel = viewModel, initialTabRoute = initialTabRoute)
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleIncomingIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        (application as LanTransferApp).notifier.refreshEnabled()
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                notifPermLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    private fun handleIncomingIntent(intent: Intent?) {
        if (intent == null) return
        when (intent.action) {
            AndroidNotifier.ACTION_OPEN_PEER -> {
                val peerId = parseDeviceId(intent.getStringExtra(AndroidNotifier.EXTRA_PEER_HEX))
                if (peerId != null) viewModel.openConversation(peerId)
            }
            AndroidNotifier.ACTION_OPEN_HISTORY -> {
                initialTabRoute = Routes.HISTORY
            }
            ShareActivity.ACTION_HANDLE_SHARE -> {
                val peerId = parseDeviceId(intent.getStringExtra(ShareActivity.EXTRA_PEER_HEX)) ?: return
                viewModel.openConversation(peerId)
                val sharedText = intent.getStringExtra(ShareActivity.EXTRA_SHARED_TEXT)
                if (!sharedText.isNullOrEmpty()) {
                    viewModel.sendText(peerId, sharedText) {}
                }
                val uris = extractClipUris(intent)
                if (uris.isNotEmpty()) {
                    viewModel.addPickedUris(peerId, uris)
                }
            }
        }
    }

    private fun extractClipUris(intent: Intent): List<Uri> {
        val out = mutableListOf<Uri>()
        intent.clipData?.let { cd ->
            for (i in 0 until cd.itemCount) {
                cd.getItemAt(i).uri?.let { out += it }
            }
        }
        return out
    }

    private fun parseDeviceId(hex: String?): DeviceId? {
        if (hex == null || hex.length != 32) return null
        return runCatching {
            DeviceId(Bytes16.of(ByteArray(16) { i -> hex.substring(i * 2, i * 2 + 2).toInt(16).toByte() }))
        }.getOrNull()
    }
}
