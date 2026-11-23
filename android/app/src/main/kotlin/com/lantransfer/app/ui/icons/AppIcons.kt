package com.lantransfer.app.ui.icons

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathBuilder
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp
import com.lantransfer.protocol.DeviceType

/**
 * 轻量自研统一矢量图标库（与 Windows WPF Geometry 完全对应，不额外引入 icons-extended 依赖）：
 * 覆盖 Android / Windows / iOS 设备图标及全部交互操作图标。
 */
object AppIcons {
    fun forDevice(type: DeviceType): ImageVector = when (type) {
        DeviceType.ANDROID -> AndroidPhone
        DeviceType.WINDOWS -> WindowsPc
        DeviceType.IOS -> IosPhone
    }

    val Logo: ImageVector by lazy {
        strokeIcon("Logo") {
            // 双向传输箭头
            moveTo(5f, 9f); lineTo(19f, 9f)
            moveTo(15f, 5f); lineTo(19f, 9f); lineTo(15f, 13f)
            moveTo(19f, 15f); lineTo(5f, 15f)
            moveTo(9f, 11f); lineTo(5f, 15f); lineTo(9f, 19f)
        }
    }

    val AndroidPhone: ImageVector by lazy {
        strokeIcon("AndroidPhone") {
            moveTo(7f, 4f); curveTo(7f, 2.9f, 7.9f, 2f, 9f, 2f); lineTo(15f, 2f)
            curveTo(16.1f, 2f, 17f, 2.9f, 17f, 4f); lineTo(17f, 20f)
            curveTo(17f, 21.1f, 16.1f, 22f, 15f, 22f); lineTo(9f, 22f)
            curveTo(7.9f, 22f, 7f, 21.1f, 7f, 20f); close()
            moveTo(10.5f, 19f); lineTo(13.5f, 19f)
        }
    }

    val WindowsPc: ImageVector by lazy {
        strokeIcon("WindowsPc") {
            moveTo(3f, 4.5f); lineTo(11f, 3.5f); lineTo(11f, 11f); lineTo(3f, 11f); close()
            moveTo(13f, 3.3f); lineTo(21f, 2.2f); lineTo(21f, 11f); lineTo(13f, 11f); close()
            moveTo(3f, 13f); lineTo(11f, 13f); lineTo(11f, 20.5f); lineTo(3f, 19.5f); close()
            moveTo(13f, 13f); lineTo(21f, 13f); lineTo(21f, 21.8f); lineTo(13f, 20.7f); close()
        }
    }

    val IosPhone: ImageVector by lazy {
        strokeIcon("IosPhone") {
            moveTo(7f, 3f); curveTo(7f, 1.9f, 7.9f, 1f, 9f, 1f); lineTo(15f, 1f)
            curveTo(16.1f, 1f, 17f, 1.9f, 17f, 3f); lineTo(17f, 21f)
            curveTo(17f, 22.1f, 16.1f, 23f, 15f, 23f); lineTo(9f, 23f)
            curveTo(7.9f, 23f, 7f, 22.1f, 7f, 21f); close()
            moveTo(10f, 4f); lineTo(14f, 4f)
            moveTo(10.5f, 20f); lineTo(13.5f, 20f)
        }
    }

    val Devices: ImageVector by lazy {
        strokeIcon("Devices") {
            moveTo(4f, 6f); lineTo(20f, 6f); lineTo(20f, 16f); lineTo(4f, 16f); close()
            moveTo(8f, 20f); lineTo(16f, 20f)
            moveTo(12f, 16f); lineTo(12f, 20f)
        }
    }

    val History: ImageVector by lazy {
        strokeIcon("History") {
            moveTo(3f, 12f); arcTo(9f, 9f, 0f, true, true, 5.6f, 18.4f)
            moveTo(3f, 5f); lineTo(3f, 10f); lineTo(8f, 10f)
            moveTo(12f, 7f); lineTo(12f, 12f); lineTo(15.5f, 14f)
        }
    }

    val Settings: ImageVector by lazy {
        strokeIcon("Settings") {
            moveTo(12f, 8.5f); arcTo(3.5f, 3.5f, 0f, true, false, 12f, 15.5f); arcTo(3.5f, 3.5f, 0f, true, false, 12f, 8.5f)
            moveTo(12f, 2f); lineTo(12f, 4.5f)
            moveTo(12f, 19.5f); lineTo(12f, 22f)
            moveTo(4.9f, 4.9f); lineTo(6.7f, 6.7f)
            moveTo(17.3f, 17.3f); lineTo(19.1f, 19.1f)
            moveTo(2f, 12f); lineTo(4.5f, 12f)
            moveTo(19.5f, 12f); lineTo(22f, 12f)
            moveTo(4.9f, 19.1f); lineTo(6.7f, 17.3f)
            moveTo(17.3f, 6.7f); lineTo(19.1f, 4.9f)
        }
    }

