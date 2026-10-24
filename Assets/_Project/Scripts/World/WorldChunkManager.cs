using System;
using System.Collections.Generic;
using Asoda.Core;
using UnityEngine;

namespace Asoda.World
{
    /// <summary>
    /// Pure C# open-world streaming system driven by <see cref="GameManager"/> via <see cref="ITickable"/>.
    /// Divides the world into 50x50 unit chunks around the Player's XZ position, loading chunks ahead
    /// and recycling chunks behind through <see cref="ChunkPool"/> without Instantiate/Destroy spikes.
    /// </summary>
    public sealed class WorldChunkManager : IGameSystem, ITickable
    {
        public const float DefaultChunkSize = 50f;
        public const int DefaultLoadRadius = 3; // 3 * 50m = 150m streaming radius around the player
        public const int DefaultMaxChunkLoadsPerTick = 4;
        private const float TargetLookupIntervalSeconds = 0.5f;

        /// <summary>
        /// Zero-allocation struct comparer that orders higher-priority chunks toward the end of the list
        /// (center chunk first, then chunks aligned with the player's forward movement direction) for O(1) tail pop.
        /// </summary>
        private sealed class ChunkLoadPriorityComparer : IComparer<ChunkCoord>
        {
            public ChunkCoord Center;
            public float MoveDirX;
            public float MoveDirZ;

            public int Compare(ChunkCoord a, ChunkCoord b)
            {
                float scoreA = EvaluateScore(a);
                float scoreB = EvaluateScore(b);
                int cmp = scoreA.CompareTo(scoreB);
                if (cmp != 0)
                {
                    return cmp;
                }

                cmp = a.X.CompareTo(b.X);
                return cmp != 0 ? cmp : a.Z.CompareTo(b.Z);
            }

            private float EvaluateScore(ChunkCoord coord)
            {
                int dx = coord.X - Center.X;
                int dz = coord.Z - Center.Z;
                int distSq = (dx * dx) + (dz * dz);
                float forwardAlignment = (dx * MoveDirX) + (dz * MoveDirZ);
                return (-distSq * 2f) + forwardAlignment;
            }
        }

        private readonly float _chunkSize;
        private readonly int _loadRadius;
        private readonly int _maxChunkLoadsPerTick;
        private readonly MapDataLoader _mapDataLoader;

        private readonly Dictionary<ChunkCoord, WorldChunk> _activeChunks;
        private readonly HashSet<ChunkCoord> _desiredCoords;
        private readonly HashSet<ChunkCoord> _pendingSet;
        private readonly List<ChunkCoord> _pendingLoadList;
        private readonly List<ChunkCoord> _unloadBuffer;
        private readonly ChunkLoadPriorityComparer _priorityComparer = new ChunkLoadPriorityComparer();

        private ChunkPool _chunkPool;
        private Transform _trackingTarget;
        private Camera _cachedFallbackCamera;
        private Vector3 _manualTrackingPosition;
        private Vector3 _lastTrackedPosition;
        private ChunkCoord _currentCenterChunk;

        private bool _useManualPosition;
        private bool _hasInitialSync;
        private float _targetLookupTimer;

        public float ChunkSize => _chunkSize;
        public int LoadRadius => _loadRadius;
        public int ActiveChunkCount => _activeChunks.Count;
        public ChunkCoord CurrentCenterChunk => _currentCenterChunk;
        public Transform TrackingTarget => _trackingTarget;
        public MapDataLoader MapLoader => _mapDataLoader;
        public ChunkPool Pool => _chunkPool;

        public event Action<ChunkCoord> OnChunkLoaded;
        public event Action<ChunkCoord> OnChunkUnloaded;

        /// <summary>
        /// Invoked with the live <see cref="WorldChunk"/> immediately after its props are populated.
        /// Used by <see cref="StaticMeshCombiner"/> to merge static chunk geometry.
        /// </summary>
        public event Action<WorldChunk> OnChunkLoadedChunk;

        /// <summary>
        /// Invoked with the live <see cref="WorldChunk"/> right before its props are cleared back to pools.
        /// </summary>
        public event Action<WorldChunk> OnChunkUnloadingChunk;

