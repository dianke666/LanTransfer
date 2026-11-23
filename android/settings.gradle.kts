pluginManagement {
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    // 仓库只在此处声明，模块内不得另加仓库
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
    }
}

rootProject.name = "LanTransfer"

// 纯 Kotlin/JVM 模块：依赖方向 engine → domain → protocol
include(":core:protocol", ":core:domain", ":core:engine")
// JVM 命令行对端，用于跨端互通测试（interop-tests/）
include(":tools:peer-cli")
// 测试向量生成工具（手动运行）
include(":tools:vector-gen")

// Android 应用模块需要 Android SDK。本机没有 SDK 时只构建 JVM 模块（例如只运行协议层测试或 peer-cli）；
// CI（CI=true）中缺少 SDK 直接失败，避免悄悄跳过 :app。
val androidSdkDir: String? = run {
    val localProperties = file("local.properties")
    val fromLocal = if (localProperties.isFile) {
        java.util.Properties().apply { localProperties.inputStream().use { load(it) } }.getProperty("sdk.dir")
    } else {
        null
    }
    listOfNotNull(fromLocal, System.getenv("ANDROID_HOME"), System.getenv("ANDROID_SDK_ROOT"))
        .firstOrNull { file(it).isDirectory }
}
when {
    androidSdkDir != null -> include(":app")
    System.getenv("CI") == "true" -> error("CI 环境必须配置 Android SDK（ANDROID_HOME），否则无法构建 :app")
    else -> logger.warn("未找到 Android SDK（local.properties 的 sdk.dir 或 ANDROID_HOME），本次构建跳过 :app 模块")
}
