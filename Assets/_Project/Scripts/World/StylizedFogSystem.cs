using Asoda.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.World
{
    /// <summary>
    /// Curated Indian regional atmosphere & fog color palettes.
    /// </summary>
    public enum FogAtmospherePreset
    {
        IndianSunsetDust = 0,
        RajasthaniNoonDust = 1,
        MorningSaffron = 2,
        MonsoonMist = 3
    }

    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> that configures Unity's linear distance fog,
    /// ambient trilight shading, camera far-clip culling, and the custom <c>Asoda/StylizedIndianSunsetSkybox</c> shader
    /// so distant chunks smoothly dissolve into warm Indian sunset/dust haze before the 150m chunk streaming edge.
    /// </summary>
    public sealed class StylizedFogSystem : IGameSystem, ITickable
    {
        public const string SkyboxShaderResourcePath = "Shaders/StylizedIndianSunsetSkybox";
        public const string SkyboxShaderName = "Asoda/StylizedIndianSunsetSkybox";
        public const float DefaultChunkHorizonMeters = 150f;

        private static readonly int ZenithColorId = Shader.PropertyToID("_ZenithColor");
        private static readonly int MidSkyColorId = Shader.PropertyToID("_MidSkyColor");
        private static readonly int HorizonColorId = Shader.PropertyToID("_HorizonColor");
        private static readonly int SunColorId = Shader.PropertyToID("_SunColor");
        private static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");

        private struct AtmospherePalette
        {
            public Color ZenithSky;
            public Color MidSky;
            public Color HorizonFog;
            public Color SunLight;
            public Color AmbientGround;
            public float SunIntensity;
        }

        private readonly WorldChunkManager _chunkManager;

        private FogAtmospherePreset _activePreset;
        private AtmospherePalette _currentPalette;
        private AtmospherePalette _sourcePalette;
        private AtmospherePalette _targetPalette;

        private Material _skyboxMaterial;
        private Camera _syncedCamera;
        private Light _syncedSunLight;

        private float _fogStartDistance;
        private float _fogEndDistance;
        private float _transitionDuration;
        private float _transitionElapsed;
        private float _cameraCheckTimer;
        private bool _customDistanceOverride;

        public FogAtmospherePreset ActivePreset => _activePreset;
        public float FogStartDistance => _fogStartDistance;
        public float FogEndDistance => _fogEndDistance;
        public Color CurrentFogColor => _currentPalette.HorizonFog;

        public StylizedFogSystem(
            WorldChunkManager chunkManager = null,
            FogAtmospherePreset initialPreset = FogAtmospherePreset.IndianSunsetDust)
        {
            _chunkManager = chunkManager;
            _activePreset = initialPreset;
        }

        public void Initialize()
        {
            _currentPalette = GetPalette(_activePreset);
            _sourcePalette = _currentPalette;
            _targetPalette = _currentPalette;
            _transitionDuration = 0f;
            _transitionElapsed = 0f;
            _cameraCheckTimer = 0f;

            CreateOrBindSkyboxMaterial();
            RecalculateFogDistancesFromChunks();
            ApplyAtmosphereToRenderSettings(_currentPalette);
        }

        public void Tick(float deltaTime)
        {
            if (_transitionDuration > 0f && _transitionElapsed < _transitionDuration)
            {
                _transitionElapsed = Mathf.Min(_transitionDuration, _transitionElapsed + deltaTime);
                float t = Mathf.SmoothStep(0f, 1f, _transitionElapsed / _transitionDuration);
                _currentPalette = LerpPalette(_sourcePalette, _targetPalette, t);
                ApplyAtmosphereToRenderSettings(_currentPalette);
            }

            _cameraCheckTimer -= deltaTime;
            if (_cameraCheckTimer <= 0f)
            {
                _cameraCheckTimer = 1.0f;
                SyncCameraAndSun();
            }
        }

        public void Shutdown()
        {
            if (_skyboxMaterial != null)
            {
                if (RenderSettings.skybox == _skyboxMaterial)
                {
                    RenderSettings.skybox = null;
                }

                Object.Destroy(_skyboxMaterial);
                _skyboxMaterial = null;
            }

            _syncedCamera = null;
            _syncedSunLight = null;
        }

        /// <summary>
        /// Smoothly transitions fog, skybox, and sun warmth to a new regional preset over <paramref name="blendSeconds"/>.
        /// </summary>
        public void SetPreset(FogAtmospherePreset preset, float blendSeconds = 2.0f)
        {
            _activePreset = preset;
            _targetPalette = GetPalette(preset);

            if (blendSeconds <= 0.01f)
            {
                _currentPalette = _targetPalette;
                _transitionDuration = 0f;
                ApplyAtmosphereToRenderSettings(_currentPalette);
                SyncCameraAndSun();
                return;
            }

            _sourcePalette = _currentPalette;
            _transitionDuration = blendSeconds;
            _transitionElapsed = 0f;
        }

        /// <summary>
        /// Explicitly overrides linear fog start/end distances (in meters) and updates camera far-clip culling.
        /// </summary>
        public void SetFogDistances(float startMeters, float endMeters)
        {
            _customDistanceOverride = true;
            _fogEndDistance = Mathf.Max(25f, endMeters);
            _fogStartDistance = Mathf.Clamp(startMeters, 5f, _fogEndDistance - 10f);

            RenderSettings.fogStartDistance = _fogStartDistance;
            RenderSettings.fogEndDistance = _fogEndDistance;
            SyncCameraAndSun();
        }

        /// <summary>
        /// Re-binds to the active MainCamera (e.g., after <c>ThirdPersonCamera</c> spawns) and Directional Sun immediately.
        /// </summary>
        public void RefreshCameraAndLightingImmediate()
        {
            _syncedCamera = null;
            _syncedSunLight = null;
            SyncCameraAndSun();
        }

        /// <summary>
        /// Recomputes fog start/end distances so linear fog reaches 100% opacity strictly inside the chunk streaming edge.
        /// </summary>
        public void RecalculateFogDistancesFromChunks()
        {
            if (_customDistanceOverride)
            {
                return;
            }

            float horizonMeters = DefaultChunkHorizonMeters;
            if (_chunkManager != null)
            {
                // Nearest distance where an incoming chunk loads = LoadRadius * ChunkSize (e.g., 3 * 50m = 150m)
                horizonMeters = _chunkManager.LoadRadius * _chunkManager.ChunkSize;
            }

            // Reach 100% fog opacity just inside the chunk loading boundary so pop-in is completely invisible
            _fogEndDistance = Mathf.Max(42f, horizonMeters - 3f);
            _fogStartDistance = Mathf.Max(15f, _fogEndDistance * 0.42f);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = _fogStartDistance;
            RenderSettings.fogEndDistance = _fogEndDistance;
        }

        private void CreateOrBindSkyboxMaterial()
        {
            Shader skyShader = Resources.Load<Shader>(SkyboxShaderResourcePath);
            if (skyShader == null)
            {
                skyShader = Shader.Find(SkyboxShaderName);
            }

            if (skyShader != null)
            {
                _skyboxMaterial = new Material(skyShader)
                {
                    name = "Asoda_StylizedIndianSunset_Skybox"
                };
                RenderSettings.skybox = _skyboxMaterial;
            }
        }

        private void ApplyAtmosphereToRenderSettings(AtmospherePalette palette)
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = palette.HorizonFog;
            RenderSettings.fogStartDistance = _fogStartDistance;
            RenderSettings.fogEndDistance = _fogEndDistance;

            // Warm stylized low-poly trilight ambient so shadowed walls never turn muddy black on mobile
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Color.Lerp(palette.MidSky, palette.ZenithSky, 0.45f);
            RenderSettings.ambientEquatorColor = palette.HorizonFog;
            RenderSettings.ambientGroundColor = palette.AmbientGround;

            if (_skyboxMaterial != null)
            {
                _skyboxMaterial.SetColor(ZenithColorId, palette.ZenithSky);
                _skyboxMaterial.SetColor(MidSkyColorId, palette.MidSky);
                _skyboxMaterial.SetColor(HorizonColorId, palette.HorizonFog);
                _skyboxMaterial.SetColor(SunColorId, palette.SunLight);
            }

            if (_syncedCamera != null)
            {
                _syncedCamera.backgroundColor = palette.HorizonFog;
            }

            if (_syncedSunLight != null)
            {
                _syncedSunLight.color = palette.SunLight;
                _syncedSunLight.intensity = palette.SunIntensity;
            }
        }

        private void SyncCameraAndSun()
        {
            if (_syncedCamera == null)
            {
                _syncedCamera = Camera.main;
            }

            if (_syncedCamera != null)
            {
                _syncedCamera.backgroundColor = _currentPalette.HorizonFog;
                _syncedCamera.clearFlags = _skyboxMaterial != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;

                // Match far clip plane tightly past the 100% fog wall to save mobile GPU fillrate and vertex shading
                float targetFarClip = _fogEndDistance + 10f;
                if (Mathf.Abs(_syncedCamera.farClipPlane - targetFarClip) > 1f)
                {
                    _syncedCamera.farClipPlane = targetFarClip;
                }
            }

            if (_syncedSunLight == null)
            {
#if UNITY_2022_2_OR_NEWER
                Light foundLight = Object.FindFirstObjectByType<Light>();
#else
                Light foundLight = Object.FindObjectOfType<Light>();
#endif
                if (foundLight != null && foundLight.type == LightType.Directional)
                {
                    _syncedSunLight = foundLight;
                }
            }

            if (_syncedSunLight != null)
            {
                _syncedSunLight.color = _currentPalette.SunLight;
                _syncedSunLight.intensity = _currentPalette.SunIntensity;

                if (_skyboxMaterial != null)
                {
                    Vector3 toSun = -_syncedSunLight.transform.forward;
                    _skyboxMaterial.SetVector(SunDirectionId, new Vector4(toSun.x, toSun.y, toSun.z, 0f));
                }
            }
        }

        private static AtmospherePalette LerpPalette(AtmospherePalette a, AtmospherePalette b, float t)
        {
            return new AtmospherePalette
            {
                ZenithSky = Color.Lerp(a.ZenithSky, b.ZenithSky, t),
                MidSky = Color.Lerp(a.MidSky, b.MidSky, t),
                HorizonFog = Color.Lerp(a.HorizonFog, b.HorizonFog, t),
                SunLight = Color.Lerp(a.SunLight, b.SunLight, t),
                AmbientGround = Color.Lerp(a.AmbientGround, b.AmbientGround, t),
                SunIntensity = Mathf.Lerp(a.SunIntensity, b.SunIntensity, t)
            };
        }

        private static AtmospherePalette GetPalette(FogAtmospherePreset preset)
        {
            switch (preset)
            {
                case FogAtmospherePreset.RajasthaniNoonDust:
                    return new AtmospherePalette
                    {
                        ZenithSky = new Color(0.32f, 0.56f, 0.82f, 1f),
                        MidSky = new Color(0.76f, 0.78f, 0.74f, 1f),
                        HorizonFog = new Color(0.88f, 0.79f, 0.64f, 1f),
                        SunLight = new Color(1.00f, 0.96f, 0.86f, 1f),
                        AmbientGround = new Color(0.54f, 0.46f, 0.36f, 1f),
                        SunIntensity = 1.20f
                    };

                case FogAtmospherePreset.MorningSaffron:
                    return new AtmospherePalette
                    {
                        ZenithSky = new Color(0.28f, 0.44f, 0.72f, 1f),
                        MidSky = new Color(0.94f, 0.68f, 0.46f, 1f),
                        HorizonFog = new Color(0.96f, 0.78f, 0.58f, 1f),
                        SunLight = new Color(1.00f, 0.91f, 0.72f, 1f),
                        AmbientGround = new Color(0.50f, 0.42f, 0.35f, 1f),
                        SunIntensity = 1.10f
                    };

                case FogAtmospherePreset.MonsoonMist:
                    return new AtmospherePalette
                    {
                        ZenithSky = new Color(0.34f, 0.41f, 0.48f, 1f),
                        MidSky = new Color(0.55f, 0.61f, 0.65f, 1f),
                        HorizonFog = new Color(0.68f, 0.73f, 0.74f, 1f),
                        SunLight = new Color(0.86f, 0.89f, 0.92f, 1f),
                        AmbientGround = new Color(0.36f, 0.40f, 0.36f, 1f),
                        SunIntensity = 0.90f
                    };

                default: // IndianSunsetDust
                    return new AtmospherePalette
                    {
                        ZenithSky = new Color(0.22f, 0.25f, 0.48f, 1f),
                        MidSky = new Color(0.86f, 0.47f, 0.34f, 1f),
                        HorizonFog = new Color(0.92f, 0.64f, 0.43f, 1f),
                        SunLight = new Color(1.00f, 0.86f, 0.62f, 1f),
                        AmbientGround = new Color(0.48f, 0.36f, 0.30f, 1f),
                        SunIntensity = 1.12f
                    };
            }
        }
    }
}