        public WorldChunkManager(
            MapDataLoader mapDataLoader = null,
            float chunkSize = DefaultChunkSize,
            int loadRadius = DefaultLoadRadius,
            int maxChunkLoadsPerTick = DefaultMaxChunkLoadsPerTick)
        {
            _chunkSize = Mathf.Max(10f, chunkSize);
            _loadRadius = Mathf.Max(1, loadRadius);
            _maxChunkLoadsPerTick = Mathf.Max(1, maxChunkLoadsPerTick);
            _mapDataLoader = mapDataLoader ?? new MapDataLoader(_chunkSize);

            int gridDiameter = (_loadRadius * 2) + 1;
            int expectedActiveCount = gridDiameter * gridDiameter;

            _activeChunks = new Dictionary<ChunkCoord, WorldChunk>(expectedActiveCount + 4);
            _desiredCoords = new HashSet<ChunkCoord>(expectedActiveCount + 4);
            _pendingSet = new HashSet<ChunkCoord>(expectedActiveCount + 4);
            _pendingLoadList = new List<ChunkCoord>(expectedActiveCount + 4);
            _unloadBuffer = new List<ChunkCoord>(expectedActiveCount);
        }

        public void Initialize()
        {
            int gridDiameter = (_loadRadius * 2) + 1;
            int initialPoolCapacity = (gridDiameter * gridDiameter) + gridDiameter; // e.g., 9 + 3 = 12 for 3x3

            _chunkPool = new ChunkPool(_chunkSize, initialPoolCapacity, createGroundTiles: true);
            _mapDataLoader.AttachPoolRoot(_chunkPool.WorldRootTransform);
            _mapDataLoader.LoadDefaultMapData();
            _mapDataLoader.PrewarmPropPools(countPerCommonType: 32);

            _hasInitialSync = false;
            _targetLookupTimer = 0f;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log(
                $"[WorldChunkManager] Initialized {_chunkSize}x{_chunkSize}m grid " +
                $"(Radius: {_loadRadius} -> {gridDiameter}x{gridDiameter} chunks, " +
                $"Map Objects: {_mapDataLoader.TotalObjectCount} across {_mapDataLoader.PopulatedChunkCount} chunks).");
#endif
        }

        public void Tick(float deltaTime)
        {
            Vector3 trackedPosition = ResolveTrackedPosition(deltaTime);

            Vector2 moveDir = Vector2.zero;
            if (_hasInitialSync)
            {
                float moveX = trackedPosition.x - _lastTrackedPosition.x;
                float moveZ = trackedPosition.z - _lastTrackedPosition.z;
                float sqrMag = (moveX * moveX) + (moveZ * moveZ);
                if (sqrMag > 0.0001f)
                {
                    float invMag = 1f / Mathf.Sqrt(sqrMag);
                    moveDir = new Vector2(moveX * invMag, moveZ * invMag);
                }
            }

            _lastTrackedPosition = trackedPosition;

            ChunkCoord targetCenter = ChunkCoord.FromWorldPosition(trackedPosition.x, trackedPosition.z, _chunkSize);
            if (!_hasInitialSync || targetCenter != _currentCenterChunk)
            {
                bool isFirstSync = !_hasInitialSync;
                _currentCenterChunk = targetCenter;
                _hasInitialSync = true;

                RefreshStreamingWindow(targetCenter, moveDir);

                if (isFirstSync)
                {
                    // Synchronously populate the initial 3x3 grid on frame 1 so the player never starts in void
                    FlushAllPendingLoads();
                    return;
                }
            }

            ProcessPendingLoads(_maxChunkLoadsPerTick);
        }

        public void Shutdown()
        {
            _pendingLoadList.Clear();
            _pendingSet.Clear();
            _desiredCoords.Clear();
            _unloadBuffer.Clear();

            foreach (KeyValuePair<ChunkCoord, WorldChunk> pair in _activeChunks)
            {
                OnChunkUnloadingChunk?.Invoke(pair.Value);
                _mapDataLoader.ClearChunk(pair.Value);
                _chunkPool.Release(pair.Value);
            }

            _activeChunks.Clear();
            _mapDataLoader.Dispose();
            _chunkPool?.Dispose();
            _chunkPool = null;
            _trackingTarget = null;
            _cachedFallbackCamera = null;
        }

        /// <summary>
        /// Assigns the Player transform whose XZ coordinates drive chunk loading and unloading.
        /// </summary>
        public void SetTrackingTarget(Transform target)
        {
            _trackingTarget = target;
            _useManualPosition = false;
        }

        /// <summary>
        /// Updates the tracked world position directly when driving player state from pure C#.
        /// </summary>
        public void SetTrackingPosition(Vector3 worldPosition)
        {
            _manualTrackingPosition = worldPosition;
            _useManualPosition = true;
        }

        /// <summary>
        /// Retrieves a currently active chunk if loaded.
        /// </summary>
        public bool TryGetActiveChunk(ChunkCoord coord, out WorldChunk chunk)
        {
            return _activeChunks.TryGetValue(coord, out chunk);
        }

