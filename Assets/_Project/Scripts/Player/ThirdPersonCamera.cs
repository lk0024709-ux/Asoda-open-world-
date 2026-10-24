using Asoda.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Asoda.Player
{
    /// <summary>
    /// Smooth, collision-aware third-person camera implementing <see cref="IGameSystem"/> and <see cref="ILateTickable"/>.
    /// Instantiates at runtime tagged as <c>MainCamera</c>, orbits via <see cref="TouchInputManager.LookInput"/>
    /// with clamped pitch [-30°, 70°], follows the player via <see cref="Vector3.SmoothDamp"/>, and pulls forward
    /// using <see cref="Physics.SphereCast"/> to prevent clipping into buildings or trees.
    /// </summary>
    public sealed class ThirdPersonCamera : IGameSystem, ILateTickable
    {
        public const float DefaultDistance = 4.6f;
        public const float MinCollisionDistance = 0.55f;
        public const float HeadPivotHeight = 1.52f;
        public const float MinPitchDegrees = -30f;
        public const float MaxPitchDegrees = 70f;

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

        private float _currentDistance = DefaultDistance;
        private float _distanceRecoverVelocity;

        public Camera CameraComponent => _camera;
        public Transform CameraTransform => _cameraTransform;
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

        public void LateTick(float deltaTime)
        {
            if (_cameraTransform == null || _followTarget == null)
            {
                return;
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
            }

            _currentYaw = Mathf.SmoothDampAngle(_currentYaw, _targetYaw, ref _yawVelocity, RotationSmoothTime, Mathf.Infinity, deltaTime);
            _currentPitch = Mathf.Clamp(
                Mathf.SmoothDamp(_currentPitch, _targetPitch, ref _pitchVelocity, RotationSmoothTime, Mathf.Infinity, deltaTime),
                MinPitchDegrees,
                MaxPitchDegrees);

            // 2. Smoothly follow the player's head pivot using Vector3.SmoothDamp
            Vector3 desiredPivot = _followTarget.position + new Vector3(0f, HeadPivotHeight, 0f);
            _smoothedPivot = Vector3.SmoothDamp(
                _smoothedPivot,
                desiredPivot,
                ref _pivotFollowVelocity,
                FollowSmoothTime,
                Mathf.Infinity,
                deltaTime);

            Quaternion orbitRotation = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
            Vector3 orbitBackDirection = orbitRotation * Vector3.back;

            // 3. Collision Handling: SphereCast from the player's head to desired camera position
            float unobstructedDistance = DefaultDistance;
            if (Physics.SphereCast(
                    _smoothedPivot,
                    CollisionSphereRadius,
                    orbitBackDirection,
                    out RaycastHit hit,
                    DefaultDistance,
                    _collisionLayerMask,
                    QueryTriggerInteraction.Ignore))
            {
                unobstructedDistance = Mathf.Clamp(hit.distance - CollisionWallOffset, MinCollisionDistance, DefaultDistance);
            }

            if (unobstructedDistance < _currentDistance)
            {
                // Pull camera forward quickly when obstructed so it never clips inside buildings or trees
                _distanceRecoverVelocity = 0f;
                _currentDistance = Mathf.Lerp(
                    _currentDistance,
                    unobstructedDistance,
                    1f - Mathf.Exp(-CollisionPullInSharpness * deltaTime));
            }
            else
            {
                // Smoothly ease back out once line of sight clears
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
            _camera.fieldOfView = 60f;
            _camera.nearClipPlane = 0.15f;
            _camera.farClipPlane = 160f;
            _camera.allowHDR = false; // Saves bandwidth on mobile tile-based GPUs
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
            _currentDistance = DefaultDistance;

            _smoothedPivot = _followTarget.position + new Vector3(0f, HeadPivotHeight, 0f);
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
