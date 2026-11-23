import org.gradle.api.tasks.testing.logging.TestExceptionFormat
import org.jetbrains.kotlin.gradle.dsl.KotlinJvmProjectExtension

// 插件类路径同样锁定（buildscript-gradle.lockfile）
buildscript {
    configurations.classpath {
        resolutionStrategy.activateDependencyLocking()
    }
}

plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.jvm) apply false
    alias(libs.plugins.kotlin.android) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.ksp) apply false
    alias(libs.plugins.room) apply false
    alias(libs.plugins.animalsniffer) apply false
}

// 依赖锁定：每个模块的全部配置都写入 gradle.lockfile 并提交。
// 修改依赖后运行 `gradlew resolveAndLockAll --write-locks` 更新锁文件。
allprojects {
    dependencyLocking {
        lockAllConfigurations()
        // kotlin-stdlib-common 2.x 只是兼容占位制品，版本由 kotlin-stdlib 的约束决定（kotlin-stdlib 本身已锁定）。
        // 它是否进入 :app 的 runtimeClasspath 依解析顺序而定：写锁时（不施加锁约束）不出现，正常构建时出现，
        // 导致锁文件永远对不上，因此不纳入锁定
        ignoredDependencies.add("org.jetbrains.kotlin:kotlin-stdlib-common")
    }
    tasks.register("resolveAndLockAll") {
        group = "dependency locking"
        description = "解析本模块全部可解析配置；配合 --write-locks 更新 gradle.lockfile"
        notCompatibleWithConfigurationCache("在执行阶段筛选并解析配置")
        doFirst {
            require(gradle.startParameter.isWriteDependencyLocks) { "$path 需要与 --write-locks 一起运行" }
        }
        doLast {
            // 只解析依赖图（锁文件据此写出），不选择制品：AGP 的 androidTest 等配置包含 :app 自身，选择制品时会有多个候选变体
            configurations.filter { it.isCanBeResolved }.forEach { it.incoming.resolutionResult.root }
        }
    }
}

subprojects {
    // 纯 Kotlin/JVM 模块（core/*、tools/peer-cli）的共用设置：JDK 17、警告即错误、JUnit 5 平台 + Kotest
    pluginManager.withPlugin("org.jetbrains.kotlin.jvm") {
        extensions.configure<KotlinJvmProjectExtension> {
            jvmToolchain(17)
            compilerOptions {
                allWarningsAsErrors.set(true)
            }
        }
        dependencies {
            "testImplementation"(platform(libs.junit.bom))
            "testImplementation"(libs.junit.jupiter)
            "testRuntimeOnly"(libs.junit.platform.launcher)
            "testImplementation"(libs.kotest.runner.junit5)
            "testImplementation"(libs.kotest.assertions.core)
            "testImplementation"(libs.kotest.property)
        }
        tasks.withType<Test>().configureEach {
            useJUnitPlatform()
            // 属性测试的规模与重放种子（LT_PBT_SCALE / LT_PBT_SEED）经环境变量传入测试进程
            testLogging {
                events("failed")
                exceptionFormat = TestExceptionFormat.FULL
            }
        }
    }

    // core 模块的 testFixtures：共享生成器（Kotest Arb）、测试向量加载器与端口的内存实现，供本模块和下游模块的测试使用
    pluginManager.withPlugin("java-test-fixtures") {
        dependencies {
            "testFixturesApi"(libs.kotest.property)
            "testFixturesApi"(libs.kotlinx.serialization.json)
        }
        // 唯一一份测试向量 protocol/test-vectors 作为测试资源，放在类路径的 test-vectors/ 下
        tasks.named<ProcessResources>("processTestResources") {
            from(rootProject.layout.projectDirectory.dir("../protocol/test-vectors")) {
                into("test-vectors")
            }
        }
    }

    // core 模块只能使用 Android API 26 上可用的 Java API：用 animal-sniffer 按 API 26 签名检查 main 源集
    pluginManager.withPlugin("ru.vyarus.animalsniffer") {
        dependencies {
            "signature"("${libs.android.api26.signature.get()}@signature")
        }
        tasks.matching { it.name == "animalsnifferTest" || it.name == "animalsnifferTestFixtures" }.configureEach {
            enabled = false
        }
    }
}
