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
        /// Composition root for registering pure C# subsystems (World, Player, Camera, Streaming)
        /// into <see cref="GameManager"/> in deterministic priority order.
        /// </summary>
        private static void RegisterCoreSystems(GameManager gameManager)
        {
            // Register pure C# World, Player, and Streaming systems here as they are implemented:
            // e.g., gameManager.RegisterSystem(new WorldStreamingSystem(), priority: -50);
            // e.g., gameManager.RegisterSystem(new PlayerSystem(), priority: 0);
        }
    }
}
