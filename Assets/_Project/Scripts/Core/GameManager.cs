using System;
using System.Collections.Generic;
using Asoda.Utils;
using UnityEngine;

namespace Asoda.Core
{
    /// <summary>
    /// Persistent central coordinator that configures mobile runtime settings (60 FPS, wake lock)
    /// and drives the pure C# subsystem game loop with zero per-frame garbage allocation.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    public sealed class GameManager : PersistentSingleton<GameManager>
    {
        public const int TargetFrameRate = 60;

        private struct SystemEntry
        {
            public IGameSystem System;
            public int Priority;
        }

        private struct PendingOperation
        {
            public bool IsRegister;
            public IGameSystem System;
            public int Priority;
        }

        private readonly List<SystemEntry> _systems = new List<SystemEntry>(16);
        private readonly Dictionary<Type, IGameSystem> _systemLookup = new Dictionary<Type, IGameSystem>(16);
        private readonly List<ITickable> _tickables = new List<ITickable>(16);
        private readonly List<IFixedTickable> _fixedTickables = new List<IFixedTickable>(8);
        private readonly List<ILateTickable> _lateTickables = new List<ILateTickable>(8);
        private readonly List<IPausable> _pausables = new List<IPausable>(8);
        private readonly List<PendingOperation> _pendingOperations = new List<PendingOperation>(4);

        private GameState _currentState = GameState.Uninitialized;
        private bool _isTicking;
        private bool _systemsShutDown;

        /// <summary>
        /// Current lifecycle state of the game loop.
        /// </summary>
        public GameState CurrentState => _currentState;

        /// <summary>
        /// True when the game loop is currently paused.
        /// </summary>
        public bool IsPaused => _currentState == GameState.Paused;

        /// <summary>
        /// Raised whenever <see cref="CurrentState"/> changes. Passes (previousState, newState).
        /// </summary>
        public event Action<GameState, GameState> OnGameStateChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            ResetSingletonState();
        }

        protected override void OnSingletonAwake()
        {
            _systemsShutDown = false;
            SetState(GameState.Bootstrapping);
            ApplyMobileRuntimeSettings();
        }

        private void Start()
        {
            if (_currentState == GameState.Bootstrapping)
            {
                StartGameLoop();
            }
        }

