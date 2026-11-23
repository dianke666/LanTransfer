package com.lantransfer.app.ui.screens

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.view.WindowManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
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
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.lantransfer.app.BackgroundRunGuide
import com.lantransfer.app.QrHelper
import com.lantransfer.app.QrImageAnalyzer
import com.lantransfer.app.ui.icons.AppIcons
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.text.UiTextMapper
import com.lantransfer.app.ui.theme.LocalLanColors
import com.lantransfer.app.ui.theme.MonoTextStyle
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.SizeFormatter
import com.lantransfer.engine.PairingService
import com.lantransfer.engine.PairingUiState
import com.lantransfer.engine.QrScreenState
import com.lantransfer.engine.RequestId
import com.lantransfer.engine.TransferTaskView
import java.util.concurrent.Executors

/** 本机二维码展示页（设置 FLAG_SECURE 防止截屏泄露令牌，需求 8.1、8.7、8.13）。 */
@Composable
fun QrShowScreen(
    pairing: PairingService?,
    onBack: () -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current

    DisposableEffect(Unit) {
        val window = (ctx as? Activity)?.window
        window?.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        onDispose {
            window?.clearFlags(WindowManager.LayoutParams.FLAG_SECURE)
        }
    }

    val qrFlow = remember(pairing) { pairing?.qrScreen() }
    val qrState by (qrFlow?.collectAsState(initial = QrScreenState.NoNetwork) ?: remember { mutableStateOf(QrScreenState.NoNetwork) })

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(colors.canvas),
    ) {
        ScreenTopBar(title = UiText.btnShowQr(ctx), onBack = onBack)

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(horizontal = 24.dp, vertical = 20.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Surface(
                shape = RoundedCornerShape(24.dp),
                color = colors.surface,
                border = BorderStroke(1.dp, colors.border),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Column(
                    modifier = Modifier.padding(24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Text(
                        text = UiText.labelQrHint(ctx),
                        style = MaterialTheme.typography.titleSmall,
                        color = colors.textPrimary,
                        textAlign = TextAlign.Center,
                    )
                    Spacer(Modifier.height(20.dp))

                    when (val st = qrState) {
                        is QrScreenState.Showing -> {
                            val bmp = remember(st.text) { QrHelper.generateBitmap(st.text, 640).asImageBitmap() }
                            Surface(
                                shape = RoundedCornerShape(18.dp),
                                color = Color.White,
                                border = BorderStroke(1.dp, Color(0xFFE2E8F0)),
                                modifier = Modifier
                                    .size(240.dp)
                                    .padding(4.dp),
                            ) {
                                Image(
                                    bitmap = bmp,
                                    contentDescription = UiText.btnQrPair(ctx),
                                    filterQuality = FilterQuality.None,
                                    modifier = Modifier
                                        .fillMaxSize()
                                        .padding(10.dp),
                                )
                            }
                            Spacer(Modifier.height(16.dp))
                            val eps = st.payload.ipv4List.joinToString(" · ") { "${Ipv4(it)}:${st.payload.port}" }
                            Text(
                                text = eps,
                                style = MonoTextStyle.copy(fontSize = 12.sp),
                                color = colors.textSecondary,
                                textAlign = TextAlign.Center,
                            )
                            Spacer(Modifier.height(8.dp))
                            Text(
                                text = UiText.labelQrAutoRefresh(ctx),
                                style = MaterialTheme.typography.bodySmall,
                                color = colors.textMuted,
                                textAlign = TextAlign.Center,
                            )
                        }
                        QrScreenState.NoNetwork -> {
                            Box(
                                modifier = Modifier
                                    .size(220.dp)
                                    .clip(RoundedCornerShape(16.dp))
                                    .background(colors.surfaceSubtle),
                                contentAlignment = Alignment.Center,
                            ) {
                                Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.padding(16.dp)) {
                                    Icon(AppIcons.Warning, contentDescription = null, tint = colors.warning, modifier = Modifier.size(36.dp))
                                    Spacer(Modifier.height(10.dp))
                                    Text(
                                        text = UiText.errorNoNetworkQr(ctx),
                                        style = MaterialTheme.typography.bodySmall,
                                        color = colors.textSecondary,
                                        textAlign = TextAlign.Center,
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

/** CameraX + ZXing 实时扫码配对页（需求 8.2–8.4、23.6，3 秒错误防抖）。 */
@Composable
fun QrScanScreen(
    onQrScanned: (String, (Boolean, String?) -> Unit) -> Unit,
    onOpenManualConnect: () -> Unit,
    onShowToast: (String) -> Unit,
    onBack: () -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    val lifecycleOwner = LocalLifecycleOwner.current

    var hasCameraPerm by remember {
        mutableStateOf(
            ContextCompat.checkSelfPermission(ctx, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED,
        )
    }
    var processing by remember { mutableStateOf(false) }
    var lastErrorMs by remember { mutableLongStateOf(0L) }

    val permLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        hasCameraPerm = granted
    }

    LaunchedEffect(Unit) {
        if (!hasCameraPerm) {
            permLauncher.launch(Manifest.permission.CAMERA)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(colors.canvas),
    ) {
        ScreenTopBar(title = UiText.btnScanQr(ctx), onBack = onBack)

        if (!hasCameraPerm) {
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(28.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center,
            ) {
                Box(
                    modifier = Modifier
                        .size(64.dp)
                        .clip(RoundedCornerShape(18.dp))
                        .background(colors.accentSoft),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(AppIcons.ScanQr, contentDescription = null, tint = colors.accent, modifier = Modifier.size(32.dp))
                }
                Spacer(Modifier.height(16.dp))
                Text(
                    text = UiText.labelCameraPermissionHint(ctx),
                    style = MaterialTheme.typography.bodyMedium,
                    color = colors.textPrimary,
                    textAlign = TextAlign.Center,
                )
                Spacer(Modifier.height(20.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Button(onClick = { BackgroundRunGuide.openAppSettings(ctx) }, shape = RoundedCornerShape(12.dp)) {
                        Text(UiText.btnOpenAppSettings(ctx))
                    }
                    OutlinedButton(onClick = onOpenManualConnect, shape = RoundedCornerShape(12.dp)) {
                        Text(UiText.btnManualConnect(ctx))
                    }
                }
            }
            return
        }

        val executor = remember { Executors.newSingleThreadExecutor() }
        DisposableEffect(Unit) {
            onDispose { executor.shutdown() }
        }

        Box(modifier = Modifier.fillMaxSize()) {
            AndroidView(
                factory = { viewCtx ->
                    val previewView = PreviewView(viewCtx)
                    val providerFuture = ProcessCameraProvider.getInstance(viewCtx)
                    providerFuture.addListener(
                        {
                            runCatching {
                                val provider = providerFuture.get()
                                val preview = Preview.Builder().build().also {
                                    it.surfaceProvider = previewView.surfaceProvider
                                }
                                val analysis = ImageAnalysis.Builder()
                                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                                    .build()
                                analysis.setAnalyzer(
                                    executor,
                                    QrImageAnalyzer { text ->
                                        if (processing) return@QrImageAnalyzer
                                        processing = true
                                        previewView.post {
                                            onQrScanned(text) { ok, err ->
                                                if (ok) {
                                                    onBack()
                                                } else {
                                                    val now = System.currentTimeMillis()
                                                    if (err != null && now - lastErrorMs >= 3_000L) {
                                                        lastErrorMs = now
                                                        onShowToast(err)
                                                    }
                                                    processing = false
                                                }
                                            }
                                        }
                                    },
                                )
                                provider.unbindAll()
                                provider.bindToLifecycle(lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
                            }
                        },
                        ContextCompat.getMainExecutor(viewCtx),
                    )
                    previewView
                },
                modifier = Modifier.fillMaxSize(),
            )

            // 中央柔光扫描框
            Box(
                modifier = Modifier
                    .align(Alignment.Center)
                    .fillMaxWidth(0.68f)
                    .aspectRatio(1f)
                    .border(2.5.dp, colors.accent, RoundedCornerShape(22.dp)),
            )

            // 底部提示与手动连接切换
            Surface(
                shape = RoundedCornerShape(18.dp),
                color = Color(0xCC0F172A),
                modifier = Modifier
                    .align(Alignment.BottomCenter)
                    .padding(horizontal = 20.dp, vertical = 28.dp)
                    .fillMaxWidth(),
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    if (processing) {
                        CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp, color = Color.White)
                        Spacer(Modifier.width(10.dp))
                    }
                    Text(
                        text = if (processing) UiText.btnConnecting(ctx) else UiText.labelQrHint(ctx),
                        style = MaterialTheme.typography.bodySmall,
                        color = Color.White,
                        modifier = Modifier.weight(1f),
                    )
                    TextButton(onClick = onOpenManualConnect) {
                        Text(UiText.btnManualConnect(ctx), color = Color(0xFF93C5FD))
                    }
                }
            }
        }
    }
}

/** 手动输入 IPv4 与端口连接页（需求 6.2–6.9）。 */
@Composable
fun ManualConnectScreen(
    onConnect: (ip: String, port: String, onResult: (String?) -> Unit) -> Unit,
    onBack: () -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    var ip by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("") }
    var errorMsg by remember { mutableStateOf<String?>(null) }
    var busy by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(colors.canvas),
    ) {
        ScreenTopBar(title = UiText.btnManualConnect(ctx), onBack = onBack)

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Surface(
                shape = RoundedCornerShape(18.dp),
                color = colors.surface,
                border = BorderStroke(1.dp, colors.border),
                modifier = Modifier.fillMaxWidth(),
            ) {
                Column(modifier = Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    Text(
                        text = UiText.labelEncryptedHint(ctx),
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.textMuted,
                    )
                    OutlinedTextField(
                        value = ip,
                        onValueChange = { ip = it; errorMsg = null },
                        label = { Text(UiText.labelIpAddress(ctx)) },
                        placeholder = { Text("例如 192.168.1.100") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Ascii),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.fillMaxWidth(),
                    )
                    OutlinedTextField(
                        value = port,
                        onValueChange = { port = it; errorMsg = null },
                        label = { Text(UiText.labelPortOptional(ctx)) },
                        placeholder = { Text("42580") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.fillMaxWidth(),
                    )
                    if (errorMsg != null) {
                        AlertBanner(
                            text = errorMsg!!,
                            bg = colors.dangerSoft,
                            border = colors.danger,
                            textColor = colors.danger,
                        )
                    }
                    Button(
                        onClick = {
                            busy = true
                            errorMsg = null
                            onConnect(ip, port) { err ->
                                busy = false
                                if (err == null) onBack() else errorMsg = err
                            }
                        },
                        enabled = !busy && ip.isNotBlank(),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(48.dp),
                    ) {
                        if (busy) {
                            CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp, color = Color.White)
                            Spacer(Modifier.width(8.dp))
                            Text(UiText.btnConnecting(ctx))
                        } else {
                            Text(UiText.btnConnect(ctx))
                        }
                    }
                }
            }
        }
    }
}

/** 全局配对状态确认/验证码弹窗（需求 7.1–7.4）。 */
@Composable
fun PairingOverlayDialog(
    state: PairingUiState,
    onRespond: (RequestId, Boolean) -> Unit,
    onCancel: () -> Unit,
) {
    if (state is PairingUiState.Idle) return
    val ctx = LocalContext.current
    val colors = LocalLanColors.current

    val title: String
    val subtitle: String
    val sas: String?
    val showProgress: Boolean
    when (state) {
        is PairingUiState.Connecting -> {
            title = UiText.btnConnecting(ctx)
            subtitle = UiText.labelEncryptedHint(ctx)
            sas = null
            showProgress = true
        }
        is PairingUiState.WaitingForPeer -> {
            title = UiText.statusPairWaiting(ctx)
            subtitle = "${state.peer.displayName} · ${UiTextMapper.deviceTypeLabel(ctx, state.peer.deviceType)}"
            sas = null
            showProgress = true
        }
        is PairingUiState.ShowingCode -> {
            title = UiText.statusPairWaiting(ctx)
            subtitle = "${state.peer.displayName} · ${UiTextMapper.deviceTypeLabel(ctx, state.peer.deviceType)}"
            sas = state.sas
            showProgress = false
        }
        is PairingUiState.Request -> {
            title = UiText.labelPairRequestFrom(ctx, state.peer.displayName)
            val ip = state.peer.ip?.let { " · $it" }.orEmpty()
            subtitle = "${UiTextMapper.deviceTypeLabel(ctx, state.peer.deviceType)}$ip"
            sas = state.sas
            showProgress = state.sas == null
        }
        is PairingUiState.Completing -> {
            title = UiText.statusPairCompleting(ctx)
            subtitle = state.peer.displayName
            sas = null
            showProgress = true
        }
        PairingUiState.Idle -> return
    }

    AlertDialog(
        onDismissRequest = {
            when (state) {
                is PairingUiState.Request -> onRespond(state.id, false)
                is PairingUiState.Connecting, is PairingUiState.WaitingForPeer, is PairingUiState.ShowingCode -> onCancel()
                else -> Unit
            }
        },
        shape = RoundedCornerShape(22.dp),
        title = {
            Text(text = title, style = MaterialTheme.typography.titleMedium, color = colors.textPrimary)
        },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(text = subtitle, style = MaterialTheme.typography.bodySmall, color = colors.textSecondary)
                if (sas != null) {
                    Surface(
                        shape = RoundedCornerShape(14.dp),
                        color = colors.accentSoft,
                        border = BorderStroke(1.dp, colors.accent.copy(alpha = 0.35f)),
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Column(
                            modifier = Modifier.padding(vertical = 16.dp, horizontal = 12.dp),
                            horizontalAlignment = Alignment.CenterHorizontally,
                        ) {
                            Text(
                                text = UiText.labelPairCodeHint(ctx),
                                style = MaterialTheme.typography.labelSmall,
                                color = colors.textSecondary,
                            )
                            Spacer(Modifier.height(6.dp))
                            val grouped = if (sas.length == 6) "${sas.substring(0, 3)} ${sas.substring(3)}" else sas
                            Text(
                                text = grouped,
                                style = MonoTextStyle.copy(fontSize = 30.sp, fontWeight = FontWeight.Bold, letterSpacing = 4.sp),
                                color = colors.accent,
                            )
                        }
                    }
                }
                if (showProgress) {
                    LinearProgressIndicator(modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(4.dp)))
                }
            }
        },
        confirmButton = {
            if (state is PairingUiState.Request) {
                Button(
                    onClick = { onRespond(state.id, true) },
                    enabled = state.sas != null,
                    shape = RoundedCornerShape(10.dp),
                ) {
                    Text(UiText.btnAccept(ctx))
                }
            }
        },
        dismissButton = {
            when (state) {
                is PairingUiState.Request -> OutlinedButton(
                    onClick = { onRespond(state.id, false) },
                    shape = RoundedCornerShape(10.dp),
                ) {
                    Text(UiText.btnReject(ctx))
                }
                is PairingUiState.Connecting, is PairingUiState.WaitingForPeer, is PairingUiState.ShowingCode -> TextButton(onClick = onCancel) {
                    Text(UiText.btnCancel(ctx))
                }
                else -> Unit
            }
        },
    )
}

/** 收到的文件传输请求弹窗（需求 15.1–15.8）。 */
@Composable
fun IncomingOfferDialog(
    view: TransferTaskView,
    onAccept: () -> Unit,
    onReject: () -> Unit,
) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
    val task = view.task
    val total = task.files.sumOf { it.size }

    AlertDialog(
        onDismissRequest = {},
        shape = RoundedCornerShape(22.dp),
        title = {
            Column {
                Text(
                    text = UiText.labelIncomingOfferFrom(ctx, task.peerName),
                    style = MaterialTheme.typography.titleMedium,
                    color = colors.textPrimary,
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    text = "${UiText.labelFileCount(ctx, task.files.size)} · ${UiText.labelTotalSize(ctx, SizeFormatter.format(total))}",
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.textSecondary,
                )
            }
        },
        text = {
            LazyColumn(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 220.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                items(task.files, key = { it.index }) { f ->
                    Surface(
                        shape = RoundedCornerShape(10.dp),
                        color = colors.surfaceSubtle,
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Row(
                            modifier = Modifier.padding(horizontal = 10.dp, vertical = 8.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Icon(AppIcons.AttachFile, contentDescription = null, tint = colors.accent, modifier = Modifier.size(16.dp))
                            Spacer(Modifier.width(8.dp))
                            Text(
                                text = f.originalName,
                                style = MaterialTheme.typography.bodySmall,
                                color = colors.textPrimary,
                                maxLines = 1,
                                modifier = Modifier.weight(1f),
                            )
                            Spacer(Modifier.width(8.dp))
                            Text(
                                text = SizeFormatter.format(f.size),
                                style = MonoTextStyle.copy(fontSize = 11.sp),
                                color = colors.textMuted,
                            )
                        }
                    }
                }
            }
        },
        confirmButton = {
            Button(onClick = onAccept, shape = RoundedCornerShape(10.dp)) {
                Text(UiText.btnReceive(ctx))
            }
        },
        dismissButton = {
            OutlinedButton(onClick = onReject, shape = RoundedCornerShape(10.dp)) {
                Text(UiText.btnReject(ctx))
            }
        },
    )
}

@Composable
fun ScreenTopBar(title: String, onBack: () -> Unit) {
    val ctx = LocalContext.current
    val colors = LocalLanColors.current
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
            Spacer(Modifier.width(4.dp))
            Text(text = title, style = MaterialTheme.typography.titleMedium, color = colors.textPrimary)
        }
    }
}
