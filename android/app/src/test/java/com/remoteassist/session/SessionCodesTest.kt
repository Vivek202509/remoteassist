package com.remoteassist.session

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionCodesTest {

    @Test fun validCodes() {
        assertTrue(SessionCodes.isValid("123456"))
        assertTrue(SessionCodes.isValid("000000"))
    }

    @Test fun invalidCodes() {
        assertFalse(SessionCodes.isValid("12345"))    // too short
        assertFalse(SessionCodes.isValid("1234567"))  // too long
        assertFalse(SessionCodes.isValid("12a456"))   // non-digit
        assertFalse(SessionCodes.isValid(""))
    }

    @Test fun qrPayloadRoundTrip() {
        val payload = SessionCodes.toQrPayload("482913")
        assertEquals("remoteassist://join?code=482913", payload)
        assertEquals("482913", SessionCodes.parseScanned(payload))
    }

    @Test fun parseBareCode() {
        assertEquals("123456", SessionCodes.parseScanned("123456"))
        assertEquals("123456", SessionCodes.parseScanned("  123456  "))
    }

    @Test fun parseDeepLinkWithTrailingParams() {
        assertEquals("654321", SessionCodes.parseScanned("remoteassist://join?code=654321&x=1"))
    }

    @Test fun parseRejectsGarbage() {
        assertNull(SessionCodes.parseScanned(null))
        assertNull(SessionCodes.parseScanned("https://example.com"))
        assertNull(SessionCodes.parseScanned("code=12"))   // too short after marker
    }
}
