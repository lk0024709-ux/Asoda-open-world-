using System.Collections.Generic;
using Asoda.Core;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.World
{
    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> that merges static chunk geometry
    /// (village houses, buildings, mandirs, and roads) inside each loaded 50x50m chunk into a single unified
    /// <see cref="Mesh"/> using <see cref="Mesh.CombineMeshes(CombineInstance[], bool, bool)"/>.
    /// Keeps individual physics colliders active while disabling individual MeshRenderers to slash draw calls on Android.
    /// </summary>
    public sealed class StaticMeshCombiner : IGameSystem, ITickable
    {
        public const int DefaultMaxChunkCombinesPerTick = 2;
        private const int UInt16MaxVertices = 65535;

        private sealed class MaterialCombineBucket
        {
            public Material Material;
            public readonly List<CombineInstance> Instances = new List<CombineInstance>(64);

            public void Reset()
            {
                Material = null;
                Instances.Clear();
            }
        }

        private readonly WorldChunkManager _chunkManager;
        private readonly int _maxCombinesPerTick;

        private readonly List<WorldChunk> _pendingChunks = new List<WorldChunk>(16);
        private readonly HashSet<int> _queuedChunkPoolIds = new HashSet<int>();
        private readonly List<MaterialCombineBucket> _bucketPool = new List<MaterialCombineBucket>(8);
        private readonly Dictionary<Material, int> _materialToBucketIndex = new Dictionary<Material, int>(8);
        private readonly List<MeshRenderer> _sourceRenderersBuffer = new List<MeshRenderer>(128);
        private readonly List<Mesh> _scratchSubMeshes = new List<Mesh>(8);
        private readonly Dictionary<int, Material[]> _materialArrayCache = new Dictionary<int, Material[]>(8);

        private bool _initialBatchFlushed;
        private int _combinedChunkCount;
        private int _mergedRendererCount;

        public int CombinedChunkCount => _combinedChunkCount;
        public int MergedRendererCount => _mergedRendererCount;

        public StaticMeshCombiner(WorldChunkManager chunkManager, int maxCombinesPerTick = DefaultMaxChunkCombinesPerTick)
        {
            _chunkManager = chunkManager;
            _maxCombinesPerTick = Mathf.Max(1, maxCombinesPerTick);
        }

        public void Initialize()
        {
            _initialBatchFlushed = false;
            _combinedChunkCount = 0;
            _mergedRendererCount = 0;

            if (_chunkManager != null)
            {
                _chunkManager.OnChunkLoadedChunk += HandleChunkLoaded;
                _chunkManager.OnChunkUnloadingChunk += HandleChunkUnloading;
            }
        }

        public void Tick(float deltaTime)
        {
            if (_pendingChunks.Count == 0)
            {
                _initialBatchFlushed = true;
                return;
            }

            // Combine all initial startup chunks on Frame 1; thereafter budget per tick for smooth 60 FPS streaming
            int budget = !_initialBatchFlushed ? int.MaxValue : _maxCombinesPerTick;
            _initialBatchFlushed = true;

            int processed = 0;
            while (_pendingChunks.Count > 0 && processed < budget)
            {
                int lastIdx = _pendingChunks.Count - 1;
                WorldChunk chunk = _pendingChunks[lastIdx];
                _pendingChunks.RemoveAt(lastIdx);
                _queuedChunkPoolIds.Remove(chunk.PoolId);

                if (chunk.IsActive && !chunk.HasCombinedStaticMesh)
                {
                    CombineChunkStaticMeshes(chunk);
                    processed++;
                }
            }
        }

        public void Shutdown()
        {
            if (_chunkManager != null)
            {
                _chunkManager.OnChunkLoadedChunk -= HandleChunkLoaded;
                _chunkManager.OnChunkUnloadingChunk -= HandleChunkUnloading;
            }

            _pendingChunks.Clear();
            _queuedChunkPoolIds.Clear();
            _materialToBucketIndex.Clear();
            _sourceRenderersBuffer.Clear();
            _materialArrayCache.Clear();

            for (int i = 0; i < _scratchSubMeshes.Count; i++)
            {
                if (_scratchSubMeshes[i] != null)
                {
                    Object.Destroy(_scratchSubMeshes[i]);
                }
            }

            _scratchSubMeshes.Clear();
            _bucketPool.Clear();
        }

        /// <summary>
        /// Immediately combines all static batchable meshes (houses, buildings, mandirs, roads) inside <paramref name="chunk"/>
        /// into the chunk's reusable <see cref="WorldChunk.CombinedStaticMesh"/>.
        /// </summary>
        public int CombineChunkStaticMeshes(WorldChunk chunk)
        {
            if (chunk == null || !chunk.IsActive || chunk.CombinedStaticMesh == null || chunk.CombinedStaticMeshRenderer == null)
            {
                return 0;
            }

            ResetBuckets();
            _sourceRenderersBuffer.Clear();

            Matrix4x4 chunkWorldToLocal = chunk.RootTransform.worldToLocalMatrix;
            List<PooledPropInstance> props = chunk.SpawnedProps;
            int totalVertices = 0;
            int activeBucketCount = 0;

            for (int i = 0; i < props.Count; i++)
            {
                PooledPropInstance prop = props[i];
                if (prop == null || !prop.IsStaticBatchable)
                {
                    continue;
                }

                MeshFilter[] filters = prop.StaticMeshFilters;
                MeshRenderer[] renderers = prop.StaticMeshRenderers;
                int partCount = Mathf.Min(filters.Length, renderers.Length);

                for (int p = 0; p < partCount; p++)
                {
                    MeshFilter mf = filters[p];
                    MeshRenderer mr = renderers[p];
                    if (mf == null || mr == null || !mr.enabled)
                    {
                        continue;
                    }

                    Mesh sharedMesh = mf.sharedMesh;
                    if (sharedMesh == null || !sharedMesh.isReadable || sharedMesh.vertexCount == 0)
                    {
                        // Non-readable meshes gracefully remain rendered via GPU Instancing without errors
                        continue;
                    }

                    Material[] sharedMats = mr.sharedMaterials;
                    int subMeshCount = Mathf.Min(sharedMesh.subMeshCount, sharedMats.Length);
                    if (subMeshCount == 0)
                    {
                        continue;
                    }

                    Matrix4x4 localToChunk = chunkWorldToLocal * mf.transform.localToWorldMatrix;
                    bool addedAnySubMesh = false;

                    for (int s = 0; s < subMeshCount; s++)
                    {
                        Material mat = sharedMats[s];
                        if (mat == null)
                        {
                            continue;
                        }

                        MaterialCombineBucket bucket = GetOrAllocateBucket(mat, ref activeBucketCount);
                        bucket.Instances.Add(new CombineInstance
                        {
                            mesh = sharedMesh,
                            subMeshIndex = s,
                            transform = localToChunk
                        });
                        addedAnySubMesh = true;
                    }

                    if (addedAnySubMesh)
                    {
                        totalVertices += sharedMesh.vertexCount;
                        _sourceRenderersBuffer.Add(mr);
                    }
                }
            }

            // Combining fewer than 2 renderers saves zero draw calls
            if (_sourceRenderersBuffer.Count < 2 || activeBucketCount == 0)
            {
                return 0;
            }

            Mesh targetMesh = chunk.CombinedStaticMesh;
            targetMesh.Clear(false);

            // Use mobile-optimal 16-bit indices whenever chunk vertices fit <= 65,535; switch to 32-bit only on dense blocks
            IndexFormat indexFormat = totalVertices > UInt16MaxVertices ? IndexFormat.UInt32 : IndexFormat.UInt16;
            targetMesh.indexFormat = indexFormat;

            if (activeBucketCount == 1)
            {
                MaterialCombineBucket onlyBucket = _bucketPool[0];
                targetMesh.CombineMeshes(onlyBucket.Instances.ToArray(), mergeSubMeshes: true, useMatrices: true);

                Material[] singleMatArray = GetCachedMaterialArray(1);
                singleMatArray[0] = onlyBucket.Material;
                chunk.CombinedStaticMeshRenderer.sharedMaterials = singleMatArray;
            }
            else
            {
                EnsureScratchSubMeshes(activeBucketCount);
                var subMeshMerges = new CombineInstance[activeBucketCount];
                Material[] multiMatArray = GetCachedMaterialArray(activeBucketCount);

                for (int b = 0; b < activeBucketCount; b++)
                {
                    MaterialCombineBucket bucket = _bucketPool[b];
                    Mesh scratch = _scratchSubMeshes[b];
                    scratch.Clear(false);
                    scratch.indexFormat = indexFormat;
                    scratch.CombineMeshes(bucket.Instances.ToArray(), mergeSubMeshes: true, useMatrices: true);

                    subMeshMerges[b] = new CombineInstance
                    {
                        mesh = scratch,
                        subMeshIndex = 0,
                        transform = Matrix4x4.identity
                    };
                    multiMatArray[b] = bucket.Material;
                }

                targetMesh.CombineMeshes(subMeshMerges, mergeSubMeshes: false, useMatrices: false);
                chunk.CombinedStaticMeshRenderer.sharedMaterials = multiMatArray;
            }

            targetMesh.RecalculateBounds();
            chunk.CombinedStaticMeshRenderer.enabled = true;
            chunk.HasCombinedStaticMesh = true;

            // Hide individual static MeshRenderers while keeping all physics BoxColliders/MeshColliders active!
            int mergedCount = _sourceRenderersBuffer.Count;
            for (int i = 0; i < mergedCount; i++)
            {
                _sourceRenderersBuffer[i].enabled = false;
            }

            _combinedChunkCount++;
            _mergedRendererCount += mergedCount;
            return mergedCount;
        }

        /// <summary>
        /// Restores individual static prop MeshRenderers and resets the chunk's combined mesh prior to pool recycling.
        /// </summary>
        public void ReleaseChunkCombinedMesh(WorldChunk chunk)
        {
            if (chunk == null)
            {
                return;
            }

            List<PooledPropInstance> props = chunk.SpawnedProps;
            for (int i = 0; i < props.Count; i++)
            {
                PooledPropInstance prop = props[i];
                if (prop != null && prop.IsStaticBatchable)
                {
                    prop.RestoreStaticRenderers();
                }
            }

            if (chunk.HasCombinedStaticMesh)
            {
                chunk.HasCombinedStaticMesh = false;
                if (_combinedChunkCount > 0)
                {
                    _combinedChunkCount--;
                }
            }

            if (chunk.CombinedStaticMeshRenderer != null)
            {
                chunk.CombinedStaticMeshRenderer.enabled = false;
            }

            if (chunk.CombinedStaticMesh != null)
            {
                chunk.CombinedStaticMesh.Clear(false);
            }
        }

        private void HandleChunkLoaded(WorldChunk chunk)
        {
            if (chunk == null || !chunk.IsActive)
            {
                return;
            }

            if (_queuedChunkPoolIds.Add(chunk.PoolId))
            {
                _pendingChunks.Add(chunk);
            }
        }

        private void HandleChunkUnloading(WorldChunk chunk)
        {
            if (chunk == null)
            {
                return;
            }

            if (_queuedChunkPoolIds.Remove(chunk.PoolId))
            {
                for (int i = _pendingChunks.Count - 1; i >= 0; i--)
                {
                    if (_pendingChunks[i].PoolId == chunk.PoolId)
                    {
                        _pendingChunks.RemoveAt(i);
                        break;
                    }
                }
            }

            ReleaseChunkCombinedMesh(chunk);
        }

        private MaterialCombineBucket GetOrAllocateBucket(Material material, ref int activeBucketCount)
        {
            if (_materialToBucketIndex.TryGetValue(material, out int existingIndex))
            {
                return _bucketPool[existingIndex];
            }

            int index = activeBucketCount++;
            if (index >= _bucketPool.Count)
            {
                _bucketPool.Add(new MaterialCombineBucket());
            }

            MaterialCombineBucket bucket = _bucketPool[index];
            bucket.Reset();
            bucket.Material = material;
            _materialToBucketIndex[material] = index;
            return bucket;
        }

        private void ResetBuckets()
        {
            for (int i = 0; i < _bucketPool.Count; i++)
            {
                _bucketPool[i].Reset();
            }

            _materialToBucketIndex.Clear();
        }

        private void EnsureScratchSubMeshes(int requiredCount)
        {
            while (_scratchSubMeshes.Count < requiredCount)
            {
                var scratch = new Mesh
                {
                    name = $"CombinerScratchSubMesh_{_scratchSubMeshes.Count}"
                };
                scratch.MarkDynamic();
                _scratchSubMeshes.Add(scratch);
            }
        }

        private Material[] GetCachedMaterialArray(int length)
        {
            if (!_materialArrayCache.TryGetValue(length, out Material[] array))
            {
                array = new Material[length];
                _materialArrayCache[length] = array;
            }

            return array;
        }
    }
}
