#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Configures Android package identifier, Target SDK 34, landscape orientation, Vehicle tag,
/// lightweight runtime shaders, and bootstrap scene registration, then delegates Graphics APIs,
/// Scripting Backend (Mono fast CI default / optional IL2CPP), and Shader Stripping to <see cref="BuildConfigurator"/>.
/// </summary>
public sealed class AndroidBuildConfigurator : IPreprocessBuildWithReport
{
    public const string DefaultCompanyName = "AsodaStudio";
    public const string DefaultProductName = "Asoda Open World";
    public const string DefaultPackageIdentifier = "com.asoda.openworld";
    public const string DefaultBootstrapScenePath = "Assets/_Project/Scenes/AsodaBootstrap.unity";
    public const int TargetAndroidApiLevel = 34;

    // Lightweight shaders only — "Standard" is intentionally excluded from AlwaysIncludedShaders
    // so Unity does not expand all 98,304 uber-shader variants during CI builds.
    private static readonly string[] RequiredRuntimeShaderNames =
    {
        "Legacy Shaders/Diffuse",
        "UI/Default",
        "Asoda/StylizedIndianSunsetSkybox"
    };

    public int callbackOrder => -100;

    [InitializeOnLoadMethod]
    private static void ConfigureOnBatchModeStartup()
    {
        if (!Application.isBatchMode)
        {
            return;
        }

        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
        {
            ApplyAndroidBuildSettings();
        }
    }

    [MenuItem("Asoda/Build/Configure Android Target Settings")]
    public static void ApplyAndroidBuildSettingsMenu()
    {
        ApplyAndroidBuildSettings();
        Debug.Log("[AndroidBuildConfigurator] Applied Android target settings via menu.");
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report != null && report.summary.platform == BuildTarget.Android)
        {
            ApplyAndroidBuildSettings();
        }
    }

    /// <summary>
    /// Applies Android SDK 34, package ID, landscape orientation, lightweight runtime shaders, bootstrap scene,
    /// and invokes <see cref="BuildConfigurator.ApplyBuildConfiguration"/> for Mono/Graphics APIs/Shader Stripping.
    /// </summary>
    public static void ApplyAndroidBuildSettings(ScriptingImplementation? backendOverride = null)
    {
        // 1. Ensure valid Company, Product, and Android Application Identifier (Package Name)
        if (string.IsNullOrWhiteSpace(PlayerSettings.companyName) ||
            string.Equals(PlayerSettings.companyName, "DefaultCompany", StringComparison.OrdinalIgnoreCase))
        {
            PlayerSettings.companyName = DefaultCompanyName;
        }

        if (string.IsNullOrWhiteSpace(PlayerSettings.productName) ||
            string.Equals(PlayerSettings.productName, "New Unity Project", StringComparison.OrdinalIgnoreCase))
        {
            PlayerSettings.productName = DefaultProductName;
        }

#if UNITY_2021_2_OR_NEWER
        NamedBuildTarget androidTarget = NamedBuildTarget.Android;
        string currentAppId = PlayerSettings.GetApplicationIdentifier(androidTarget);
        if (IsDefaultOrEmptyPackageId(currentAppId))
        {
            PlayerSettings.SetApplicationIdentifier(androidTarget, DefaultPackageIdentifier);
        }
#else
        string currentAppId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
        if (IsDefaultOrEmptyPackageId(currentAppId))
        {
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, DefaultPackageIdentifier);
        }
