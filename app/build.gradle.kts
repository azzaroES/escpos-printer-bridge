plugins {
    id("com.android.application")
}

android {
    namespace = "com.usblanbridge"
    compileSdk {
        version = release(36) {
            minorApiLevel = 1
        }
    }

    defaultConfig {
        applicationId = "com.usblanbridge"
        // Android 5.0. Old phones make good dedicated print servers, so the floor is deliberately low.
        // Everything above API 21 is called behind an explicit Build.VERSION guard.
        minSdk = 21
        targetSdk = 36
        versionCode = 3
        versionName = "1.2"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
        debug {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }

    buildFeatures {
        // Needed for the vendor printer interface in src/main/aidl.
        aidl = true
    }

    lint {
        // A missing translation must not fail a build of a single-language utility.
        disable += "MissingTranslation"
        abortOnError = false
    }

    // No ABI splits and no NDK on purpose: this app contains zero native code, so one APK runs on
    // arm64-v8a, armeabi-v7a, x86 and x86_64 alike. Bundling a native SDK would have forced per-ABI builds.
}
