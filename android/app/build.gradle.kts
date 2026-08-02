plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    // id("com.google.gms.google-services") // enable with google-services.json
}

android {
    namespace = "com.remoteassist"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.remoteassist"
        minSdk = 26
        targetSdk = 35
        versionCode = 1
        versionName = "1.0"

        // Point the app at your signaling server. Override per build type as needed.
        buildConfigField("String", "SIGNALING_URL", "\"ws://10.0.2.2:8080\"")
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }

    buildTypes {
        debug {
            // 10.0.2.2 = host loopback from the Android emulator
            buildConfigField("String", "SIGNALING_URL", "\"ws://10.0.2.2:8080\"")
        }
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            buildConfigField("String", "SIGNALING_URL", "\"wss://signal.yourdomain.com\"")
        }
    }
}

dependencies {
    // WebRTC (community prebuilt)
    implementation("io.github.webrtc-sdk:android:125.6422.07")

    // Signaling transport
    implementation("com.squareup.okhttp3:okhttp:4.12.0")

    // Coroutines
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")

    // Compose
    implementation(platform("androidx.compose:compose-bom:2024.09.03"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.activity:activity-compose:1.9.2")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.6")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.8.6")
    debugImplementation("androidx.compose.ui:ui-tooling")

    // Core + security-crypto (EncryptedSharedPreferences)
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.security:security-crypto:1.1.0-alpha06")
    implementation("androidx.biometric:biometric:1.2.0-alpha05")

    // FCM (wake device). Requires google-services plugin + google-services.json.
    implementation(platform("com.google.firebase:firebase-bom:33.4.0"))
    implementation("com.google.firebase:firebase-messaging-ktx")

    // QR generation (session code) + scanning (CameraX + ML Kit barcode)
    implementation("com.google.zxing:core:3.5.3")
    implementation("androidx.camera:camera-core:1.3.4")
    implementation("androidx.camera:camera-camera2:1.3.4")
    implementation("androidx.camera:camera-lifecycle:1.3.4")
    implementation("androidx.camera:camera-view:1.3.4")
    implementation("com.google.mlkit:barcode-scanning:17.3.0")

    // Unit tests
    testImplementation("junit:junit:4.13.2")
}
