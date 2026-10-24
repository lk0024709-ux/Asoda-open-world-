using UnityEngine;

namespace Asoda.Utils
{
    /// <summary>
    /// Persistent MonoBehaviour singleton base class that survives scene transitions
    /// and guards against phantom instantiation during application shutdown.
    /// </summary>
    /// <typeparam name="T">Concrete MonoBehaviour singleton type.</typeparam>
    public abstract class PersistentSingleton<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static readonly object InstanceLock = new object();
        private static T _instance;
        private static bool _isShuttingDown;

        /// <summary>
        /// True when a live instance exists and the application is not shutting down.
        /// Safe to check inside OnDestroy or OnDisable without spawning a new GameObject.
        /// </summary>
        public static bool HasInstance => _instance != null && !_isShuttingDown;

        /// <summary>
        /// True once the application has begun quitting.
        /// </summary>
        public static bool IsShuttingDown => _isShuttingDown;

        /// <summary>
        /// Global singleton instance. Lazily locates or creates a persistent root GameObject if needed.
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_isShuttingDown)
                {
                    return null;
                }

                lock (InstanceLock)
                {
                    if (_instance != null)
                    {
                        return _instance;
                    }

#if UNITY_2022_2_OR_NEWER
                    _instance = FindFirstObjectByType<T>();
#else
                    _instance = FindObjectOfType<T>();
#endif

                    if (_instance == null)
                    {
                        var singletonObject = new GameObject($"[{typeof(T).Name}]");
                        DontDestroyOnLoad(singletonObject);
                        _instance = singletonObject.AddComponent<T>();
                    }

                    return _instance;
                }
            }
        }

        /// <summary>
        /// True when this component is the active authoritative singleton instance.
        /// </summary>
        protected bool IsSingletonInstance => _instance == this;

        protected virtual void Awake()
        {
            _isShuttingDown = false;

            if (_instance != null && _instance != this)
            {
                enabled = false;
                Destroy(gameObject);
                return;
            }

            _instance = this as T;

            if (transform.parent != null)
            {
                transform.SetParent(null);
            }

            DontDestroyOnLoad(gameObject);
            OnSingletonAwake();
        }

        /// <summary>
        /// Invoked once on the authoritative singleton instance during Awake.
        /// Override this instead of Awake to guarantee duplicate instances never initialize.
        /// </summary>
        protected virtual void OnSingletonAwake()
        {
        }

        protected virtual void OnApplicationQuit()
        {
            _isShuttingDown = true;
        }

        protected virtual void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        /// <summary>
        /// Resets static fields when Unity Editor Enter Play Mode Options disables Domain Reload.
        /// Call from a concrete subclass method decorated with RuntimeInitializeLoadType.SubsystemRegistration.
        /// </summary>
        protected static void ResetSingletonState()
        {
            _instance = null;
            _isShuttingDown = false;
        }
    }
}
