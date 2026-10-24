using System;
using UnityEngine;

namespace Asoda.Utils
{
    /// <summary>
    /// Applies per-instance color variations to Vehicles, Weapons, and repeating low-poly props
    /// using a shared <see cref="MaterialPropertyBlock"/> so Unity's GPU Instancing stays intact
    /// and zero duplicate Material instances are ever allocated in RAM.
    /// </summary>
    public static class MaterialVariantApplier
    {
        private static readonly int BaseColorPropertyId = Shader.PropertyToID("_BaseColor");
        private static readonly int LegacyColorPropertyId = Shader.PropertyToID("_Color");
        private static readonly MaterialPropertyBlock SharedBlock = new MaterialPropertyBlock();

        /// <summary>
        /// Curated Indian street vehicle & auto-rickshaw body colors.
        /// </summary>
        public static readonly Color[] VehicleBodyPalette =
        {
            new Color(0.14f, 0.56f, 0.28f, 1f), // Classic Green/Saffron Auto-Rickshaw
            new Color(0.90f, 0.68f, 0.14f, 1f), // Golden-Yellow City Auto / Taxi
            new Color(0.78f, 0.23f, 0.20f, 1f), // Mahindra / Rural Tractor Crimson
            new Color(0.20f, 0.46f, 0.78f, 1f), // Jodhpur Highway Blue Truck/Rickshaw
            new Color(0.89f, 0.90f, 0.92f, 1f), // White Utility Bolero / Van
            new Color(0.83f, 0.62f, 0.38f, 1f)  // Desert Sand Jeep
        };

        /// <summary>
        /// Curated low-poly weapon finish variations (receivers, stocks, melee blades).
        /// </summary>
        public static readonly Color[] WeaponFinishPalette =
        {
            new Color(0.26f, 0.28f, 0.31f, 1f), // Matte Gunmetal Steel
            new Color(0.46f, 0.30f, 0.18f, 1f), // Burnished Indian Rosewood / Walnut
            new Color(0.32f, 0.38f, 0.27f, 1f), // Olive Field Polymer
            new Color(0.72f, 0.61f, 0.42f, 1f), // Desert Khaki / Tan Coating
            new Color(0.68f, 0.54f, 0.24f, 1f)  // Ceremonial Brass Accent
        };

        /// <summary>
        /// Ensures GPU Instancing is enabled on every shared material across a GameObject hierarchy
        /// without ever calling renderer.material (which would clone materials).
        /// </summary>
        public static void EnableGpuInstancing(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            EnableGpuInstancing(renderers);
        }

        /// <summary>
        /// Enables GPU Instancing on pre-cached renderers with zero GetComponent overhead.
        /// </summary>
        public static void EnableGpuInstancing(Renderer[] renderers)
        {
            if (renderers == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null)
                {
                    continue;
                }

                Material[] sharedMats = r.sharedMaterials;
                for (int m = 0; m < sharedMats.Length; m++)
                {
                    Material mat = sharedMats[m];
                    if (mat != null && !mat.enableInstancing)
                    {
                        mat.enableInstancing = true;
                    }
                }
            }
        }

        /// <summary>
        /// Applies a vehicle paint variation across all body renderers (including LOD0/LOD1/LOD2)
        /// via MaterialPropertyBlock without breaking GPU Instancing.
        /// </summary>
        public static void ApplyVehicleVariant(GameObject vehicleRoot, int variantIndex, bool preserveTextureAlbedo = false)
        {
            if (vehicleRoot == null)
            {
                return;
            }

            MeshRenderer[] renderers = CollectPrimaryTintRenderers(vehicleRoot, isVehicle: true);
            ApplyPaletteVariant(renderers, VehicleBodyPalette, variantIndex, preserveTextureAlbedo ? 0.30f : 1.0f);
        }

        /// <summary>
        /// Applies a weapon finish variation across all primary weapon renderers (including LOD0/LOD1/LOD2)
        /// via MaterialPropertyBlock without creating new Material instances.
        /// </summary>
        public static void ApplyWeaponVariant(GameObject weaponRoot, int variantIndex, bool preserveTextureAlbedo = false)
        {
            if (weaponRoot == null)
            {
                return;
            }

            MeshRenderer[] renderers = CollectPrimaryTintRenderers(weaponRoot, isVehicle: false);
            ApplyPaletteVariant(renderers, WeaponFinishPalette, variantIndex, preserveTextureAlbedo ? 0.25f : 1.0f);
        }