#endif

        // 2. Enforce Android Min SDK (API 24 / Android 7.0) & Target SDK (API 34 / Android 14)
        if ((int)PlayerSettings.Android.minSdkVersion < (int)AndroidSdkVersions.AndroidApiLevel24)
        {
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
        }

        if (PlayerSettings.Android.targetSdkVersion == AndroidSdkVersions.AndroidApiLevelAuto ||
            (int)PlayerSettings.Android.targetSdkVersion < TargetAndroidApiLevel)
        {
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)TargetAndroidApiLevel;
        }

        // 3. Landscape widescreen orientation for dual-thumb mobile open-world controls
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

        // 4. Register Vehicle custom tag, lightweight shaders, and bootstrap scene
        EnsureVehicleTagRegistered();
        EnsureRequiredRuntimeShadersIncluded();
        EnsureBootstrapSceneRegistered();

        // 5. Apply Vulkan+OpenGLES3, Mono/IL2CPP backend + matching arch, and GraphicsSettings shader stripping
        BuildConfigurator.ApplyBuildConfiguration(backendOverride);

        AssetDatabase.SaveAssets();

        Debug.Log(
            $"[AndroidBuildConfigurator] Configured Android | " +
            $"AppId={DefaultPackageIdentifier} | " +
            $"Arch={PlayerSettings.Android.targetArchitectures} | " +
            $"MinSDK={(int)PlayerSettings.Android.minSdkVersion} | " +
            $"TargetSDK={(int)PlayerSettings.Android.targetSdkVersion}");
    }

    private static bool IsDefaultOrEmptyPackageId(string appId)
    {
        return string.IsNullOrWhiteSpace(appId) ||
               appId.StartsWith("com.DefaultCompany", StringComparison.OrdinalIgnoreCase) ||
               appId.StartsWith("com.Company", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureVehicleTagRegistered()
    {
        UnityEngine.Object[] tagAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
        if (tagAssets == null || tagAssets.Length == 0)
        {
            return;
        }

        var tagManager = new SerializedObject(tagAssets[0]);
        SerializedProperty tagsProp = tagManager.FindProperty("tags");
        if (tagsProp == null)
        {
            return;
        }

        for (int i = 0; i < tagsProp.arraySize; i++)
        {
            SerializedProperty entry = tagsProp.GetArrayElementAtIndex(i);
            if (entry != null && entry.stringValue == "Vehicle")
            {
                return;
            }
        }

        int idx = tagsProp.arraySize;
        tagsProp.InsertArrayElementAtIndex(idx);
        tagsProp.GetArrayElementAtIndex(idx).stringValue = "Vehicle";
        tagManager.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void EnsureRequiredRuntimeShadersIncluded()
    {
        UnityEngine.Object[] graphicsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (graphicsAssets == null || graphicsAssets.Length == 0)
        {
            return;
        }

        var graphicsSettings = new SerializedObject(graphicsAssets[0]);
        SerializedProperty alwaysIncluded = graphicsSettings.FindProperty("m_AlwaysIncludedShaders");
        if (alwaysIncluded == null)
        {
            return;
        }

        bool modified = false;
        for (int s = 0; s < RequiredRuntimeShaderNames.Length; s++)
        {
            Shader shader = Shader.Find(RequiredRuntimeShaderNames[s]);
            if (shader == null)
            {
                continue;
            }

            bool alreadyPresent = false;
            for (int i = 0; i < alwaysIncluded.arraySize; i++)
            {
                if (alwaysIncluded.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                {
                    alreadyPresent = true;
                    break;
                }
            }

            if (!alreadyPresent)
            {
                int newIdx = alwaysIncluded.arraySize;
                alwaysIncluded.InsertArrayElementAtIndex(newIdx);
                alwaysIncluded.GetArrayElementAtIndex(newIdx).objectReferenceValue = shader;
                modified = true;
            }
        }

        if (modified)
        {
            graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    public static string[] EnsureBootstrapSceneRegistered()
    {
        string[] enabledScenes = EditorBuildSettings.scenes
            .Where(s => s != null && s.enabled && File.Exists(s.path))
            .Select(s => s.path)
            .ToArray();

        if (enabledScenes.Length > 0)
        {
            return enabledScenes;
        }

        if (!File.Exists(DefaultBootstrapScenePath))
        {
            string dir = Path.GetDirectoryName(DefaultBootstrapScenePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(newScene, DefaultBootstrapScenePath);
            AssetDatabase.Refresh();
        }

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(DefaultBootstrapScenePath, true)
        };

        return new[] { DefaultBootstrapScenePath };
    }
}

/// <summary>
/// CLI entry point (<c>BuildCommand.PerformBuild</c>) compatible with GameCI <c>game-ci/unity-builder@v4</c>
/// as well as standalone local headless <c>unity-editor -batchmode -executeMethod BuildCommand.PerformBuild</c> runs.
/// </summary>
public static class BuildCommand
{
    public static void PerformBuild()
    {
        Dictionary<string, string> args = ParseCommandLineArgs();

        ScriptingImplementation? backendOverride = null;
        if (args.TryGetValue("scriptingBackend", out string backendArg) && !string.IsNullOrEmpty(backendArg))
        {
            backendOverride = string.Equals(backendArg, "IL2CPP", StringComparison.OrdinalIgnoreCase)
                ? ScriptingImplementation.IL2CPP
                : ScriptingImplementation.Mono2x;
        }

        // 1. Apply Android target settings + BuildConfigurator (Vulkan+GLES3, Mono/IL2CPP, Shader Stripping)
        AndroidBuildConfigurator.ApplyAndroidBuildSettings(backendOverride);

        // 2. Apply GameCI CLI overrides when passed
        if (args.TryGetValue("buildVersion", out string buildVersion) && !string.IsNullOrEmpty(buildVersion))
        {
            PlayerSettings.bundleVersion = buildVersion;
        }

        if (args.TryGetValue("androidVersionCode", out string versionCodeStr) &&
            int.TryParse(versionCodeStr, out int versionCode) &&
            versionCode > 0)
        {
            PlayerSettings.Android.bundleVersionCode = versionCode;
        }

        if (args.TryGetValue("androidTargetSdkVersion", out string sdkStr) && !string.IsNullOrEmpty(sdkStr))
        {
            try
            {
                PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)Enum.Parse(typeof(AndroidSdkVersions), sdkStr);
            }
            catch
            {
                PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)AndroidBuildConfigurator.TargetAndroidApiLevel;
            }
        }

        // Enforce direct APK output unless explicitly overridden to androidAppBundle
        bool buildAab = args.TryGetValue("androidExportType", out string exportType) &&
                        string.Equals(exportType, "androidAppBundle", StringComparison.OrdinalIgnoreCase);
        EditorUserBuildSettings.buildAppBundle = buildAab;
        EditorUserBuildSettings.exportAsGoogleAndroidProject = false;

        // Apply optional Android keystore signing credentials if passed via GameCI inputs
        if (args.TryGetValue("androidKeystoreName", out string keystoreName) && !string.IsNullOrEmpty(keystoreName))
        {
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystoreName;
        }

        if (args.TryGetValue("androidKeystorePass", out string keystorePass) && !string.IsNullOrEmpty(keystorePass))
        {
            PlayerSettings.Android.keystorePass = keystorePass;
        }

        if (args.TryGetValue("androidKeyaliasName", out string keyaliasName) && !string.IsNullOrEmpty(keyaliasName))
        {
            PlayerSettings.Android.keyaliasName = keyaliasName;
        }

        if (args.TryGetValue("androidKeyaliasPass", out string keyaliasPass) && !string.IsNullOrEmpty(keyaliasPass))
        {
            PlayerSettings.Android.keyaliasPass = keyaliasPass;
        }

        string[] scenes = AndroidBuildConfigurator.EnsureBootstrapSceneRegistered();

        string outputPath = args.TryGetValue("customBuildPath", out string customPath) && !string.IsNullOrEmpty(customPath)
            ? customPath
            : "build/Android/AsodaOpenWorld.apk";

        string outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var buildPlayerOptions = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

#if UNITY_2021_2_OR_NEWER
        ScriptingImplementation activeBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android);
#else
        ScriptingImplementation activeBackend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Android);
#endif

        Debug.Log(
            $"[BuildCommand] Starting Android APK build ({activeBackend}, {PlayerSettings.Android.targetArchitectures}) -> " +
            $"{outputPath} ({scenes.Length} scene(s))");

        BuildReport report = BuildPipeline.BuildPlayer(buildPlayerOptions);
        BuildSummary summary = report.summary;

        Debug.Log(
            $"[BuildCommand] Build finished: Result={summary.result} | " +
            $"Size={(summary.totalSize / (1024f * 1024f)):F2} MB | " +
            $"Duration={summary.totalTime.TotalSeconds:F1}s | " +
            $"Errors={summary.totalErrors}");

        if (Application.isBatchMode)
        {
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
        }
    }

    private static Dictionary<string, string> ParseCommandLineArgs()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] rawArgs = Environment.GetCommandLineArgs();
        for (int i = 0; i < rawArgs.Length; i++)
        {
            string current = rawArgs[i];
            if (!current.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            string key = current.TrimStart('-');
            string value = (i + 1 < rawArgs.Length && !rawArgs[i + 1].StartsWith("-", StringComparison.Ordinal))
                ? rawArgs[++i]
                : string.Empty;
            result[key] = value;
        }

        return result;
    }
}
#endif
