using System;
using System.Collections.Generic;
using System.IO;
using Asoda.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.World
{
    /// <summary>
    /// Categories of world entities displayed as colored UI dots on the circular minimap.
    /// </summary>
    public enum MinimapBlipCategory : byte
    {
        Building = 0,
        Mandir = 1,
        Vehicle = 2,
        Mission = 3
    }

    /// <summary>
    /// Lightweight value-type blip entry for zero-allocation minimap rendering.
    /// </summary>
    public readonly struct MinimapBlipData
    {
        public readonly float WorldX;
        public readonly float WorldZ;
        public readonly MinimapBlipCategory Category;

        public Vector3 WorldPosition => new Vector3(WorldX, 0f, WorldZ);

        public MinimapBlipData(float worldX, float worldZ, MinimapBlipCategory category)
        {
            WorldX = worldX;
            WorldZ = worldZ;
            Category = category;
        }

        public MinimapBlipData(Vector3 worldPosition, MinimapBlipCategory category)
        {
            WorldX = worldPosition.x;
            WorldZ = worldPosition.z;
            Category = category;
        }
    }

    /// <summary>
    /// Marker component attached to root Vehicle GameObjects so Physics.OverlapSphere on child colliders
    /// resolves the root vehicle cleanly alongside the "Vehicle" tag.
    /// </summary>
    public sealed class DrivableVehicleMarker : MonoBehaviour
    {
        public PooledPropInstance SourcePropInstance;
        public Collider[] ParkedColliders;
    }

    /// <summary>
    /// Compact 24-byte value-type placement record parsed from map_data.json.
    /// Contains zero managed references to avoid GC pressure on large maps.
    /// </summary>
    public readonly struct MapObjectRecord
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;
        public readonly float RotationY;
        public readonly float Scale;
        public readonly ushort AssetTypeId;
        public readonly ushort Variant;

        public MapObjectRecord(float x, float y, float z, float rotationY, float scale, ushort assetTypeId, ushort variant)
        {
            X = x;
            Y = y;
            Z = z;
            RotationY = rotationY;
            Scale = scale;
            AssetTypeId = assetTypeId;
            Variant = variant;
        }
    }

    /// <summary>
    /// High-performance single-pass JSON map loader, spatial chunk bucketer, and GPU-instanced prefab pooler.
    /// Integrates <see cref="LODGroupSetup"/>, <see cref="MaterialVariantApplier"/>, and static-batch metadata
    /// for <see cref="StaticMeshCombiner"/>.
    /// </summary>
    public sealed class MapDataLoader
    {
        public const string DefaultResourceName = "map_data";
        public const string PrefabResourceFolder = "Prefabs/";
        public const string VehicleTagName = "Vehicle";

        private sealed class AssetTypeSlot
        {
            public ushort Id;
            public string Name;
            public uint NameHash;
            public GameObject Template;
            public bool IsProceduralFallback;
            public bool IsStaticBatchable;
            public bool IsVehicle;
            public bool IsWeapon;
            public Color[] InstancedPalette;
            public readonly Stack<PooledPropInstance> Pool = new Stack<PooledPropInstance>(32);
        }

        private readonly float _chunkSize;
        private readonly Dictionary<ChunkCoord, List<MapObjectRecord>> _chunkBuckets = new Dictionary<ChunkCoord, List<MapObjectRecord>>(128);
        private readonly List<AssetTypeSlot> _assetTypes = new List<AssetTypeSlot>(16);
        private readonly Dictionary<string, ushort> _customPrefabLookup = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Material> _runtimeMaterials = new List<Material>(16);

        private Mesh _sharedReadableBoxMesh;
        private Mesh _sharedWaterQuadMesh;
        private Transform _templatesRoot;
        private Transform _pooledPropsRoot;
        private int _totalObjectCount;

        private ushort _buildingTypeId;
        private ushort _houseTypeId;
        private ushort _mandirTypeId;
        private ushort _vehicleTypeId;
        private bool _vehicleTagVerified;
        private bool _vehicleTagSupported;

        public int TotalObjectCount => _totalObjectCount;
        public int PopulatedChunkCount => _chunkBuckets.Count;

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void EnsureCustomProjectTagsInEditor()
        {
            Object[] tagAssets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (tagAssets == null || tagAssets.Length == 0)
            {
                return;
            }

            var tagManager = new UnityEditor.SerializedObject(tagAssets[0]);
            UnityEditor.SerializedProperty tagsProp = tagManager.FindProperty("tags");
            if (tagsProp == null)
            {
                return;
            }

            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                UnityEditor.SerializedProperty entry = tagsProp.GetArrayElementAtIndex(i);
                if (entry != null && entry.stringValue == "Vehicle")
                {
                    return;
                }
            }

            int insertIdx = tagsProp.arraySize;
            tagsProp.InsertArrayElementAtIndex(insertIdx);
            tagsProp.GetArrayElementAtIndex(insertIdx).stringValue = "Vehicle";
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }
#endif

        public MapDataLoader(float chunkSize = WorldChunkManager.DefaultChunkSize)
        {
            _chunkSize = chunkSize;
            PreRegisterCoreAssetTypes();
        }

        /// <summary>
        /// Attaches the internal prop pool hierarchy under the persistent world streaming root.
        /// </summary>
        public void AttachPoolRoot(Transform worldRoot)
        {
            if (_pooledPropsRoot != null)
            {
                return;
            }

            _sharedReadableBoxMesh = ChunkPool.CreateReadableUnitBoxMesh();
            _sharedWaterQuadMesh = ChunkPool.CreateHorizontalQuadMesh(1f);

            var templatesGo = new GameObject("PrototypeTemplates");
            templatesGo.SetActive(false);
            _templatesRoot = templatesGo.transform;
            _templatesRoot.SetParent(worldRoot, false);

            var poolGo = new GameObject("PooledProps");
            _pooledPropsRoot = poolGo.transform;
            _pooledPropsRoot.SetParent(worldRoot, false);
        }

        /// <summary>
        /// Explicitly registers a custom Unity prefab (e.g., ready-made Blender vehicle, weapon, or building)
        /// and automatically applies GPU Instancing and 3-tier LODGroup setup where applicable.
        /// </summary>
        public void RegisterPrefab(string assetType, GameObject prefab, bool? isStaticOverride = null)
        {
            if (string.IsNullOrEmpty(assetType) || prefab == null)
            {
                return;
            }

            ushort id = ResolveOrRegisterAssetType(assetType, 0, assetType.Length);
            AssetTypeSlot slot = _assetTypes[id];
            if (isStaticOverride.HasValue)
            {
                slot.IsStaticBatchable = isStaticOverride.Value;
            }

            MaterialVariantApplier.EnableGpuInstancing(prefab);
            ConfigureLodIfDynamicAsset(slot, prefab);

            slot.Template = prefab;
            slot.IsProceduralFallback = false;
        }

        /// <summary>
        /// Loads map_data.json from persistentDataPath override, Resources/map_data, StreamingAssets,
        /// or falls back to procedural Asoda test JSON if none exists yet.
        /// </summary>
        public void LoadDefaultMapData()
        {
            string json = TryReadMapJsonText();
            if (string.IsNullOrEmpty(json))
            {
                json = AsodaTestMapGenerator.GenerateDummyMapJson();
            }

            LoadFromJson(json);
        }

        /// <summary>
        /// Parses raw map_data.json text in a single zero-substring pass and buckets placements by 50x50 chunk coordinates.
        /// </summary>
        public void LoadFromJson(string json)
        {
            _chunkBuckets.Clear();
            _totalObjectCount = 0;

            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            int cursor = 0;
            int length = json.Length;
            SkipWhitespace(json, length, ref cursor);

            if (cursor >= length)
            {
                return;
            }

            char first = json[cursor];
            if (first == '[')
            {
                ParsePlacementsArray(json, length, ref cursor);
            }
            else if (first == '{')
            {
                cursor++; // skip '{'
                while (cursor < length)
                {
                    SkipWhitespace(json, length, ref cursor);
                    if (cursor >= length || json[cursor] == '}')
                    {
                        cursor++;
                        break;
                    }

                    if (!TryReadQuotedSlice(json, length, ref cursor, out int keyStart, out int keyLen))
                    {
                        break;
                    }

                    SkipWhitespace(json, length, ref cursor);
                    if (cursor < length && json[cursor] == ':')
                    {
                        cursor++;
                    }

                    SkipWhitespace(json, length, ref cursor);
                    if (cursor < length && json[cursor] == '[' &&
                        (SliceEqualsIgnoreCase(json, keyStart, keyLen, "objects") ||
                         SliceEqualsIgnoreCase(json, keyStart, keyLen, "items") ||
                         SliceEqualsIgnoreCase(json, keyStart, keyLen, "placements") ||
                         SliceEqualsIgnoreCase(json, keyStart, keyLen, "entities")))
                    {
                        ParsePlacementsArray(json, length, ref cursor);
                    }
                    else
                    {
                        SkipJsonValue(json, length, ref cursor);
                    }

                    SkipWhitespace(json, length, ref cursor);
                    if (cursor < length && json[cursor] == ',')
                    {
                        cursor++;
                    }
                }
            }
        }

        /// <summary>
        /// Pre-warms pooled prop instances per asset type so chunk transitions do not spike frame times.
        /// </summary>
        public void PrewarmPropPools(int countPerCommonType = 24)
        {
            for (int i = 0; i < _assetTypes.Count; i++)
            {
                AssetTypeSlot slot = _assetTypes[i];
                EnsureSlotTemplate(slot);
                if (slot.Template == null)
                {
                    continue;
                }

                int targetCount = (i <= 1 || slot.Name == "Road") ? countPerCommonType : Mathf.Max(6, countPerCommonType / 3);
                while (slot.Pool.Count < targetCount)
                {
                    PooledPropInstance instance = CreatePropInstance(slot);
                    instance.GameObject.SetActive(false);
                    instance.Transform.SetParent(_pooledPropsRoot, false);
                    slot.Pool.Push(instance);
                }
            }
        }

        /// <summary>
        /// Spawns all map objects falling within the target chunk's 50x50 boundaries using pooled instances
        /// and GPU-instanced MaterialPropertyBlock variations.
        /// </summary>
        public int PopulateChunk(WorldChunk chunk)
        {
            if (chunk == null || !_chunkBuckets.TryGetValue(chunk.Coord, out List<MapObjectRecord> records))
            {
                return 0;
            }

            ChunkBounds bounds = chunk.Bounds;
            Transform chunkRoot = chunk.RootTransform;
            int spawnedCount = 0;

            for (int i = 0; i < records.Count; i++)
            {
                MapObjectRecord record = records[i];
                if (!bounds.Contains(record.X, record.Z))
                {
                    continue;
                }

                PooledPropInstance instance = AcquireProp(record.AssetTypeId);
                if (instance == null)
                {
                    continue;
                }

                Transform propTransform = instance.Transform;
                propTransform.SetParent(chunkRoot, false);
                propTransform.localPosition = new Vector3(
                    record.X - bounds.MinX,
                    record.Y,
                    record.Z - bounds.MinZ);
                propTransform.localRotation = Quaternion.Euler(0f, record.RotationY, 0f);
                propTransform.localScale = Vector3.one * record.Scale;

                instance.RestoreStaticRenderers();
                ApplyGpuInstancedVariation(instance, record);

                instance.GameObject.SetActive(true);
                chunk.SpawnedProps.Add(instance);
                spawnedCount++;
            }

            return spawnedCount;
        }

        /// <summary>
        /// Returns all spawned prop instances inside an unloading chunk back to their respective object pools.
        /// </summary>
        public void ClearChunk(WorldChunk chunk)
        {
            if (chunk == null)
            {
                return;
            }

            List<PooledPropInstance> spawned = chunk.SpawnedProps;
            for (int i = 0; i < spawned.Count; i++)
            {
                PooledPropInstance instance = spawned[i];
                if (instance == null || instance.GameObject == null)
                {
                    continue;
                }

                instance.RestoreStaticRenderers();
                instance.GameObject.SetActive(false);

                if (_pooledPropsRoot != null)
                {
                    instance.Transform.SetParent(_pooledPropsRoot, false);
                }

                _assetTypes[instance.AssetTypeId].Pool.Push(instance);
            }

            spawned.Clear();
        }

        /// <summary>
        /// Cleans up runtime fallback materials and meshes on application shutdown.
        /// </summary>
        public void Dispose()
        {
            _chunkBuckets.Clear();

            for (int i = 0; i < _runtimeMaterials.Count; i++)
            {
                if (_runtimeMaterials[i] != null)
                {
                    Object.Destroy(_runtimeMaterials[i]);
                }
            }

            _runtimeMaterials.Clear();

            if (_sharedReadableBoxMesh != null)
            {
                Object.Destroy(_sharedReadableBoxMesh);
                _sharedReadableBoxMesh = null;
            }

            if (_sharedWaterQuadMesh != null)
            {
                Object.Destroy(_sharedWaterQuadMesh);
                _sharedWaterQuadMesh = null;
            }
        }

        #region Fast Single-Pass JSON Parser

        private void ParsePlacementsArray(string json, int length, ref int cursor)
        {
            cursor++; // skip '['
            while (cursor < length)
            {
                SkipWhitespace(json, length, ref cursor);
                if (cursor >= length)
                {
                    break;
                }

                if (json[cursor] == ']')
                {
                    cursor++;
                    break;
                }

                if (json[cursor] == '{')
                {
                    ParseSinglePlacementObject(json, length, ref cursor);
                }
                else
                {
                    SkipJsonValue(json, length, ref cursor);
                }

                SkipWhitespace(json, length, ref cursor);
                if (cursor < length && json[cursor] == ',')
                {
                    cursor++;
                }
            }
        }

        private void ParseSinglePlacementObject(string json, int length, ref int cursor)
        {
            cursor++; // skip '{'

            float x = 0f;
            float y = 0f;
            float z = 0f;
            float rotationY = 0f;
            float scale = 1f;
            ushort typeId = 0;
            ushort variant = 0;
            bool hasType = false;
            bool hasVariant = false;

            while (cursor < length)
            {
                SkipWhitespace(json, length, ref cursor);
                if (cursor >= length)
                {
                    break;
                }

                if (json[cursor] == '}')
                {
                    cursor++;
                    break;
                }

                if (!TryReadQuotedSlice(json, length, ref cursor, out int kStart, out int kLen))
                {
                    break;
                }

                SkipWhitespace(json, length, ref cursor);
                if (cursor < length && json[cursor] == ':')
                {
                    cursor++;
                }

                SkipWhitespace(json, length, ref cursor);

                if (kLen == 1)
                {
                    char k = json[kStart];
                    if (k == 'x' || k == 'X')
                    {
                        ParseFloat(json, length, ref cursor, out x);
                    }
                    else if (k == 'y' || k == 'Y')
                    {
                        ParseFloat(json, length, ref cursor, out y);
                    }
                    else if (k == 'z' || k == 'Z')
                    {
                        ParseFloat(json, length, ref cursor, out z);
                    }
                    else
                    {
                        SkipJsonValue(json, length, ref cursor);
                    }
                }
                else if (SliceEqualsIgnoreCase(json, kStart, kLen, "type") ||
                         SliceEqualsIgnoreCase(json, kStart, kLen, "assetType") ||
                         SliceEqualsIgnoreCase(json, kStart, kLen, "prefab"))
                {
                    if (TryReadQuotedSlice(json, length, ref cursor, out int vStart, out int vLen) && vLen > 0)
                    {
                        typeId = ResolveOrRegisterAssetType(json, vStart, vLen);
                        hasType = true;
                    }
                }
                else if (SliceEqualsIgnoreCase(json, kStart, kLen, "rotationY") ||
                         SliceEqualsIgnoreCase(json, kStart, kLen, "rotY") ||
                         SliceEqualsIgnoreCase(json, kStart, kLen, "yaw") ||
                         SliceEqualsIgnoreCase(json, kStart, kLen, "rotation"))
                {
                    ParseFloat(json, length, ref cursor, out rotationY);
                }
                else if (SliceEqualsIgnoreCase(json, kStart, kLen, "scale"))
                {
                    ParseFloat(json, length, ref cursor, out scale);
                }
                else if (SliceEqualsIgnoreCase(json, kStart, kLen, "variant"))
                {
                    ParseFloat(json, length, ref cursor, out float rawVariant);
                    variant = (ushort)Mathf.Clamp(Mathf.RoundToInt(rawVariant), 0, ushort.MaxValue);
                    hasVariant = true;
                }
                else
                {
                    SkipJsonValue(json, length, ref cursor);
                }

                SkipWhitespace(json, length, ref cursor);
                if (cursor < length && json[cursor] == ',')
                {
                    cursor++;
                }
            }

            if (!hasType)
            {
                return;
            }

            if (scale <= 0.001f)
            {
                scale = 1f;
            }

            if (!hasVariant)
            {
                int spatialSeed = unchecked((Mathf.RoundToInt(x * 10f) * 73856093) ^ (Mathf.RoundToInt(z * 10f) * 19349663));
                variant = (ushort)(spatialSeed & 0xFFFF);
            }

            var record = new MapObjectRecord(x, y, z, rotationY, scale, typeId, variant);
            ChunkCoord coord = ChunkCoord.FromWorldPosition(x, z, _chunkSize);

            if (!_chunkBuckets.TryGetValue(coord, out List<MapObjectRecord> bucket))
            {
                bucket = new List<MapObjectRecord>(32);
                _chunkBuckets[coord] = bucket;
            }

            bucket.Add(record);
            _totalObjectCount++;
        }

        private static void SkipWhitespace(string json, int length, ref int cursor)
        {
            while (cursor < length)
            {
                char c = json[cursor];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                {
                    return;
                }

                cursor++;
            }
        }

        private static bool TryReadQuotedSlice(string json, int length, ref int cursor, out int sliceStart, out int sliceLength)
        {
            SkipWhitespace(json, length, ref cursor);
            if (cursor >= length || json[cursor] != '"')
            {
                sliceStart = 0;
                sliceLength = 0;
                return false;
            }

            cursor++;
            sliceStart = cursor;

            while (cursor < length)
            {
                char c = json[cursor];
                if (c == '\\')
                {
                    cursor += 2;
                    continue;
                }

                if (c == '"')
                {
                    sliceLength = cursor - sliceStart;
                    cursor++;
                    return true;
                }

                cursor++;
            }

            sliceLength = 0;
            return false;
        }

        private static void ParseFloat(string json, int length, ref int cursor, out float result)
        {
            SkipWhitespace(json, length, ref cursor);

            bool negative = false;
            if (cursor < length)
            {
                char sign = json[cursor];
                if (sign == '-')
                {
                    negative = true;
                    cursor++;
                }
                else if (sign == '+')
                {
                    cursor++;
                }
            }

            double value = 0.0;
            while (cursor < length)
            {
                int digit = json[cursor] - '0';
                if ((uint)digit > 9u)
                {
                    break;
                }

                value = (value * 10.0) + digit;
                cursor++;
            }

            if (cursor < length && json[cursor] == '.')
            {
                cursor++;
                double divisor = 10.0;
                while (cursor < length)
                {
                    int digit = json[cursor] - '0';
                    if ((uint)digit > 9u)
                    {
                        break;
                    }

                    value += digit / divisor;
                    divisor *= 10.0;
                    cursor++;
                }
            }

            if (cursor < length && (json[cursor] == 'e' || json[cursor] == 'E'))
            {
                cursor++;
                bool expNeg = false;
                if (cursor < length)
                {
                    if (json[cursor] == '-')
                    {
                        expNeg = true;
                        cursor++;
                    }
                    else if (json[cursor] == '+')
                    {
                        cursor++;
                    }
                }

                int exp = 0;
                while (cursor < length)
                {
                    int digit = json[cursor] - '0';
                    if ((uint)digit > 9u)
                    {
                        break;
                    }

                    exp = (exp * 10) + digit;
                    cursor++;
                }

                if (exp != 0)
                {
                    double factor = Math.Pow(10.0, exp);
                    value = expNeg ? (value / factor) : (value * factor);
                }
            }

            result = negative ? -(float)value : (float)value;
        }

        private static void SkipJsonValue(string json, int length, ref int cursor)
        {
            SkipWhitespace(json, length, ref cursor);
            if (cursor >= length)
            {
                return;
            }

            char c = json[cursor];
            if (c == '"')
            {
                TryReadQuotedSlice(json, length, ref cursor, out _, out _);
                return;
            }

            if (c == '{' || c == '[')
            {
                char open = c;
                char close = (c == '{') ? '}' : ']';
                int depth = 1;
                cursor++;

                while (cursor < length && depth > 0)
                {
                    char ch = json[cursor];
                    if (ch == '"')
                    {
                        TryReadQuotedSlice(json, length, ref cursor, out _, out _);
                        continue;
                    }

                    if (ch == open)
                    {
                        depth++;
                    }
                    else if (ch == close)
                    {
                        depth--;
                    }

                    cursor++;
                }

                return;
            }

            while (cursor < length)
            {
                char ch = json[cursor];
                if (ch == ',' || ch == '}' || ch == ']' || ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n')
                {
                    break;
                }

                cursor++;
            }
        }

        private static bool SliceEqualsIgnoreCase(string text, int start, int length, string target)
        {
            if (length != target.Length)
            {
                return false;
            }

            for (int i = 0; i < length; i++)
            {
                char a = text[start + i];
                char b = target[i];
                if (a == b)
                {
                    continue;
                }

                if (a >= 'A' && a <= 'Z')
                {
                    a = (char)(a + 32);
                }

                if (b >= 'A' && b <= 'Z')
                {
                    b = (char)(b + 32);
                }

                if (a != b)
                {
                    return false;
                }
            }

            return true;
        }

        private static uint ComputeSliceHashIgnoreCase(string text, int start, int length)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < length; i++)
            {
                char c = text[start + i];
                if (c >= 'A' && c <= 'Z')
                {
                    c = (char)(c + 32);
                }

                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }

        #endregion

        #region Asset Type Registry, LOD, Static Batching & MaterialPropertyBlock

        private void PreRegisterCoreAssetTypes()
        {
            // Static architectural & road assets (merged per-chunk via StaticMeshCombiner)
            _buildingTypeId = RegisterAssetSlot("Building", isStaticBatchable: true, isVehicle: false, isWeapon: false, palette: new[]
            {
                new Color(0.89f, 0.76f, 0.58f, 1f),
                new Color(0.84f, 0.56f, 0.42f, 1f),
                new Color(0.92f, 0.89f, 0.82f, 1f),
                new Color(0.72f, 0.82f, 0.90f, 1f),
                new Color(0.88f, 0.79f, 0.50f, 1f)
            });

            // GPU-instanced repeating foliage
            RegisterAssetSlot("Tree", isStaticBatchable: false, isVehicle: false, isWeapon: false, palette: new[]
            {
                new Color(0.24f, 0.54f, 0.22f, 1f),
                new Color(0.19f, 0.46f, 0.20f, 1f),
                new Color(0.32f, 0.59f, 0.24f, 1f),
                new Color(0.28f, 0.50f, 0.26f, 1f)
            });

            // Dynamic LOD + MaterialPropertyBlock Vehicles (auto-rickshaws, tractors, cars)
            _vehicleTypeId = RegisterAssetSlot("Vehicle", isStaticBatchable: false, isVehicle: true, isWeapon: false, palette: MaterialVariantApplier.VehicleBodyPalette);

            // Static sacred Mandir architecture
            _mandirTypeId = RegisterAssetSlot("Mandir", isStaticBatchable: true, isVehicle: false, isWeapon: false, palette: new[]
            {
                new Color(0.93f, 0.62f, 0.28f, 1f),
                new Color(0.90f, 0.74f, 0.52f, 1f)
            });

            // Sarovar water tiles
            RegisterAssetSlot("Lake", isStaticBatchable: false, isVehicle: false, isWeapon: false, palette: new[]
            {
                new Color(0.16f, 0.49f, 0.64f, 1f),
                new Color(0.20f, 0.54f, 0.68f, 1f)
            });

            _houseTypeId = RegisterAssetSlot("House", isStaticBatchable: true, isVehicle: false, isWeapon: false, palette: new[]
            {
                new Color(0.88f, 0.74f, 0.56f, 1f),
                new Color(0.85f, 0.60f, 0.46f, 1f),
                new Color(0.91f, 0.88f, 0.80f, 1f)
            });

            // Static village road segments (merged into chunk combined mesh via StaticMeshCombiner)
            RegisterAssetSlot("Road", isStaticBatchable: true, isVehicle: false, isWeapon: false, palette: null);

            // Dynamic LOD + MaterialPropertyBlock Weapons
            RegisterAssetSlot("Weapon", isStaticBatchable: false, isVehicle: false, isWeapon: true, palette: MaterialVariantApplier.WeaponFinishPalette);
        }

        private ushort RegisterAssetSlot(
            string name,
            bool isStaticBatchable,
            bool isVehicle,
            bool isWeapon,
            Color[] palette)
        {
            ushort id = (ushort)_assetTypes.Count;
            var slot = new AssetTypeSlot
            {
                Id = id,
                Name = name,
                NameHash = ComputeSliceHashIgnoreCase(name, 0, name.Length),
                IsStaticBatchable = isStaticBatchable,
                IsVehicle = isVehicle,
                IsWeapon = isWeapon,
                InstancedPalette = palette
            };
            _assetTypes.Add(slot);
            _customPrefabLookup[name] = id;
            return id;
        }

        private ushort ResolveOrRegisterAssetType(string text, int start, int length)
        {
            uint hash = ComputeSliceHashIgnoreCase(text, start, length);
            for (int i = 0; i < _assetTypes.Count; i++)
            {
                AssetTypeSlot existing = _assetTypes[i];
                if (existing.NameHash == hash && SliceEqualsIgnoreCase(text, start, length, existing.Name))
                {
                    return existing.Id;
                }
            }

            string newTypeName = text.Substring(start, length);
            bool isWeapon = newTypeName.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            newTypeName.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            newTypeName.IndexOf("Rifle", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isVehicle = newTypeName.IndexOf("Vehicle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             newTypeName.IndexOf("Auto", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             newTypeName.IndexOf("Car", StringComparison.OrdinalIgnoreCase) >= 0;

            Color[] palette = isVehicle
                ? MaterialVariantApplier.VehicleBodyPalette
                : (isWeapon ? MaterialVariantApplier.WeaponFinishPalette : null);

            return RegisterAssetSlot(newTypeName, isStaticBatchable: false, isVehicle: isVehicle, isWeapon: isWeapon, palette: palette);
        }

        private PooledPropInstance AcquireProp(ushort assetTypeId)
        {
            if (assetTypeId >= _assetTypes.Count)
            {
                return null;
            }

            AssetTypeSlot slot = _assetTypes[assetTypeId];
            if (slot.Pool.Count > 0)
            {
                return slot.Pool.Pop();
            }

            EnsureSlotTemplate(slot);
            return slot.Template != null ? CreatePropInstance(slot) : null;
        }

        private void EnsureSlotTemplate(AssetTypeSlot slot)
        {
            if (slot.Template != null)
            {
                return;
            }

            // 1. Check project Resources/Prefabs/<AssetName> for ready-made Blender prefabs
            GameObject projectPrefab = Resources.Load<GameObject>(PrefabResourceFolder + slot.Name);
            if (projectPrefab != null)
            {
                MaterialVariantApplier.EnableGpuInstancing(projectPrefab);
                slot.Template = projectPrefab;
                slot.IsProceduralFallback = false;
                return;
            }

            // 2. Fallback: build a GPU-instanced low-poly placeholder prototype for immediate testing
            slot.Template = BuildProceduralPrototype(slot);
            slot.IsProceduralFallback = true;
        }

        private PooledPropInstance CreatePropInstance(AssetTypeSlot slot)
        {
            GameObject instanceGo = Object.Instantiate(slot.Template, _pooledPropsRoot, false);
            instanceGo.name = slot.Name;

            ConfigureLodIfDynamicAsset(slot, instanceGo);

            MeshRenderer[] tintRenderers;
            if (slot.IsVehicle || slot.IsWeapon)
            {
                // Collect body/receiver renderers across LOD0, LOD1, and LOD2 so colors stay consistent across LOD transitions
                tintRenderers = MaterialVariantApplier.CollectPrimaryTintRenderers(instanceGo, isVehicle: slot.IsVehicle);
            }
            else if (slot.IsProceduralFallback)
            {
                MeshRenderer primary = instanceGo.GetComponentInChildren<MeshRenderer>(true);
                tintRenderers = primary != null ? new[] { primary } : Array.Empty<MeshRenderer>();
            }
            else
            {
                tintRenderers = instanceGo.GetComponentsInChildren<MeshRenderer>(true);
                MaterialVariantApplier.EnableGpuInstancing(tintRenderers);
            }

            // Cache static batchable MeshFilter/MeshRenderer pairs once (skipping LODGroup-managed hierarchies)
            bool canStaticBatch = slot.IsStaticBatchable && instanceGo.GetComponentInChildren<LODGroup>(true) == null;
            MeshFilter[] staticFilters = Array.Empty<MeshFilter>();
            MeshRenderer[] staticRenderers = Array.Empty<MeshRenderer>();

            if (canStaticBatch)
            {
                MeshFilter[] foundFilters = instanceGo.GetComponentsInChildren<MeshFilter>(true);
                var validFilters = new List<MeshFilter>(foundFilters.Length);
                var validRenderers = new List<MeshRenderer>(foundFilters.Length);

                for (int i = 0; i < foundFilters.Length; i++)
                {
                    MeshFilter mf = foundFilters[i];
                    if (mf == null)
                    {
                        continue;
                    }

                    MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                    if (mr != null)
                    {
                        validFilters.Add(mf);
                        validRenderers.Add(mr);
                    }
                }

                staticFilters = validFilters.ToArray();
                staticRenderers = validRenderers.ToArray();
            }

            var pooledInstance = new PooledPropInstance(
                slot.Id,
                instanceGo,
                tintRenderers,
                canStaticBatch,
                staticFilters,
                staticRenderers);

            if (slot.IsVehicle)
            {
                PrepareVehicleInstanceMetadata(instanceGo, pooledInstance);
            }

            return pooledInstance;
        }

        private void PrepareVehicleInstanceMetadata(GameObject vehicleGo, PooledPropInstance pooledInstance)
        {
            TryAssignVehicleTagRecursively(vehicleGo);

            DrivableVehicleMarker marker = vehicleGo.GetComponent<DrivableVehicleMarker>();
            if (marker == null)
            {
                marker = vehicleGo.AddComponent<DrivableVehicleMarker>();
            }

            marker.SourcePropInstance = pooledInstance;
            marker.ParkedColliders = vehicleGo.GetComponentsInChildren<Collider>(true);
        }

        private void TryAssignVehicleTagRecursively(GameObject root)
        {
            if (!_vehicleTagVerified)
            {
                _vehicleTagVerified = true;
                try
                {
                    root.tag = "Vehicle";
                    _vehicleTagSupported = true;
                }
                catch
                {
                    _vehicleTagSupported = false;
                }
            }

            if (!_vehicleTagSupported)
            {
                return;
            }

            root.tag = "Vehicle";
            Transform t = root.transform;
            int count = t.childCount;
            for (int i = 0; i < count; i++)
            {
                TryAssignVehicleTagRecursively(t.GetChild(i).gameObject);
            }
        }

        /// <summary>
        /// Appends static Building and Mandir coordinates within <paramref name="coord"/> for zero-allocation minimap rendering.
        /// </summary>
        public void AppendStaticMinimapBlips(ChunkCoord coord, Vector3 center, float maxRadiusSq, List<MinimapBlipData> outBlips)
        {
            if (!_chunkBuckets.TryGetValue(coord, out List<MapObjectRecord> records))
            {
                return;
            }

            for (int i = 0; i < records.Count; i++)
            {
                MapObjectRecord rec = records[i];
                ushort id = rec.AssetTypeId;

                MinimapBlipCategory cat;
                if (id == _mandirTypeId)
                {
                    cat = MinimapBlipCategory.Mandir;
                }
                else if (id == _buildingTypeId || id == _houseTypeId)
                {
                    cat = MinimapBlipCategory.Building;
                }
                else
                {
                    continue;
                }

                float dx = rec.X - center.x;
                float dz = rec.Z - center.z;
                if ((dx * dx) + (dz * dz) <= maxRadiusSq)
                {
                    outBlips.Add(new MinimapBlipData(rec.X, rec.Z, cat));
                }
            }
        }

        public bool IsVehicleAssetType(ushort assetTypeId) => assetTypeId == _vehicleTypeId;

        private static void ConfigureLodIfDynamicAsset(AssetTypeSlot slot, GameObject target)
        {
            if (target == null)
            {
                return;
            }

            if (slot.IsVehicle)
            {
                LODGroupSetup.ConfigureVehicleLOD(target);
            }
            else if (slot.IsWeapon)
            {
                LODGroupSetup.ConfigureWeaponLOD(target);
            }
        }

        private void ApplyGpuInstancedVariation(PooledPropInstance instance, MapObjectRecord record)
        {
            AssetTypeSlot slot = _assetTypes[instance.AssetTypeId];
            if (slot.InstancedPalette == null || slot.InstancedPalette.Length == 0)
            {
                return;
            }

            // Static-batched procedural buildings share unified chunk submeshes; non-static & textured assets get per-instance MPB tint
            if (instance.IsStaticBatchable && slot.IsProceduralFallback)
            {
                return;
            }

            float tintStrength = slot.IsProceduralFallback ? 1.0f : (slot.IsVehicle || slot.IsWeapon ? 0.30f : 0.18f);
            MaterialVariantApplier.ApplyPaletteVariant(instance.Renderers, slot.InstancedPalette, record.Variant, tintStrength);
        }

        #endregion

        #region Procedural Low-Poly Stand-In Prototypes (Immediate Zero-Asset Testing)

        private GameObject BuildProceduralPrototype(AssetTypeSlot slot)
        {
            string typeName = slot.Name;
            var root = new GameObject($"Template_{typeName}");
            root.transform.SetParent(_templatesRoot, false);

            if (SliceEqualsIgnoreCase(typeName, 0, typeName.Length, "Building") ||
                SliceEqualsIgnoreCase(typeName, 0, typeName.Length, "House"))
            {
                Material wallMat = CreateTrackedMaterial("Asoda_BuildingWall_Mat", new Color(0.89f, 0.75f, 0.56f, 1f));
                Material roofMat = CreateTrackedMaterial("Asoda_BuildingRoof_Mat", new Color(0.60f, 0.35f, 0.25f, 1f));

                CreateBoxPart(root.transform, "Walls", new Vector3(0f, 2.25f, 0f), new Vector3(6.5f, 4.5f, 6.5f), wallMat, addBoxCollider: true);
                CreateBoxPart(root.transform, "ParapetRoof", new Vector3(0f, 4.7f, 0f), new Vector3(6.9f, 0.45f, 6.9f), roofMat, addBoxCollider: false);
            }
            else if (SliceEqualsIgnoreCase(typeName, 0, typeName.Length, "Road"))
            {
                Material roadMat = CreateTrackedMaterial("Asoda_RoadAsphalt_Mat", new Color(0.28f, 0.27f, 0.29f, 1f));
                CreateBoxPart(root.transform, "RoadSlab", new Vector3(0f, 0.03f, 0f), new Vector3(9.0f, 0.08f, 12.5f), roadMat, addBoxCollider: false);
            }
            else if (SliceEqualsIgnoreCase(typeName, 0, typeName.Length, "Tree"))
            {
                Material canopyMat = CreateTrackedMaterial("Asoda_TreeCanopy_Mat", new Color(0.24f, 0.54f, 0.22f, 1f));
                Material trunkMat = CreateTrackedMaterial("Asoda_TreeTrunk_Mat", new Color(0.42f, 0.27f, 0.16f, 1f));

                CreatePrimitivePart(root.transform, PrimitiveType.Sphere, "Canopy", new Vector3(0f, 4.2f, 0f), new Vector3(3.8f, 3.2f, 3.8f), canopyMat, keepCollider: false);
                CreatePrimitivePart(root.transform, PrimitiveType.Cylinder, "Trunk", new Vector3(0f, 1.4f, 0f), new Vector3(0.55f, 1.4f, 0.55f), trunkMat, keepCollider: true);
            }
            else if (SliceEqualsIgnoreCase(typeName, 0, typeName.Length, "Mandir"))
            {
                Material stoneMat = CreateTrackedMaterial("Asoda_MandirStone_Mat", new Color(0.92f, 0.62f, 0.28f, 1f));
                Material plinthMat = CreateTrackedMaterial("Asoda_MandirPlinth_Mat", new Color(0.76f, 0.62f, 0.46f, 1f));
                Material kalashMat = CreateTrackedMaterial("Asoda_MandirKalash_Mat", new Color(0.95f, 0.80f, 0.22f, 1f));

                CreateBoxPart(root.transform, "Garbhagriha", new Vector3(0f, 3.0f, 0f), new Vector3(6.0f, 4.0f, 6.0f), stoneMat, addBoxCollider: true);
                CreateBoxPart(root.transform, "JagatiPlinth", new Vector3(0f, 0.5f, 0f), new Vector3(9.0f, 1.0f, 9.0f), plinthMat, addBoxCollider: true);
                CreateBoxPart(root.transform, "ShikharaTier1", new Vector3(0f, 6.0f, 0f), new Vector3(4.2f, 2.2f, 4.2f), stoneMat, addBoxCollider: false);
                CreateBoxPart(root.transform, "ShikharaTier2", new Vector3(0f, 8.0f, 0f), new Vector3(2.5f, 2.0f, 2.5f), stoneMat, addBoxCollider: false);
                CreateBoxPart(root.transform, "Kalash", new Vector3(0f, 9.4f, 0f), new Vector3(1.0f, 1.2f, 1.0f), kalashMat, addBoxCollider: false);
            }
            else if (SliceEqualsIgnoreCase(typeName, 0, typeName.Length, "Vehicle"))
            {
                // Multi-part Indian auto-rickshaw / vehicle: Chassis + Canopy + 4 Wheels (demonstrates automatic LOD0 -> LOD1 -> LOD2)
                Material bodyMat = CreateTrackedMaterial("Asoda_VehicleBody_Mat", new Color(0.16f, 0.56f, 0.29f, 1f));
                Material canopyMat = CreateTrackedMaterial("Asoda_VehicleCanopy_Mat", new Color(0.92f, 0.78f, 0.18f, 1f));
                Material tireMat = CreateTrackedMaterial("Asoda_VehicleWheel_Mat", new Color(0.15f, 0.15f, 0.16f, 1f));

                CreateBoxPart(root.transform, "Chassis", new Vector3(0f, 0.68f, 0f), new Vector3(1.7f, 0.88f, 3.2f), bodyMat, addBoxCollider: true);
                CreateBoxPart(root.transform, "Canopy", new Vector3(0f, 1.48f, -0.15f), new Vector3(1.62f, 0.75f, 2.2f), canopyMat, addBoxCollider: false);
                CreateBoxPart(root.transform, "Wheel_FL", new Vector3(-0.82f, 0.32f, 1.05f), new Vector3(0.28f, 0.62f, 0.62f), tireMat, addBoxCollider: false);
                CreateBoxPart(root.transform, "Wheel_FR", new Vector3( 0.82f, 0.32f, 1.05f), new Vector3(0.28f, 0.62f, 0.62f), tireMat, addBoxCollider: false);
                CreateBoxPart(root.transform, "Wheel_RL", new Vector3(-0.82f, 0.32f, -1.05f), new Vector3(0.28f, 0.62f, 0.62f), tireMat, addBoxCollider: false);
                CreateBoxPart(root.transform, "Wheel_RR", new Vector3( 0.82f, 0.32f, -1.05f), new Vector3(0.28f, 0.62f, 0.62f), tireMat, addBoxCollider: false);

                LODGroupSetup.ConfigureVehicleLOD(root);
            }
            else if (SliceEqualsIgnoreCase(typeName, 0, typeName.Length, "Weapon"))
            {
                Material metalMat = CreateTrackedMaterial("Asoda_WeaponReceiver_Mat", new Color(0.26f, 0.28f, 0.31f, 1f));
                Material stockMat = CreateTrackedMaterial("Asoda_WeaponGrip_Mat", new Color(0.44f, 0.28f, 0.17f, 1f));

                CreateBoxPart(root.transform, "Receiver", new Vector3(0f, 0.15f, 0f), new Vector3(0.10f, 0.16f, 0.58f), metalMat, addBoxCollider: false);
                CreateBoxPart(root.transform, "Barrel", new Vector3(0f, 0.18f, 0.42f), new Vector3(0.05f, 0.05f, 0.38f), metalMat, addBoxCollider: false);
                CreateBoxPart(root.transform, "Stock", new Vector3(0f, 0.11f, -0.36f), new Vector3(0.08f, 0.14f, 0.28f), stockMat, addBoxCollider: false);

                LODGroupSetup.ConfigureWeaponLOD(root);
            }
            else if (SliceEqualsIgnoreCase(typeName, 0, typeName.Length, "Lake"))
            {
                Material waterMat = CreateTrackedMaterial("Asoda_LakeWater_Mat", new Color(0.16f, 0.49f, 0.64f, 1f));
                var waterGo = new GameObject("WaterSurface");
                Transform t = waterGo.transform;
                t.SetParent(root.transform, false);
                t.localPosition = new Vector3(-6f, 0.04f, -6f);
                t.localScale = new Vector3(12f, 1f, 12f);

                var mf = waterGo.AddComponent<MeshFilter>();
                mf.sharedMesh = _sharedWaterQuadMesh;

                var mr = waterGo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = waterMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
            else
            {
                Material propMat = CreateTrackedMaterial($"Asoda_{typeName}_Mat", new Color(0.75f, 0.70f, 0.62f, 1f));
                CreateBoxPart(root.transform, "Mesh", new Vector3(0f, 1.0f, 0f), new Vector3(2.0f, 2.0f, 2.0f), propMat, addBoxCollider: true);
            }

            root.SetActive(false);
            return root;
        }

        private void CreateBoxPart(
            Transform parent,
            string partName,
            Vector3 localPosition,
            Vector3 localScale,
            Material sharedMaterial,
            bool addBoxCollider)
        {
            var part = new GameObject(partName);
            Transform t = part.transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            t.localRotation = Quaternion.identity;
            t.localScale = localScale;

            var mf = part.AddComponent<MeshFilter>();
            mf.sharedMesh = _sharedReadableBoxMesh;

            var mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterial = sharedMaterial;

            if (addBoxCollider)
            {
                part.AddComponent<BoxCollider>();
            }
        }

        private static void CreatePrimitivePart(
            Transform parent,
            PrimitiveType primitiveType,
            string partName,
            Vector3 localPosition,
            Vector3 localScale,
            Material sharedMaterial,
            bool keepCollider)
        {
            GameObject part = GameObject.CreatePrimitive(primitiveType);
            part.name = partName;

            if (!keepCollider)
            {
                Collider col = part.GetComponent<Collider>();
                if (col != null)
                {
                    Object.Destroy(col);
                }
            }

            Transform t = part.transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            t.localRotation = Quaternion.identity;
            t.localScale = localScale;

            MeshRenderer mr = part.GetComponent<MeshRenderer>();
            mr.sharedMaterial = sharedMaterial;
        }

        private Material CreateTrackedMaterial(string name, Color color)
        {
            Material mat = ChunkPool.CreateInstancedSurfaceMaterial(name, color);
            _runtimeMaterials.Add(mat);
            return mat;
        }

        #endregion

        #region File & Resource Loading

        private static string TryReadMapJsonText()
        {
            string persistentPath = Path.Combine(Application.persistentDataPath, "map_data.json");
            if (File.Exists(persistentPath))
            {
                return File.ReadAllText(persistentPath);
            }

            TextAsset resourceAsset = Resources.Load<TextAsset>(DefaultResourceName);
            if (resourceAsset != null && !string.IsNullOrEmpty(resourceAsset.text))
            {
                return resourceAsset.text;
            }

#if !UNITY_ANDROID || UNITY_EDITOR
            string streamingPath = Path.Combine(Application.streamingAssetsPath, "map_data.json");
            if (File.Exists(streamingPath))
            {
                return File.ReadAllText(streamingPath);
            }
#endif

            return null;
        }

        #endregion
    }
}
