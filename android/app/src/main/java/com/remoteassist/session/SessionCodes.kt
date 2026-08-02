package com.remoteassist.session

/**
 * Pure helpers for the attended 6-digit session code and its QR payload.
 * Kept free of Android types so it is unit-testable on the JVM.
 */
object SessionCodes {
    private const val SCHEME = "remoteassist"
    private val CODE_REGEX = Regex("^\\d{6}$")

    fun isValid(code: String): Boolean = CODE_REGEX.matches(code)

    /** The string encoded into the host's QR: a deep link carrying the code. */
    fun toQrPayload(code: String): String = "$SCHEME://join?code=$code"

    /**
     * Extract a 6-digit code from scanned QR text. Accepts either the deep-link
     * form (`remoteassist://join?code=123456`) or a bare 6-digit string, so a
     * plainly-encoded code still works. Returns null if no valid code is present.
     */
    fun parseScanned(raw: String?): String? {
        val text = raw?.trim().orEmpty()
        if (text.isEmpty()) return null
        if (isValid(text)) return text
        val marker = "code="
        val idx = text.indexOf(marker)
        if (idx >= 0) {
            val candidate = text.substring(idx + marker.length).takeWhile { it.isDigit() }
            if (isValid(candidate)) return candidate
        }
        return null
    }
}
