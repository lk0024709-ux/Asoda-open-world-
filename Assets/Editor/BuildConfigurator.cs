#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Pre-build and on-load configurator that eliminates the 98,304+ shader variant explosion on CI runners,
/// restricts Android Graphics APIs to Vulkan + OpenGLES3 (removing OpenGLES2), and configures the
/// Mono scripting backend (with matching ARMv7 target architecture) for fast 5–8 minute CI APK builds.
/// </summary>
[InitializeOnLoad]
public sealed class BuildConfigurator : IPreprocessBuildWithReport, IPreprocessShaders
{
    private const string GraphicsSettingsAssetPath = "ProjectSettings/GraphicsSettings.asset";

    // Global keywords stripped across all shaders
    private static readonly string[] GlobalStrippedKeywordNames =
    {
        "FOG_EXP",
        "FOG_EXP2",
        "INSTANCING_ON",
        "PROCEDURAL_INSTANCING_ON",
        "LIGHTMAP_ON",
        "DIRLIGHTMAP_COMBINED",
        "DYNAMICLIGHTMAP_ON",
        "LIGHTMAP_SHADOW_MIXING",
        "SHADOWS_SHADOWMASK",
        "DIRECTIONAL_COOKIE",
        "POINT_COOKIE",
        "STEREO_INSTANCING_ON",
        "STEREO_MULTIVIEW_ON",
        "UNITY_SINGLE_PASS_STEREO"
    };

    // PBR texture/blend feature keywords stripped from non-UI / non-Sprite 3D shaders
    // (the procedural low-poly world uses untextured flat colors via MaterialPropertyBlock)
    private static readonly string[] SurfacePbrStrippedKeywordNames =
    {
        "_NORMALMAP",
        "_PARALLAXMAP",
        "_METALLICGLOSSMAP",
        "_SPECGLOSSMAP",
        "_DETAIL_MULX2",
        "_EMISSION",
        "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A",
        "_SPECULARHIGHLIGHTS_OFF",
        "_GLOSSYREFLECTIONS_OFF",
        "_ALPHATEST_ON",
        "_ALPHABLEND_ON",
        "_ALPHAPREMULTIPLY_ON"
    };

    private ShaderKeyword[] _globalStrippedKeywords;
    private ShaderKeyword[] _surfaceStrippedKeywords;
    private bool _keywordsInitialized;

    static BuildConfigurator()
    {
        // Defer AssetDatabase / GraphicsSettings mutations until the Editor has finished loading ProjectSettings
        EditorApplication.delayCall += ApplyOnEditorLoad;
    }

    /// <summary>
    /// Runs after <see cref="AndroidBuildConfigurator"/> (-100) and before Unity compiles player scripts/shaders.
    /// </summary>
    public int callbackOrder => -50;

    [InitializeOnLoadMethod]
    private static void ApplyOnEditorLoad()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            return;
        }

        if (Application.isBatchMode || EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
        {
            ApplyBuildConfiguration();
        }
    }

    [MenuItem("Asoda/Build/Configure Fast CI Build (Mono + Stripped Shaders)")]
    public static void ApplyBuildConfigurationMenu()
    {
        ApplyBuildConfiguration();
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildConfigurator] Applied Vulkan+GLES3, Mono backend, and aggressive shader stripping.");
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report == null || report.summary.platform == BuildTarget.Android)
        {
            ApplyBuildConfiguration();
            AssetDatabase.SaveAssets();
        }
    }

    /// <summary>
    /// Applies Android Graphics APIs (Vulkan + OpenGLES3 only), Mono scripting backend, and GraphicsSettings shader stripping.
    /// </summary>
    public static void ApplyBuildConfiguration(ScriptingImplementation? backendOverride = null)
    {
        ConfigureAndroidGraphicsApis();
        ConfigureAndroidScriptingBackend(backendOverride);
        ConfigureGraphicsShaderStripping();
    }

    /// <summary>
    /// Restricts Android Graphics APIs strictly to Vulkan and OpenGLES3 (removes OpenGLES2 to halve shader variants).
    /// </summary>
    public static void ConfigureAndroidGraphicsApis()
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        var graphicsApis = new[]
        {
            GraphicsDeviceType.Vulkan,
            GraphicsDeviceType.OpenGLES3
        };
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, graphicsApis);
    }

    /// <summary>
    /// Configures Android Scripting Backend to Mono (<see cref="ScriptingImplementation.Mono2x"/> + ARMv7) by default
    /// for fast CI builds, while still honoring an explicit <c>-scriptingBackend IL2CPP</c> CLI flag when requested.
    /// </summary>
    public static void ConfigureAndroidScriptingBackend(ScriptingImplementation? backendOverride = null)
    {
        ScriptingImplementation targetBackend = backendOverride ?? ResolveRequestedScriptingBackend();

#if UNITY_2021_2_OR_NEWER
        NamedBuildTarget androidTarget = NamedBuildTarget.Android;
        PlayerSettings.SetScriptingBackend(androidTarget, targetBackend);
        PlayerSettings.SetManagedStrippingLevel(androidTarget, ManagedStrippingLevel.Low);
#else
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, targetBackend);
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Android, ManagedStrippingLevel.Low);
#endif

        if (targetBackend == ScriptingImplementation.IL2CPP)
        {
#if UNITY_2021_2_OR_NEWER
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);
#else
            PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Android, Il2CppCompilerConfiguration.Release);
