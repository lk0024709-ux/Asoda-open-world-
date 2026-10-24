using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Asoda.World
{
    /// <summary>
    /// Cached wrapper around a pooled prop GameObject and its renderers so chunk streaming
    /// never calls GetComponentsInChildren or allocates memory during gameplay.
    /// </summary>
    public sealed class PooledPropInstance
    {
        public readonly ushort AssetTypeId;
        public readonly GameObject GameObject;
        public readonly Transform Transform;
        public readonly MeshRenderer[] Renderers;

        public PooledPropInstance(ushort assetTypeId, GameObject gameObject, MeshRenderer[] renderers)
        {
            AssetTypeId = assetTypeId;
            GameObject = gameObject;
            Transform = gameObject.transform;
            Renderers = renderers;
        }
    }

    /// <summary>
    /// Represents a single reusable 50x50 world chunk container managed by <see cref="ChunkPool"/>.
    /// </summary>
    public sealed class WorldChunk
    {
        public readonly int PoolId;
        public readonly GameObject RootGameObject;
        public readonly Transform RootTransform;
        public readonly MeshRenderer GroundRenderer;
        public readonly List<PooledPropInstance> SpawnedProps = new List<PooledPropInstance>(64);

        public ChunkCoord Coord { get; private set; }
        public ChunkBounds Bounds { get; private set; }
        public bool IsActive { get; private set; }

        public WorldChunk(int poolId, GameObject rootGameObject, MeshRenderer groundRenderer)
        {
            PoolId = poolId;
            RootGameObject = rootGameObject;
            RootTransform = rootGameObject.transform;
            GroundRenderer = groundRenderer;
        }

        internal void Activate(ChunkCoord coord, float chunkSize, Transform activeParent, MaterialPropertyBlock propertyBlock, int baseColorId, int legacyColorId)
        {
            Coord = coord;
            Bounds = coord.GetBounds(chunkSize);
            IsActive = true;

            RootTransform.SetParent(activeParent, false);
            RootTransform.localPosition = coord.GetWorldOrigin(chunkSize);
            RootTransform.localRotation = Quaternion.identity;
            RootTransform.localScale = Vector3.one;

#if UNITY_EDITOR
            RootGameObject.name = $"Chunk_({coord.X},{coord.Z})";
#endif

            if (GroundRenderer != null && propertyBlock != null)
            {
                // Subtle deterministic dry-earth / rural grass variation per chunk using GPU Instancing
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
            SpawnedProps.Clear();
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
    /// Reuses chunk containers across coordinate transitions to eliminate Instantiate/Destroy GC spikes on Android.
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

        /// <summary>
        /// Pre-allocates chunk root GameObjects at startup so runtime chunk streaming never allocates chunk roots.
        /// </summary>
        public void Prewarm(int targetCount)
        {
            while (_allChunks.Count < targetCount)
            {
                WorldChunk chunk = CreatePooledChunk();
                _availableChunks.Push(chunk);
            }
        }

        /// <summary>
        /// Acquires a pooled chunk root, positions it at the target grid coordinate, and activates it.
        /// </summary>
        public WorldChunk Acquire(ChunkCoord coord)
        {
            WorldChunk chunk = _availableChunks.Count > 0 ? _availableChunks.Pop() : CreatePooledChunk();
            chunk.Activate(coord, _chunkSize, _activeChunksParent, _groundPropertyBlock, BaseColorPropertyId, LegacyColorPropertyId);
            return chunk;
        }

        /// <summary>
        /// Deactivates a chunk root and returns it to the reusable pool without destroying its GameObject.
        /// </summary>
        public void Release(WorldChunk chunk)
        {
            if (chunk == null || !chunk.IsActive)
            {
                return;
            }

            chunk.Deactivate(_pooledChunksParent);
            _availableChunks.Push(chunk);
        }

        /// <summary>
        /// Releases native Unity objects only when the entire game shuts down.
        /// </summary>
        public void Dispose()
        {
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
                // Shared 2-triangle XZ mesh constructed directly without Instantiate or temporary colliders
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

            rootGo.SetActive(false);

            var chunk = new WorldChunk(poolId, rootGo, groundRenderer);
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
                Vector3.up,
                Vector3.up,
                Vector3.up,
                Vector3.up
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

            // Reduce specular glare for stylized low-poly look across URP and Built-in RP
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

            // Ultimate build-safe fallback: extract Unity's built-in primitive shader
            GameObject tempPrimitive = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Shader fallbackShader = tempPrimitive.GetComponent<MeshRenderer>().sharedMaterial.shader;
            Object.Destroy(tempPrimitive);
            return fallbackShader;
        }
    }
}
