package com.lantransfer.app

import android.app.Activity
import android.content.Context
import android.os.Bundle
import android.widget.Toast
import com.lantransfer.app.platform.AndroidNotifier
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.engine.ports.ClipWrite
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.MessageId
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/** 通知栏“复制”按钮透明中转 Activity（设计 C6.4、需求 21.6）。 */
class CopyActivity : Activity() {
    override fun attachBaseContext(newBase: Context) {
        super.attachBaseContext(LocaleHelper.wrap(newBase))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val app = application as LanTransferApp
        val peerHex = intent.getStringExtra(AndroidNotifier.EXTRA_PEER_HEX)
        val msgHex = intent.getStringExtra(AndroidNotifier.EXTRA_MSG_HEX)
        val peerId = parseBytes16(peerHex)?.let { DeviceId(it) }
        val msgId = parseBytes16(msgHex)?.let { MessageId(it) }

        val orig = if (peerId != null && msgId != null) {
            app.services?.text?.originalText(peerId, msgId)
                ?: app.historyStore.findText(peerId, msgId)?.textUtf8?.toByteArray()?.decodeToString()
        } else {
            null
        }

        if (orig == null) {
            finish()
            return
        }

        CoroutineScope(Dispatchers.Main).launch {
            val res = app.clipboard.writeText(orig)
            val msg = when {
                res is ClipWrite.Ok && orig.contains('\u0000') -> UiText.toastCopiedWithNul(this@CopyActivity)
                res is ClipWrite.Ok -> UiText.toastCopied(this@CopyActivity)
                else -> UiText.toastClipboardFailed(this@CopyActivity)
            }
            Toast.makeText(this@CopyActivity, msg, Toast.LENGTH_SHORT).show()
            finish()
        }
    }

    private fun parseBytes16(hex: String?): Bytes16? {
        if (hex == null || hex.length != 32) return null
        return runCatching {
            Bytes16.of(ByteArray(16) { i -> hex.substring(i * 2, i * 2 + 2).toInt(16).toByte() })
        }.getOrNull()
    }
}
