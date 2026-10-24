using System;
using System.Globalization;
using System.IO;
using System.Text;
using Asoda.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Asoda.World
{
    /// <summary>
    /// Generates deterministic Indian-inspired dummy map_data.json layouts (Asoda village blocks,
    /// Mandir shrines, Asoda Sarovar lake area, roadside vehicles, and neem/mango groves)
    /// so 50x50 chunk streaming can be tested immediately without final Blender assets.
    /// </summary>
    public static class AsodaTestMapGenerator
    {
        public const string EditorOutputPath = "Assets/_Project/Resources/map_data.json";

        private const float LakeCenterX = 80f;
        private const float LakeCenterZ = 80f;
        private const float LakeRadius = 38f;

#if UNITY_EDITOR
        [UnityEditor.MenuItem("Asoda/World/Generate Dummy map_data.json")]
        public static void GenerateMapDataFileInEditor()
        {
            string json = GenerateDummyMapJson(108);
            string directory = Path.GetDirectoryName(EditorOutputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(EditorOutputPath, json, Encoding.UTF8);
            UnityEditor.AssetDatabase.Refresh();
            Debug.Log($"[AsodaTestMapGenerator] Wrote dummy map data to {EditorOutputPath} ({json.Length} chars).");
        }
#endif

        /// <summary>
        /// Builds a complete JSON document spanning roughly [-180..180] x [-180..180] across 8x8 chunks.
        /// </summary>
        public static string GenerateDummyMapJson(int seed = 108)
        {
            var rng = new System.Random(seed);
            var sb = new StringBuilder(32768);

            sb.Append("{\n");
            sb.Append("  \"mapName\": \"Asoda\",\n");
            sb.Append("  \"version\": 1,\n");
            sb.Append("  \"chunkSize\": 50,\n");
            sb.Append("  \"objects\": [\n");

            bool first = true;

            // 1. Sacred Mandir landmarks (Village Chowk, Sarovar Ghat, Southern Shrine)
            AppendEntry(sb, ref first, "Mandir", -14f, 16f, 180f, 1.05f, 0);
            AppendEntry(sb, ref first, "Mandir", 34f, 80f, 90f, 1.15f, 1);
            AppendEntry(sb, ref first, "Mandir", 18f, -92f, 270f, 0.95f, 0);

            // 2. Asoda Sarovar (Lake area spanning chunks (0,0), (1,0), (2,0), (0,1), (1,1), (2,1), (1,2), (2,2))
            for (float lx = 44f; lx <= 116f; lx += 11f)
            {
                for (float lz = 44f; lz <= 116f; lz += 11f)
                {
                    float dx = lx - LakeCenterX;
                    float dz = lz - LakeCenterZ;
                    if ((dx * dx) + (dz * dz) <= (LakeRadius * LakeRadius))
                    {
                        int variant = ((Mathf.RoundToInt(lx) + Mathf.RoundToInt(lz)) & 1);
                        AppendEntry(sb, ref first, "Lake", lx, lz, 0f, 1.0f, variant);
                    }
                }
            }

            // Dense ring of neem/peepal trees around the lake shoreline
            const int shorelineTreeCount = 34;
            for (int i = 0; i < shorelineTreeCount; i++)
            {
                float angle = (i / (float)shorelineTreeCount) * Mathf.PI * 2f;
                // Leave an open ghat sightline directly in front of the lakeside Mandir on the western shore
                if (Mathf.Abs(Mathf.DeltaAngle(angle * Mathf.Rad2Deg, 180f)) < 22f)
                {
                    continue;
                }

                float radius = LakeRadius + 5.5f + NextRange(rng, 0f, 5.5f);
                float tx = LakeCenterX + (Mathf.Cos(angle) * radius);
                float tz = LakeCenterZ + (Mathf.Sin(angle) * radius);
                float scale = NextRange(rng, 0.85f, 1.30f);
                AppendEntry(sb, ref first, "Tree", tx, tz, NextRange(rng, 0f, 360f), scale, i % 4);
            }

            // 3. Structured Village & Market Building Blocks
            // North-West Residential Mohalla (4 street blocks)
            GenerateBuildingBlock(sb, ref first, rng, minX: -115f, maxX: -72f, minZ: 18f, maxZ: 58f, step: 13.5f);
            GenerateBuildingBlock(sb, ref first, rng, minX: -58f, maxX: -18f, minZ: 28f, maxZ: 62f, step: 13.5f);
            GenerateBuildingBlock(sb, ref first, rng, minX: -115f, maxX: -72f, minZ: 74f, maxZ: 118f, step: 14.0f);
            GenerateBuildingBlock(sb, ref first, rng, minX: -58f, maxX: -18f, minZ: 76f, maxZ: 118f, step: 14.0f);

            // South-West Bazaar & Town Quarter
            GenerateBuildingBlock(sb, ref first, rng, minX: -118f, maxX: -70f, minZ: -62f, maxZ: -18f, step: 13.0f);
            GenerateBuildingBlock(sb, ref first, rng, minX: -56f, maxX: -16f, minZ: -62f, maxZ: -16f, step: 12.5f);
            GenerateBuildingBlock(sb, ref first, rng, minX: -112f, maxX: -22f, minZ: -118f, maxZ: -78f, step: 14.5f);

            // South-East Residential Quarter
            GenerateBuildingBlock(sb, ref first, rng, minX: 18f, maxX: 64f, minZ: -64f, maxZ: -18f, step: 13.5f);
            GenerateBuildingBlock(sb, ref first, rng, minX: 78f, maxX: 126f, minZ: -64f, maxZ: -18f, step: 14.0f);
            GenerateBuildingBlock(sb, ref first, rng, minX: 32f, maxX: 118f, minZ: -120f, maxZ: -78f, step: 15.0f);

            // 4. Roadside Vehicles along main N-S (X = 0) and E-W (Z = 0) village roads
            float[] roadPositions = { -135f, -95f, -55f, -24f, 22f, 58f, 96f, 138f };
            for (int i = 0; i < roadPositions.Length; i++)
            {
                float pos = roadPositions[i];
                // East-West road lanes
                AppendEntry(sb, ref first, "Vehicle", pos, -3.5f, 90f, 1.0f, i % 4);
                if ((i & 1) == 0)
                {
                    AppendEntry(sb, ref first, "Vehicle", pos + 8f, 3.5f, 270f, 1.0f, (i + 1) % 4);
                }

                // North-South road lanes
                AppendEntry(sb, ref first, "Vehicle", 3.5f, pos, 0f, 1.0f, (i + 2) % 4);
                if ((i & 1) == 1)
                {
                    AppendEntry(sb, ref first, "Vehicle", -3.5f, pos - 7f, 180f, 1.0f, (i + 3) % 4);
                }
            }

            // 5. Avenue Trees along crossroads & outer countryside orchards across [-175..175]
            for (float d = -165f; d <= 165f; d += 22f)
            {
                if (Mathf.Abs(d) < 14f)
                {
                    continue;
                }

                if (!IsNearLake(d, 9.5f))
                {
                    AppendEntry(sb, ref first, "Tree", d, 9.5f, NextRange(rng, 0f, 360f), NextRange(rng, 0.9f, 1.2f), rng.Next(0, 4));
                }

                if (!IsNearLake(d, -9.5f))
                {
                    AppendEntry(sb, ref first, "Tree", d, -9.5f, NextRange(rng, 0f, 360f), NextRange(rng, 0.9f, 1.2f), rng.Next(0, 4));
                }

                if (!IsNearLake(9.5f, d))
                {
                    AppendEntry(sb, ref first, "Tree", 9.5f, d, NextRange(rng, 0f, 360f), NextRange(rng, 0.9f, 1.2f), rng.Next(0, 4));
                }

                if (!IsNearLake(-9.5f, d))
                {
                    AppendEntry(sb, ref first, "Tree", -9.5f, d, NextRange(rng, 0f, 360f), NextRange(rng, 0.9f, 1.2f), rng.Next(0, 4));
                }
            }

            // Outer rural groves so outer chunks (-3..+3) visibly stream foliage
            const int outerTreeCount = 110;
            for (int i = 0; i < outerTreeCount; i++)
            {
                float tx = NextRange(rng, -175f, 175f);
                float tz = NextRange(rng, -175f, 175f);

                // Keep central building blocks, main roads, and lake basin clear
                if (Mathf.Abs(tx) < 128f && Mathf.Abs(tz) < 125f)
                {
                    continue;
                }

                if (Mathf.Abs(tx) < 12f || Mathf.Abs(tz) < 12f || IsNearLake(tx, tz))
                {
                    continue;
                }

                AppendEntry(sb, ref first, "Tree", tx, tz, NextRange(rng, 0f, 360f), NextRange(rng, 0.8f, 1.35f), i % 4);
            }

            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        private static void GenerateBuildingBlock(
            StringBuilder sb,
            ref bool first,
            System.Random rng,
            float minX,
            float maxX,
            float minZ,
            float maxZ,
            float step)
        {
            float midX = (minX + maxX) * 0.5f;
            float midZ = (minZ + maxZ) * 0.5f;

            for (float bx = minX; bx <= maxX; bx += step)
            {
                for (float bz = minZ; bz <= maxZ; bz += step)
                {
                    if (IsNearLake(bx, bz))
                    {
                        continue;
                    }

                    // Leave interior courtyard space with an occasional shade tree
                    bool isInterior = bx > minX + 4f && bx < maxX - 4f && bz > minZ + 4f && bz < maxZ - 4f;
                    if (isInterior)
                    {
                        if (rng.NextDouble() < 0.45)
                        {
                            AppendEntry(sb, ref first, "Tree", bx, bz, NextRange(rng, 0f, 360f), NextRange(rng, 0.85f, 1.15f), rng.Next(0, 4));
                        }

                        continue;
                    }

                    float jitterX = NextRange(rng, -1.2f, 1.2f);
                    float jitterZ = NextRange(rng, -1.2f, 1.2f);
                    float rotY = DetermineStreetFacingYaw(bx - midX, bz - midZ);
                    float scale = NextRange(rng, 0.88f, 1.16f);
                    int variant = rng.Next(0, 5);

                    AppendEntry(sb, ref first, "Building", bx + jitterX, bz + jitterZ, rotY, scale, variant);
                }
            }
        }

        private static float DetermineStreetFacingYaw(float dx, float dz)
        {
            if (Mathf.Abs(dx) > Mathf.Abs(dz))
            {
                return dx > 0f ? 90f : 270f;
            }

            return dz > 0f ? 0f : 180f;
        }

        private static bool IsNearLake(float x, float z)
        {
            float dx = x - LakeCenterX;
            float dz = z - LakeCenterZ;
            float paddedRadius = LakeRadius + 10f;
            return (dx * dx) + (dz * dz) <= (paddedRadius * paddedRadius);
        }

        private static float NextRange(System.Random rng, float min, float max)
        {
            return min + ((float)rng.NextDouble() * (max - min));
        }

        private static void AppendEntry(
            StringBuilder sb,
            ref bool first,
            string type,
            float x,
            float z,
            float rotationY,
            float scale,
            int variant)
        {
            if (!first)
            {
                sb.Append(",\n");
            }

            first = false;
            sb.Append("    { \"type\": \"");
            sb.Append(type);
            sb.Append("\", \"x\": ");
            sb.Append(x.ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(", \"z\": ");
            sb.Append(z.ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(", \"rotationY\": ");
            sb.Append(rotationY.ToString("F0", CultureInfo.InvariantCulture));
            sb.Append(", \"scale\": ");
            sb.Append(scale.ToString("F2", CultureInfo.InvariantCulture));
            sb.Append(", \"variant\": ");
            sb.Append(variant.ToString(CultureInfo.InvariantCulture));
            sb.Append(" }");
        }
    }

    /// <summary>
    /// Zero-scene pure C# preview helper implementing <see cref="IGameSystem"/> and <see cref="ITickable"/>.
    /// Ensures a camera and directional sun exist when starting from an empty scene, and allows panning
    /// across 50x50 chunks (WASD/Arrows or Android touch drag) whenever no Player object is active yet.
    /// </summary>
    public sealed class WorldTestPreviewSystem : IGameSystem, ITickable
    {
        private const float PanSpeed = 35f;
        private const float TouchPanSensitivity = 0.18f;

        private readonly WorldChunkManager _chunkManager;
        private GameObject _createdCameraGo;
        private GameObject _createdLightGo;
        private Transform _previewCameraTransform;
        private bool _environmentVerified;

        public WorldTestPreviewSystem(WorldChunkManager chunkManager)
        {
            _chunkManager = chunkManager;
        }

        public void Initialize()
        {
            _environmentVerified = false;
        }

        public void Tick(float deltaTime)
        {
            if (!_environmentVerified)
            {
                _environmentVerified = true;
                EnsureSceneCameraAndLighting();
            }

            // Automatically yield control as soon as a real Player target is assigned
            if (_chunkManager == null || _chunkManager.TrackingTarget != null)
            {
                return;
            }

            if (_previewCameraTransform == null)
            {
                Camera mainCam = Camera.main;
                if (mainCam != null)
                {
                    _previewCameraTransform = mainCam.transform;
                }
                else
                {
                    return;
                }
            }

#if ENABLE_LEGACY_INPUT_MANAGER
            Vector3 moveDelta = ReadPreviewPanInput(deltaTime);
            if (moveDelta.sqrMagnitude > 0.00001f)
            {
                _previewCameraTransform.position += moveDelta;
            }
#endif
        }

        public void Shutdown()
        {
            if (_createdCameraGo != null)
            {
                Object.Destroy(_createdCameraGo);
                _createdCameraGo = null;
            }

            if (_createdLightGo != null)
            {
                Object.Destroy(_createdLightGo);
                _createdLightGo = null;
            }

            _previewCameraTransform = null;
        }

        private void EnsureSceneCameraAndLighting()
        {
            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                _createdCameraGo = new GameObject("[PreviewMainCamera]")
                {
                    tag = "MainCamera"
                };
                Object.DontDestroyOnLoad(_createdCameraGo);

                mainCam = _createdCameraGo.AddComponent<Camera>();
                mainCam.clearFlags = CameraClearFlags.SolidColor;
                mainCam.backgroundColor = new Color(0.68f, 0.84f, 0.95f, 1f);
                mainCam.nearClipPlane = 0.5f;
                mainCam.farClipPlane = 260f;

                _createdCameraGo.transform.position = new Vector3(0f, 42f, -38f);
                _createdCameraGo.transform.rotation = Quaternion.Euler(44f, 0f, 0f);
            }

            _previewCameraTransform = mainCam.transform;

#if UNITY_2022_2_OR_NEWER
            Light existingLight = Object.FindFirstObjectByType<Light>();
#else
            Light existingLight = Object.FindObjectOfType<Light>();
#endif
            if (existingLight == null)
            {
                _createdLightGo = new GameObject("[DirectionalSun]");
                Object.DontDestroyOnLoad(_createdLightGo);

                var sun = _createdLightGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.color = new Color(1.0f, 0.95f, 0.84f, 1f);
                sun.intensity = 1.15f;
                sun.shadows = LightShadows.Soft;

                _createdLightGo.transform.rotation = Quaternion.Euler(48f, -36f, 0f);
            }
        }

#if ENABLE_LEGACY_INPUT_MANAGER
        private static Vector3 ReadPreviewPanInput(float deltaTime)
        {
            if (Input.touchCount == 1)
            {
                Touch touch = Input.GetTouch(0);
                if (touch.phase == TouchPhase.Moved)
                {
                    return new Vector3(
                        -touch.deltaPosition.x * TouchPanSensitivity,
                        0f,
                        -touch.deltaPosition.y * TouchPanSensitivity);
                }
            }

            float horizontal = 0f;
            float vertical = 0f;

            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow))
            {
                horizontal += 1f;
            }

            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))
            {
                horizontal -= 1f;
            }

            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))
            {
                vertical += 1f;
            }

            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))
            {
                vertical -= 1f;
            }

            if (Mathf.Approximately(horizontal, 0f) && Mathf.Approximately(vertical, 0f))
            {
                return Vector3.zero;
            }

            Vector3 dir = new Vector3(horizontal, 0f, vertical);
            if (dir.sqrMagnitude > 1f)
            {
                dir.Normalize();
            }

            float speedMultiplier = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 2.2f : 1f;
            return dir * (PanSpeed * speedMultiplier * deltaTime);
        }
#endif
    }
}
