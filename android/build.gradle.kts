// Root build file — plugin versions declared here, applied in :app.
plugins {
    id("com.android.application") version "8.6.1" apply false
    id("org.jetbrains.kotlin.android") version "2.0.20" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.0.20" apply false
    // Uncomment when wiring FCM with a real google-services.json:
    // id("com.google.gms.google-services") version "4.4.2" apply false
}