        private void Update()
        {
            if (_currentState != GameState.Playing)
            {
                return;
            }

            float deltaTime = Time.deltaTime;
            _isTicking = true;

            try
            {
                for (int i = 0; i < _tickables.Count; i++)
                {
                    try
                    {
                        _tickables[i].Tick(deltaTime);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, this);
                    }
                }
            }
            finally
            {
                _isTicking = false;
                FlushPendingOperations();
            }
        }

        private void FixedUpdate()
        {
            if (_currentState != GameState.Playing)
            {
                return;
            }

            float fixedDeltaTime = Time.fixedDeltaTime;
            _isTicking = true;

            try
            {
                for (int i = 0; i < _fixedTickables.Count; i++)
                {
                    try
                    {
                        _fixedTickables[i].FixedTick(fixedDeltaTime);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, this);
                    }
                }
            }
            finally
            {
                _isTicking = false;
                FlushPendingOperations();
            }
        }

        private void LateUpdate()
        {
            if (_currentState != GameState.Playing)
            {
                return;
            }

            float deltaTime = Time.deltaTime;
            _isTicking = true;

            try
            {
                for (int i = 0; i < _lateTickables.Count; i++)
                {
                    try
                    {
                        _lateTickables[i].LateTick(deltaTime);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex, this);
                    }
                }
            }
            finally
            {
                _isTicking = false;
                FlushPendingOperations();
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!pauseStatus && _currentState != GameState.Quitting)
            {
                ApplyMobileRuntimeSettings();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && _currentState != GameState.Quitting)
            {
                ApplyMobileRuntimeSettings();
            }
        }

        protected override void OnApplicationQuit()
        {
            ShutdownAllSystems();
            base.OnApplicationQuit();
        }

        protected override void OnDestroy()
        {
            if (IsSingletonInstance)
            {
                ShutdownAllSystems();
            }

            base.OnDestroy();
        }

        /// <summary>
        /// Enforces 60 FPS rendering (disabling VSync override) and prevents Android screen sleep.
        /// </summary>
        public void ApplyMobileRuntimeSettings()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        /// <summary>
        /// Transitions the game manager from bootstrapping into the active gameplay loop.
        /// </summary>
        public void StartGameLoop()
        {
            if (_currentState == GameState.Playing || _currentState == GameState.Quitting)
            {
                return;
            }

            Time.timeScale = 1f;
            SetState(GameState.Playing);
        }

        /// <summary>
        /// Pauses or resumes the active game loop and notifies registered <see cref="IPausable"/> systems.
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (_currentState == GameState.Quitting || _currentState == GameState.Bootstrapping)
            {
                return;
            }

            GameState targetState = paused ? GameState.Paused : GameState.Playing;
            if (_currentState == targetState)
            {
                return;
            }

            Time.timeScale = paused ? 0f : 1f;
            SetState(targetState);

            for (int i = 0; i < _pausables.Count; i++)
            {
                try
                {
                    _pausables[i].OnPauseChanged(paused);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, this);
                }
            }
        }

        public void PauseGame() => SetPaused(true);

        public void ResumeGame() => SetPaused(false);

        /// <summary>
        /// Registers and initializes a pure C# subsystem in deterministic priority order (lower runs earlier).
        /// </summary>
        public T RegisterSystem<T>(T system, int priority = 0) where T : class, IGameSystem
        {
            if (system == null)
            {
                throw new ArgumentNullException(nameof(system));
            }

            if (_currentState == GameState.Quitting)
            {
                return system;
            }

            if (_isTicking)
            {
                _pendingOperations.Add(new PendingOperation
                {
                    IsRegister = true,
                    System = system,
                    Priority = priority
                });
                return system;
            }

            RegisterSystemInternal(system, priority);
            return system;
        }

        /// <summary>
        /// Shuts down and removes a previously registered pure C# subsystem.
        /// </summary>
        public void UnregisterSystem(IGameSystem system)
        {
            if (system == null)
            {
                return;
            }

            if (_isTicking)
            {
                _pendingOperations.Add(new PendingOperation
                {
                    IsRegister = false,
                    System = system,
                    Priority = 0
                });
                return;
            }

            UnregisterSystemInternal(system);
        }

        /// <summary>
        /// Retrieves a registered subsystem by concrete type, or null if not registered.
        /// </summary>
        public T GetSystem<T>() where T : class, IGameSystem
        {
            return _systemLookup.TryGetValue(typeof(T), out IGameSystem system) ? system as T : null;
        }

        /// <summary>
        /// Attempts to retrieve a registered subsystem by concrete type without allocations.
        /// </summary>
        public bool TryGetSystem<T>(out T system) where T : class, IGameSystem
        {
            if (_systemLookup.TryGetValue(typeof(T), out IGameSystem found) && found is T typed)
            {
                system = typed;
                return true;
            }

            system = null;
            return false;
        }

        private void RegisterSystemInternal(IGameSystem system, int priority)
        {
            Type systemType = system.GetType();
            if (_systemLookup.ContainsKey(systemType))
            {
                return;
            }

            int insertIndex = _systems.Count;
            for (int i = 0; i < _systems.Count; i++)
            {
                if (priority < _systems[i].Priority)
                {
                    insertIndex = i;
                    break;
                }
            }

            _systems.Insert(insertIndex, new SystemEntry
            {
                System = system,
                Priority = priority
            });
            _systemLookup[systemType] = system;

            RebuildDispatchLists();

            try
            {
                system.Initialize();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
            }
        }

        private void UnregisterSystemInternal(IGameSystem system)
        {
            Type systemType = system.GetType();
            if (!_systemLookup.Remove(systemType))
            {
                return;
            }

            for (int i = 0; i < _systems.Count; i++)
            {
                if (ReferenceEquals(_systems[i].System, system))
                {
                    _systems.RemoveAt(i);
                    break;
                }
            }

            RebuildDispatchLists();

            try
            {
                system.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex, this);
            }
        }

        private void RebuildDispatchLists()
        {
            _tickables.Clear();
            _fixedTickables.Clear();
            _lateTickables.Clear();
            _pausables.Clear();

            for (int i = 0; i < _systems.Count; i++)
            {
                IGameSystem system = _systems[i].System;

                if (system is ITickable tickable)
                {
                    _tickables.Add(tickable);
                }

                if (system is IFixedTickable fixedTickable)
                {
                    _fixedTickables.Add(fixedTickable);
                }

                if (system is ILateTickable lateTickable)
                {
                    _lateTickables.Add(lateTickable);
                }

                if (system is IPausable pausable)
                {
                    _pausables.Add(pausable);
                }
            }
        }

        private void FlushPendingOperations()
        {
            if (_pendingOperations.Count == 0)
            {
                return;
            }

            for (int i = 0; i < _pendingOperations.Count; i++)
            {
                PendingOperation op = _pendingOperations[i];
                if (op.IsRegister)
                {
                    RegisterSystemInternal(op.System, op.Priority);
                }
                else
                {
                    UnregisterSystemInternal(op.System);
                }
            }

            _pendingOperations.Clear();
        }

        private void ShutdownAllSystems()
        {
            if (_systemsShutDown)
            {
                return;
            }

            _systemsShutDown = true;
            Time.timeScale = 1f;
            SetState(GameState.Quitting);

            _pendingOperations.Clear();
            _tickables.Clear();
            _fixedTickables.Clear();
            _lateTickables.Clear();
            _pausables.Clear();

            for (int i = _systems.Count - 1; i >= 0; i--)
            {
                try
                {
                    _systems[i].System.Shutdown();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex, this);
                }
            }

            _systems.Clear();
            _systemLookup.Clear();
        }

        private void SetState(GameState newState)
        {
            if (_currentState == newState)
            {
                return;
            }

            GameState previousState = _currentState;
            _currentState = newState;
            OnGameStateChanged?.Invoke(previousState, newState);
        }
    }
}