        private void RefreshStreamingWindow(ChunkCoord center, Vector2 moveDir)
        {
            _desiredCoords.Clear();
            for (int dz = -_loadRadius; dz <= _loadRadius; dz++)
            {
                for (int dx = -_loadRadius; dx <= _loadRadius; dx++)
                {
                    _desiredCoords.Add(new ChunkCoord(center.X + dx, center.Z + dz));
                }
            }

            // 1. Unload chunks behind FIRST so their pooled roots and prop GameObjects
            //    are immediately available for reuse by the incoming chunks ahead.
            _unloadBuffer.Clear();
            foreach (KeyValuePair<ChunkCoord, WorldChunk> pair in _activeChunks)
            {
                if (!_desiredCoords.Contains(pair.Key))
                {
                    _unloadBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < _unloadBuffer.Count; i++)
            {
                UnloadChunk(_unloadBuffer[i]);
            }

            // Prune any queued chunks that fell outside the desired radius before loading
            for (int i = _pendingLoadList.Count - 1; i >= 0; i--)
            {
                ChunkCoord pending = _pendingLoadList[i];
                if (!_desiredCoords.Contains(pending))
                {
                    _pendingLoadList.RemoveAt(i);
                    _pendingSet.Remove(pending);
                }
            }

            // 2. Queue newly uncovered chunks ahead
            bool addedAny = false;
            foreach (ChunkCoord coord in _desiredCoords)
            {
                if (!_activeChunks.ContainsKey(coord) && _pendingSet.Add(coord))
                {
                    _pendingLoadList.Add(coord);
                    addedAny = true;
                }
            }

            if (addedAny && _pendingLoadList.Count > 1)
            {
                _priorityComparer.Center = center;
                _priorityComparer.MoveDirX = moveDir.x;
                _priorityComparer.MoveDirZ = moveDir.y;
                _pendingLoadList.Sort(_priorityComparer);
            }
        }

        private void ProcessPendingLoads(int maxCount)
        {
            int loadedThisTick = 0;
            while (_pendingLoadList.Count > 0 && loadedThisTick < maxCount)
            {
                int tailIndex = _pendingLoadList.Count - 1;
                ChunkCoord nextCoord = _pendingLoadList[tailIndex];
                _pendingLoadList.RemoveAt(tailIndex);
                _pendingSet.Remove(nextCoord);

                if (_desiredCoords.Contains(nextCoord) && !_activeChunks.ContainsKey(nextCoord))
                {
                    LoadChunk(nextCoord);
                    loadedThisTick++;
                }
            }
        }

        private void FlushAllPendingLoads()
        {
            ProcessPendingLoads(int.MaxValue);
        }

        private void LoadChunk(ChunkCoord coord)
        {
            WorldChunk chunk = _chunkPool.Acquire(coord);
            _mapDataLoader.PopulateChunk(chunk);
            _activeChunks[coord] = chunk;
            OnChunkLoadedChunk?.Invoke(chunk);
            OnChunkLoaded?.Invoke(coord);
        }

        private void UnloadChunk(ChunkCoord coord)
        {
            if (!_activeChunks.TryGetValue(coord, out WorldChunk chunk))
            {
                return;
            }

            _activeChunks.Remove(coord);
            OnChunkUnloadingChunk?.Invoke(chunk);
            _mapDataLoader.ClearChunk(chunk);
            _chunkPool.Release(chunk);
            OnChunkUnloaded?.Invoke(coord);
        }

        private Vector3 ResolveTrackedPosition(float deltaTime)
        {
            if (_trackingTarget != null)
            {
                return _trackingTarget.position;
            }

            if (_useManualPosition)
            {
                return _manualTrackingPosition;
            }

            // Periodically search for a "Player"-tagged GameObject without per-frame Find overhead
            _targetLookupTimer -= deltaTime;
            if (_targetLookupTimer <= 0f)
            {
                _targetLookupTimer = TargetLookupIntervalSeconds;
                GameObject playerObj = GameObject.FindGameObjectWithTag("Player");
                if (playerObj != null)
                {
                    _trackingTarget = playerObj.transform;
                    return _trackingTarget.position;
                }

                if (_cachedFallbackCamera == null)
                {
                    _cachedFallbackCamera = Camera.main;
                }
            }

            if (_cachedFallbackCamera != null)
            {
                return _cachedFallbackCamera.transform.position;
            }

            return Vector3.zero;
        }
    }
}