    val QrCode: ImageVector by lazy {
        strokeIcon("QrCode") {
            moveTo(3f, 3f); lineTo(10f, 3f); lineTo(10f, 10f); lineTo(3f, 10f); close()
            moveTo(14f, 3f); lineTo(21f, 3f); lineTo(21f, 10f); lineTo(14f, 10f); close()
            moveTo(3f, 14f); lineTo(10f, 14f); lineTo(10f, 21f); lineTo(3f, 21f); close()
            moveTo(14f, 14f); lineTo(16f, 14f); lineTo(16f, 16f); lineTo(14f, 16f); close()
            moveTo(18f, 14f); lineTo(21f, 14f); lineTo(21f, 17f)
            moveTo(14f, 19f); lineTo(17f, 19f); lineTo(17f, 21f)
            moveTo(20f, 20f); lineTo(21f, 20f)
        }
    }

    val ScanQr: ImageVector by lazy {
        strokeIcon("ScanQr") {
            moveTo(4f, 8f); lineTo(4f, 4f); lineTo(8f, 4f)
            moveTo(16f, 4f); lineTo(20f, 4f); lineTo(20f, 8f)
            moveTo(20f, 16f); lineTo(20f, 20f); lineTo(16f, 20f)
            moveTo(8f, 20f); lineTo(4f, 20f); lineTo(4f, 16f)
            moveTo(6f, 12f); lineTo(18f, 12f)
        }
    }

    val ManualLink: ImageVector by lazy {
        strokeIcon("ManualLink") {
            moveTo(12f, 2f); arcTo(10f, 10f, 0f, true, false, 12f, 22f); arcTo(10f, 10f, 0f, true, false, 12f, 2f)
            moveTo(2f, 12f); lineTo(22f, 12f)
            moveTo(12f, 2f); curveTo(15f, 5.5f, 15f, 18.5f, 12f, 22f)
            moveTo(12f, 2f); curveTo(9f, 5.5f, 9f, 18.5f, 12f, 22f)
        }
    }

    val Send: ImageVector by lazy {
        strokeIcon("Send") {
            moveTo(22f, 2f); lineTo(11f, 13f)
            moveTo(22f, 2f); lineTo(15f, 22f); lineTo(11f, 13f); lineTo(2f, 9f); close()
        }
    }

    val AttachFile: ImageVector by lazy {
        strokeIcon("AttachFile") {
            moveTo(14f, 2f); lineTo(6f, 2f); curveTo(4.9f, 2f, 4f, 2.9f, 4f, 4f)
            lineTo(4f, 20f); curveTo(4f, 21.1f, 4.9f, 22f, 6f, 22f)
            lineTo(18f, 22f); curveTo(19.1f, 22f, 20f, 21.1f, 20f, 20f)
            lineTo(20f, 8f); close()
            moveTo(14f, 2f); lineTo(14f, 8f); lineTo(20f, 8f)
        }
    }

    val Folder: ImageVector by lazy {
        strokeIcon("Folder") {
            moveTo(3f, 6f); curveTo(3f, 4.9f, 3.9f, 4f, 5f, 4f); lineTo(10f, 4f)
            lineTo(12f, 6f); lineTo(19f, 6f); curveTo(20.1f, 6f, 21f, 6.9f, 21f, 8f)
            lineTo(21f, 18f); curveTo(21f, 19.1f, 20.1f, 20f, 19f, 20f); lineTo(5f, 20f)
            curveTo(3.9f, 20f, 3f, 19.1f, 3f, 18f); close()
        }
    }

    val Copy: ImageVector by lazy {
        strokeIcon("Copy") {
            moveTo(9f, 9f); lineTo(19f, 9f); curveTo(20.1f, 9f, 21f, 9.9f, 21f, 11f)
            lineTo(21f, 19f); curveTo(21f, 20.1f, 20.1f, 21f, 19f, 21f)
            lineTo(9f, 21f); curveTo(7.9f, 21f, 7f, 20.1f, 7f, 19f)
            lineTo(7f, 11f); curveTo(7f, 9.9f, 7.9f, 9f, 9f, 9f); close()
            moveTo(5f, 15f); lineTo(4f, 15f); curveTo(3.4f, 15f, 3f, 14.6f, 3f, 14f)
            lineTo(3f, 5f); curveTo(3f, 3.9f, 3.9f, 3f, 5f, 3f); lineTo(15f, 3f)
        }
    }

