namespace Asoda.Core
{
    /// <summary>
    /// High-level lifecycle states of the game loop.
    /// </summary>
    public enum GameState
    {
        Uninitialized = 0,
        Bootstrapping = 1,
        Playing = 2,
        Paused = 3,
        Quitting = 4
    }

    /// <summary>
    /// Base contract for pure C# subsystems managed by <see cref="GameManager"/>.
    /// Centralizing systems avoids per-object MonoBehaviour interop overhead on mobile CPUs.
    /// </summary>
    public interface IGameSystem
    {
        void Initialize();
        void Shutdown();
    }

    /// <summary>
    /// Implemented by pure C# systems that execute every frame during Update.
    /// </summary>
    public interface ITickable
    {
        void Tick(float deltaTime);
    }

    /// <summary>
    /// Implemented by pure C# systems that execute on the physics step during FixedUpdate.
    /// </summary>
    public interface IFixedTickable
    {
        void FixedTick(float fixedDeltaTime);
    }

    /// <summary>
    /// Implemented by pure C# systems that execute after Update (e.g., camera follow) during LateUpdate.
    /// </summary>
    public interface ILateTickable
    {
        void LateTick(float deltaTime);
    }

    /// <summary>
    /// Implemented by systems that respond to game pause/resume transitions.
    /// </summary>
    public interface IPausable
    {
        void OnPauseChanged(bool isPaused);
    }
}