        /// <summary>
        /// Applies a palette color directly to pre-cached renderers (zero allocations, hot-path safe).
        /// </summary>
        public static void ApplyPaletteVariant(
            MeshRenderer[] renderers,
            Color[] palette,
            int variantIndex,
            float tintStrength = 1.0f)
        {
            if (renderers == null || renderers.Length == 0 || palette == null || palette.Length == 0)
            {
                return;
            }

            int safeIndex = (variantIndex & 0x7FFFFFFF) % palette.Length;
            Color targetColor = palette[safeIndex];
            if (tintStrength < 0.999f)
            {
                targetColor = Color.Lerp(Color.white, targetColor, Mathf.Clamp01(tintStrength));
            }

            ApplyColor(renderers, targetColor);
        }

        /// <summary>
        /// Writes a per-instance _BaseColor / _Color override using the single shared MaterialPropertyBlock.
        /// </summary>
        public static void ApplyColor(MeshRenderer[] renderers, Color color)
        {
            if (renderers == null || renderers.Length == 0)
            {
                return;
            }

            SharedBlock.Clear();
            SharedBlock.SetColor(BaseColorPropertyId, color);
            SharedBlock.SetColor(LegacyColorPropertyId, color);

            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer r = renderers[i];
                if (r != null)
                {
                    r.SetPropertyBlock(SharedBlock);
                }
            }
        }

        /// <summary>
        /// Clears any per-instance MaterialPropertyBlock overrides from the given renderers.
        /// </summary>
        public static void ClearOverrides(MeshRenderer[] renderers)
        {
            if (renderers == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    renderers[i].SetPropertyBlock(null);
                }
            }
        }

        /// <summary>
        /// Collects body/receiver renderers across all LOD tiers while skipping tires, glass, canopies, or muzzle flashes
        /// so multi-LOD vehicles and weapons maintain consistent paint colors at every distance.
        /// </summary>
        public static MeshRenderer[] CollectPrimaryTintRenderers(GameObject root, bool isVehicle)
        {
            if (root == null)
            {
                return Array.Empty<MeshRenderer>();
            }

            MeshRenderer[] all = root.GetComponentsInChildren<MeshRenderer>(true);
            if (all.Length == 0)
            {
                return all;
            }

            EnableGpuInstancing(all);

            int matchCount = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (ShouldTintRenderer(all[i], isVehicle))
                {
                    matchCount++;
                }
            }

            // Fallback: if no renderer matched naming heuristics, tint the first primary renderer
            if (matchCount == 0)
            {
                return new[] { all[0] };
            }

            var filtered = new MeshRenderer[matchCount];
            int writeIndex = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (ShouldTintRenderer(all[i], isVehicle))
                {
                    filtered[writeIndex++] = all[i];
                }
            }

            return filtered;
        }

        private static bool ShouldTintRenderer(MeshRenderer renderer, bool isVehicle)
        {
            if (renderer == null)
            {
                return false;
            }

            string name = renderer.gameObject.name;
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }

            if (isVehicle)
            {
                // Skip wheels/tires, glass windshields, headlights, and soft-top auto canopies
                if (ContainsIgnoreCase(name, "Wheel") ||
                    ContainsIgnoreCase(name, "Tire") ||
                    ContainsIgnoreCase(name, "Tyre") ||
                    ContainsIgnoreCase(name, "Glass") ||
                    ContainsIgnoreCase(name, "Window") ||
                    ContainsIgnoreCase(name, "Canopy") ||
                    ContainsIgnoreCase(name, "Light"))
                {
                    return false;
                }

                return true;
            }

            // For weapons: skip scopes/lenses and muzzle attachments
            if (ContainsIgnoreCase(name, "Glass") ||
                ContainsIgnoreCase(name, "Lens") ||
                ContainsIgnoreCase(name, "Flash"))
            {
                return false;
            }

            return true;
        }

        private static bool ContainsIgnoreCase(string source, string value)
        {
            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
