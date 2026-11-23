// 引擎层：会话、配对、文字、文件传输、发现。只通过端口访问网络、存储与系统服务（coroutines + StateFlow）。
plugins {
    alias(libs.plugins.kotlin.jvm)
    `java-test-fixtures`
    alias(libs.plugins.animalsniffer)
}

dependencies {
    api(project(":core:domain"))
    api(libs.kotlinx.coroutines.core)

    // InMemoryTransport、FakeClock、内存 Store 等放在 testFixtures，供本模块测试与 peer-cli 之外的下游测试使用
    testFixturesApi(testFixtures(project(":core:domain")))
    testFixturesApi(libs.kotlinx.coroutines.test)

    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.turbine)
    testImplementation(libs.mockk)
}
