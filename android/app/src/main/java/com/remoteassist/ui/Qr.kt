package com.remoteassist.ui

import androidx.compose.foundation.Image
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.painter.BitmapPainter
import android.graphics.Bitmap
import android.graphics.Color
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel

/** Encode [text] into a QR ImageBitmap of [sizePx] square. Returns null on failure. */
fun encodeQr(text: String, sizePx: Int = 640): ImageBitmap? = try {
    val hints = mapOf(
        EncodeHintType.ERROR_CORRECTION to ErrorCorrectionLevel.M,
        EncodeHintType.MARGIN to 1,
    )
    val matrix = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, sizePx, sizePx, hints)
    val bmp = Bitmap.createBitmap(sizePx, sizePx, Bitmap.Config.RGB_565)
    for (x in 0 until sizePx) {
        for (y in 0 until sizePx) {
            bmp.setPixel(x, y, if (matrix[x, y]) Color.BLACK else Color.WHITE)
        }
    }
    bmp.asImageBitmap()
} catch (e: Exception) {
    null
}

@Composable
fun QrImage(text: String, modifier: Modifier = Modifier, sizePx: Int = 640) {
    val image = remember(text, sizePx) { encodeQr(text, sizePx) }
    if (image != null) {
        Image(painter = BitmapPainter(image), contentDescription = "QR code", modifier = modifier)
    }
}