#endif
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.stripEngineCode = true;
        }
        else
        {
            // Unity Android Mono2x strictly requires 32-bit ARMv7 (ARM64 is unsupported under Mono2x in Unity 2022.3)
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARMv7;
            PlayerSettings.stripEngineCode = false;
        }

        Debug.Log(
            $"[BuildConfigurator] Android ScriptingBackend={targetBackend} | " +
            $"TargetArch={PlayerSettings.Android.targetArchitectures} | " +
            "GraphicsAPIs=Vulkan,OpenGLES3");
    }

    /// <summary>
    /// Forcefully configures <c>ProjectSettings/GraphicsSettings.asset</c> to strip unused Fog, Instancing,
    /// and Lightmap shader variants, and removes uber-shaders (e.g. <c>Standard</c>) from Always Included Shaders.
    /// </summary>
    public static void ConfigureGraphicsShaderStripping()
    {
        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(GraphicsSettingsAssetPath);
        if (assets == null || assets.Length == 0 || assets[0] == null)
        {
            return;
        }

        var graphicsSettings = new SerializedObject(assets[0]);
        graphicsSettings.Update();

        // 1. Fog Stripping: Custom (1) -> Keep ONLY Linear fog (StylizedFogSystem), strip Exp & Exp2
        SetIntProperty(graphicsSettings, "m_FogStripping", 1);
        SetBoolProperty(graphicsSettings, "m_FogKeepLinear", true);
        SetBoolProperty(graphicsSettings, "m_FogKeepExp", false);
        SetBoolProperty(graphicsSettings, "m_FogKeepExp2", false);

        // 2. Instancing Variants: Strip All (1) -> MaterialPropertyBlock per-renderer colors do not require INSTANCING_ON
        SetIntProperty(graphicsSettings, "m_InstancingStripping", 1);
        SetIntProperty(graphicsSettings, "m_BrgStripping", 1);

        // 3. Lightmap Modes: Custom (1) -> Strip all Baked & Realtime GI lightmap variants (100% procedural dynamic world)
        SetIntProperty(graphicsSettings, "m_LightmapStripping", 1);
        SetBoolProperty(graphicsSettings, "m_LightmapKeepPlain", false);
        SetBoolProperty(graphicsSettings, "m_LightmapKeepDirCombined", false);
        SetBoolProperty(graphicsSettings, "m_LightmapKeepDynamicPlain", false);
        SetBoolProperty(graphicsSettings, "m_LightmapKeepDynamicDirCombined", false);
        SetBoolProperty(graphicsSettings, "m_LightmapKeepShadowMask", false);
        SetBoolProperty(graphicsSettings, "m_LightmapKeepSubtractive", false);

        // 4. Purge heavy uber-shaders ("Standard", URP Lit) from m_AlwaysIncludedShaders
        //    ("Standard" in AlwaysIncludedShaders alone generates 98,304 variants!)
        SerializedProperty alwaysIncluded = graphicsSettings.FindProperty("m_AlwaysIncludedShaders");
        if (alwaysIncluded != null && alwaysIncluded.isArray)
        {
            for (int i = alwaysIncluded.arraySize - 1; i >= 0; i--)
            {
                Shader shader = alwaysIncluded.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (shader == null)
                {
                    alwaysIncluded.DeleteArrayElementAtIndex(i);
                    continue;
                }

                string shaderName = shader.name ?? string.Empty;
                if (string.Equals(shaderName, "Standard", StringComparison.Ordinal) ||
                    string.Equals(shaderName, "Standard (Specular setup)", StringComparison.Ordinal) ||
                    shaderName.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal) ||
                    shaderName.StartsWith("Autodesk ", StringComparison.Ordinal))
                {
                    // In Unity SerializedProperty arrays of Object references, clearing a non-null entry
                    // requires setting null first if Unity doesn't shrink the array in one call.
                    int sizeBefore = alwaysIncluded.arraySize;
                    alwaysIncluded.DeleteArrayElementAtIndex(i);
                    if (alwaysIncluded.arraySize == sizeBefore)
                    {
                        alwaysIncluded.DeleteArrayElementAtIndex(i);
                    }
                }
            }
        }

        graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
    }

    /// <summary>
    /// Compile-time shader variant stripper (<see cref="IPreprocessShaders"/>) that prunes unused Deferred/Meta passes
    /// and unused lightmap, fog, instancing, and PBR texture keywords before Unity invokes the shader compiler.
    /// </summary>
    public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
    {
        if (data == null || data.Count == 0)
        {
            return;
        }

        // Mobile forward rendering never uses Deferred, Legacy PrePass, or Editor Lightmapper Meta passes at runtime
        PassType passType = snippet.passType;
        if (passType == PassType.Deferred ||
            passType == PassType.LightPrePassBase ||
            passType == PassType.LightPrePassFinal ||
            passType == PassType.Meta)
        {
            data.Clear();
            return;
        }

        EnsureKeywordsInitialized();

        string shaderName = shader != null ? shader.name ?? string.Empty : string.Empty;
        bool isUiOrSpriteShader =
            shaderName.StartsWith("UI/", StringComparison.Ordinal) ||
            shaderName.StartsWith("Sprites/", StringComparison.Ordinal) ||
            shaderName.StartsWith("Hidden/Internal-GUITexture", StringComparison.Ordinal);

        for (int i = data.Count - 1; i >= 0; i--)
        {
            ShaderKeywordSet keywordSet = data[i].shaderKeywordSet;

            if (HasAnyKeyword(keywordSet, _globalStrippedKeywords))
            {
                data.RemoveAt(i);
                continue;
            }

            if (!isUiOrSpriteShader && HasAnyKeyword(keywordSet, _surfaceStrippedKeywords))
            {
                data.RemoveAt(i);
            }
        }
    }

    public static ScriptingImplementation ResolveRequestedScriptingBackend()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "-scriptingBackend", StringComparison.OrdinalIgnoreCase))
            {
                string val = args[i + 1];
                if (string.Equals(val, "IL2CPP", StringComparison.OrdinalIgnoreCase))
                {
                    return ScriptingImplementation.IL2CPP;
                }

                if (string.Equals(val, "Mono", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(val, "Mono2x", StringComparison.OrdinalIgnoreCase))
                {
                    return ScriptingImplementation.Mono2x;
                }
            }
        }

        string envBackend = Environment.GetEnvironmentVariable("SCRIPTING_BACKEND");
        if (string.Equals(envBackend, "IL2CPP", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptingImplementation.IL2CPP;
        }

        // Default to Mono2x for fast 5–8 minute CI iteration
        return ScriptingImplementation.Mono2x;
    }

    private void EnsureKeywordsInitialized()
    {
        if (_keywordsInitialized)
        {
            return;
        }

        _globalStrippedKeywords = new ShaderKeyword[GlobalStrippedKeywordNames.Length];
        for (int i = 0; i < GlobalStrippedKeywordNames.Length; i++)
        {
            _globalStrippedKeywords[i] = new ShaderKeyword(GlobalStrippedKeywordNames[i]);
        }

        _surfaceStrippedKeywords = new ShaderKeyword[SurfacePbrStrippedKeywordNames.Length];
        for (int i = 0; i < SurfacePbrStrippedKeywordNames.Length; i++)
        {
            _surfaceStrippedKeywords[i] = new ShaderKeyword(SurfacePbrStrippedKeywordNames[i]);
        }

        _keywordsInitialized = true;
    }

    private static bool HasAnyKeyword(ShaderKeywordSet keywordSet, ShaderKeyword[] keywords)
    {
        for (int k = 0; k < keywords.Length; k++)
        {
            if (keywordSet.IsEnabled(keywords[k]))
            {
                return true;
            }
        }

        return false;
    }

    private static void SetIntProperty(SerializedObject obj, string propertyName, int value)
    {
        SerializedProperty prop = obj.FindProperty(propertyName);
        if (prop != null)
        {
            prop.intValue = value;
        }
    }

    private static void SetBoolProperty(SerializedObject obj, string propertyName, bool value)
    {
        SerializedProperty prop = obj.FindProperty(propertyName);
        if (prop != null)
        {
            prop.boolValue = value;
        }
    }
}
#endif
