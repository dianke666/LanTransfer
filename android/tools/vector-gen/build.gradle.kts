// 测试向量生成工具（任务 6.1、8.3）：只在手动调用时运行，CI 不重新生成期望字节。
// 用法：gradlew :tools:vector-gen:run    （输出到仓库的 protocol/test-vectors/）
plugins {
    alias(libs.plugins.kotlin.jvm)
    application
}

dependencies {
    implementation(testFixtures(project(":core:protocol")))
    implementation(project(":core:domain"))
}

application {
    mainClass.set("com.lantransfer.vectorgen.MainKt")
}

tasks.named<JavaExec>("run") {
    args(rootProject.layout.projectDirectory.dir("../protocol/test-vectors").asFile.absolutePath)
}
