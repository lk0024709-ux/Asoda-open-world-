using Asoda.Core;
using Asoda.World;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.Player
{
    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> player controller backed by a runtime
    /// <see cref="CharacterController"/> component. Supports camera-relative movement, joystick-edge sprinting,
    /// gravity, coyote-time jumping, and automatic <see cref="WorldChunkManager"/> target registration.
    /// </summary>
    public sealed class PlayerController : IGameSystem, ITickable
    {
        public const string PlayerPrefabResourcePath = "Prefabs/Player";
        public const float DefaultWalkSpeed = 5.2f;
        public const float DefaultSprintSpeed = 9.2f;
        public const float DefaultJumpHeight = 1.45f;
        public const float DefaultGravity = -22.0f;

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

        private Material _bodyMaterial;
        private Material _visorMaterial;

        private float _verticalVelocity;
        private float _currentPlanarSpeed;
        private float _yawVelocity;
        private float _coyoteTimer;

        public GameObject PlayerGameObject => _playerGo;
        public Transform PlayerTransform => _playerTransform;
        public CharacterController Controller => _characterController;
        public bool IsGrounded => _characterController != null && _characterController.isGrounded;
        public bool IsSprinting { get; private set; }
        public float CurrentSpeed => _currentPlanarSpeed;

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

        /// <summary>
        /// Binds the active <see cref="ThirdPersonCamera"/> transform so movement aligns with camera forward.
        /// </summary>
        public void SetCameraTransform(Transform cameraTransform)
        {
            _cameraTransform = cameraTransform;
        }

        public void Tick(float deltaTime)
        {
            if (_characterController == null || _playerTransform == null)
            {
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

            // Safety net in case player ever steps off an unbuilt map edge
            if (_playerTransform.position.y < VoidSafetyRespawnY)
            {
                Teleport(_initialSpawnPosition);
                return;
            }

            Vector2 moveInput = _inputManager != null ? _inputManager.MoveInput : Vector2.zero;
            float inputMagnitude = Mathf.Clamp01(moveInput.magnitude);
            bool wantsSprint = _inputManager != null && _inputManager.IsSprinting;
            bool jumpPressed = _inputManager != null && _inputManager.JumpPressed;

            // 1. Camera-relative horizontal movement direction on the XZ plane
            Vector3 moveDirection = ComputeCameraRelativeDirection(moveInput);

            float desiredSpeed = 0f;
            if (inputMagnitude > 0.02f)
            {
                IsSprinting = wantsSprint;
                desiredSpeed = wantsSprint
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

            _currentPlanarSpeed = Mathf.Lerp(
                _currentPlanarSpeed,
                desiredSpeed,
                1f - Mathf.Exp(-SpeedBlendSharpness * deltaTime));

            // 2. Grounding, coyote-time jump, and gravity integration
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

            // 3. Final CharacterController displacement
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
        }

        /// <summary>
        /// Safely teleports a CharacterController-driven player without physics overlap conflicts.
        /// </summary>
        public void Teleport(Vector3 worldPosition)
        {
            if (_characterController == null || _playerTransform == null)
            {
                return;
            }

            _characterController.enabled = false;
            _playerTransform.position = worldPosition;
            _verticalVelocity = 0f;
            _currentPlanarSpeed = 0f;
            _characterController.enabled = true;
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

                if (forward.sqrMagnitude > 0.0001f)
                {
                    forward.Normalize();
                }
                else
                {
                    forward = Vector3.forward;
                }

                if (right.sqrMagnitude > 0.0001f)
                {
                    right.Normalize();
                }
                else
                {
                    right = Vector3.right;
                }
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
            }
            else
            {
                _playerGo = BuildPlaceholderCapsulePlayer(_initialSpawnPosition);
            }

            _playerGo.tag = "Player";

            // Place Player on Ignore Raycast layer (layer 2) so camera collision SphereCast never hits the player capsule
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

        private GameObject BuildPlaceholderCapsulePlayer(Vector3 spawnPosition)
        {
            var root = new GameObject("[Player]");
            root.transform.position = spawnPosition;

            _bodyMaterial = ChunkPool.CreateInstancedSurfaceMaterial("Asoda_PlayerKurta_Mat", new Color(0.92f, 0.48f, 0.18f, 1f));
            _visorMaterial = ChunkPool.CreateInstancedSurfaceMaterial("Asoda_PlayerAccent_Mat", new Color(0.18f, 0.22f, 0.30f, 1f));

            // Visual capsule body (1.8m tall, pivot at feet y = 0)
            GameObject capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            capsule.name = "CapsuleVisual";
            Collider primCollider = capsule.GetComponent<Collider>();
            if (primCollider != null)
            {
                primCollider.enabled = false;
                Object.Destroy(primCollider);
            }

            Transform capTransform = capsule.transform;
            capTransform.SetParent(root.transform, false);
            capTransform.localPosition = new Vector3(0f, 0.90f, 0f);
            capTransform.localScale = new Vector3(0.72f, 0.90f, 0.72f);

            var capRenderer = capsule.GetComponent<MeshRenderer>();
            capRenderer.sharedMaterial = _bodyMaterial;
            capRenderer.shadowCastingMode = ShadowCastingMode.On;

            // Forward direction visor so facing rotation is immediately readable in third-person view
            GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            visor.name = "FacingVisor";
            Collider visorCol = visor.GetComponent<Collider>();
            if (visorCol != null)
            {
                visorCol.enabled = false;
                Object.Destroy(visorCol);
            }

            Transform visorTransform = visor.transform;
            visorTransform.SetParent(root.transform, false);
            visorTransform.localPosition = new Vector3(0f, 1.42f, 0.28f);
            visorTransform.localScale = new Vector3(0.42f, 0.16f, 0.22f);

            var visorRenderer = visor.GetComponent<MeshRenderer>();
            visorRenderer.sharedMaterial = _visorMaterial;
            visorRenderer.shadowCastingMode = ShadowCastingMode.Off;

            return root;
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
