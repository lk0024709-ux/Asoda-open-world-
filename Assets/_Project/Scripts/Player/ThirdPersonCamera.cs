using Asoda.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Asoda.Player
{
    /// <summary>
    /// Smooth, collision-aware third-person & vehicle driving camera implementing <see cref="IGameSystem"/> and <see cref="ILateTickable"/>.
    /// Instantiates at runtime tagged as <c>MainCamera</c>, orbits via <see cref="TouchInputManager.LookInput"/>
    /// with clamped pitch [-30°, 70°], follows via <see cref="Vector3.SmoothDamp"/>, and pulls forward
    /// using <see cref="Physics.SphereCast"/> to prevent clipping into buildings or trees.
    /// </summary>
    public sealed class ThirdPersonCamera : IGameSystem, ILateTickable
    {
        public const float DefaultDistance = 4.6f;
        public const float DrivingDistance = 7.2f;
        public const float MinCollisionDistance = 0.55f;
        public const float HeadPivotHeight = 1.52f;
        public const float DrivingPivotHeight = 1.85f;
        public const float MinPitchDegrees = -30f;
        public const float MaxPitchDegrees = 70f;

        private const float OnFootFov = 60f;
        private const float DrivingFov = 66f;
        private const float FollowSmoothTime = 0.075f;
        private const float RotationSmoothTime = 0.035f;
        private const float CollisionSphereRadius = 0.24f;
        private const float CollisionWallOffset = 0.14f;
        private const float CollisionPullInSharpness = 28f;
        private const float CollisionRecoverSmoothTime = 0.18f;
        private const float DefaultLookSensitivityX = 0.22f;
        private const float DefaultLookSensitivityY = 0.18f;

        private readonly TouchInputManager _inputManager;
        private Transform _followTarget;

        private GameObject _cameraGo;
        private Transform _cameraTransform;
        private Camera _camera;
        private int _collisionLayerMask;

        private Vector3 _smoothedPivot;
        private Vector3 _pivotFollowVelocity;

        private float _targetYaw;
        private float _targetPitch = 16f;
        private float _currentYaw;
        private float _currentPitch = 16f;
        private float _yawVelocity;
        private float _pitchVelocity;

        private float _desiredMaxDistance = DefaultDistance;
        private float _desiredPivotHeight = HeadPivotHeight;
        private float _currentDistance = DefaultDistance;
        private float _distanceRecoverVelocity;
        private float _manualLookCooldown;
        private bool _isDrivingMode;

        public Camera CameraComponent => _camera;
        public Transform CameraTransform => _cameraTransform;
        public float CurrentYaw => _currentYaw;
        public bool IsDrivingMode => _isDrivingMode;
        public float LookSensitivityX { get; set; } = DefaultLookSensitivityX;
        public float LookSensitivityY { get; set; } = DefaultLookSensitivityY;

        public ThirdPersonCamera(Transform followTarget, TouchInputManager inputManager)
        {
            _followTarget = followTarget;
            _inputManager = inputManager;
        }

        public void Initialize()
        {
            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            _collisionLayerMask = ignoreRaycastLayer >= 0 ? ~(1 << ignoreRaycastLayer) : ~0;

            DisablePreExistingSceneCameras();
            CreateRuntimeCamera();
            SnapImmediatelyToTarget();
        }

        public void SetFollowTarget(Transform target, bool snapImmediately = true)
        {
            _followTarget = target;
            if (snapImmediately)
            {
                SnapImmediatelyToTarget();
            }
        }

        /// <summary>
        /// Switches smoothly between On-Foot camera framing and GTA-style Vehicle Driving chase framing.
        /// </summary>
        public void SetVehicleDrivingMode(Transform target, bool drivingMode)
        {
            _followTarget = target;
            _isDrivingMode = drivingMode;
            _desiredMaxDistance = drivingMode ? DrivingDistance : DefaultDistance;
            _desiredPivotHeight = drivingMode ? DrivingPivotHeight : HeadPivotHeight;
            _manualLookCooldown = 0f;

            if (drivingMode && target != null)
            {
                _targetYaw = target.eulerAngles.y;
                _targetPitch = 18f;
            }
        }

        public void LateTick(float deltaTime)
        {
            if (_cameraTransform == null || _followTarget == null)
            {
                return;
            }

            // Smoothly blend FOV between on-foot and driving
            if (_camera != null)
            {
                float targetFov = _isDrivingMode ? DrivingFov : OnFootFov;
                if (Mathf.Abs(_camera.fieldOfView - targetFov) > 0.05f)
                {
                    _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, targetFov, 1f - Mathf.Exp(-8f * deltaTime));
                }
            }

            // 1. Accumulate right-side touch swipe input & clamp pitch strictly to [-30°, 70°]
            Vector2 lookDelta = _inputManager != null ? _inputManager.LookInput : Vector2.zero;
            if (lookDelta.sqrMagnitude > 0.00001f)
            {
                _targetYaw += lookDelta.x * LookSensitivityX;
                _targetPitch = Mathf.Clamp(
                    _targetPitch - (lookDelta.y * LookSensitivityY),
                    MinPitchDegrees,
                    MaxPitchDegrees);
                _manualLookCooldown = 1.35f;
            }
            else if (_isDrivingMode)
            {
                // Gently swing driving camera behind vehicle heading when steering without manual camera drag
                _manualLookCooldown = Mathf.Max(0f, _manualLookCooldown - deltaTime);
                Vector2 move = _inputManager != null ? _inputManager.MoveInput : Vector2.zero;
                if (_manualLookCooldown <= 0f && move.sqrMagnitude > 0.04f)
                {
                    float vehicleYaw = _followTarget.eulerAngles.y;
                    _targetYaw = Mathf.LerpAngle(_targetYaw, vehicleYaw, 1f - Mathf.Exp(-3.2f * deltaTime));
                }
            }

            _currentYaw = Mathf.SmoothDampAngle(_currentYaw, _targetYaw, ref _yawVelocity, RotationSmoothTime, Mathf.Infinity, deltaTime);
            _currentPitch = Mathf.Clamp(
                Mathf.SmoothDamp(_currentPitch, _targetPitch, ref _pitchVelocity, RotationSmoothTime, Mathf.Infinity, deltaTime),
                MinPitchDegrees,
                MaxPitchDegrees);

            // 2. Smoothly follow the target's pivot using Vector3.SmoothDamp
            Vector3 desiredPivot = _followTarget.position + new Vector3(0f, _desiredPivotHeight, 0f);
            _smoothedPivot = Vector3.SmoothDamp(
                _smoothedPivot,
                desiredPivot,
                ref _pivotFollowVelocity,
                FollowSmoothTime,
                Mathf.Infinity,
                deltaTime);

            Quaternion orbitRotation = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
            Vector3 orbitBackDirection = orbitRotation * Vector3.back;

            // 3. Collision Handling: SphereCast from pivot to desired camera position
            float unobstructedDistance = _desiredMaxDistance;
            if (Physics.SphereCast(
                    _smoothedPivot,
                    CollisionSphereRadius,
                    orbitBackDirection,
                    out RaycastHit hit,
                    _desiredMaxDistance,
                    _collisionLayerMask,
                    QueryTriggerInteraction.Ignore))
            {
                unobstructedDistance = Mathf.Clamp(hit.distance - CollisionWallOffset, MinCollisionDistance, _desiredMaxDistance);
            }

            if (unobstructedDistance < _currentDistance)
            {
                _distanceRecoverVelocity = 0f;
                _currentDistance = Mathf.Lerp(
                    _currentDistance,
                    unobstructedDistance,
                    1f - Mathf.Exp(-CollisionPullInSharpness * deltaTime));
            }
            else
            {
                _currentDistance = Mathf.SmoothDamp(
                    _currentDistance,
                    unobstructedDistance,
                    ref _distanceRecoverVelocity,
                    CollisionRecoverSmoothTime,
                    Mathf.Infinity,
                    deltaTime);
            }

            Vector3 finalCameraPosition = _smoothedPivot + (orbitBackDirection * _currentDistance);
            _cameraTransform.SetPositionAndRotation(finalCameraPosition, orbitRotation);
        }

        public void Shutdown()
        {
            if (_cameraGo != null)
            {
                Object.Destroy(_cameraGo);
                _cameraGo = null;
            }

            _cameraTransform = null;
            _camera = null;
            _followTarget = null;
        }

        private void CreateRuntimeCamera()
        {
            _cameraGo = new GameObject("[ThirdPersonCamera]")
            {
                tag = "MainCamera"
            };
            Object.DontDestroyOnLoad(_cameraGo);

            _cameraTransform = _cameraGo.transform;
            _camera = _cameraGo.AddComponent<Camera>();
            _camera.clearFlags = CameraClearFlags.Skybox;
            _camera.backgroundColor = new Color(0.92f, 0.64f, 0.43f, 1f);
            _camera.fieldOfView = OnFootFov;
            _camera.nearClipPlane = 0.15f;
            _camera.farClipPlane = 160f;
            _camera.allowHDR = false;
            _camera.allowMSAA = false;

            _cameraGo.AddComponent<AudioListener>();
        }

        private void SnapImmediatelyToTarget()
        {
            if (_cameraTransform == null || _followTarget == null)
            {
                return;
            }

            _targetYaw = _followTarget.eulerAngles.y;
            _currentYaw = _targetYaw;
            _targetPitch = Mathf.Clamp(_targetPitch, MinPitchDegrees, MaxPitchDegrees);
            _currentPitch = _targetPitch;
            _yawVelocity = 0f;
            _pitchVelocity = 0f;
            _pivotFollowVelocity = Vector3.zero;
            _distanceRecoverVelocity = 0f;
            _currentDistance = _desiredMaxDistance;

            _smoothedPivot = _followTarget.position + new Vector3(0f, _desiredPivotHeight, 0f);
            Quaternion rot = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
            Vector3 pos = _smoothedPivot + ((rot * Vector3.back) * _currentDistance);
            _cameraTransform.SetPositionAndRotation(pos, rot);
        }

        private static void DisablePreExistingSceneCameras()
        {
            Camera[] existingCameras = Camera.allCameras;
            for (int i = 0; i < existingCameras.Length; i++)
            {
                Camera existing = existingCameras[i];
                if (existing != null)
                {
                    existing.gameObject.SetActive(false);
                }
            }

#if UNITY_2022_2_OR_NEWER
            AudioListener existingListener = Object.FindFirstObjectByType<AudioListener>();
#else
            AudioListener existingListener = Object.FindObjectOfType<AudioListener>();
#endif
            if (existingListener != null)
            {
                existingListener.enabled = false;
            }
        }
    }
}
