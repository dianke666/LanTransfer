package com.lantransfer.app.platform

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.DocumentsContract
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import androidx.lifecycle.DefaultLifecycleObserver
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.ProcessLifecycleOwner
import com.lantransfer.app.CopyActivity
import com.lantransfer.app.MainActivity
import com.lantransfer.app.R
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.domain.DirRef
import com.lantransfer.domain.Rng
import com.lantransfer.domain.StoredFileRef
import com.lantransfer.engine.ports.AppLifecycle
import com.lantransfer.engine.ports.ClipRead
import com.lantransfer.engine.ports.ClipWrite
import com.lantransfer.engine.ports.Clipboard
import com.lantransfer.engine.ports.FileLocator
import com.lantransfer.engine.ports.LocateResult
import com.lantransfer.engine.ports.Notifier
import com.lantransfer.engine.ports.NotifyKey
import com.lantransfer.engine.ports.RequestKind
import com.lantransfer.protocol.TransferId
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import java.io.File
import java.security.SecureRandom

object SecureRandomRng : Rng {
    private val random = SecureRandom()

    override fun nextBytes(n: Int): ByteArray = ByteArray(n).also { random.nextBytes(it) }
}

/** Android 前后台状态监测（ProcessLifecycleOwner，设计 C6.1）。 */
class ProcessLifecycleAppLifecycle : AppLifecycle {
    private val backgroundFlow = MutableStateFlow(false)
    override val background: StateFlow<Boolean> = backgroundFlow.asStateFlow()

    fun attach() {
        ProcessLifecycleOwner.get().lifecycle.addObserver(
            object : DefaultLifecycleObserver {
                override fun onStart(owner: LifecycleOwner) {
                    backgroundFlow.value = false
                }

                override fun onStop(owner: LifecycleOwner) {
                    backgroundFlow.value = true
                }
            },
        )
    }

    fun setBackgroundForTesting(bg: Boolean) {
        backgroundFlow.value = bg
    }
}

/** Android 剪贴板适配（需求 13.1–13.10）：写入后立即读回逐码点校验。 */
class AndroidClipboard(private val context: Context) : Clipboard {
    private val cm by lazy {
        context.applicationContext.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    }

    override suspend fun readText(): ClipRead = withContext(Dispatchers.Main) {
        try {
            val clip = cm.primaryClip ?: return@withContext ClipRead.NoText
            if (clip.itemCount == 0) return@withContext ClipRead.NoText
            val text = clip.getItemAt(0).coerceToText(context)?.toString()
            if (text.isNullOrEmpty()) ClipRead.NoText else ClipRead.Text(text)
        } catch (_: Throwable) {
            ClipRead.Failed
        }
    }

    override suspend fun writeText(s: String): ClipWrite = withContext(Dispatchers.Main) {
        try {
            cm.setPrimaryClip(ClipData.newPlainText("LanTransfer", s))
            val back = cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString()
            if (back == s) ClipWrite.Ok else ClipWrite.Failed
        } catch (_: Throwable) {
            ClipWrite.Failed
        }
    }
}

