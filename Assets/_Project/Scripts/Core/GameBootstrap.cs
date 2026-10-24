using Asoda.Utils;
using Asoda.World;
using UnityEngine;

namespace Asoda.Core
{
    /// <summary>
    /// Zero-scene entry point that initializes the game purely from C# code
    /// before any Unity scene loads—requiring no manual Editor prefab or scene setup.
    /// </summary>
    public static class GameBootstrap
    {
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

            RegisterCoreSystems(gameManager);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log("[GameBootstrap] Initialized zero-scene runtime before scene load (Target: 60 FPS, NeverSleep).");
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InitializeAfterSceneLoad()
        {
            if (!GameManager.HasInstance)
            {
                return;
            }

            GameManager.Instance.StartGameLoop();
        }

        private static void ConfigureDeviceInput()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            Input.multiTouchEnabled = true;
#endif
        }

        /// <summary>
        /// Composition root for registering pure C# subsystems (World, Rendering Optimization, LOD, Player)
        /// into <see cref="GameManager"/> in deterministic priority order.
        /// </summary>
        private static void RegisterCoreSystems(GameManager gameManager)
        {
            var mapDataLoader = new MapDataLoader(WorldChunkManager.DefaultChunkSize);
            var worldChunkManager = new WorldChunkManager(
                mapDataLoader,
                chunkSize: WorldChunkManager.DefaultChunkSize,
                loadRadius: WorldChunkManager.DefaultLoadRadius);

            // 1. Camera/Input & Mobile Rendering Quality Systems (run before chunk streaming)
            gameManager.RegisterSystem(new WorldTestPreviewSystem(worldChunkManager), priority: -25);
            gameManager.RegisterSystem(new StylizedFogSystem(worldChunkManager, FogAtmospherePreset.IndianSunsetDust), priority: -15);
            gameManager.RegisterSystem(new DynamicLodBiasSystem(LodQualityPreset.Auto), priority: -10);

            // 2. Open-World Chunk Streaming (50) followed immediately on the same frame by Static Mesh Batching (60)
            gameManager.RegisterSystem(worldChunkManager, priority: 50);
            gameManager.RegisterSystem(new StaticMeshCombiner(worldChunkManager), priority: 60);
        }
    }
}
