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
    androidResources {
        generateLocaleConfig = true
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

    androidTestImplementation(libs.androidx.test.ext.junit)
    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.room.testing)
    androidTestImplementation(libs.kotlinx.coroutines.test)

    baselineProfile(project(":baselineprofile"))
}
