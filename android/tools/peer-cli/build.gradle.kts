// JVM 命令行对端：JSON Lines 控制接口，使用真实的 Protocol / Domain / Engine，用于跨端互通测试（interop-tests/）
plugins {
    alias(libs.plugins.kotlin.jvm)
    application
}

dependencies {
    implementation(project(":core:engine"))
}

application {
    mainClass.set("com.lantransfer.peercli.MainKt")
    applicationName = "peer-cli"
}
