using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.World
{
    /// <summary>
    /// Cached wrapper around a pooled prop GameObject, its tintable renderers, and its static-batchable
    /// mesh components so chunk streaming and <see cref="StaticMeshCombiner"/> never call GetComponent during gameplay.
    /// </summary>
    public sealed class PooledPropInstance
    {
        public readonly ushort AssetTypeId;
        public readonly GameObject GameObject;
        public readonly Transform Transform;
        public readonly MeshRenderer[] Renderers;
        public readonly bool IsStaticBatchable;
        public readonly MeshFilter[] StaticMeshFilters;
        public readonly MeshRenderer[] StaticMeshRenderers;

        public PooledPropInstance(
            ushort assetTypeId,
            GameObject gameObject,
            MeshRenderer[] tintRenderers,
            bool isStaticBatchable,
            MeshFilter[] staticMeshFilters,
            MeshRenderer[] staticMeshRenderers)
        {
            AssetTypeId = assetTypeId;
            GameObject = gameObject;
            Transform = gameObject.transform;
            Renderers = tintRenderers ?? Array.Empty<MeshRenderer>();
            IsStaticBatchable = isStaticBatchable;
            StaticMeshFilters = staticMeshFilters ?? Array.Empty<MeshFilter>();
            StaticMeshRenderers = staticMeshRenderers ?? Array.Empty<MeshRenderer>();
        }

        /// <summary>
        /// Re-enables individual MeshRenderers that were hidden while merged into a chunk's combined static mesh.
        /// </summary>
        public void RestoreStaticRenderers()
        {
            for (int i = 0; i < StaticMeshRenderers.Length; i++)
            {
                MeshRenderer r = StaticMeshRenderers[i];
                if (r != null && !r.enabled)
                {
                    r.enabled = true;
                }
            }
        }
    }

    /// <summary>
    /// Represents a single reusable 50x50 world chunk container managed by <see cref="ChunkPool"/>.
    /// Includes a persistent combined static mesh slot for <see cref="StaticMeshCombiner"/>.
    /// </summary>
    public sealed class WorldChunk
    {
        public readonly int PoolId;
        public readonly GameObject RootGameObject;
        public readonly Transform RootTransform;
        public readonly MeshRenderer GroundRenderer;
        public readonly MeshFilter CombinedStaticMeshFilter;
        public readonly MeshRenderer CombinedStaticMeshRenderer;
        public readonly Mesh CombinedStaticMesh;
        public readonly List<PooledPropInstance> SpawnedProps = new List<PooledPropInstance>(64);

        public ChunkCoord Coord { get; private set; }
        public ChunkBounds Bounds { get; private set; }
        public bool IsActive { get; private set; }
        public bool HasCombinedStaticMesh { get; internal set; }

        public WorldChunk(
            int poolId,
            GameObject rootGameObject,
            MeshRenderer groundRenderer,
            MeshFilter combinedMeshFilter,
            MeshRenderer combinedMeshRenderer,
            Mesh combinedMesh)
        {
            PoolId = poolId;
            RootGameObject = rootGameObject;
            RootTransform = rootGameObject.transform;
            GroundRenderer = groundRenderer;
            CombinedStaticMeshFilter = combinedMeshFilter;
            CombinedStaticMeshRenderer = combinedMeshRenderer;
            CombinedStaticMesh = combinedMesh;
        }

        internal void Activate(ChunkCoord coord, float chunkSize, Transform activeParent, MaterialPropertyBlock propertyBlock, int baseColorId, int legacyColorId)
        {
            Coord = coord;
            Bounds = coord.GetBounds(chunkSize);
            IsActive = true;
            HasCombinedStaticMesh = false;

            if (CombinedStaticMeshRenderer != null)
            {
                CombinedStaticMeshRenderer.enabled = false;
            }

            RootTransform.SetParent(activeParent, false);
            RootTransform.localPosition = coord.GetWorldOrigin(chunkSize);
            RootTransform.localRotation = Quaternion.identity;
            RootTransform.localScale = Vector3.one;

#if UNITY_EDITOR
            RootGameObject.name = $"Chunk_({coord.X},{coord.Z})";
#endif

            if (GroundRenderer != null && propertyBlock != null)
            {
                int hash = unchecked((coord.X * 92837111) ^ (coord.Z * 689287499));
                float t = ((hash & 0xFF) / 255f);
                Color groundTint = Color.Lerp(
                    new Color(0.74f, 0.66f, 0.48f, 1f),
                    new Color(0.58f, 0.64f, 0.43f, 1f),
                    t);

                propertyBlock.Clear();
                propertyBlock.SetColor(baseColorId, groundTint);
                propertyBlock.SetColor(legacyColorId, groundTint);
                GroundRenderer.SetPropertyBlock(propertyBlock);
            }

            RootGameObject.SetActive(true);
        }

        internal void Deactivate(Transform poolParent)
        {
            IsActive = false;
            HasCombinedStaticMesh = false;
            SpawnedProps.Clear();

            if (CombinedStaticMesh != null)
            {
                CombinedStaticMesh.Clear(false);
            }

            if (CombinedStaticMeshRenderer != null)
            {
                CombinedStaticMeshRenderer.enabled = false;
            }

            if (RootGameObject != null)
            {
                RootGameObject.SetActive(false);
            }

            if (RootTransform != null && poolParent != null)
            {
                RootTransform.SetParent(poolParent, false);
            }
        }
    }

    /// <summary>
    /// Pre-warmed object pool for chunk root GameObjects.
    /// Reuses chunk containers and combined static meshes across coordinate transitions to prevent GC spikes on mobile.
    /// </summary>
    public sealed class ChunkPool
    {
        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorPropertyId = Shader.PropertyToID("_Color");

        private readonly Stack<WorldChunk> _availableChunks;
        private readonly List<WorldChunk> _allChunks;
        private readonly Transform _worldRootTransform;
        private readonly Transform _activeChunksParent;
        private readonly Transform _pooledChunksParent;
        private readonly float _chunkSize;
        private readonly bool _createGroundTiles;
        private readonly Mesh _sharedGroundMesh;
        private readonly Material _sharedGroundMaterial;
        private readonly MaterialPropertyBlock _groundPropertyBlock;

        private int _nextPoolId;

        public Transform WorldRootTransform => _worldRootTransform;
        public int AvailableCount => _availableChunks.Count;
        public int TotalCreatedCount => _allChunks.Count;
        public int ActiveCount => _allChunks.Count - _availableChunks.Count;

        public ChunkPool(float chunkSize, int initialCapacity, bool createGroundTiles = true)
        {
            _chunkSize = chunkSize;
            _createGroundTiles = createGroundTiles;
            _availableChunks = new Stack<WorldChunk>(initialCapacity);
            _allChunks = new List<WorldChunk>(initialCapacity);
            _groundPropertyBlock = new MaterialPropertyBlock();

            var worldRoot = new GameObject("[WorldStreamingRoot]");
            Object.DontDestroyOnLoad(worldRoot);
            _worldRootTransform = worldRoot.transform;

            var activeGo = new GameObject("ActiveChunks");
            _activeChunksParent = activeGo.transform;
            _activeChunksParent.SetParent(_worldRootTransform, false);

            var pooledGo = new GameObject("PooledChunks");
            _pooledChunksParent = pooledGo.transform;
            _pooledChunksParent.SetParent(_worldRootTransform, false);

            if (_createGroundTiles)
            {
                _sharedGroundMesh = CreateHorizontalQuadMesh(_chunkSize);
                _sharedGroundMaterial = CreateInstancedSurfaceMaterial("Asoda_ChunkGround_Mat", new Color(0.68f, 0.65f, 0.46f, 1f));
            }

            Prewarm(initialCapacity);
        }

        public void Prewarm(int targetCount)
        {
            while (_allChunks.Count < targetCount)
            {
                WorldChunk chunk = CreatePooledChunk();
                _availableChunks.Push(chunk);
            }
        }

        public WorldChunk Acquire(ChunkCoord coord)
        {
            WorldChunk chunk = _availableChunks.Count > 0 ? _availableChunks.Pop() : CreatePooledChunk();
            chunk.Activate(coord, _chunkSize, _activeChunksParent, _groundPropertyBlock, BaseColorPropertyId, LegacyColorPropertyId);
            return chunk;
        }

        public void Release(WorldChunk chunk)
        {
            if (chunk == null || !chunk.IsActive)
            {
                return;
            }

            chunk.Deactivate(_pooledChunksParent);
            _availableChunks.Push(chunk);
        }

        public void Dispose()
        {
            for (int i = 0; i < _allChunks.Count; i++)
            {
                if (_allChunks[i].CombinedStaticMesh != null)
                {
                    Object.Destroy(_allChunks[i].CombinedStaticMesh);
                }
            }

            _availableChunks.Clear();
            _allChunks.Clear();

            if (_worldRootTransform != null)
            {
                Object.Destroy(_worldRootTransform.gameObject);
            }

            if (_sharedGroundMaterial != null)
            {
                Object.Destroy(_sharedGroundMaterial);
            }

            if (_sharedGroundMesh != null)
            {
                Object.Destroy(_sharedGroundMesh);
            }
        }

        private WorldChunk CreatePooledChunk()
        {
            int poolId = _nextPoolId++;
            var rootGo = new GameObject($"ChunkRoot_{poolId:D2}");
            Transform rootTransform = rootGo.transform;
            rootTransform.SetParent(_pooledChunksParent, false);

            // Solid lightweight physics floor collider across the 50x50 chunk
            var groundCollider = rootGo.AddComponent<BoxCollider>();
            float halfSize = _chunkSize * 0.5f;
            groundCollider.center = new Vector3(halfSize, -0.25f, halfSize);
            groundCollider.size = new Vector3(_chunkSize, 0.5f, _chunkSize);

            MeshRenderer groundRenderer = null;
            if (_createGroundTiles)
            {
                var groundGo = new GameObject("GroundTile");
                Transform groundTransform = groundGo.transform;
                groundTransform.SetParent(rootTransform, false);
                groundTransform.localPosition = new Vector3(0f, -0.02f, 0f);

                var meshFilter = groundGo.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = _sharedGroundMesh;

                groundRenderer = groundGo.AddComponent<MeshRenderer>();
                groundRenderer.sharedMaterial = _sharedGroundMaterial;
                groundRenderer.shadowCastingMode = ShadowCastingMode.Off;
                groundRenderer.receiveShadows = true;
            }

            // Persistent combined static mesh holder per chunk (reused across chunk unloads/loads)
            var combinedGo = new GameObject("CombinedStaticMesh");
            combinedGo.transform.SetParent(rootTransform, false);

            var combinedMesh = new Mesh
            {
                name = $"ChunkCombinedStatic_{poolId:D2}"
            };
            combinedMesh.MarkDynamic();

            var combinedFilter = combinedGo.AddComponent<MeshFilter>();
            combinedFilter.sharedMesh = combinedMesh;

            var combinedRenderer = combinedGo.AddComponent<MeshRenderer>();
            combinedRenderer.shadowCastingMode = ShadowCastingMode.On;
            combinedRenderer.receiveShadows = true;
            combinedRenderer.enabled = false;

            rootGo.SetActive(false);

            var chunk = new WorldChunk(poolId, rootGo, groundRenderer, combinedFilter, combinedRenderer, combinedMesh);
            _allChunks.Add(chunk);
            return chunk;
        }

        internal static Mesh CreateHorizontalQuadMesh(float size)
        {
            var mesh = new Mesh
            {
                name = $"QuadXZ_{size:F0}"
            };

            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 0f, size),
                new Vector3(size, 0f, size),
                new Vector3(size, 0f, 0f)
            };

            mesh.normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up
            };

            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f)
            };

            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Builds a guaranteed CPU-readable (isReadable == true) 24-vertex unit box mesh [-0.5..0.5]^3
        /// so Mesh.CombineMeshes always succeeds in stripped Android IL2CPP builds.
        /// </summary>
        internal static Mesh CreateReadableUnitBoxMesh()
        {
            var mesh = new Mesh
            {
                name = "Asoda_ReadableUnitBox"
            };

            mesh.vertices = new[]
            {
                // Top (+Y)
                new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(-0.5f,  0.5f,  0.5f),
                new Vector3( 0.5f,  0.5f,  0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
                // Bottom (-Y)
                new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
                // Back (-Z)
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f,  0.5f, -0.5f),
                new Vector3( 0.5f,  0.5f, -0.5f), new Vector3( 0.5f, -0.5f, -0.5f),
                // Front (+Z)
                new Vector3( 0.5f, -0.5f,  0.5f), new Vector3( 0.5f,  0.5f,  0.5f),
                new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(-0.5f, -0.5f,  0.5f),
                // Right (+X)
                new Vector3( 0.5f, -0.5f, -0.5f), new Vector3( 0.5f,  0.5f, -0.5f),
                new Vector3( 0.5f,  0.5f,  0.5f), new Vector3( 0.5f, -0.5f,  0.5f),
                // Left (-X)
                new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(-0.5f,  0.5f,  0.5f),
                new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f)
            };

            mesh.normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up,
                Vector3.down, Vector3.down, Vector3.down, Vector3.down,
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
                Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
                Vector3.right, Vector3.right, Vector3.right, Vector3.right,
                Vector3.left, Vector3.left, Vector3.left, Vector3.left
            };

            var uvs = new Vector2[24];
            for (int f = 0; f < 6; f++)
            {
                int b = f * 4;
                uvs[b + 0] = new Vector2(0f, 0f);
                uvs[b + 1] = new Vector2(0f, 1f);
                uvs[b + 2] = new Vector2(1f, 1f);
                uvs[b + 3] = new Vector2(1f, 0f);
            }
            mesh.uv = uvs;

            var tris = new int[36];
            for (int f = 0; f < 6; f++)
            {
                int b = f * 4;
                int t = f * 6;
                tris[t + 0] = b + 0;
                tris[t + 1] = b + 1;
                tris[t + 2] = b + 2;
                tris[t + 3] = b + 0;
                tris[t + 4] = b + 2;
                tris[t + 5] = b + 3;
            }
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        internal static Material CreateInstancedSurfaceMaterial(string materialName, Color baseColor)
        {
            Shader shader = ResolvePipelineLitShader();
            var material = new Material(shader)
            {
                name = materialName,
                enableInstancing = true,
                color = baseColor
            };

            if (material.HasProperty(BaseColorPropertyId))
            {
                material.SetColor(BaseColorPropertyId, baseColor);
            }

            if (material.HasProperty(LegacyColorPropertyId))
            {
                material.SetColor(LegacyColorPropertyId, baseColor);
            }

            int smoothnessId = Shader.PropertyToID("_Smoothness");
            if (material.HasProperty(smoothnessId))
            {
                material.SetFloat(smoothnessId, 0.1f);
            }

            int glossinessId = Shader.PropertyToID("_Glossiness");
            if (material.HasProperty(glossinessId))
            {
                material.SetFloat(glossinessId, 0.1f);
            }

            return material;
        }

        internal static Shader ResolvePipelineLitShader()
        {
            RenderPipelineAsset activePipeline = GraphicsSettings.currentRenderPipeline;
            if (activePipeline != null && activePipeline.defaultMaterial != null && activePipeline.defaultMaterial.shader != null)
            {
                return activePipeline.defaultMaterial.shader;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (shader != null)
            {
                return shader;
            }

            shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
            {
                return shader;
            }

            shader = Shader.Find("Standard");
            if (shader != null)
            {
                return shader;
            }

            GameObject tempPrimitive = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Shader fallbackShader = tempPrimitive.GetComponent<MeshRenderer>().sharedMaterial.shader;
            Object.Destroy(tempPrimitive);
            return fallbackShader;
        }
    }
}
