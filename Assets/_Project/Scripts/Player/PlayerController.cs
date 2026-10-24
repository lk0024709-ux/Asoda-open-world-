using Asoda.Core;
using Asoda.World;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.Player
{
    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> player controller backed by a runtime
    /// <see cref="CharacterController"/> component. Supports camera-relative movement, stamina-aware sprinting,
    /// gravity, jumping, articulated hand sockets for combat, and seamless vehicle entry/exit suspension.
    /// </summary>
    public sealed class PlayerController : IGameSystem, ITickable
    {
        public const string PlayerPrefabResourcePath = "Prefabs/Player";
        public const float DefaultWalkSpeed = 5.2f;
        public const float DefaultSprintSpeed = 9.2f;
        public const float DefaultJumpHeight = 1.45f;
        public const float DefaultGravity = -22.0f;

        public const float MaxHealth = 100f;
        public const float MaxArmor = 100f;
        public const float MaxStamina = 100f;

        private const float StaminaSprintDrainPerSec = 16f;
        private const float StaminaRegenPerSec = 22f;
        private const float GroundedStickVelocity = -2.2f;
        private const float CoyoteGraceSeconds = 0.12f;
        private const float RotationSmoothTime = 0.085f;
        private const float SpeedBlendSharpness = 12f;
        private const float VoidSafetyRespawnY = -30f;

        private readonly TouchInputManager _inputManager;
        private readonly WorldChunkManager _chunkManager;
        private readonly Vector3 _initialSpawnPosition;
        private readonly GameObject _customPlayerPrefab;

        private GameObject _playerGo;
        private Transform _playerTransform;
        private CharacterController _characterController;
        private Transform _cameraTransform;
        private Transform _leftArmPivot;
        private Transform _rightArmPivot;
        private Transform _rightHandSocket;

        private Material _bodyMaterial;
        private Material _visorMaterial;

        private float _verticalVelocity;
        private float _currentPlanarSpeed;
        private float _yawVelocity;
        private float _coyoteTimer;
        private bool _controlEnabled = true;
        private bool _staminaExhausted;

        public GameObject PlayerGameObject => _playerGo;
        public Transform PlayerTransform => _playerTransform;
        public Transform LeftArmPivot => _leftArmPivot;
        public Transform RightArmPivot => _rightArmPivot;
        public Transform RightHandSocket => _rightHandSocket;
        public CharacterController Controller => _characterController;
        public bool IsControlEnabled => _controlEnabled;
        public bool IsGrounded => _characterController != null && _characterController.enabled && _characterController.isGrounded;
        public bool IsSprinting { get; private set; }
        public float CurrentSpeed => _currentPlanarSpeed;

        public float Health { get; private set; } = MaxHealth;
        public float Armor { get; private set; } = 50f;
        public float Stamina { get; private set; } = MaxStamina;

        public PlayerController(
            TouchInputManager inputManager,
            WorldChunkManager chunkManager,
            Vector3 initialSpawnPosition,
            GameObject customPlayerPrefab = null)
        {
            _inputManager = inputManager;
            _chunkManager = chunkManager;
            _initialSpawnPosition = initialSpawnPosition;
            _customPlayerPrefab = customPlayerPrefab;
        }

        public void Initialize()
        {
            Health = MaxHealth;
            Armor = 50f;
            Stamina = MaxStamina;
            _controlEnabled = true;
            _staminaExhausted = false;

            SpawnPlayerRuntimeObject();

            WorldChunkManager chunkMgr = _chunkManager;
            if (chunkMgr == null && GameManager.HasInstance)
            {
                chunkMgr = GameManager.Instance.GetSystem<WorldChunkManager>();
            }

            if (chunkMgr != null && _playerTransform != null)
            {
                chunkMgr.SetTrackingTarget(_playerTransform);
            }
        }

        public void SetCameraTransform(Transform cameraTransform)
        {
            _cameraTransform = cameraTransform;
        }

        /// <summary>
        /// Enables or suspends on-foot CharacterController locomotion (used when entering/exiting a Vehicle).
        /// </summary>
        public void SetControlEnabled(bool enabled)
        {
            _controlEnabled = enabled;
            _currentPlanarSpeed = 0f;
            _verticalVelocity = 0f;
            IsSprinting = false;

            if (_characterController != null)
            {
                _characterController.enabled = enabled;
            }
        }

        public bool TryConsumeStamina(float amount)
        {
            if (Stamina < amount)
            {
                return false;
            }

            Stamina = Mathf.Max(0f, Stamina - amount);
            if (Stamina <= 0.5f)
            {
                _staminaExhausted = true;
            }

            return true;
        }

        public void RestoreVitals(float healthGain, float armorGain)
        {
            Health = Mathf.Clamp(Health + healthGain, 0f, MaxHealth);
            Armor = Mathf.Clamp(Armor + armorGain, 0f, MaxArmor);
            Stamina = MaxStamina;
        }

        public void ApplyDamage(float amount)
        {
            if (amount <= 0f)
            {
                return;
            }

            if (Armor > 0f)
            {
                float absorbed = Mathf.Min(Armor, amount * 0.65f);
                Armor -= absorbed;
                amount -= absorbed;
            }

            Health = Mathf.Max(0f, Health - amount);
            if (Health <= 0f)
            {
                Health = MaxHealth;
                Teleport(_initialSpawnPosition);
            }
        }

        public void Tick(float deltaTime)
        {
            if (!_controlEnabled || _characterController == null || _playerTransform == null)
            {
                // Regenerate stamina while seated in a vehicle
                Stamina = Mathf.Min(MaxStamina, Stamina + (StaminaRegenPerSec * deltaTime));
                return;
            }

            if (_cameraTransform == null)
            {
                Camera mainCam = Camera.main;
                if (mainCam != null)
                {
                    _cameraTransform = mainCam.transform;
                }
            }

            if (_playerTransform.position.y < VoidSafetyRespawnY)
            {
                Teleport(_initialSpawnPosition);
                return;
            }

            Vector2 moveInput = _inputManager != null ? _inputManager.MoveInput : Vector2.zero;
            float inputMagnitude = Mathf.Clamp01(moveInput.magnitude);
            bool wantsSprint = _inputManager != null && _inputManager.IsSprinting && !_staminaExhausted;
            bool jumpPressed = _inputManager != null && _inputManager.JumpPressed;

            Vector3 moveDirection = ComputeCameraRelativeDirection(moveInput);

            float desiredSpeed = 0f;
            if (inputMagnitude > 0.02f)
            {
                IsSprinting = wantsSprint && Stamina > 1f;
                desiredSpeed = IsSprinting
                    ? DefaultSprintSpeed
                    : (DefaultWalkSpeed * Mathf.Clamp01(inputMagnitude / TouchInputManager.DefaultSprintEdgeThreshold));

                if (moveDirection.sqrMagnitude > 0.0001f)
                {
                    float targetYaw = Mathf.Atan2(moveDirection.x, moveDirection.z) * Mathf.Rad2Deg;
                    float smoothedYaw = Mathf.SmoothDampAngle(
                        _playerTransform.eulerAngles.y,
                        targetYaw,
                        ref _yawVelocity,
                        RotationSmoothTime,
                        Mathf.Infinity,
                        deltaTime);
                    _playerTransform.rotation = Quaternion.Euler(0f, smoothedYaw, 0f);
                }
            }
            else
            {
                IsSprinting = false;
            }

            if (IsSprinting)
            {
                Stamina = Mathf.Max(0f, Stamina - (StaminaSprintDrainPerSec * deltaTime));
                if (Stamina <= 0.1f)
                {
                    _staminaExhausted = true;
                }
            }
            else
            {
                Stamina = Mathf.Min(MaxStamina, Stamina + (StaminaRegenPerSec * deltaTime));
                if (_staminaExhausted && Stamina >= 20f)
                {
                    _staminaExhausted = false;
                }
            }

            _currentPlanarSpeed = Mathf.Lerp(
                _currentPlanarSpeed,
                desiredSpeed,
                1f - Mathf.Exp(-SpeedBlendSharpness * deltaTime));

            if (_characterController.isGrounded)
            {
                _coyoteTimer = CoyoteGraceSeconds;
                if (_verticalVelocity < 0f)
                {
                    _verticalVelocity = GroundedStickVelocity;
                }
            }
            else
            {
                _coyoteTimer = Mathf.Max(0f, _coyoteTimer - deltaTime);
            }

            if (jumpPressed && _coyoteTimer > 0f)
            {
                _coyoteTimer = 0f;
                _verticalVelocity = Mathf.Sqrt(DefaultJumpHeight * -2f * DefaultGravity);
            }

            _verticalVelocity += DefaultGravity * deltaTime;

            Vector3 horizontalVelocity = moveDirection * _currentPlanarSpeed;
            Vector3 finalVelocity = new Vector3(horizontalVelocity.x, _verticalVelocity, horizontalVelocity.z);
            _characterController.Move(finalVelocity * deltaTime);
        }

        public void Shutdown()
        {
            if (_playerGo != null)
            {
                Object.Destroy(_playerGo);
                _playerGo = null;
            }

            if (_bodyMaterial != null)
            {
                Object.Destroy(_bodyMaterial);
                _bodyMaterial = null;
            }

            if (_visorMaterial != null)
            {
                Object.Destroy(_visorMaterial);
                _visorMaterial = null;
            }

            _playerTransform = null;
            _characterController = null;
            _cameraTransform = null;
            _leftArmPivot = null;
            _rightArmPivot = null;
            _rightHandSocket = null;
        }

        public void Teleport(Vector3 worldPosition)
        {
            if (_playerTransform == null)
            {
                return;
            }

            bool wasEnabled = _characterController != null && _characterController.enabled;
            if (_characterController != null)
            {
                _characterController.enabled = false;
            }

            _playerTransform.position = worldPosition;
            _verticalVelocity = 0f;
            _currentPlanarSpeed = 0f;

            if (_characterController != null && wasEnabled)
            {
                _characterController.enabled = true;
            }
        }

        private Vector3 ComputeCameraRelativeDirection(Vector2 moveInput)
        {
            if (moveInput.sqrMagnitude < 0.0004f)
            {
                return Vector3.zero;
            }

            Vector3 forward = Vector3.forward;
            Vector3 right = Vector3.right;

            if (_cameraTransform != null)
            {
                forward = _cameraTransform.forward;
                right = _cameraTransform.right;
                forward.y = 0f;
                right.y = 0f;
                forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
                right = right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.right;
            }

            Vector3 combined = (forward * moveInput.y) + (right * moveInput.x);
            if (combined.sqrMagnitude > 1f)
            {
                combined.Normalize();
            }

            return combined;
        }

        private void SpawnPlayerRuntimeObject()
        {
            GameObject prefab = _customPlayerPrefab != null
                ? _customPlayerPrefab
                : Resources.Load<GameObject>(PlayerPrefabResourcePath);

            if (prefab != null)
            {
                _playerGo = Object.Instantiate(prefab, _initialSpawnPosition, Quaternion.identity);
                _playerGo.name = "[Player]";
                EnsureHandSocketOnImportedCharacter(_playerGo.transform);
            }
            else
            {
                _playerGo = BuildPlaceholderCapsulePlayer(_initialSpawnPosition);
            }

            _playerGo.tag = "Player";

            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycastLayer >= 0)
            {
                SetLayerRecursively(_playerGo.transform, ignoreRaycastLayer);
            }

            Object.DontDestroyOnLoad(_playerGo);
            _playerTransform = _playerGo.transform;

            _characterController = _playerGo.GetComponent<CharacterController>();
            if (_characterController == null)
            {
                _characterController = _playerGo.AddComponent<CharacterController>();
                _characterController.height = 1.80f;
                _characterController.radius = 0.36f;
                _characterController.center = new Vector3(0f, 0.90f, 0f);
                _characterController.slopeLimit = 45f;
                _characterController.stepOffset = 0.35f;
                _characterController.skinWidth = 0.04f;
                _characterController.minMoveDistance = 0.0005f;
            }
        }

        private void EnsureHandSocketOnImportedCharacter(Transform root)
        {
            Transform foundHand = FindDeepChildContains(root, "RightHand") ?? FindDeepChildContains(root, "Hand_R");
            if (foundHand != null)
            {
                var socketGo = new GameObject("RightHandWeaponSocket");
                _rightHandSocket = socketGo.transform;
                _rightHandSocket.SetParent(foundHand, false);
                return;
            }

            var fallbackSocket = new GameObject("RightHandWeaponSocket");
            _rightHandSocket = fallbackSocket.transform;
            _rightHandSocket.SetParent(root, false);
            _rightHandSocket.localPosition = new Vector3(0.38f, 1.12f, 0.35f);
        }

        private static Transform FindDeepChildContains(Transform parent, string substring)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name.IndexOf(substring, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return child;
                }

                Transform nested = FindDeepChildContains(child, substring);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private GameObject BuildPlaceholderCapsulePlayer(Vector3 spawnPosition)
        {
            var root = new GameObject("[Player]");
            root.transform.position = spawnPosition;

            _bodyMaterial = ChunkPool.CreateInstancedSurfaceMaterial("Asoda_PlayerKurta_Mat", new Color(0.92f, 0.48f, 0.18f, 1f));
            _visorMaterial = ChunkPool.CreateInstancedSurfaceMaterial("Asoda_PlayerAccent_Mat", new Color(0.18f, 0.22f, 0.30f, 1f));

            // Capsule body
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "CapsuleVisual";
            StripCollider(capsule);

            Transform capTransform = capsule.transform;
            capTransform.SetParent(root.transform, false);
            capTransform.localPosition = new Vector3(0f, 0.90f, 0f);
            capTransform.localScale = new Vector3(0.70f, 0.90f, 0.70f);
            capsule.GetComponent<MeshRenderer>().sharedMaterial = _bodyMaterial;

            // Facing visor
            GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "FacingVisor";
            StripCollider(visor);

            Transform visorTransform = visor.transform;
            visorTransform.SetParent(root.transform, false);
            visorTransform.localPosition = new Vector3(0f, 1.44f, 0.28f);
            visorTransform.localScale = new Vector3(0.40f, 0.15f, 0.22f);

            var visorRenderer = visor.GetComponent<MeshRenderer>();
            visorRenderer.sharedMaterial = _visorMaterial;
            visorRenderer.shadowCastingMode = ShadowCastingMode.Off;

            // Articulated Left & Right Arm Pivots for visible Punching & Weapon Attachment
            var leftPivotGo = new GameObject("LeftArmPivot");
            _leftArmPivot = leftPivotGo.transform;
            _leftArmPivot.SetParent(root.transform, false);
            _leftArmPivot.localPosition = new Vector3(-0.42f, 1.18f, 0.08f);

            GameObject leftFist = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leftFist.name = "LeftFist";
            StripCollider(leftFist);
            leftFist.transform.SetParent(_leftArmPivot, false);
            leftFist.transform.localPosition = new Vector3(0f, -0.06f, 0.22f);
            leftFist.transform.localScale = new Vector3(0.16f, 0.16f, 0.38f);
            leftFist.GetComponent<MeshRenderer>().sharedMaterial = _bodyMaterial;

            var rightPivotGo = new GameObject("RightArmPivot");
            _rightArmPivot = rightPivotGo.transform;
            _rightArmPivot.SetParent(root.transform, false);
            _rightArmPivot.localPosition = new Vector3(0.42f, 1.18f, 0.08f);

            GameObject rightFist = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rightFist.name = "RightFist";
            StripCollider(rightFist);
            rightFist.transform.SetParent(_rightArmPivot, false);
            rightFist.transform.localPosition = new Vector3(0f, -0.06f, 0.22f);
            rightFist.transform.localScale = new Vector3(0.16f, 0.16f, 0.38f);
            rightFist.GetComponent<MeshRenderer>().sharedMaterial = _bodyMaterial;

            var socketGo = new GameObject("RightHandWeaponSocket");
            _rightHandSocket = socketGo.transform;
            _rightHandSocket.SetParent(_rightArmPivot, false);
            _rightHandSocket.localPosition = new Vector3(0f, -0.02f, 0.36f);

            return root;
        }

        private static void StripCollider(GameObject go)
        {
            Collider col = go.GetComponent<Collider>();
            if (col != null)
            {
                col.enabled = false;
                Object.Destroy(col);
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            int childCount = root.childCount;
            for (int i = 0; i < childCount; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }
    }
}
