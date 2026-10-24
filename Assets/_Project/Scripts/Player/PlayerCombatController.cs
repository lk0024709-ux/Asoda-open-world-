using Asoda.Core;
using Asoda.Utils;
using Asoda.World;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.Player
{
    /// <summary>
    /// Optional component that can be attached to world props, crates, or target dummies to react to
    /// <see cref="PlayerCombatController"/> melee punches and camera-center weapon raycasts.
    /// </summary>
    public sealed class CombatHitReceiver : MonoBehaviour
    {
        public float Health = 60f;
        private Vector3 _baseScale = Vector3.one;
        private bool _initialized;

        public void ApplyHit(float damage, Vector3 hitPoint, Vector3 hitNormal)
        {
            if (!_initialized)
            {
                _baseScale = transform.localScale;
                _initialized = true;
            }

            Health -= damage;
            if (Health <= 0f)
            {
                Health = 60f;
                transform.localScale = _baseScale * 0.86f;
            }
            else
            {
                transform.localScale = _baseScale * 0.94f;
            }
        }
    }

    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> implementing Hand-to-Hand Punching
    /// and a Raycast Weapon System loaded from <c>Resources/Prefabs/Weapons/</c> (with a zero-prefab
    /// 3-tier LOD procedural fallback attached to the player's hand).
    /// </summary>
    public sealed class PlayerCombatController : IGameSystem, ITickable
    {
        public const string WeaponsResourcesFolder = "Prefabs/Weapons";
        public const int DefaultClipSize = 12;
        public const int DefaultStartingReserveAmmo = 72;
        public const float MaxWeaponRayDistance = 95f;
        public const float MeleePunchRange = 2.05f;

        private const float PunchCooldownSeconds = 0.28f;
        private const float PunchAnimDuration = 0.22f;
        private const float PunchStaminaCost = 7f;
        private const float PunchDamage = 25f;

        private const float FireIntervalSeconds = 0.19f;
        private const float ReloadDurationSeconds = 1.10f;
        private const float BulletDamage = 34f;
        private const int ImpactPoolSize = 6;

        private readonly TouchInputManager _inputManager;
        private readonly PlayerController _playerController;
        private readonly ThirdPersonCamera _thirdPersonCamera;
        private readonly InteractionSystem _interactionSystem;

        private GameObject _equippedWeaponGo;
        private Transform _muzzleTransform;
        private GameObject _muzzleFlashGo;
        private Material _weaponSharedMaterial;
        private Material _fxSharedMaterial;

        private GameObject[] _impactSparkPool;
        private float[] _impactSparkTimers;
        private int _impactPoolCursor;

        private GameObject _tracerBeamGo;
        private float _tracerTimer;

        private int _combatHitLayerMask;
        private string _loadedWeaponDisplayName = "ASODA 9MM";
        private bool _weaponHolstered;

        private int _currentClipAmmo = DefaultClipSize;
        private int _reserveAmmo = DefaultStartingReserveAmmo;

        private float _fireCooldownTimer;
        private float _reloadTimer;
        private bool _isReloading;

        private float _punchCooldownTimer;
        private float _punchAnimTimer;
        private bool _activePunchUsesRightArm;

        private float _recoilPitchKick;
        private float _crosshairBloom;
        private float _hitMarkerTimer;

        public string DisplayWeaponName => _weaponHolstered ? "UNARMED (FISTS)" : _loadedWeaponDisplayName;
        public bool IsWeaponEquipped => !_weaponHolstered && _equippedWeaponGo != null;
        public bool IsReloading => _isReloading;
        public int CurrentClipAmmo => _currentClipAmmo;
        public int ReserveAmmo => _reserveAmmo;
        public float CrosshairBloomNormalized => Mathf.Clamp01(_crosshairBloom);
        public bool HasRecentHitConfirmation => _hitMarkerTimer > 0f;

        public PlayerCombatController(
            TouchInputManager inputManager,
            PlayerController playerController,
            ThirdPersonCamera thirdPersonCamera,
            InteractionSystem interactionSystem)
        {
            _inputManager = inputManager;
            _playerController = playerController;
            _thirdPersonCamera = thirdPersonCamera;
            _interactionSystem = interactionSystem;
        }

        public void Initialize()
        {
            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            _combatHitLayerMask = ignoreRaycastLayer >= 0 ? ~(1 << ignoreRaycastLayer) : ~0;

            _currentClipAmmo = DefaultClipSize;
            _reserveAmmo = DefaultStartingReserveAmmo;
            _weaponHolstered = false;
            _isReloading = false;
            _fireCooldownTimer = 0f;
            _punchCooldownTimer = 0f;
            _punchAnimTimer = 0f;
            _crosshairBloom = 0f;
            _hitMarkerTimer = 0f;

            _weaponSharedMaterial = ChunkPool.CreateInstancedSurfaceMaterial(
                "Asoda_WeaponMetal_Mat",
                new Color(0.24f, 0.26f, 0.30f, 1f));
            _fxSharedMaterial = ChunkPool.CreateInstancedSurfaceMaterial(
                "Asoda_CombatFx_Mat",
                new Color(1.0f, 0.82f, 0.26f, 1f));

            LoadAndAttachWeaponToHand();
            BuildCombatFxPool();
        }

        /// <summary>
        /// Toggles between Armed firearm mode and Unarmed melee mode (invoked when tapping the Top-Right Weapon Slot UI).
        /// </summary>
        public void ToggleWeaponHolsterOrReload()
        {
            if (!_weaponHolstered && !_isReloading && _currentClipAmmo < DefaultClipSize && _reserveAmmo > 0)
            {
                BeginReload();
                return;
            }

            SetWeaponHolstered(!_weaponHolstered);
        }

        public void RefillAmmo(int extraReserve = 48)
        {
            _currentClipAmmo = DefaultClipSize;
            _reserveAmmo = Mathf.Min(180, _reserveAmmo + extraReserve);
            _isReloading = false;
        }

        public void Tick(float deltaTime)
        {
            TickPooledCombatFx(deltaTime);

            if (_fireCooldownTimer > 0f)
            {
                _fireCooldownTimer = Mathf.Max(0f, _fireCooldownTimer - deltaTime);
            }

            if (_punchCooldownTimer > 0f)
            {
                _punchCooldownTimer = Mathf.Max(0f, _punchCooldownTimer - deltaTime);
            }

            if (_hitMarkerTimer > 0f)
            {
                _hitMarkerTimer = Mathf.Max(0f, _hitMarkerTimer - deltaTime);
            }

            _crosshairBloom = Mathf.MoveTowards(_crosshairBloom, 0f, 2.6f * deltaTime);
            _recoilPitchKick = Mathf.Lerp(_recoilPitchKick, 0f, 1f - Mathf.Exp(-16f * deltaTime));

            // Suspend combat actions while seated inside a vehicle
            if (_interactionSystem != null && _interactionSystem.IsDriving)
            {
                ResetArmPoses();
                return;
            }

            if (_inputManager != null && _inputManager.WeaponSwitchPressed)
            {
                SetWeaponHolstered(!_weaponHolstered);
            }

            // Reload progression
            if (_isReloading)
            {
                _reloadTimer -= deltaTime;
                if (_reloadTimer <= 0f)
                {
                    CompleteReload();
                }
            }

            // 1. Hand-to-Hand Punching (triggered by circular Punch button)
            if (_inputManager != null && _inputManager.PunchPressed && _punchCooldownTimer <= 0f)
            {
                ExecuteMeleePunch();
            }

            // 2. Raycast Weapon Shooting (triggered by circular Shoot button)
            bool wantsToShoot = _inputManager != null && (_inputManager.ShootPressed || _inputManager.ShootHeld);
            if (wantsToShoot && _fireCooldownTimer <= 0f)
            {
                if (_weaponHolstered)
                {
                    SetWeaponHolstered(false);
                }

                TryFireEquippedWeapon();
            }

            UpdateArmCombatAnimation(deltaTime);
        }

        public void Shutdown()
        {
            if (_equippedWeaponGo != null)
            {
                Object.Destroy(_equippedWeaponGo);
                _equippedWeaponGo = null;
            }

            if (_tracerBeamGo != null)
            {
                Object.Destroy(_tracerBeamGo);
                _tracerBeamGo = null;
            }

            if (_impactSparkPool != null)
            {
                for (int i = 0; i < _impactSparkPool.Length; i++)
                {
                    if (_impactSparkPool[i] != null)
                    {
                        Object.Destroy(_impactSparkPool[i]);
                    }
                }

                _impactSparkPool = null;
            }

            if (_weaponSharedMaterial != null)
            {
                Object.Destroy(_weaponSharedMaterial);
                _weaponSharedMaterial = null;
            }

            if (_fxSharedMaterial != null)
            {
                Object.Destroy(_fxSharedMaterial);
                _fxSharedMaterial = null;
            }
        }

        private void SetWeaponHolstered(bool holstered)
        {
            _weaponHolstered = holstered;
            if (_equippedWeaponGo != null)
            {
                _equippedWeaponGo.SetActive(!holstered);
            }
        }

        private void ExecuteMeleePunch()
        {
            _punchCooldownTimer = PunchCooldownSeconds;
            _punchAnimTimer = PunchAnimDuration;
            _activePunchUsesRightArm = !_activePunchUsesRightArm;

            if (_playerController != null)
            {
                _playerController.TryConsumeStamina(PunchStaminaCost);
            }

            AlignPlayerToCameraYaw();

            Transform playerTransform = _playerController != null ? _playerController.PlayerTransform : null;
            if (playerTransform == null)
            {
                return;
            }

            Vector3 origin = playerTransform.position + new Vector3(0f, 1.15f, 0f);
            Vector3 direction = playerTransform.forward;

            if (Physics.SphereCast(
                    origin,
                    0.42f,
                    direction,
                    out RaycastHit hit,
                    MeleePunchRange,
                    _combatHitLayerMask,
                    QueryTriggerInteraction.Ignore))
            {
                SpawnPooledImpactSpark(hit.point + (hit.normal * 0.04f), isBullet: false);
                _hitMarkerTimer = 0.16f;
                _crosshairBloom = Mathf.Clamp01(_crosshairBloom + 0.35f);

                CombatHitReceiver receiver = hit.collider.GetComponentInParent<CombatHitReceiver>();
                if (receiver != null)
                {
                    receiver.ApplyHit(PunchDamage, hit.point, hit.normal);
                }
            }
        }

        private void TryFireEquippedWeapon()
        {
            if (_isReloading)
            {
                return;
            }

            if (_currentClipAmmo <= 0)
            {
                if (_reserveAmmo > 0)
                {
                    BeginReload();
                }

                return;
            }

            Camera cam = _thirdPersonCamera != null ? _thirdPersonCamera.CameraComponent : Camera.main;
            if (cam == null)
            {
                return;
            }

            // Reduce ammo & apply recoil/bloom
            _currentClipAmmo--;
            _fireCooldownTimer = FireIntervalSeconds;
            _recoilPitchKick = -24f;
            _crosshairBloom = Mathf.Clamp01(_crosshairBloom + 0.48f);

            AlignPlayerToCameraYaw();

            // Raycast strictly from exact Camera Viewport Center (0.5, 0.5)
            Ray aimRay = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            Vector3 tracerEndPoint;

            if (Physics.Raycast(
                    aimRay,
                    out RaycastHit hit,
                    MaxWeaponRayDistance,
                    _combatHitLayerMask,
                    QueryTriggerInteraction.Ignore))
            {
                tracerEndPoint = hit.point;
                SpawnPooledImpactSpark(hit.point + (hit.normal * 0.03f), isBullet: true);
                _hitMarkerTimer = 0.15f;

                CombatHitReceiver receiver = hit.collider.GetComponentInParent<CombatHitReceiver>();
                if (receiver != null)
                {
                    receiver.ApplyHit(BulletDamage, hit.point, hit.normal);
                }
            }
            else
            {
                tracerEndPoint = aimRay.GetPoint(MaxWeaponRayDistance);
            }

            Vector3 muzzleOrigin = _muzzleTransform != null
                ? _muzzleTransform.position
                : (_playerController != null && _playerController.PlayerTransform != null
                    ? _playerController.PlayerTransform.position + new Vector3(0f, 1.3f, 0.4f)
                    : aimRay.origin);

            TriggerMuzzleAndTracerFx(muzzleOrigin, tracerEndPoint);

            if (_currentClipAmmo <= 0 && _reserveAmmo > 0)
            {
                BeginReload();
            }
        }

        private void BeginReload()
        {
            if (_isReloading || _reserveAmmo <= 0 || _currentClipAmmo >= DefaultClipSize)
            {
                return;
            }

            _isReloading = true;
            _reloadTimer = ReloadDurationSeconds;
        }

        private void CompleteReload()
        {
            _isReloading = false;
            int needed = DefaultClipSize - _currentClipAmmo;
            int loaded = Mathf.Min(needed, _reserveAmmo);
            _currentClipAmmo += loaded;
            _reserveAmmo -= loaded;
        }

        private void AlignPlayerToCameraYaw()
        {
            if (_playerController == null || _playerController.PlayerTransform == null || _thirdPersonCamera == null)
            {
                return;
            }

            _playerController.PlayerTransform.rotation = Quaternion.Euler(0f, _thirdPersonCamera.CurrentYaw, 0f);
        }

        private void UpdateArmCombatAnimation(float deltaTime)
        {
            if (_playerController == null)
            {
                return;
            }

            Transform leftArm = _playerController.LeftArmPivot;
            Transform rightArm = _playerController.RightArmPivot;

            float punchExtension = 0f;
            if (_punchAnimTimer > 0f)
            {
                _punchAnimTimer = Mathf.Max(0f, _punchAnimTimer - deltaTime);
                float normalized = 1f - (_punchAnimTimer / PunchAnimDuration);
                punchExtension = Mathf.Sin(normalized * Mathf.PI);
            }

            if (leftArm != null)
            {
                float leftZ = (!_activePunchUsesRightArm ? punchExtension * 0.52f : 0f);
                leftArm.localPosition = new Vector3(-0.42f, 1.18f, 0.08f + leftZ);
                leftArm.localRotation = Quaternion.Euler(!_activePunchUsesRightArm ? -punchExtension * 18f : 0f, 0f, 0f);
            }

            if (rightArm != null)
            {
                float rightZ = (_activePunchUsesRightArm ? punchExtension * 0.52f : 0f);
                float aimRaise = (!_weaponHolstered ? -10f : 0f) + _recoilPitchKick + (_isReloading ? 28f : 0f);
                rightArm.localPosition = new Vector3(0.42f, 1.18f, 0.08f + rightZ);
                rightArm.localRotation = Quaternion.Euler(aimRaise, 0f, 0f);
            }
        }

        private void ResetArmPoses()
        {
            if (_playerController == null)
            {
                return;
            }

            if (_playerController.LeftArmPivot != null)
            {
                _playerController.LeftArmPivot.localPosition = new Vector3(-0.42f, 1.18f, 0.08f);
                _playerController.LeftArmPivot.localRotation = Quaternion.identity;
            }

            if (_playerController.RightArmPivot != null)
            {
                _playerController.RightArmPivot.localPosition = new Vector3(0.42f, 1.18f, 0.08f);
                _playerController.RightArmPivot.localRotation = Quaternion.identity;
            }
        }

        private void LoadAndAttachWeaponToHand()
        {
            Transform handSocket = _playerController != null ? _playerController.RightHandSocket : null;

            // Attempt to load weapon prefab from Resources/Prefabs/Weapons/
            GameObject loadedPrefab = Resources.Load<GameObject>(WeaponsResourcesFolder + "/Pistol");
            if (loadedPrefab == null)
            {
                GameObject[] allWeaponPrefabs = Resources.LoadAll<GameObject>(WeaponsResourcesFolder);
                if (allWeaponPrefabs != null && allWeaponPrefabs.Length > 0)
                {
                    loadedPrefab = allWeaponPrefabs[0];
                }
            }

            if (loadedPrefab != null)
            {
                _equippedWeaponGo = Object.Instantiate(loadedPrefab);
                _equippedWeaponGo.name = loadedPrefab.name;
                _loadedWeaponDisplayName = loadedPrefab.name.ToUpperInvariant();
            }
            else
            {
                _equippedWeaponGo = BuildProceduralLowPolyPistol();
                _loadedWeaponDisplayName = "ASODA 9MM";
            }

            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycastLayer >= 0)
            {
                SetLayerRecursively(_equippedWeaponGo.transform, ignoreRaycastLayer);
            }

            if (handSocket != null)
            {
                _equippedWeaponGo.transform.SetParent(handSocket, worldPositionStays: false);
                _equippedWeaponGo.transform.localPosition = Vector3.zero;
                _equippedWeaponGo.transform.localRotation = Quaternion.identity;
            }

            Transform existingMuzzle = _equippedWeaponGo.transform.Find("MuzzlePoint");
            if (existingMuzzle != null)
            {
                _muzzleTransform = existingMuzzle;
            }
            else
            {
                var muzzleGo = new GameObject("MuzzlePoint");
                _muzzleTransform = muzzleGo.transform;
                _muzzleTransform.SetParent(_equippedWeaponGo.transform, false);
                _muzzleTransform.localPosition = new Vector3(0f, 0.06f, 0.34f);
            }

            _muzzleFlashGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _muzzleFlashGo.name = "MuzzleFlashFx";
            Object.Destroy(_muzzleFlashGo.GetComponent<Collider>());
            if (ignoreRaycastLayer >= 0)
            {
                _muzzleFlashGo.layer = ignoreRaycastLayer;
            }

            _muzzleFlashGo.transform.SetParent(_muzzleTransform, false);
            _muzzleFlashGo.transform.localPosition = new Vector3(0f, 0f, 0.06f);
            _muzzleFlashGo.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            _muzzleFlashGo.transform.localScale = new Vector3(0.11f, 0.11f, 0.16f);

            var flashRenderer = _muzzleFlashGo.GetComponent<MeshRenderer>();
            flashRenderer.sharedMaterial = _fxSharedMaterial;
            flashRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _muzzleFlashGo.SetActive(false);
        }

        private GameObject BuildProceduralLowPolyPistol()
        {
            var root = new GameObject("Weapon_Asoda9mm");

            // LOD0: Detailed Slide + Brass Sight + Grip
            CreateWeaponBox(root.transform, "Slide_LOD0", new Vector3(0f, 0.055f, 0.14f), new Vector3(0.065f, 0.075f, 0.36f), _weaponSharedMaterial, new Color(0.20f, 0.23f, 0.28f));
            CreateWeaponBox(root.transform, "BarrelAccent_LOD0", new Vector3(0f, 0.06f, 0.33f), new Vector3(0.035f, 0.035f, 0.06f), _weaponSharedMaterial, new Color(0.88f, 0.66f, 0.22f));
            CreateWeaponBox(root.transform, "Grip_LOD0", new Vector3(0f, -0.045f, 0.01f), new Vector3(0.058f, 0.15f, 0.09f), _weaponSharedMaterial, new Color(0.36f, 0.22f, 0.14f));

            // LOD1: Simplified Slide + Grip
            CreateWeaponBox(root.transform, "Slide_LOD1", new Vector3(0f, 0.055f, 0.14f), new Vector3(0.065f, 0.075f, 0.36f), _weaponSharedMaterial, new Color(0.20f, 0.23f, 0.28f));
            CreateWeaponBox(root.transform, "Grip_LOD1", new Vector3(0f, -0.045f, 0.01f), new Vector3(0.058f, 0.15f, 0.09f), _weaponSharedMaterial, new Color(0.36f, 0.22f, 0.14f));

            // LOD2: Single silhouette block
            CreateWeaponBox(root.transform, "Body_LOD2", new Vector3(0f, 0.02f, 0.12f), new Vector3(0.065f, 0.14f, 0.34f), _weaponSharedMaterial, new Color(0.20f, 0.23f, 0.28f));

            LODGroupSetup.ConfigureWeaponLOD(root);
            return root;
        }

        private static void CreateWeaponBox(Transform parent, string name, Vector3 localPos, Vector3 localScale, Material sharedMat, Color tint)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            Object.Destroy(box.GetComponent<Collider>());
            box.transform.SetParent(parent, false);
            box.transform.localPosition = localPos;
            box.transform.localScale = localScale;

            var mr = box.GetComponent<MeshRenderer>();
            mr.sharedMaterial = sharedMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            MaterialVariantApplier.ApplyColor(new[] { mr }, tint);
        }

        private void BuildCombatFxPool()
        {
            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");

            _impactSparkPool = new GameObject[ImpactPoolSize];
            _impactSparkTimers = new float[ImpactPoolSize];

            for (int i = 0; i < ImpactPoolSize; i++)
            {
                GameObject spark = GameObject.CreatePrimitive(PrimitiveType.Cube);
                spark.name = $"[CombatImpactFx_{i}]";
                Object.Destroy(spark.GetComponent<Collider>());
                if (ignoreRaycastLayer >= 0)
                {
                    spark.layer = ignoreRaycastLayer;
                }

                Object.DontDestroyOnLoad(spark);
                var mr = spark.GetComponent<MeshRenderer>();
                mr.sharedMaterial = _fxSharedMaterial;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                spark.SetActive(false);

                _impactSparkPool[i] = spark;
            }

            _tracerBeamGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _tracerBeamGo.name = "[BulletTracerBeam]";
            Object.Destroy(_tracerBeamGo.GetComponent<Collider>());
            if (ignoreRaycastLayer >= 0)
            {
                _tracerBeamGo.layer = ignoreRaycastLayer;
            }

            Object.DontDestroyOnLoad(_tracerBeamGo);
            var tracerRenderer = _tracerBeamGo.GetComponent<MeshRenderer>();
            tracerRenderer.sharedMaterial = _fxSharedMaterial;
            tracerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            _tracerBeamGo.SetActive(false);
        }

        private void SpawnPooledImpactSpark(Vector3 worldPos, bool isBullet)
        {
            if (_impactSparkPool == null || _impactSparkPool.Length == 0)
            {
                return;
            }

            int idx = _impactPoolCursor;
            _impactPoolCursor = (_impactPoolCursor + 1) % _impactSparkPool.Length;

            GameObject spark = _impactSparkPool[idx];
            if (spark == null)
            {
                return;
            }

            spark.transform.position = worldPos;
            spark.transform.rotation = Quaternion.Euler(35f, idx * 55f, 25f);
            float s = isBullet ? 0.20f : 0.28f;
            spark.transform.localScale = new Vector3(s, s, s);
            spark.SetActive(true);
            _impactSparkTimers[idx] = 0.11f;
        }

        private void TriggerMuzzleAndTracerFx(Vector3 start, Vector3 end)
        {
            if (_muzzleFlashGo != null)
            {
                _muzzleFlashGo.SetActive(true);
            }

            if (_tracerBeamGo != null)
            {
                Vector3 delta = end - start;
                float length = delta.magnitude;
                if (length > 0.25f)
                {
                    float clampedLen = Mathf.Min(length, 32f);
                    Vector3 dir = delta / length;
                    _tracerBeamGo.transform.position = start + (dir * (clampedLen * 0.5f));
                    _tracerBeamGo.transform.rotation = Quaternion.LookRotation(dir);
                    _tracerBeamGo.transform.localScale = new Vector3(0.025f, 0.025f, clampedLen);
                    _tracerBeamGo.SetActive(true);
                }
            }

            _tracerTimer = 0.055f;
        }

        private void TickPooledCombatFx(float deltaTime)
        {
            if (_tracerTimer > 0f)
            {
                _tracerTimer -= deltaTime;
                if (_tracerTimer <= 0f)
                {
                    if (_muzzleFlashGo != null)
                    {
                        _muzzleFlashGo.SetActive(false);
                    }

                    if (_tracerBeamGo != null)
                    {
                        _tracerBeamGo.SetActive(false);
                    }
                }
            }

            if (_impactSparkPool == null)
            {
                return;
            }

            for (int i = 0; i < _impactSparkPool.Length; i++)
            {
                if (_impactSparkTimers[i] > 0f)
                {
                    _impactSparkTimers[i] -= deltaTime;
                    GameObject spark = _impactSparkPool[i];
                    if (spark != null)
                    {
                        if (_impactSparkTimers[i] <= 0f)
                        {
                            spark.SetActive(false);
                        }
                        else
                        {
                            spark.transform.localScale *= (1f - (5.5f * deltaTime));
                        }
                    }
                }
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }
    }
}
