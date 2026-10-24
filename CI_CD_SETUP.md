# Automated Android APK CI/CD Setup (GitHub Actions + GameCI v4)

Every push to `main` (and every Pull Request targeting `main`) triggers `.github/workflows/build.yml` on `ubuntu-latest` to compile an **ARM64 + IL2CPP Android `.apk`**, upload it as a GitHub Actions artifact, and publish a permanent GitHub Release on `main`.

---

## 1. Configure Required GitHub Secrets

Open your GitHub repository and navigate to:  
**Settings → Secrets and variables → Actions → New repository secret**

### Option A — Unity Personal (Free) License
1. Open **Unity Hub** locally and sign in to your Unity account.
2. Go to **Preferences → Licenses → Add → Get a free personal license** so Unity Hub writes `Unity_lic.ulf` to disk.
3. Locate `Unity_lic.ulf` on your machine (enable hidden files if needed):
   - **Windows**: `C:\ProgramData\Unity\Unity_lic.ulf`
   - **macOS**: `/Library/Application Support/Unity/Unity_lic.ulf`
   - **Linux**: `~/.local/share/unity3d/Unity/Unity_lic.ulf`
4. Add these **3 secrets** in GitHub:
   | Secret Name | Value |
   | --- | --- |
   | `UNITY_LICENSE` | Entire XML contents of `Unity_lic.ulf` |
   | `UNITY_EMAIL` | Your Unity ID email address |
   | `UNITY_PASSWORD` | Your Unity ID password *(tip: avoid shell-special characters if possible)* |

### Option B — Unity Plus / Pro License
Add these **3 secrets** in GitHub:
| Secret Name | Value |
| --- | --- |
| `UNITY_SERIAL` | Your Unity Pro/Plus serial key (`XX-XXXX-XXXX-XXXX-XXXX-XXXX`) |
| `UNITY_EMAIL` | Your Unity ID email address |
| `UNITY_PASSWORD` | Your Unity ID password |

---

## 2. Optional Release Signing Keystore Secrets

Without keystore secrets, Unity builds an installable debug-signed `.apk` (`build/Android/AsodaOpenWorld.apk`) suitable for sideloading and QA testing. To sign with your own release keystore, add:

```bash
base64 -i release.keystore | tr -d '\n'
```

| Optional Secret | Description |
| --- | --- |
| `ANDROID_KEYSTORE_NAME` | Filename, e.g. `user.keystore` |
| `ANDROID_KEYSTORE_BASE64` | Base64-encoded `.keystore` file |
| `ANDROID_KEYSTORE_PASS` | Keystore password |
| `ANDROID_KEYALIAS_NAME` | Key alias name |
| `ANDROID_KEYALIAS_PASS` | Key alias password |

---

## 3. Pipeline Architecture Overview

- **Workflow**: `.github/workflows/build.yml` (`buildAndroid` job on `ubuntu-latest`)
  1. Frees runner disk space so the Unity Android + NDK + IL2CPP container fits comfortably.
  2. Clones the repository (`actions/checkout@v4`, `fetch-depth: 0`, `lfs: true`).
  3. Caches `Library/` (`actions/cache@v4`) keyed by `Assets/**`, `Packages/**`, and `ProjectSettings/**`.
  4. Runs `game-ci/unity-builder@v4` targeting `Android` (`androidExportType: androidPackage`, `androidTargetSdkVersion: 34`, `buildMethod: BuildCommand.PerformBuild`).
  5. Uploads `build/Android/*.apk` via `actions/upload-artifact@v4`.
  6. Publishes a tagged GitHub Release attaching `build/Android/*.apk` via `softprops/action-gh-release@v2` on every push to `main`.
- **Pre-Build Enforcer**: `Assets/Editor/AndroidBuildConfigurator.cs`
  - Automatically enforces **IL2CPP** (`ScriptingImplementation.IL2CPP`), **ARM64** (`AndroidArchitecture.ARM64`), **Target SDK 34**, **Min SDK 24**, landscape orientation, valid `com.asoda.openworld` package ID, runtime shader preservation (`AlwaysIncludedShaders`), and bootstrap scene registration before every build.
