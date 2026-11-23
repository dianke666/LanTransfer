package com.lantransfer.app

import android.Manifest
import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.lantransfer.app.platform.AndroidNotifier
import com.lantransfer.app.ui.text.UiText

/**
 * Android 前台服务（设计 C6.1、需求 21.1–21.2）：
 * - foregroundServiceType = connectedDevice；
 * - 有活跃文件传输时持有 PARTIAL_WAKE_LOCK 与 Wi-Fi 低延迟/高性能锁并每秒更新进度通知；
 * - 开启“后台接收”时常驻静默通知保持局域网监听。
 */
class TransferForegroundService : Service() {
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null
    private var lastUpdateMs: Long = 0L

    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(LocaleHelper.wrap(newBase))
    }

    override fun onCreate() {
        super.onCreate()
        val app = application as LanTransferApp
        app.notifier.setProgressListener { _, peerName, fileName, percent ->
            val now = System.currentTimeMillis()
            if (percent >= 100 || now - lastUpdateMs >= 900L) {
                lastUpdateMs = now
                updateNotification(buildProgressNotification(peerName, fileName, percent))
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val app = application as LanTransferApp
        val notification = buildIdleBackgroundNotification()
        val fgsType = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE
        } else {
            0
        }
        runCatching {
            ServiceCompat.startForeground(this, NOTIFICATION_ID, notification, fgsType)
        }
        if (app.hasActiveTransfers()) {
            acquireLocks()
        } else {
            releaseLocks()
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        (application as? LanTransferApp)?.notifier?.setProgressListener(null)
        releaseLocks()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun acquireLocks() {
        if (wakeLock?.isHeld != true) {
            val pm = getSystemService(Context.POWER_SERVICE) as? PowerManager
            wakeLock = pm?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "LanTransfer:Transfer")?.apply {
                setReferenceCounted(false)
                runCatching { acquire(60 * 60 * 1000L) }
            }
        }
        if (wifiLock?.isHeld != true) {
            val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
            val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                WifiManager.WIFI_MODE_FULL_LOW_LATENCY
            } else {
                @Suppress("DEPRECATION")
                WifiManager.WIFI_MODE_FULL_HIGH_PERF
            }
            wifiLock = wm?.createWifiLock(mode, "LanTransfer:WifiTransfer")?.apply {
                setReferenceCounted(false)
                runCatching { acquire() }
            }
        }
    }

    private fun releaseLocks() {
        runCatching {
            if (wakeLock?.isHeld == true) wakeLock?.release()
        }
        wakeLock = null
        runCatching {
            if (wifiLock?.isHeld == true) wifiLock?.release()
        }
        wifiLock = null
    }

    private fun buildIdleBackgroundNotification(): Notification {
        val openPi = PendingIntent.getActivity(
            this,
            100,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, AndroidNotifier.CHANNEL_BACKGROUND)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(UiText.notifyBgRunningTitle(this))
            .setContentText(UiText.notifyBgRunningBody(this))
            .setOngoing(true)
            .setSilent(true)
            .setContentIntent(openPi)
            .build()
    }

    private fun buildProgressNotification(peerName: String, fileName: String, percent: Int): Notification {
        val clamped = percent.coerceIn(0, 100)
        val openPi = PendingIntent.getActivity(
            this,
            101,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, AndroidNotifier.CHANNEL_PROGRESS)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle(UiText.notifyProgressTitle(this, peerName, clamped))
            .setContentText(fileName)
            .setProgress(100, clamped, false)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setSilent(true)
            .setContentIntent(openPi)
            .build()
    }

    private fun updateNotification(notification: Notification) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        try {
            NotificationManagerCompat.from(this).notify(NOTIFICATION_ID, notification)
        } catch (_: SecurityException) {
            // 权限在运行中被收回时忽略
        }
    }

    companion object {
        private const val NOTIFICATION_ID = 42580

        fun start(context: Context) {
            val intent = Intent(context, TransferForegroundService::class.java)
            runCatching { ContextCompat.startForegroundService(context, intent) }
        }

        fun stop(context: Context) {
            runCatching { context.stopService(Intent(context, TransferForegroundService::class.java)) }
        }
    }
}