    val Clipboard: ImageVector by lazy {
        strokeIcon("Clipboard") {
            moveTo(9f, 3f); lineTo(15f, 3f); lineTo(15f, 6f); lineTo(9f, 6f); close()
            moveTo(15f, 4.5f); lineTo(18f, 4.5f); curveTo(19.1f, 4.5f, 20f, 5.4f, 20f, 6.5f)
            lineTo(20f, 19.5f); curveTo(20f, 20.6f, 19.1f, 21.5f, 18f, 21.5f)
            lineTo(6f, 21.5f); curveTo(4.9f, 21.5f, 4f, 20.6f, 4f, 19.5f)
            lineTo(4f, 6.5f); curveTo(4f, 5.4f, 4.9f, 4.5f, 6f, 4.5f); lineTo(9f, 4.5f)
        }
    }

    val Shield: ImageVector by lazy {
        strokeIcon("Shield") {
            moveTo(12f, 2f); lineTo(4f, 5f); lineTo(4f, 11f)
            curveTo(4f, 16.5f, 7.4f, 20.8f, 12f, 22f)
            curveTo(16.6f, 20.8f, 20f, 16.5f, 20f, 11f); lineTo(20f, 5f); close()
            moveTo(9f, 12f); lineTo(11f, 14f); lineTo(15.5f, 9.5f)
        }
    }

    val Warning: ImageVector by lazy {
        strokeIcon("Warning") {
            moveTo(12f, 3f); lineTo(2f, 20f); lineTo(22f, 20f); close()
            moveTo(12f, 9f); lineTo(12f, 14f)
            moveTo(12f, 17f); lineTo(12f, 17.5f)
        }
    }

    val Trash: ImageVector by lazy {
        strokeIcon("Trash") {
            moveTo(4f, 6f); lineTo(20f, 6f)
            moveTo(9f, 6f); lineTo(9f, 4f); lineTo(15f, 4f); lineTo(15f, 6f)
            moveTo(6f, 6f); lineTo(7f, 20f); lineTo(17f, 20f); lineTo(18f, 6f)
        }
    }

    val Back: ImageVector by lazy {
        strokeIcon("Back") {
            moveTo(19f, 12f); lineTo(5f, 12f)
            moveTo(12f, 19f); lineTo(5f, 12f); lineTo(12f, 5f)
        }
    }

    val Close: ImageVector by lazy {
        strokeIcon("Close") {
            moveTo(6f, 6f); lineTo(18f, 18f)
            moveTo(18f, 6f); lineTo(6f, 18f)
        }
    }

    val Check: ImageVector by lazy {
        strokeIcon("Check") {
            moveTo(5f, 12f); lineTo(10f, 17f); lineTo(19f, 7f)
        }
    }

    val Sun: ImageVector by lazy {
        strokeIcon("Sun") {
            moveTo(12f, 7.5f); arcTo(4.5f, 4.5f, 0f, true, false, 12f, 16.5f); arcTo(4.5f, 4.5f, 0f, true, false, 12f, 7.5f)
            moveTo(12f, 2f); lineTo(12f, 4f)
            moveTo(12f, 20f); lineTo(12f, 22f)
            moveTo(4.9f, 4.9f); lineTo(6.3f, 6.3f)
            moveTo(17.7f, 17.7f); lineTo(19.1f, 19.1f)
            moveTo(2f, 12f); lineTo(4f, 12f)
            moveTo(20f, 12f); lineTo(22f, 12f)
            moveTo(4.9f, 19.1f); lineTo(6.3f, 17.7f)
            moveTo(17.7f, 6.3f); lineTo(19.1f, 4.9f)
        }
    }

    val Search: ImageVector by lazy {
        strokeIcon("Search") {
            moveTo(11f, 4f); arcTo(7f, 7f, 0f, true, false, 11f, 18f); arcTo(7f, 7f, 0f, true, false, 11f, 4f)
            moveTo(20f, 20f); lineTo(16f, 16f)
        }
    }

    private fun strokeIcon(name: String, builder: PathBuilder.() -> Unit): ImageVector =
        ImageVector.Builder(
            name = name,
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f,
        ).path(
            fill = null,
            stroke = SolidColor(Color.Black),
            strokeLineWidth = 1.9f,
            strokeLineCap = StrokeCap.Round,
            strokeLineJoin = StrokeJoin.Round,
            pathBuilder = builder,
        ).build()
}
