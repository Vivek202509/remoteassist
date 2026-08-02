pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
        // WebRTC prebuilt (community fork; Google no longer publishes org.webrtc)
        maven { url = uri("https://jitpack.io") }
    }
}

rootProject.name = "RemoteAssist"
include(":app")