/** Android 文件打开与定位（需求 19.1–19.11）。 */
class AndroidFileLocator(private val context: Context) : FileLocator {
    override fun openFile(f: StoredFileRef, mime: String): LocateResult {
        val uri = toContentUri(f) ?: return LocateResult.NOT_FOUND
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, mime.ifBlank { "*/*" })
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        return try {
            context.startActivity(intent)
            LocateResult.OK
        } catch (_: ActivityNotFoundException) {
            LocateResult.NO_HANDLER
        } catch (_: SecurityException) {
            LocateResult.NO_ACCESS
        } catch (_: Throwable) {
            LocateResult.NO_HANDLER
        }
    }

    override fun revealInFolder(f: StoredFileRef): LocateResult {
        val exists = checkExists(f)
        if (!exists) return LocateResult.NOT_FOUND
        val dirUri = if (f.value.startsWith("content://")) {
            Uri.parse(f.value)
        } else {
            val parent = File(f.value).parentFile ?: return LocateResult.NO_ACCESS
            Uri.fromFile(parent)
        }
        return openDirectoryUri(dirUri)
    }

    override fun openDirectory(dir: DirRef): LocateResult = when (dir) {
        DirRef.Default -> {
            val uri = Uri.parse("content://com.android.externalstorage.documents/document/primary%3ADownload%2FLanTransfer")
            openDirectoryUri(uri)
        }
        is DirRef.Tree -> openDirectoryUri(Uri.parse(dir.uri))
        is DirRef.Path -> {
            val f = File(dir.path)
            if (!f.exists()) LocateResult.NO_ACCESS else openDirectoryUri(Uri.fromFile(f))
        }
    }

    private fun openDirectoryUri(uri: Uri): LocateResult {
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, DocumentsContract.Document.MIME_TYPE_DIR)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        return try {
            context.startActivity(intent)
            LocateResult.OK
        } catch (_: ActivityNotFoundException) {
            // 尝试系统下载应用
            val fallback = Intent(android.app.DownloadManager.ACTION_VIEW_DOWNLOADS).apply {
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            }
            try {
                context.startActivity(fallback)
                LocateResult.OK
            } catch (_: Throwable) {
                LocateResult.NO_FILE_MANAGER
            }
        } catch (_: Throwable) {
            LocateResult.NO_FILE_MANAGER
        }
    }

    private fun toContentUri(f: StoredFileRef): Uri? {
        if (f.value.startsWith("content://")) {
            val uri = Uri.parse(f.value)
            val ok = runCatching { context.contentResolver.openAssetFileDescriptor(uri, "r")?.close(); true }.getOrDefault(false)
            return if (ok) uri else null
        }
        val file = File(f.value)
        if (!file.exists()) return null
        return runCatching {
            FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
        }.getOrNull()
    }

    private fun checkExists(f: StoredFileRef): Boolean =
        if (f.value.startsWith("content://")) {
            runCatching { context.contentResolver.openAssetFileDescriptor(Uri.parse(f.value), "r")?.close(); true }.getOrDefault(false)
        } else {
            File(f.value).exists()
        }

    fun defaultDownloadsPath(): String =
        File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), AndroidReceiveStorage.DEFAULT_SUBDIR).absolutePath
}

/**
 * Android 系统通知适配（设计 C6.4、需求 21.1、21.6–21.11）：
 * 3 个通知渠道：progress（静默）、content（重要）、background（静默）。
 */
class AndroidNotifier(private val context: Context) : Notifier {
    private val appCtx = context.applicationContext
    private val nm = NotificationManagerCompat.from(appCtx)
    private val enabledFlow = MutableStateFlow(true)
    private var lastProgressCallback: ((TransferId, String, String, Int) -> Unit)? = null

    override val enabled: StateFlow<Boolean> = enabledFlow.asStateFlow()

    init {
        ensureChannels()
        refreshEnabled()
    }

    fun setProgressListener(listener: ((TransferId, String, String, Int) -> Unit)?) {
        lastProgressCallback = listener
    }

    fun refreshEnabled() {
        enabledFlow.value = runCatching { nm.areNotificationsEnabled() }.getOrDefault(true)
    }

