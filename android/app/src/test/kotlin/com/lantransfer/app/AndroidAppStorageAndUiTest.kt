package com.lantransfer.app

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.lantransfer.app.storage.LanTransferDatabase
import com.lantransfer.app.storage.RoomHistoryStore
import com.lantransfer.app.storage.RoomTaskStore
import com.lantransfer.app.storage.RoomTrustStore
import com.lantransfer.app.ui.text.UiText
import com.lantransfer.app.ui.text.UiTextMapper
import com.lantransfer.domain.DirRef
import com.lantransfer.domain.Direction
import com.lantransfer.domain.FileState
import com.lantransfer.domain.Ipv4
import com.lantransfer.domain.RecordTime
import com.lantransfer.domain.TaskRecord
import com.lantransfer.domain.TaskState
import com.lantransfer.domain.TextRecord
import com.lantransfer.domain.TextStatus
import com.lantransfer.domain.TransferFile
import com.lantransfer.domain.TrustedDevice
import com.lantransfer.engine.ports.Clock
import com.lantransfer.engine.ports.ListResult
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.MessageId
import com.lantransfer.protocol.TransferId
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.LocalDateTime

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class AndroidAppStorageAndUiTest {
    private lateinit var ctx: Context
    private lateinit var db: LanTransferDatabase
    private lateinit var trustStore: RoomTrustStore
    private lateinit var historyStore: RoomHistoryStore
    private lateinit var taskStore: RoomTaskStore

    private val fakeClock = object : Clock {
        override fun monotonicMs(): Long = 1000L
        override fun wallMs(): Long = 1_700_000_000_000L
        override fun localNow(): LocalDateTime = LocalDateTime.of(2026, 1, 1, 12, 0)
    }

    @Before
    fun setUp() {
        ctx = LocaleHelper.wrap(ApplicationProvider.getApplicationContext())
        db = LanTransferDatabase.createInMemory(ctx)
        trustStore = RoomTrustStore(db)
        historyStore = RoomHistoryStore(db)
        taskStore = RoomTaskStore(db, fakeClock)
    }

    @After
    fun tearDown() {
        db.close()
    }

    @Test
    fun roomTrustAndHistoryStore_roundTripAndClearAllRules() {
        val peer = DeviceId(Bytes16.of(ByteArray(16) { (it + 1).toByte() }))
        val pub = Bytes.of(ByteArray(32) { (it + 2).toByte() })
        val dev = TrustedDevice(
            deviceId = peer,
            displayName = "iPhone 16 Pro",
            deviceType = DeviceType.IOS,
            publicKey = pub,
            autoAccept = false,
            pairedAtUtcMs = 1_700_000_000_000L,
            lastAddress = Ipv4.of(192, 168, 1, 42),
            lastPort = 42580,
        )
        assertEquals(StoreResult.Ok, trustStore.upsert(dev))
        assertEquals(DeviceType.IOS, trustStore.get(peer)?.deviceType)

        // 文字写入与重复消息拦截
        val mid = MessageId(Bytes16.of(ByteArray(16) { 7 }))
        val rec = TextRecord(
            seq = 0L,
            peer = peer,
            peerName = "iPhone 16 Pro",
            messageId = mid,
            direction = Direction.RECEIVE,
            textUtf8 = Bytes.of("你好 LanTransfer\u0000无损".toByteArray()),
            sentAtUtcMs = 1_700_000_000_100L,
            recordedAt = RecordTime(1_700_000_000_100L, 480),
            status = TextStatus.RECEIVED,
        )
        assertEquals(StoreResult.Ok, historyStore.appendText(rec))
        assertEquals(StoreResult.Duplicate, historyStore.appendText(rec))

        // 任务 + 文件 + pending_cancel 持久化
        val tid = TransferId(Bytes16.of(ByteArray(16) { 9 }))
        val task = TaskRecord(
            transferId = tid,
            peer = peer,
            peerName = "iPhone 16 Pro",
            direction = Direction.RECEIVE,
            state = TaskState.INTERRUPTED,
            interrupt = null,
            receiveDir = DirRef.Default,
            recordedAt = RecordTime(1_700_000_000_200L, 480),
            updatedAtUtcMs = 1_700_000_000_200L,
            files = listOf(
                TransferFile(
                    transferId = tid,
                    index = 0,
                    seq = 0L,
                    originalName = "demo.bin",
                    size = 4096L,
                    mime = "application/octet-stream",
                    mtime = 1_700_000_000L,
                    state = FileState.TRANSFERRING,
                    persisted = 1024L,
                ),
            ),
        )
        assertEquals(StoreResult.Ok, taskStore.save(task))
        assertEquals(StoreResult.Ok, taskStore.addPendingCancel(peer, tid))
        assertEquals(1, taskStore.unfinished().size)

        // 清空历史不动 trusted_device 与 pending_cancel（需求 20.5）
        assertEquals(StoreResult.Ok, historyStore.clearAll())
        val listed = historyStore.list() as ListResult.Ok
        assertTrue(listed.items.isEmpty())
        assertNotNull(trustStore.get(peer))
        assertEquals(listOf(tid), taskStore.takePendingCancels(peer))
        assertNull(taskStore.get(tid))
    }

    @Test
    fun uiTextAndIosLabels_matchRequirements() {
        assertEquals("局域网传输", UiText.appName(ctx))
        assertEquals("目标地址不在本地网络内", UiText.errorNotLocal(ctx))
        assertEquals("对方版本不兼容，请将两端升级到最新版本", UiText.errorVersionIncompatible(ctx))
        assertEquals("暂不支持发送文件夹", UiText.errorFoldersNotSupported(ctx))
        assertEquals("单次最多发送 1000 个文件。", UiText.errorTooManyFiles(ctx))
        assertEquals("iOS", UiTextMapper.deviceTypeLabel(ctx, DeviceType.IOS))
        assertEquals("Windows", UiTextMapper.deviceTypeLabel(ctx, DeviceType.WINDOWS))
        assertEquals("Android", UiTextMapper.deviceTypeLabel(ctx, DeviceType.ANDROID))
    }

    @Test
    fun qrHelper_generatesSharpBitmap() {
        val bmp = QrHelper.generateBitmap("LT1:test-payload", 128)
        assertEquals(128, bmp.width)
        assertEquals(128, bmp.height)
    }
}
