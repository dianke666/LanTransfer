package com.lantransfer.app.platform

import android.content.Context
import android.os.Build
import android.provider.Settings as AndroidSettings
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.lantransfer.domain.DeviceIdentity
import com.lantransfer.domain.DirRef
import com.lantransfer.domain.DisplayNameRules
import com.lantransfer.domain.Rng
import com.lantransfer.domain.Settings
import com.lantransfer.engine.ports.IdentityLoad
import com.lantransfer.engine.ports.IdentityStore
import com.lantransfer.engine.ports.SettingsStore
import com.lantransfer.engine.ports.StoreResult
import com.lantransfer.protocol.Bytes
import com.lantransfer.protocol.Bytes16
import com.lantransfer.protocol.DeviceId
import com.lantransfer.protocol.DeviceType
import com.lantransfer.protocol.X25519KeyPair
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.io.File
import java.nio.ByteBuffer
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

enum class ThemeMode(val wire: String) {
    SYSTEM("system"),
    LIGHT("light"),
    DARK("dark"),
    ;

    companion object {
        fun parse(s: String?): ThemeMode = entries.firstOrNull { it.wire == s } ?: SYSTEM
    }
}

private val Context.lanTransferDataStore: DataStore<Preferences> by preferencesDataStore(name = "lantransfer_settings")

/**
 * Android Preferences DataStore 设置存储（设计 M5）：
 * 持久化接收目录、自动复制、后台接收、设备显示名称、外观主题与后台运行引导状态。
 */
class DataStoreSettingsStore(
    private val context: Context,
    private val scope: CoroutineScope,
) : SettingsStore {
    private val dataStore = context.applicationContext.lanTransferDataStore
    private val settingsFlow = MutableStateFlow(Settings())
    private val themeFlow = MutableStateFlow(ThemeMode.SYSTEM)
    private val displayNameFlow = MutableStateFlow<String?>(null)
    private val bgGuideShownFlow = MutableStateFlow(false)

    override val changes: Flow<Settings> = settingsFlow.asStateFlow()
    val theme: StateFlow<ThemeMode> = themeFlow.asStateFlow()
    val backgroundGuideShown: StateFlow<Boolean> = bgGuideShownFlow.asStateFlow()

    init {
        val prefs = runBlocking { runCatching { dataStore.data.first() }.getOrNull() }
        if (prefs != null) {
            applyPrefs(prefs)
        }
        scope.launch {
            dataStore.data.collect { applyPrefs(it) }
        }
    }

    override fun current(): Settings = settingsFlow.value

    override fun update(transform: (Settings) -> Settings): StoreResult {
        val next = transform(settingsFlow.value)
        return runCatching {
            runBlocking {
                dataStore.edit { p ->
                    p[KEY_RECEIVE_DIR] = next.receiveDirectory.serialize()
                    p[KEY_AUTO_COPY] = next.autoCopy
                    p[KEY_BG_RECEIVE] = next.backgroundReceive
                }
            }
            settingsFlow.value = next
            StoreResult.Ok
        }.getOrElse { StoreResult.Failed(it.message ?: "保存设置失败") }
    }

    fun getDisplayName(): String? = displayNameFlow.value

    fun setDisplayName(name: String): StoreResult = runCatching {
        runBlocking {
            dataStore.edit { p -> p[KEY_DISPLAY_NAME] = name }
        }
        displayNameFlow.value = name
        StoreResult.Ok
    }.getOrElse { StoreResult.Failed(it.message ?: "保存设备名称失败") }

    fun setTheme(mode: ThemeMode): StoreResult = runCatching {
        runBlocking {
            dataStore.edit { p -> p[KEY_THEME_MODE] = mode.wire }
        }
        themeFlow.value = mode
        StoreResult.Ok
    }.getOrElse { StoreResult.Failed(it.message ?: "保存主题失败") }

    fun markBackgroundGuideShown() {
        bgGuideShownFlow.value = true
        scope.launch {
            runCatching { dataStore.edit { p -> p[KEY_BG_GUIDE_SHOWN] = true } }
        }
    }

    private fun applyPrefs(p: Preferences) {
        val dir = p[KEY_RECEIVE_DIR]?.let { DirRef.parse(it) } ?: DirRef.Default
        settingsFlow.value = Settings(
            receiveDirectory = dir,
            autoCopy = p[KEY_AUTO_COPY] ?: false,
            backgroundReceive = p[KEY_BG_RECEIVE] ?: false,
        )
        themeFlow.value = ThemeMode.parse(p[KEY_THEME_MODE])
        displayNameFlow.value = p[KEY_DISPLAY_NAME]
        bgGuideShownFlow.value = p[KEY_BG_GUIDE_SHOWN] ?: false
    }

    private companion object {
        val KEY_RECEIVE_DIR = stringPreferencesKey("receive_directory")
        val KEY_AUTO_COPY = booleanPreferencesKey("auto_copy")
        val KEY_BG_RECEIVE = booleanPreferencesKey("background_receive")
        val KEY_DISPLAY_NAME = stringPreferencesKey("display_name")
        val KEY_THEME_MODE = stringPreferencesKey("theme_mode")
        val KEY_BG_GUIDE_SHOWN = booleanPreferencesKey("background_guide_shown")
    }
}