    override fun text(key: NotifyKey, peerName: String, preview: String) {
        if (!enabledFlow.value) return
        val tk = key as? NotifyKey.Text ?: return
        val openIntent = Intent(appCtx, MainActivity::class.java).apply {
            action = ACTION_OPEN_PEER
            putExtra(EXTRA_PEER_HEX, tk.peer.value.toHex())
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        val openPi = PendingIntent.getActivity(appCtx, key.hashCode(), openIntent, PI_FLAGS)

        val copyIntent = Intent(appCtx, CopyActivity::class.java).apply {
            putExtra(EXTRA_PEER_HEX, tk.peer.value.toHex())
            putExtra(EXTRA_MSG_HEX, tk.messageId.value.toHex())
        }
        val copyPi = PendingIntent.getActivity(appCtx, key.hashCode() xor 0x5A5A, copyIntent, PI_FLAGS)

        val notification = NotificationCompat.Builder(appCtx, CHANNEL_CONTENT)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(UiText.notifyTextTitle(appCtx, peerName))
            .setContentText(preview)
            .setStyle(NotificationCompat.BigTextStyle().bigText(preview))
            .setAutoCancel(true)
            .setContentIntent(openPi)
            .addAction(0, UiText.btnCopy(appCtx), copyPi)
            .build()
        notifySafe(key, notification)
    }

    override fun fileCompleted(key: NotifyKey, peerName: String, fileName: String, file: StoredFileRef) {
        if (!enabledFlow.value) return
        val openIntent = Intent(appCtx, MainActivity::class.java).apply {
            action = ACTION_OPEN_HISTORY
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        val openPi = PendingIntent.getActivity(appCtx, key.hashCode(), openIntent, PI_FLAGS)
        val notification = NotificationCompat.Builder(appCtx, CHANNEL_CONTENT)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(UiText.notifyFileDoneTitle(appCtx, peerName))
            .setContentText(fileName)
            .setAutoCancel(true)
            .setContentIntent(openPi)
            .build()
        notifySafe(key, notification)
    }

    override fun request(key: NotifyKey, peerName: String, kind: RequestKind, fileCount: Int?) {
        if (!enabledFlow.value) return
        val title = if (kind == RequestKind.PAIRING) UiText.notifyPairReqTitle(appCtx) else UiText.notifyFileReqTitle(appCtx)
        val body = if (kind == RequestKind.PAIRING) {
            UiText.notifyPairReqBody(appCtx, peerName)
        } else {
            UiText.notifyFileReqBody(appCtx, peerName, fileCount ?: 1)
        }
        val intent = Intent(appCtx, MainActivity::class.java).apply {
            action = ACTION_OPEN_MAIN
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        val pi = PendingIntent.getActivity(appCtx, key.hashCode(), intent, PI_FLAGS)
        val notification = NotificationCompat.Builder(appCtx, CHANNEL_CONTENT)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(title)
            .setContentText(body)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .setContentIntent(pi)
            .build()
        notifySafe(key, notification)
    }

    override fun problem(key: NotifyKey, peerName: String, fileName: String?, status: String) {
        if (!enabledFlow.value) return
        val body = if (fileName.isNullOrBlank()) status else "$fileName：$status"
        val intent = Intent(appCtx, MainActivity::class.java).apply {
            action = ACTION_OPEN_HISTORY
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        val pi = PendingIntent.getActivity(appCtx, key.hashCode(), intent, PI_FLAGS)
        val notification = NotificationCompat.Builder(appCtx, CHANNEL_CONTENT)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(UiText.notifyProblemTitle(appCtx, peerName))
            .setContentText(body)
            .setAutoCancel(true)
            .setContentIntent(pi)
            .build()
        notifySafe(key, notification)
    }

    override fun progress(task: TransferId, peerName: String, fileName: String, percent: Int) {
        lastProgressCallback?.invoke(task, peerName, fileName, percent)
    }

    override fun cancel(key: NotifyKey) {
        val (tag, id) = tagAndId(key)
        runCatching { nm.cancel(tag, id) }
    }

    private fun ensureChannels() {
        val sysNm = appCtx.getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager ?: return
        sysNm.createNotificationChannels(
            listOf(
                NotificationChannel(CHANNEL_PROGRESS, UiText.notifyChannelProgress(appCtx), NotificationManager.IMPORTANCE_LOW),
                NotificationChannel(CHANNEL_CONTENT, UiText.notifyChannelContent(appCtx), NotificationManager.IMPORTANCE_HIGH),
                NotificationChannel(CHANNEL_BACKGROUND, UiText.notifyChannelBackground(appCtx), NotificationManager.IMPORTANCE_LOW),
            ),
        )
    }

    private fun notifySafe(key: NotifyKey, notification: android.app.Notification) {
        val (tag, id) = tagAndId(key)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(appCtx, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        try {
            nm.notify(tag, id, notification)
        } catch (_: SecurityException) {
            // 用户随时可能撤销通知权限
        }
    }

    private fun tagAndId(key: NotifyKey): Pair<String, Int> = when (key) {
        is NotifyKey.Text -> "text:${key.peer.value.toHex()}" to key.messageId.value.hashCode()
        is NotifyKey.PairingRequest -> "pair" to key.peer.value.hashCode()
        is NotifyKey.Transfer -> "xfer" to key.transferId.value.hashCode()
        is NotifyKey.File -> "file:${key.transferId.value.toHex()}" to key.index
    }

    companion object {
        const val CHANNEL_PROGRESS = "lan_transfer_progress"
        const val CHANNEL_CONTENT = "lan_transfer_content"
        const val CHANNEL_BACKGROUND = "lan_transfer_background"
        const val ACTION_OPEN_MAIN = "com.lantransfer.app.OPEN_MAIN"
        const val ACTION_OPEN_PEER = "com.lantransfer.app.OPEN_PEER"
        const val ACTION_OPEN_HISTORY = "com.lantransfer.app.OPEN_HISTORY"
        const val EXTRA_PEER_HEX = "peer_hex"
        const val EXTRA_MSG_HEX = "msg_hex"
        private const val PI_FLAGS = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
    }
}
