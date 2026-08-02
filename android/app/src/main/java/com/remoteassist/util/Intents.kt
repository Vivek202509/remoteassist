package com.remoteassist.util

import android.content.Intent
import android.os.Build
import android.os.Parcelable

/** Version-safe Intent parcelable extra: typed API on 33+, deprecated fallback below. */
inline fun <reified T : Parcelable> Intent.parcelable(name: String): T? =
    if (Build.VERSION.SDK_INT >= 33) {
        getParcelableExtra(name, T::class.java)
    } else {
        @Suppress("DEPRECATION")
        getParcelableExtra(name)
    }
