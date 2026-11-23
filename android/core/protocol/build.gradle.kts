// 协议层：TLV、帧、发现公告、二维码、前导、Noise_XX、记录层、验证码。纯 Kotlin/JVM，不做 I/O。
plugins {
    alias(libs.plugins.kotlin.jvm)
    `java-test-fixtures`
    alias(libs.plugins.animalsniffer)
}

dependencies {
    // 只使用 X25519；AES-GCM、SHA-256、HMAC-SHA256 用平台 JCA 实现
    implementation(libs.bouncycastle.bcprov)
}