/**
 * Android Keystore 保护的本机身份存储（设计 M4、需求 4.1–4.7、Q14）：
 * 使用 AndroidKeyStore AES-256-GCM 密钥加密 device_id(16) || private_key(32) || public_key(32)，
 * 原子写入 noBackupFilesDir/identity.bin；损坏时重置并报告 ResetCorrupted。
 */
class KeystoreIdentityStore(
    private val context: Context,
    private val settings: DataStoreSettingsStore,
    private val rng: Rng,
) : IdentityStore {
    private val identityFile = File(context.applicationContext.noBackupFilesDir, "identity.bin")
    private var cached: DeviceIdentity? = null

    @Synchronized
    override fun loadOrCreate(): IdentityLoad {
        cached?.let { return IdentityLoad.Loaded(it) }
        val existed = identityFile.exists()
        if (existed) {
            val loaded = tryLoad()
            if (loaded != null) {
                cached = loaded
                return IdentityLoad.Loaded(loaded)
            }
        }

        val id = DeviceId(Bytes16.of(rng.nextBytes(16)))
        val kp = X25519KeyPair.fromPrivateKey(rng.nextBytes(32))
        val fresh = DeviceIdentity(id, kp, displayName())

        return when (val err = trySave(fresh)) {
            null -> {
                cached = fresh
                if (existed) IdentityLoad.ResetCorrupted(fresh) else IdentityLoad.Created(fresh)
            }
            else -> IdentityLoad.SaveFailed(err, corrupted = existed)
        }
    }

    override fun displayName(): String {
        settings.getDisplayName()?.takeIf { it.isNotBlank() }?.let { return it }
        val systemCandidate = runCatching {
            AndroidSettings.Global.getString(context.contentResolver, AndroidSettings.Global.DEVICE_NAME)
        }.getOrNull() ?: Build.MODEL
        return DisplayNameRules.defaultName(systemCandidate, DeviceType.ANDROID)
    }

    override fun setDisplayName(name: String): StoreResult {
        val r = settings.setDisplayName(name)
        if (r is StoreResult.Ok) {
            cached?.let { cached = DeviceIdentity(it.deviceId, it.keyPair, name) }
        }
        return r
    }

    private fun tryLoad(): DeviceIdentity? = runCatching {
        val raw = identityFile.readBytes()
        if (raw.size < 1 + 12 + 80) return@runCatching null
        val plain = when (raw[0].toInt()) {
            FORMAT_KEYSTORE_GCM -> {
                val iv = raw.copyOfRange(1, 13)
                val ct = raw.copyOfRange(13, raw.size)
                val key = getOrCreateKeystoreKey() ?: return@runCatching null
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, iv))
                cipher.doFinal(ct)
            }
            FORMAT_FALLBACK_PLAIN -> raw.copyOfRange(1, raw.size)
            else -> return@runCatching null
        }
        if (plain.size != 80) return@runCatching null
        val id = DeviceId(Bytes16.of(plain.copyOfRange(0, 16)))
        val privBytes = plain.copyOfRange(16, 48)
        val pub = Bytes.of(plain.copyOfRange(48, 80))
        val kp = X25519KeyPair.fromPrivateKey(privBytes)
        if (kp.publicKey != pub) return@runCatching null
        DeviceIdentity(id, kp, displayName())
    }.getOrNull()

    private fun trySave(identity: DeviceIdentity): String? = try {
        val plain = ByteBuffer.allocate(80)
            .put(identity.deviceId.value.toByteArray())
            .put(identity.keyPair.privateKeyBytes())
            .put(identity.keyPair.publicKey.toByteArray())
            .array()

        val key = getOrCreateKeystoreKey()
        val encoded = if (key != null) {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, key)
            val iv = cipher.iv
            val ct = cipher.doFinal(plain)
            ByteBuffer.allocate(1 + iv.size + ct.size)
                .put(FORMAT_KEYSTORE_GCM.toByte())
                .put(iv)
                .put(ct)
                .array()
        } else {
            ByteBuffer.allocate(1 + plain.size)
                .put(FORMAT_FALLBACK_PLAIN.toByte())
                .put(plain)
                .array()
        }

        identityFile.parentFile?.mkdirs()
        val tmp = File(identityFile.parentFile, "${identityFile.name}.tmp")
        tmp.writeBytes(encoded)
        if (!tmp.renameTo(identityFile)) {
            identityFile.writeBytes(encoded)
            tmp.delete()
        }
        null
    } catch (e: Throwable) {
        e.message ?: "无法写入身份文件"
    }

    private fun getOrCreateKeystoreKey(): SecretKey? = runCatching {
        val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (ks.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return@runCatching it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        gen.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build(),
        )
        gen.generateKey()
    }.getOrNull()

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "lantransfer_identity_v1"
        const val FORMAT_KEYSTORE_GCM = 1
        const val FORMAT_FALLBACK_PLAIN = 2
    }
}
