import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.ksp)
    alias(libs.plugins.hilt)
    alias(libs.plugins.baselineprofile)
    alias(libs.plugins.room)
}

/** 当前分支的提交数；没有 Git（比如解压的源码包）时为 1。 */
val gitCommitCount: Int = providers.exec {
    commandLine("git", "rev-list", "--count", "HEAD")
    isIgnoreExitValue = true
}.standardOutput.asText.get().trim().toIntOrNull() ?: 1

android {
    namespace = "com.tinyfish.jeekmusic"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.tinyfish.jeekmusic"
        minSdk = 31
        targetSdk = 37
        // 版本号 = Git 提交数（CI 需要完整历史：actions/checkout 的 fetch-depth: 0）
        versionCode = gitCommitCount
        versionName = "1.0.$gitCommitCount"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        // 只打包 64 位 ARM（手机）和 x86_64（模拟器）：LiteRT 和 TagLib 的原生库每种架构几 MB，
        // Android 12+ 的手机几乎都是 64 位
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
    }

    // 正式签名：本地从 keystore.properties 读取（不提交到仓库），CI 从环境变量读取（GitHub Secrets）。
    // 本地和 CI 必须用同一个签名，App 内的自动更新才能覆盖安装。都没有时退回 debug 签名（PR 构建）。
    val keystore = Properties().apply {
        rootProject.file("keystore.properties").takeIf { it.exists() }?.inputStream()?.use(::load)
    }
    fun secret(name: String): String? =
        (keystore.getProperty(name) ?: System.getenv("JEEK_" + name.uppercase()))?.takeIf { it.isNotBlank() }
    val releaseSigning = secret("storeFile")?.let { path ->
        signingConfigs.create("release") {
            storeFile = rootProject.file(path)
            storePassword = secret("storePassword")
            keyAlias = secret("keyAlias")
            keyPassword = secret("keyPassword")
        }
    }

    buildTypes {
        // debug 包用不同的包名，和正式签名的 release 包共存：跑设备测试时不会覆盖或卸载正式版
        debug {
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
        }
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = releaseSigning ?: signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    // 生成 localeConfig：Android 13+ 可以在系统设置里单独设置 JeekMusic 的语言
    // 原生库（LiteRT、TagLib）压缩打包：安装包小很多（GitHub 下载和自动更新更快），代价是安装时解压一次
    packaging {
        jniLibs { useLegacyPackaging = true }
    }

    androidResources {
        generateLocaleConfig = true
        // 模型文件不压缩，才能直接内存映射给 LiteRT（docs/design.md §6.3）
        noCompress += "tflite"
    }

    // 单元测试读两端共用的测试用例（仓库根目录的 shared/testcases/，desktop/docs/design.md §3）
    testOptions {
        unitTests.all {
            val testcases = rootProject.file("../shared/testcases")
            it.systemProperty("jeek.testcases", testcases.absolutePath)
            it.inputs.dir(testcases)
            // Native-tag JVM regressions read the exact synthetic Android fixture bytes too.
            it.inputs.dir(file("src/androidTest/assets"))
        }
    }
}

room {
    schemaDirectory("$projectDir/schemas")
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.core.splashscreen)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.process)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.graphics)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.media3.exoplayer)
    implementation(libs.androidx.media3.session)
    implementation(libs.androidx.profileinstaller)
    implementation(libs.hilt.android)
    implementation(libs.hilt.lifecycle.viewmodel.compose)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.kotlinx.coroutines.guava)
    implementation(libs.taglib)
    implementation(libs.litert)
    // room-testing（迁移测试）需要较新的 kotlinx-serialization；设备测试和 App 共用同一份依赖，所以在这里统一版本
    implementation(platform(libs.kotlinx.serialization.bom))
    implementation(libs.androidx.room.runtime)
    implementation(libs.androidx.room.ktx)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.androidx.navigation3.runtime)
    implementation(libs.androidx.navigation3.ui)
    implementation(libs.androidx.lifecycle.viewmodel.navigation3)
    implementation(libs.coil.compose)
    implementation(libs.reorderable)
    implementation(libs.androidx.work.runtime)
    ksp(libs.hilt.compiler)
    ksp(libs.androidx.room.compiler)

    debugImplementation(libs.androidx.compose.ui.tooling)

    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    // 读 shared/testcases 的 JSON（android.jar 里的 org.json 在单元测试中只是空壳）
    testImplementation(libs.org.json)
    // 拼音、繁简的共用用例：JVM 上没有 android.icu，用同源的 ICU4J（版本同 Android 16 的 ICU）
    testImplementation(libs.icu4j)

    androidTestImplementation(libs.androidx.test.ext.junit)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.room.testing)
    androidTestImplementation(libs.kotlinx.coroutines.test)

    baselineProfile(project(":baselineprofile"))
}
