using Asoda.Player;
using Asoda.Utils;
using Asoda.World;
using UnityEngine;

namespace Asoda.Core
{
    /// <summary>
    /// Zero-scene entry point that initializes the game purely from C# code
    /// before and after the initial Unity scene loads—requiring zero manual Editor prefabs or scene setup.
    /// </summary>
    public static class GameBootstrap
    {
        public static readonly Vector3 DefaultPlayerSpawnPosition = new Vector3(0f, 0.15f, -12f);

        private static bool _isInitialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _isInitialized = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeBeforeSceneLoad()
        {
            if (_isInitialized)
            {
                return;
            }

            _isInitialized = true;

            ConfigureDeviceInput();

            GameManager gameManager = GameManager.Instance;
            gameManager.ApplyMobileRuntimeSettings();

            RegisterWorldAndInputSystems(gameManager);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log("[GameBootstrap] Initialized zero-scene core & world systems before scene load (Target: 60 FPS, NeverSleep).");
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeAfterSceneLoad()
        {
            if (!GameManager.HasInstance)
            {
                return;
            }

            GameManager gameManager = GameManager.Instance;

            var touchInput = gameManager.GetSystem<TouchInputManager>();
            var worldChunkManager = gameManager.GetSystem<WorldChunkManager>();
            var staticMeshCombiner = gameManager.GetSystem<StaticMeshCombiner>();
            var stylizedFogSystem = gameManager.GetSystem<StylizedFogSystem>();

            // 1. Ensure directional sunlight exists even in a completely empty scene
            EnsureDirectionalSun(worldChunkManager != null && worldChunkManager.Pool != null
                ? worldChunkManager.Pool.WorldRootTransform
                : null);

            // 2. Build runtime Mobile Touch UI (EventSystem, Canvas, Joystick, Camera Swipe Zone, Jump Button)
            touchInput?.EnsureTouchUiCreated();

            // 3. Synchronously stream & static-batch initial world chunks around the spawn point BEFORE spawning Player
            worldChunkManager?.PrepareInitialWorldAt(DefaultPlayerSpawnPosition);
            staticMeshCombiner?.FlushPendingCombinesImmediate();

            // 4. Spawn Player (placeholder capsule if no Prefabs/Player asset exists) & auto-bind to WorldChunkManager
            var playerController = new PlayerController(
                touchInput,
                worldChunkManager,
                DefaultPlayerSpawnPosition);
            gameManager.RegisterSystem(playerController, priority: 0);

            // 5. Spawn collision-aware ThirdPersonCamera (tagged MainCamera) & link to Player + Stylized Fog
            var thirdPersonCamera = new ThirdPersonCamera(
                playerController.PlayerTransform,
                touchInput);
            gameManager.RegisterSystem(thirdPersonCamera, priority: 75);

            playerController.SetCameraTransform(thirdPersonCamera.CameraTransform);
            stylizedFogSystem?.RefreshCameraAndLightingImmediate();

            // 6. Start the active 60 FPS pure C# game loop
            gameManager.StartGameLoop();
        }

        private static void ConfigureDeviceInput()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            Input.multiTouchEnabled = true;
#endif
        }

        /// <summary>
        /// Registers core input, rendering optimization, and world streaming systems into <see cref="GameManager"/>.
        /// Execution order per frame:
        ///   Update: TouchInputManager (-100) -> StylizedFog (-20) -> DynamicLod (-10) -> PlayerController (0) -> WorldChunkManager (50) -> StaticMeshCombiner (60)
        ///   LateUpdate: ThirdPersonCamera (75)
        /// </summary>
        private static void RegisterWorldAndInputSystems(GameManager gameManager)
        {
            var mapDataLoader = new MapDataLoader(WorldChunkManager.DefaultChunkSize);
            var worldChunkManager = new WorldChunkManager(
                mapDataLoader,
                chunkSize: WorldChunkManager.DefaultChunkSize,
                loadRadius: WorldChunkManager.DefaultLoadRadius);

            gameManager.RegisterSystem(new TouchInputManager(), priority: -100);
            gameManager.RegisterSystem(new StylizedFogSystem(worldChunkManager, FogAtmospherePreset.IndianSunsetDust), priority: -20);
            gameManager.RegisterSystem(new DynamicLodBiasSystem(LodQualityPreset.Auto), priority: -10);
            gameManager.RegisterSystem(worldChunkManager, priority: 50);
            gameManager.RegisterSystem(new StaticMeshCombiner(worldChunkManager), priority: 60);
        }

        private static void EnsureDirectionalSun(Transform parentRoot)
        {
#if UNITY_2022_2_OR_NEWER
            Light existingLight = Object.FindFirstObjectByType<Light>();
#else
            Light existingLight = Object.FindObjectOfType<Light>();
#endif
            if (existingLight != null && existingLight.type == LightType.Directional)
            {
                return;
            }

            var sunGo = new GameObject("[DirectionalSun]");
            if (parentRoot != null)
            {
                sunGo.transform.SetParent(parentRoot, false);
            }
            else
            {
                Object.DontDestroyOnLoad(sunGo);
            }

            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1.0f, 0.86f, 0.62f, 1f);
            sun.intensity = 1.12f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(18f, -25f, 0f);
        }
    }
}
