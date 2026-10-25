# Automated Android APK CI/CD Setup (Online Activation — No PC Needed)

Every push to `main` (and manual **Run workflow** dispatch) triggers `.github/workflows/build.yml` on `ubuntu-latest` to activate a **Personal Unity license online**, compile an **ARM64 + IL2CPP Android `.apk`**, upload it as a GitHub Actions artifact, and publish a GitHub Release.

---

## 1. Configure Required GitHub Secrets (Only 2 Secrets Needed)

No PC, Unity Hub installation, or manual license file is needed. Open your GitHub repository and go to:  
**Settings → Secrets and variables → Actions → New repository secret**

| Secret Name | Value |
| --- | --- |
| `UNITY_EMAIL` | Your Unity ID email address |
| `UNITY_PASSWORD` | Your Unity ID password *(note: Unity online CLI activation requires direct Unity ID email/password login, not Google/Apple SSO-only login — if you signed up with Google, set a password on `id.unity.com`)* |

---

## 2. Optional Release Signing Keystore Secrets

Without keystore secrets, Unity builds an installable debug-signed `.apk` (`build/Android/AsodaOpenWorld.apk`) ready to download and test on any 64-bit Android phone. To sign with a custom keystore later, you can optionally add `ANDROID_KEYSTORE_NAME`, `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASS`, `ANDROID_KEYALIAS_NAME`, and `ANDROID_KEYALIAS_PASS`.

---

## 3. Pipeline Architecture Overview

- **Single Workflow**: `.github/workflows/build.yml` (`buildAndroid` job on `ubuntu-latest`)
  1. Frees unused runner disk space so the Unity Android + NDK + IL2CPP container fits comfortably.
  2. Clones the repository (`actions/checkout@v4`) and caches `Library/` (`actions/cache@v4`).
  3. Installs the Unity Hub Licensing Client (`@rage-against-the-pixel/unity-cli hub-install`) and activates a **Personal** Unity license online via **`buildalon/activate-unity-license@v2`** using `UNITY_EMAIL` and `UNITY_PASSWORD`.
  4. Bridges the activated Personal seat into `game-ci/unity-builder@v4` (`targetPlatform: Android`, `androidExportType: androidPackage`, `androidTargetSdkVersion: 34`, `buildMethod: BuildCommand.PerformBuild`).
  5. Uploads `build/Android/*.apk` via `actions/upload-artifact@v4` and publishes a tagged GitHub Release via `softprops/action-gh-release@v2` on `main`.
