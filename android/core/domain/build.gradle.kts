// 领域层：文件名处理、各类规则、文本显示映射、状态归约器。纯函数与确定性对象，不做 I/O。
plugins {
    alias(libs.plugins.kotlin.jvm)
    `java-test-fixtures`
    alias(libs.plugins.animalsniffer)
}

dependencies {
    api(project(":core:protocol"))

    testFixturesApi(testFixtures(project(":core:protocol")))
}
