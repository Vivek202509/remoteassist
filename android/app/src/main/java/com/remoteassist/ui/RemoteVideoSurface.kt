package com.remoteassist.ui

import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import org.webrtc.EglBase
import org.webrtc.RendererCommon
import org.webrtc.SurfaceViewRenderer
import org.webrtc.VideoTrack

/**
 * Renders a remote [VideoTrack] via a WebRTC [SurfaceViewRenderer] and maps
 * touches into normalized (0..1) control events for the host.
 *
 * Lifecycle handled explicitly:
 *  - init() the renderer with the shared EGL context in the AndroidView factory;
 *  - add/remove the track sink in a DisposableEffect keyed on the track;
 *  - release() the renderer when the composable leaves composition (covers
 *    navigation away, orientation recreation, and process teardown).
 *
 * Loading state: shows a spinner until the first frame renders, and again if the
 * track drops (connection loss), so the user sees "reconnecting" rather than a
 * frozen last frame.
 */
@Composable
fun RemoteVideoSurface(
    eglBase: EglBase,
    track: VideoTrack?,
    onTap: (nx: Float, ny: Float) -> Unit,
    onSwipe: (x1: Float, y1: Float, x2: Float, y2: Float, ms: Long) -> Unit,
    modifier: Modifier = Modifier,
) {
    var renderer by remember { mutableStateOf<SurfaceViewRenderer?>(null) }
    var firstFrame by remember { mutableStateOf(false) }

    // Reset the loading state whenever the track identity changes (new/lost stream).
    LaunchedEffect(track) { firstFrame = false }

    Box(modifier) {
        AndroidView(
            factory = { ctx ->
                SurfaceViewRenderer(ctx).apply {
                    init(eglBase.eglBaseContext, object : RendererCommon.RendererEvents {
                        override fun onFirstFrameRendered() { firstFrame = true }
                        override fun onFrameResolutionChanged(w: Int, h: Int, rotation: Int) {}
                    })
                    setScalingType(RendererCommon.ScalingType.SCALE_ASPECT_FIT)
                    setEnableHardwareScaler(true)
                    renderer = this
                }
            },
            modifier = Modifier.fillMaxSize()
        )

        // Touch overlay: normalize against the overlay size (view-relative). The
        // host maps 0..1 back onto its own screen, so this is resolution-independent.
        Box(
            Modifier
                .fillMaxSize()
                .pointerInput(Unit) {
                    detectTapGestures { off ->
                        onTap(clamp01(off.x / size.width), clamp01(off.y / size.height))
                    }
                }
                .pointerInput(Unit) {
                    var start = Offset.Zero
                    var current = Offset.Zero
                    var startMs = 0L
                    detectDragGestures(
                        onDragStart = { start = it; current = it; startMs = System.currentTimeMillis() },
                        onDrag = { change, drag -> current += drag; change.consume() },
                        onDragEnd = {
                            val w = size.width.toFloat().coerceAtLeast(1f)
                            val h = size.height.toFloat().coerceAtLeast(1f)
                            val ms = (System.currentTimeMillis() - startMs).coerceIn(50, 2000)
                            onSwipe(
                                clamp01(start.x / w), clamp01(start.y / h),
                                clamp01(current.x / w), clamp01(current.y / h),
                                ms
                            )
                        }
                    )
                }
        )

        if (!firstFrame) {
            Column(
                Modifier.fillMaxSize(),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center
            ) {
                CircularProgressIndicator()
                Box(Modifier.height(12.dp))
                Text(
                    if (track == null) "Waiting for remote screen…" else "Connecting video…",
                    style = MaterialTheme.typography.bodyMedium
                )
            }
        }
    }

    DisposableEffect(track, renderer) {
        val r = renderer
        if (r != null && track != null) track.addSink(r)
        onDispose { if (r != null && track != null) track.removeSink(r) }
    }
    DisposableEffect(Unit) {
        onDispose { renderer?.release() }
    }
}

private fun clamp01(v: Float): Float = v.coerceIn(0f, 1f)
