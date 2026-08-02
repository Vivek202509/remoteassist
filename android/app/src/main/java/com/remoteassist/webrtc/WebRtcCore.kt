package com.remoteassist.webrtc

import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjection
import org.json.JSONArray
import org.webrtc.*

/**
 * Owns the process-wide PeerConnectionFactory + EglBase, and builds the screen
 * VideoTrack from a MediaProjection grant. One instance per app process.
 */
class WebRtcCore(context: Context) {
    val eglBase: EglBase = EglBase.create()
    val factory: PeerConnectionFactory

    init {
        PeerConnectionFactory.initialize(
            PeerConnectionFactory.InitializationOptions.builder(context.applicationContext)
                .createInitializationOptions()
        )
        factory = PeerConnectionFactory.builder()
            .setVideoEncoderFactory(DefaultVideoEncoderFactory(eglBase.eglBaseContext, true, true))
            .setVideoDecoderFactory(DefaultVideoDecoderFactory(eglBase.eglBaseContext))
            .createPeerConnectionFactory()
    }

    /**
     * Build a screencast VideoTrack from a MediaProjection permission [permissionData]
     * (the Intent returned by the system screen-capture dialog). ScreenCapturerAndroid
     * creates and owns the MediaProjection internally.
     *
     * Platform note (unattended): a given permission Intent yields a single projection.
     * A production unattended path keeps the capturer/track ALIVE across sessions rather
     * than re-creating from a stale Intent (see README "Unattended access limitations").
     */
    fun buildScreenTrack(
        appContext: Context,
        widthPx: Int,
        heightPx: Int,
        fps: Int = 30,
        permissionData: Intent,
    ): Pair<VideoTrack, VideoCapturer> {
        val capturer: VideoCapturer =
            ScreenCapturerAndroid(permissionData, object : MediaProjection.Callback() {})
        val source = factory.createVideoSource(/* isScreencast = */ true)
        val helper = SurfaceTextureHelper.create("CaptureThread", eglBase.eglBaseContext)
        capturer.initialize(helper, appContext, source.capturerObserver)
        capturer.startCapture(widthPx, heightPx, fps)
        return factory.createVideoTrack("screen0", source) to capturer
    }

    companion object {
        /** Convert the server's iceServers JSON into WebRTC IceServer objects. */
        fun parseIceServers(arr: JSONArray): List<PeerConnection.IceServer> {
            val out = ArrayList<PeerConnection.IceServer>()
            for (i in 0 until arr.length()) {
                val o = arr.optJSONObject(i) ?: continue
                if (!o.has("urls")) continue // skip malformed entries rather than aborting all
                val urls = o.get("urls")
                val builder = when (urls) {
                    is String -> PeerConnection.IceServer.builder(urls)
                    else -> PeerConnection.IceServer.builder((urls as JSONArray).let { list ->
                        (0 until list.length()).map { list.getString(it) }
                    })
                }
                if (o.has("username")) builder.setUsername(o.getString("username"))
                if (o.has("credential")) builder.setPassword(o.getString("credential"))
                out.add(builder.createIceServer())
            }
            if (out.isEmpty()) out.add(PeerConnection.IceServer.builder("stun:stun.l.google.com:19302").createIceServer())
            return out
        }
    }
}
