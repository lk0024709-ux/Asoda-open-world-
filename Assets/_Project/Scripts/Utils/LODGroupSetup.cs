using System;
using System.Collections.Generic;
using Asoda.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.Utils
{
    /// <summary>
    /// User-selectable LOD quality presets for mobile performance scaling.
    /// </summary>
    public enum LodQualityPreset
    {
        Auto = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    /// <summary>
    /// Normalized mobile device thermal states used to scale <see cref="QualitySettings.lodBias"/>.
    /// </summary>
    public enum DeviceThermalState
    {
        Nominal = 0,
        Fair = 1,
        Serious = 2,
        Critical = 3
    }

    /// <summary>
    /// World-space distance thresholds (in meters from the player camera) for 3-tier LOD setup.
    /// </summary>
    public readonly struct LODDistanceProfile
    {
        public readonly float Lod0MaxDistance;
        public readonly float Lod1MaxDistance;
        public readonly float CullDistance;
        public readonly float ReferenceFieldOfView;
        public readonly float FallbackBoundsDiameter;
        public readonly bool CreateBillboardProxyForLod2;

        public LODDistanceProfile(
            float lod0MaxDistance,
            float lod1MaxDistance,
            float cullDistance,
            float fallbackBoundsDiameter,
            bool createBillboardProxyForLod2,
            float referenceFieldOfView = 60f)
        {
            Lod0MaxDistance = Mathf.Max(2f, lod0MaxDistance);
            Lod1MaxDistance = Mathf.Max(Lod0MaxDistance + 4f, lod1MaxDistance);
            CullDistance = Mathf.Max(Lod1MaxDistance + 6f, cullDistance);
            FallbackBoundsDiameter = Mathf.Max(0.25f, fallbackBoundsDiameter);
            CreateBillboardProxyForLod2 = createBillboardProxyForLod2;
            ReferenceFieldOfView = Mathf.Clamp(referenceFieldOfView, 30f, 110f);
        }

        public static LODDistanceProfile DefaultVehicle => new LODDistanceProfile(
            lod0MaxDistance: 26f,
            lod1MaxDistance: 62f,
            cullDistance: 120f,
            fallbackBoundsDiameter: 3.2f,
            createBillboardProxyForLod2: true);

        public static LODDistanceProfile DefaultWeapon => new LODDistanceProfile(
            lod0MaxDistance: 12f,
            lod1MaxDistance: 28f,
            cullDistance: 55f,
            fallbackBoundsDiameter: 0.85f,
            createBillboardProxyForLod2: false);
    }

    /// <summary>
    /// Automatically configures 3-tier Unity <see cref="LODGroup"/> components—LOD0 (High),
    /// LOD1 (Medium), and LOD2 (Billboard Silhouette / Culled)—on ready-made Vehicles and Weapons
    /// based on distance from the player.
    /// </summary>
    public static class LODGroupSetup
    {
        private const string BillboardProxyName = "LOD2_BillboardProxy";

        private static Mesh _sharedBillboardMesh;

        /// <summary>
        /// Configures LOD0 (High), LOD1 (Medium), and LOD2 (Billboard/Culled) for a ready-made Vehicle prefab or instance.
        /// </summary>
        public static LODGroup ConfigureVehicleLOD(GameObject vehicleRoot)
        {
            return ConfigureLODGroup(vehicleRoot, LODDistanceProfile.DefaultVehicle);
        }

        /// <summary>
        /// Configures LOD0 (High), LOD1 (Medium), and LOD2 (Culled) for a ready-made Weapon prefab or instance.
        /// </summary>
        public static LODGroup ConfigureWeaponLOD(GameObject weaponRoot)
        {
            return ConfigureLODGroup(weaponRoot, LODDistanceProfile.DefaultWeapon);
        }

        /// <summary>
        /// Configures a 3-stage <see cref="LODGroup"/> on <paramref name="targetRoot"/> using world-space distance thresholds.
        /// Supports both prefabs with explicit _LOD0/_LOD1/_LOD2 child meshes and multi-part assets without pre-made LODs.
        /// </summary>
        public static LODGroup ConfigureLODGroup(GameObject targetRoot, LODDistanceProfile profile)
        {
            if (targetRoot == null)
            {
                return null;
            }

            Renderer[] allRenderers = targetRoot.GetComponentsInChildren<Renderer>(true);
            if (allRenderers == null || allRenderers.Length == 0)
            {
                return null;
            }

            MaterialVariantApplier.EnableGpuInstancing(allRenderers);

            LODGroup lodGroup = targetRoot.GetComponent<LODGroup>();
            if (lodGroup == null)
            {
                lodGroup = targetRoot.AddComponent<LODGroup>();
            }

            lodGroup.fadeMode = LODFadeMode.None;
            lodGroup.animateCrossFading = false;

            float diameter = EstimateAssetDiameter(targetRoot.transform, allRenderers, profile.FallbackBoundsDiameter);

            float h0 = DistanceToScreenHeight(diameter, profile.Lod0MaxDistance, profile.ReferenceFieldOfView);
            float h1 = DistanceToScreenHeight(diameter, profile.Lod1MaxDistance, profile.ReferenceFieldOfView);
            float h2 = DistanceToScreenHeight(diameter, profile.CullDistance, profile.ReferenceFieldOfView);

            // Enforce strictly descending screen-relative transition heights in (0.005 .. 0.85)
            h2 = Mathf.Clamp(h2, 0.008f, 0.25f);
            h1 = Mathf.Clamp(Mathf.Max(h1, h2 + 0.025f), 0.035f, 0.50f);
            h0 = Mathf.Clamp(Mathf.Max(h0, h1 + 0.045f), 0.085f, 0.85f);

            PartitionRenderersIntoLods(
                targetRoot,
                allRenderers,
                diameter,
                profile.CreateBillboardProxyForLod2,
                out Renderer[] lod0Renderers,
                out Renderer[] lod1Renderers,
                out Renderer[] lod2Renderers);

            var lods = new LOD[3];
            lods[0] = new LOD(h0, lod0Renderers);
            lods[1] = new LOD(h1, lod1Renderers);
            lods[2] = new LOD(h2, lod2Renderers); // Below h2, Unity automatically culls the object completely

            lodGroup.SetLODs(lods);
            lodGroup.RecalculateBounds();
            return lodGroup;
        }

        /// <summary>
        /// Converts a world-space distance (meters from camera) into Unity's screen-relative height [0..1].
        /// </summary>
        public static float DistanceToScreenHeight(float objectDiameter, float distanceMeters, float verticalFovDegrees = 60f)
        {
            float halfFovRad = verticalFovDegrees * 0.5f * Mathf.Deg2Rad;
            float frustumHeightPerMeter = 2f * Mathf.Tan(halfFovRad);
            float denom = Mathf.Max(0.1f, distanceMeters * frustumHeightPerMeter);
            return objectDiameter / denom;
        }

        private static void PartitionRenderersIntoLods(
            GameObject root,
            Renderer[] allRenderers,
            float diameter,
            bool createBillboardProxy,
            out Renderer[] lod0,
            out Renderer[] lod1,
            out Renderer[] lod2)
        {
            var explicitLod0 = new List<Renderer>(allRenderers.Length);
            var explicitLod1 = new List<Renderer>(allRenderers.Length);
            var explicitLod2 = new List<Renderer>(4);

            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer r = allRenderers[i];
                if (r == null)
                {
                    continue;
                }

                string n = r.gameObject.name;
                if (string.Equals(n, BillboardProxyName, StringComparison.Ordinal))
                {
                    explicitLod2.Add(r);
                }
                else if (ContainsLodTag(n, "LOD0") || ContainsLodTag(n, "_High"))
                {
                    explicitLod0.Add(r);
                }
                else if (ContainsLodTag(n, "LOD1") || ContainsLodTag(n, "_Med"))
                {
                    explicitLod1.Add(r);
                }
                else if (ContainsLodTag(n, "LOD2") || ContainsLodTag(n, "_Low") || ContainsLodTag(n, "Billboard"))
                {
                    explicitLod2.Add(r);
                }
            }

            // Pattern A: Asset already ships with explicit LOD child meshes
            if (explicitLod0.Count > 0)
            {
                lod0 = explicitLod0.ToArray();
                lod1 = explicitLod1.Count > 0 ? explicitLod1.ToArray() : lod0;
                lod2 = explicitLod2.Count > 0 ? explicitLod2.ToArray() : Array.Empty<Renderer>();
                return;
            }

            // Pattern B: Ready-made multi-part asset without explicit _LOD1/_LOD2 meshes
            // LOD0 = all parts; LOD1 = major structural hulls (culling tiny details like mirrors, bolts, sights, wheels);
            // LOD2 = lightweight silhouette card (for vehicles) or culled empty array (for small weapons).
            var realParts = new List<Renderer>(allRenderers.Length);
            float maxPartSize = 0f;

            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer r = allRenderers[i];
                if (r == null || string.Equals(r.gameObject.name, BillboardProxyName, StringComparison.Ordinal))
                {
                    continue;
                }

                realParts.Add(r);
                float partSize = EstimateRendererLocalSize(r);
                if (partSize > maxPartSize)
                {
                    maxPartSize = partSize;
                }
            }

            lod0 = realParts.ToArray();

            var mediumParts = new List<Renderer>(realParts.Count);
            float mediumThreshold = maxPartSize * 0.35f;
            for (int i = 0; i < realParts.Count; i++)
            {
                if (EstimateRendererLocalSize(realParts[i]) >= mediumThreshold)
                {
                    mediumParts.Add(realParts[i]);
                }
            }

            if (mediumParts.Count == 0 && realParts.Count > 0)
            {
                mediumParts.Add(realParts[0]);
            }

            lod1 = mediumParts.ToArray();

            if (createBillboardProxy && realParts.Count > 0)
            {
                Renderer proxy = GetOrCreateBillboardProxy(root, realParts[0], diameter);
                lod2 = proxy != null ? new[] { proxy } : Array.Empty<Renderer>();
            }
            else
            {
                lod2 = Array.Empty<Renderer>(); // Culled at LOD2 transition
            }
        }

        private static Renderer GetOrCreateBillboardProxy(GameObject root, Renderer primaryRenderer, float diameter)
        {
            Transform existing = root.transform.Find(BillboardProxyName);
            if (existing != null)
            {
                return existing.GetComponent<MeshRenderer>();
            }

            Material primaryMat = primaryRenderer != null ? primaryRenderer.sharedMaterial : null;
            if (primaryMat == null)
            {
                return null;
            }

            if (_sharedBillboardMesh == null)
            {
                _sharedBillboardMesh = BuildCrossedImpostorMesh();
            }

            var proxyGo = new GameObject(BillboardProxyName);
            Transform t = proxyGo.transform;
            t.SetParent(root.transform, false);

            float height = Mathf.Max(1.0f, diameter * 0.52f);
            float width = Mathf.Max(1.2f, diameter * 0.78f);
            t.localPosition = new Vector3(0f, height * 0.5f, 0f);
            t.localRotation = Quaternion.identity;
            t.localScale = new Vector3(width, height, width);

            var mf = proxyGo.AddComponent<MeshFilter>();
            mf.sharedMesh = _sharedBillboardMesh;

            var mr = proxyGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = primaryMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            return mr;
        }

        /// <summary>
        /// Ultra-low-poly 4-triangle crossed silhouette impostor mesh (visible from all 360° angles with zero per-frame CPU rotation).
        /// </summary>
        private static Mesh BuildCrossedImpostorMesh()
        {
            var mesh = new Mesh
            {
                name = "LOD2_CrossedSilhouetteProxy"
            };

            mesh.vertices = new[]
            {
                // Quad A (XY plane)
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                // Quad B (ZY plane)
                new Vector3(0f, -0.5f, -0.5f),
                new Vector3(0f,  0.5f, -0.5f),
                new Vector3(0f,  0.5f,  0.5f),
                new Vector3(0f, -0.5f,  0.5f)
            };

            mesh.normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up,
                Vector3.up, Vector3.up, Vector3.up, Vector3.up
            };

            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f)
            };

            // Double-sided triangles (8 tris total) so Cull Back lit shaders render cleanly from every direction
            mesh.triangles = new[]
            {
                0, 1, 2, 0, 2, 3,
                2, 1, 0, 3, 2, 0,
                4, 5, 6, 4, 6, 7,
                6, 5, 4, 7, 6, 4
            };

            mesh.RecalculateBounds();
            return mesh;
        }

        private static float EstimateAssetDiameter(Transform root, Renderer[] renderers, float fallbackDiameter)
        {
            float maxExtent = 0f;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null || string.Equals(r.gameObject.name, BillboardProxyName, StringComparison.Ordinal))
                {
                    continue;
                }

                float size = EstimateRendererLocalSize(r);
                Vector3 localOffset = root.InverseTransformPoint(r.transform.position);
                float reach = localOffset.magnitude + (size * 0.5f);
                if (reach * 2f > maxExtent)
                {
                    maxExtent = reach * 2f;
                }

                if (size > maxExtent)
                {
                    maxExtent = size;
                }
            }

            return maxExtent > 0.1f ? maxExtent : fallbackDiameter;
        }

        private static float EstimateRendererLocalSize(Renderer r)
        {
            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                Vector3 meshSize = mf.sharedMesh.bounds.size;
                Vector3 scale = r.transform.localScale;
                float sx = Mathf.Abs(meshSize.x * scale.x);
                float sy = Mathf.Abs(meshSize.y * scale.y);
                float sz = Mathf.Abs(meshSize.z * scale.z);
                return Mathf.Max(sx, Mathf.Max(sy, sz));
            }

            Vector3 b = r.bounds.size;
            return Mathf.Max(b.x, Mathf.Max(b.y, b.z));
        }

        private static bool ContainsLodTag(string name, string tag)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> that dynamically regulates
    /// <see cref="QualitySettings.lodBias"/> based on Android thermal state, user quality preset,
    /// and a 60 FPS frame-time moving average.
    /// </summary>
    public sealed class DynamicLodBiasSystem : IGameSystem, ITickable
    {
        public const float MinLodBias = 0.38f;
        public const float MaxLodBias = 1.20f;

        private const float EvaluationIntervalSeconds = 1.5f;
        private const float ThermalPollIntervalSeconds = 10.0f;
        private const float BiasApplyEpsilon = 0.04f;

        private LodQualityPreset _qualityPreset;
        private DeviceThermalState _currentThermalState = DeviceThermalState.Nominal;
        private DeviceThermalState? _thermalOverride;

        private float _smoothedFrameDelta = 1f / 60f;
        private float _evaluationTimer;
        private float _thermalPollTimer;
        private float _adaptiveBias = 1.0f;
        private float _appliedLodBias = -1f;
        private float _initialProjectLodBias = 1.0f;

        public LodQualityPreset QualityPreset => _qualityPreset;
        public DeviceThermalState CurrentThermalState => _thermalOverride ?? _currentThermalState;
        public float CurrentLodBias => _appliedLodBias;
        public float EstimatedFps => _smoothedFrameDelta > 0.0001f ? (1f / _smoothedFrameDelta) : 60f;

        public DynamicLodBiasSystem(LodQualityPreset initialPreset = LodQualityPreset.Auto)
        {
            _qualityPreset = initialPreset;
        }

        public void Initialize()
        {
            _initialProjectLodBias = QualitySettings.lodBias;
            _smoothedFrameDelta = 1f / GameManager.TargetFrameRate;
            _evaluationTimer = EvaluationIntervalSeconds;
            _thermalPollTimer = 0.25f;

            PollAndroidThermalState();
            _adaptiveBias = GetPresetBaseLodBias(_qualityPreset);
            ApplyLodBiasImmediate(Mathf.Min(_adaptiveBias, GetThermalCeiling(CurrentThermalState)));
        }

        public void Tick(float deltaTime)
        {
            float unscaledDt = Time.unscaledDeltaTime;
            if (unscaledDt > 0.0001f && unscaledDt < 0.5f)
            {
                _smoothedFrameDelta = Mathf.Lerp(_smoothedFrameDelta, unscaledDt, 0.08f);
            }

            _thermalPollTimer -= unscaledDt;
            if (_thermalPollTimer <= 0f)
            {
                _thermalPollTimer = ThermalPollIntervalSeconds;
                PollAndroidThermalState();
            }

            _evaluationTimer -= unscaledDt;
            if (_evaluationTimer > 0f)
            {
                return;
            }

            _evaluationTimer = EvaluationIntervalSeconds;
            EvaluateAndAdjustLodBias();
        }

        public void Shutdown()
        {
            QualitySettings.lodBias = _initialProjectLodBias;
        }

        /// <summary>
        /// Changes the active LOD quality preset (Auto, Low, Medium, High) at runtime—callable from Settings UI.
        /// </summary>
        public void SetQualityPreset(LodQualityPreset preset)
        {
            _qualityPreset = preset;
            _adaptiveBias = GetPresetBaseLodBias(preset);
            EvaluateAndAdjustLodBias();
        }

        /// <summary>
        /// Overrides or clears the reported thermal state (useful for testing thermal throttling in Editor or hooking OS callbacks).
        /// </summary>
        public void SetThermalStateOverride(DeviceThermalState? overrideState)
        {
            _thermalOverride = overrideState;
            EvaluateAndAdjustLodBias();
        }

        private void EvaluateAndAdjustLodBias()
        {
            float thermalCeiling = GetThermalCeiling(CurrentThermalState);

            if (_qualityPreset != LodQualityPreset.Auto)
            {
                float presetTarget = Mathf.Clamp(Mathf.Min(GetPresetBaseLodBias(_qualityPreset), thermalCeiling), MinLodBias, MaxLodBias);
                ApplyLodBiasIfChanged(presetTarget);
                return;
            }

            float currentFps = EstimatedFps;
            if (currentFps < 54f)
            {
                // Step down LOD bias when frame rate dips below 60 FPS target
                _adaptiveBias = Mathf.Max(MinLodBias, _adaptiveBias - 0.10f);
            }
            else if (currentFps >= 58.5f && _adaptiveBias < thermalCeiling)
            {
                // Gently recover visual distance when holding stable 60 FPS with thermal headroom
                _adaptiveBias = Mathf.Min(thermalCeiling, _adaptiveBias + 0.05f);
            }

            float finalBias = Mathf.Clamp(Mathf.Min(_adaptiveBias, thermalCeiling), MinLodBias, MaxLodBias);
            ApplyLodBiasIfChanged(finalBias);
        }

        private void ApplyLodBiasIfChanged(float targetBias)
        {
            if (Mathf.Abs(targetBias - _appliedLodBias) >= BiasApplyEpsilon)
            {
                ApplyLodBiasImmediate(targetBias);
            }
        }

        private void ApplyLodBiasImmediate(float targetBias)
        {
            _appliedLodBias = targetBias;
            QualitySettings.lodBias = targetBias;
        }

        private static float GetPresetBaseLodBias(LodQualityPreset preset)
        {
            switch (preset)
            {
                case LodQualityPreset.Low:
                    return 0.55f;
                case LodQualityPreset.Medium:
                    return 0.85f;
                case LodQualityPreset.High:
                    return 1.15f;
                default:
                    return 1.00f;
            }
        }

        private static float GetThermalCeiling(DeviceThermalState state)
        {
            switch (state)
            {
                case DeviceThermalState.Nominal:
                    return MaxLodBias;
                case DeviceThermalState.Fair:
                    return 0.85f;
                case DeviceThermalState.Serious:
                    return 0.62f;
                case DeviceThermalState.Critical:
                    return 0.42f;
                default:
                    return 1.0f;
            }
        }

        private void PollAndroidThermalState()
        {
            if (_thermalOverride.HasValue)
            {
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intentFilter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED"))
                using (var batteryIntent = activity.Call<AndroidJavaObject>("registerReceiver", null, intentFilter))
                {
                    if (batteryIntent != null)
                    {
                        // Reported in tenths of a degree Celsius (e.g., 395 => 39.5°C)
                        int tempTenthsC = batteryIntent.Call<int>("getIntExtra", "temperature", 300);
                        if (tempTenthsC >= 440)
                        {
                            _currentThermalState = DeviceThermalState.Critical;
                        }
                        else if (tempTenthsC >= 410)
                        {
                            _currentThermalState = DeviceThermalState.Serious;
                        }
                        else if (tempTenthsC >= 380)
                        {
                            _currentThermalState = DeviceThermalState.Fair;
                        }
                        else
                        {
                            _currentThermalState = DeviceThermalState.Nominal;
                        }
                    }
                }
            }
            catch
            {
                _currentThermalState = DeviceThermalState.Nominal;
            }
#endif
        }
    }
}
