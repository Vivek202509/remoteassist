# WebRTC JNI classes must be kept.
-keep class org.webrtc.** { *; }
-dontwarn org.webrtc.**

# OkHttp
-dontwarn okhttp3.**
-dontwarn okio.**

# Kotlinx coroutines
-dontwarn kotlinx.coroutines.**
