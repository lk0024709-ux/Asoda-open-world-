using System;
using System.Collections.Generic;
using Asoda.Core;
using Asoda.World;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Asoda.Player
{
    /// <summary>
    /// Lightweight marker attached to procedural world mission beacons so <see cref="Physics.OverlapSphere"/>
    /// detects mission start triggers with zero pre-made prefabs.
    /// </summary>
    public sealed class MissionTriggerZone : MonoBehaviour
    {
        public string MissionId = "asoda_express_01";
        public string MissionTitle = "ASODA EXPRESS";
    }

    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> managing proximity detection via
    /// <see cref="Physics.OverlapSphere"/>, seamless GTA-style Vehicle Entry/Driving/Exit, and Asoda Mission checkpoints.
    /// </summary>
    public sealed class InteractionSystem : IGameSystem, ITickable
    {
        public const string PromptEnterVehicle = "ENTER VEHICLE";
        public const string PromptStartMission = "START MISSION";

        public const float InteractionRadius = 3.8f;
        private const float ProximityScanInterval = 0.08f;

        // Arcade low-poly vehicle handling constants
        private const float MaxForwardSpeed = 18.5f;
        private const float MaxReverseSpeed = -7.5f;
        private const float ForwardAcceleration = 15.5f;
        private const float ReverseAcceleration = 10.5f;
        private const float BrakeDeceleration = 28.0f;
        private const float RollingDeceleration = 5.2f;
        private const float MaxSteerDegreesPerSec = 108f;
        private const float WheelSpinDegPerMeter = 115f;
        private const float MissionCheckpointRadius = 5.5f;

        private readonly TouchInputManager _inputManager;
        private readonly PlayerController _playerController;
        private readonly ThirdPersonCamera _thirdPersonCamera;
        private readonly WorldChunkManager _chunkManager;

        private readonly List<GameObject> _detachedVehicles = new List<GameObject>(8);
        private readonly List<Transform> _activeVehicleWheels = new List<Transform>(6);
        private readonly List<Renderer> _cachedPlayerRenderers = new List<Renderer>(8);

        private Vector3[] _missionWaypoints;
        private string[] _missionStageDescriptions;

        private GameObject _missionBeaconGo;
        private Transform _missionBeaconRing;
        private Material _missionBeaconMaterial;

        private GameObject _nearbyVehicleRoot;
        private MissionTriggerZone _nearbyMissionZone;

        private GameObject _currentDrivenVehicle;
        private Transform _currentDrivenTransform;
        private int _currentDrivenOriginalLayer;
        private float _currentVehicleSpeed;
        private float _currentSteerVisualAngle;
        private int _worldCollisionMask;

        private float _proximityScanTimer;
        private bool _missionActive;
        private int _missionWaypointIndex;
        private float _missionBannerTimer;
        private string _missionBannerMessage;

        public bool IsDriving => _currentDrivenVehicle != null;
        public GameObject CurrentDrivenVehicle => _currentDrivenVehicle;
        public Transform ActiveFocusTransform => IsDriving ? _currentDrivenTransform : (_playerController != null ? _playerController.PlayerTransform : null);

        public bool HasInteractionPrompt => !IsDriving && !string.IsNullOrEmpty(CurrentInteractionPrompt);
        public string CurrentInteractionPrompt { get; private set; }
        public bool IsMissionActive => _missionActive;
        public string ActiveMissionBannerText => _missionActive
            ? _missionStageDescriptions[_missionWaypointIndex]
            : (_missionBannerTimer > 0f ? _missionBannerMessage : null);

        public InteractionSystem(
            TouchInputManager inputManager,
            PlayerController playerController,
            ThirdPersonCamera thirdPersonCamera,
            WorldChunkManager chunkManager)
        {
            _inputManager = inputManager;
            _playerController = playerController;
            _thirdPersonCamera = thirdPersonCamera;
            _chunkManager = chunkManager;
        }

        public void Initialize()
        {
            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            _worldCollisionMask = ignoreRaycastLayer >= 0 ? ~(1 << ignoreRaycastLayer) : ~0;

            _missionWaypoints = new[]
            {
                new Vector3(0f, 0f, 78f),
                new Vector3(52f, 0f, 0f),
                new Vector3(0f, 0f, 10f)
            };

            _missionStageDescriptions = new[]
            {
                "MISSION: Drive North to Sarovar Ghat Junction (1/3)",
                "MISSION: Deliver Goods via East Bazaar Road (2/3)",
                "MISSION: Return to Asoda Chowk Finish (3/3)"
            };

            CurrentInteractionPrompt = null;
            _missionActive = false;
            _missionWaypointIndex = 0;
            _missionBannerTimer = 0f;

            SpawnMissionBeacon(new Vector3(6.5f, 0f, -4.5f));
        }

        public void Tick(float deltaTime)
        {
            AnimateMissionBeacon(deltaTime);

            if (_missionBannerTimer > 0f)
            {
                _missionBannerTimer = Mathf.Max(0f, _missionBannerTimer - deltaTime);
            }

            if (IsDriving)
            {
                CurrentInteractionPrompt = null;
                TickVehicleDriving(deltaTime);

                if (_inputManager != null && _inputManager.ExitVehiclePressed)
                {
                    ExitCurrentVehicle();
                }

                TickMissionProgress();
                return;
            }

            _proximityScanTimer -= deltaTime;
            if (_proximityScanTimer <= 0f)
            {
                _proximityScanTimer = ProximityScanInterval;
                ScanNearbyInteractablesWithOverlapSphere();
            }

            if (_inputManager != null && _inputManager.InteractPressed && HasInteractionPrompt)
            {
                if (_nearbyVehicleRoot != null)
                {
                    EnterVehicle(_nearbyVehicleRoot);
                }
                else if (_nearbyMissionZone != null && !_missionActive)
                {
                    StartAsodaMission();
                }
            }

            TickMissionProgress();
        }

        /// <summary>
        /// Appends detached/driven vehicles and active mission markers to the Minimap blip list with zero allocations.
        /// </summary>
        public void AppendDynamicMinimapBlips(Vector3 center, float maxRadius, List<MinimapBlipData> results)
        {
            if (results == null)
            {
                return;
            }

            float maxRadiusSq = maxRadius * maxRadius;

            for (int i = _detachedVehicles.Count - 1; i >= 0; i--)
            {
                GameObject veh = _detachedVehicles[i];
                if (veh == null)
                {
                    _detachedVehicles.RemoveAt(i);
                    continue;
                }

                // Skip the vehicle the player is currently sitting inside so it doesn't cover the center arrow
                if (veh == _currentDrivenVehicle)
                {
                    continue;
                }

                Vector3 pos = veh.transform.position;
                float dx = pos.x - center.x;
                float dz = pos.z - center.z;
                if ((dx * dx) + (dz * dz) <= maxRadiusSq)
                {
                    results.Add(new MinimapBlipData(pos, MinimapBlipCategory.Vehicle));
                }
            }

            if (_missionBeaconGo != null && _missionBeaconGo.activeSelf)
            {
                results.Add(new MinimapBlipData(_missionBeaconGo.transform.position, MinimapBlipCategory.Mission));
            }
        }

        public void Shutdown()
        {
            if (IsDriving)
            {
                ExitCurrentVehicle();
            }

            for (int i = 0; i < _detachedVehicles.Count; i++)
            {
                if (_detachedVehicles[i] != null)
                {
                    Object.Destroy(_detachedVehicles[i]);
                }
            }

            _detachedVehicles.Clear();
            _activeVehicleWheels.Clear();
            _cachedPlayerRenderers.Clear();

            if (_missionBeaconGo != null)
            {
                Object.Destroy(_missionBeaconGo);
                _missionBeaconGo = null;
            }

            if (_missionBeaconMaterial != null)
            {
                Object.Destroy(_missionBeaconMaterial);
                _missionBeaconMaterial = null;
            }
        }

        private void ScanNearbyInteractablesWithOverlapSphere()
        {
            _nearbyVehicleRoot = null;
            _nearbyMissionZone = null;
            CurrentInteractionPrompt = null;

            Transform playerTransform = _playerController != null ? _playerController.PlayerTransform : null;
            if (playerTransform == null)
            {
                return;
            }

            Vector3 scanOrigin = playerTransform.position + new Vector3(0f, 0.8f, 0f);
            Collider[] hits = Physics.OverlapSphere(
                scanOrigin,
                InteractionRadius,
                ~0,
                QueryTriggerInteraction.Collide);

            float closestVehicleDistSq = float.MaxValue;
            float closestMissionDistSq = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                Collider col = hits[i];
                if (col == null)
                {
                    continue;
                }

                if (TryResolveVehicleRoot(col, out GameObject vehicleRoot))
                {
                    float distSq = (vehicleRoot.transform.position - playerTransform.position).sqrMagnitude;
                    if (distSq < closestVehicleDistSq)
                    {
                        closestVehicleDistSq = distSq;
                        _nearbyVehicleRoot = vehicleRoot;
                    }

                    continue;
                }

                if (!_missionActive)
                {
                    MissionTriggerZone missionZone = col.GetComponentInParent<MissionTriggerZone>();
                    if (missionZone != null)
                    {
                        float distSq = (missionZone.transform.position - playerTransform.position).sqrMagnitude;
                        if (distSq < closestMissionDistSq)
                        {
                            closestMissionDistSq = distSq;
                            _nearbyMissionZone = missionZone;
                        }
                    }
                }
            }

            if (_nearbyVehicleRoot != null)
            {
                CurrentInteractionPrompt = PromptEnterVehicle;
            }
            else if (_nearbyMissionZone != null && !_missionActive)
            {
                CurrentInteractionPrompt = PromptStartMission;
            }
        }

        private static bool TryResolveVehicleRoot(Collider col, out GameObject vehicleRoot)
        {
            vehicleRoot = null;

            DrivableVehicleMarker marker = col.GetComponentInParent<DrivableVehicleMarker>();
            if (marker != null)
            {
                vehicleRoot = marker.gameObject;
                return true;
            }

            if (HasVehicleTagSafe(col.gameObject))
            {
                Transform t = col.transform;
                while (t.parent != null && HasVehicleTagSafe(t.parent.gameObject))
                {
                    t = t.parent;
                }

                vehicleRoot = t.gameObject;
                return true;
            }

            return false;
        }

        private static bool HasVehicleTagSafe(GameObject go)
        {
            if (go == null)
            {
                return false;
            }

            try
            {
                return go.CompareTag(MapDataLoader.VehicleTagName);
            }
            catch
            {
                return false;
            }
        }

        private void EnterVehicle(GameObject vehicleGo)
        {
            if (vehicleGo == null || _playerController == null || _playerController.PlayerTransform == null)
            {
                return;
            }

            // 1. Detach from streamed chunk so driving across chunk boundaries never despawns the vehicle
            if (_chunkManager != null)
            {
                _chunkManager.DetachPropFromActiveChunks(vehicleGo);
            }

            Object.DontDestroyOnLoad(vehicleGo);
            if (!_detachedVehicles.Contains(vehicleGo))
            {
                _detachedVehicles.Add(vehicleGo);
            }

            _currentDrivenVehicle = vehicleGo;
            _currentDrivenTransform = vehicleGo.transform;
            _currentDrivenOriginalLayer = vehicleGo.layer;
            _currentVehicleSpeed = 0f;
            _currentSteerVisualAngle = 0f;

            // Place driven vehicle on Ignore Raycast while occupied so camera SphereCast & weapon raycasts ignore it
            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycastLayer >= 0)
            {
                SetLayerRecursively(_currentDrivenTransform, ignoreRaycastLayer);
            }

            CollectVehicleWheels(_currentDrivenTransform);

            // 2. Disable PlayerController & parent the player cleanly into the vehicle cabin
            _playerController.SetControlEnabled(false);
            SetPlayerVisualsVisible(false);

            Transform playerTransform = _playerController.PlayerTransform;
            playerTransform.SetParent(_currentDrivenTransform, worldPositionStays: false);
            playerTransform.localPosition = new Vector3(0f, 0.45f, 0f);
            playerTransform.localRotation = Quaternion.identity;

            // 3. Switch world streaming, camera, and touch UI to Driving Mode
            if (_chunkManager != null)
            {
                _chunkManager.SetTrackingTarget(_currentDrivenTransform);
            }

            if (_thirdPersonCamera != null)
            {
                _thirdPersonCamera.SetVehicleDrivingMode(_currentDrivenTransform, drivingMode: true);
            }

            if (_inputManager != null)
            {
                _inputManager.SetDrivingUiMode(driving: true);
            }

            _nearbyVehicleRoot = null;
            CurrentInteractionPrompt = null;
        }

        private void ExitCurrentVehicle()
        {
            if (_currentDrivenVehicle == null || _playerController == null || _playerController.PlayerTransform == null)
            {
                return;
            }

            Transform vehicleTransform = _currentDrivenTransform;

            // Restore vehicle collision layer
            SetLayerRecursively(vehicleTransform, _currentDrivenOriginalLayer);

            // Unparent player & place beside unobstructed vehicle door
            Transform playerTransform = _playerController.PlayerTransform;
            playerTransform.SetParent(null, worldPositionStays: true);
            Object.DontDestroyOnLoad(_playerController.PlayerGameObject);

            Vector3 exitPosition = FindSafeExitPosition(vehicleTransform);
            playerTransform.rotation = Quaternion.Euler(0f, vehicleTransform.eulerAngles.y, 0f);

            SetPlayerVisualsVisible(true);
            _playerController.SetControlEnabled(true);
            _playerController.Teleport(exitPosition);

            if (_chunkManager != null)
            {
                _chunkManager.SetTrackingTarget(playerTransform);
            }

            if (_thirdPersonCamera != null)
            {
                _thirdPersonCamera.SetVehicleDrivingMode(playerTransform, drivingMode: false);
            }

            if (_inputManager != null)
            {
                _inputManager.SetDrivingUiMode(driving: false);
            }

            _currentDrivenVehicle = null;
            _currentDrivenTransform = null;
            _currentVehicleSpeed = 0f;
            _activeVehicleWheels.Clear();
            _proximityScanTimer = 0f;
        }

        private Vector3 FindSafeExitPosition(Transform vehicleTransform)
        {
            Vector3 leftDoor = vehicleTransform.position - (vehicleTransform.right * 1.95f) + new Vector3(0f, 0.12f, 0f);
            if (!Physics.CheckSphere(leftDoor + new Vector3(0f, 0.9f, 0f), 0.42f, _worldCollisionMask, QueryTriggerInteraction.Ignore))
            {
                return leftDoor;
            }

            Vector3 rightDoor = vehicleTransform.position + (vehicleTransform.right * 1.95f) + new Vector3(0f, 0.12f, 0f);
            if (!Physics.CheckSphere(rightDoor + new Vector3(0f, 0.9f, 0f), 0.42f, _worldCollisionMask, QueryTriggerInteraction.Ignore))
            {
                return rightDoor;
            }

            return vehicleTransform.position + new Vector3(0f, 2.2f, 0f);
        }

        private void TickVehicleDriving(float deltaTime)
        {
            if (_currentDrivenTransform == null)
            {
                return;
            }

            Vector2 moveInput = _inputManager != null ? _inputManager.MoveInput : Vector2.zero;
            bool brakeHeld = _inputManager != null && _inputManager.BrakeHeld;

            float throttle = Mathf.Clamp(moveInput.y, -1f, 1f);
            float steer = Mathf.Clamp(moveInput.x, -1f, 1f);

            // 1. Acceleration / Braking / Rolling Drag
            if (brakeHeld)
            {
                _currentVehicleSpeed = Mathf.MoveTowards(_currentVehicleSpeed, 0f, BrakeDeceleration * deltaTime);
            }
            else if (throttle > 0.08f)
            {
                float accel = _currentVehicleSpeed < 0f ? BrakeDeceleration : ForwardAcceleration;
                _currentVehicleSpeed = Mathf.MoveTowards(_currentVehicleSpeed, MaxForwardSpeed * throttle, accel * deltaTime);
            }
            else if (throttle < -0.08f)
            {
                float accel = _currentVehicleSpeed > 0f ? BrakeDeceleration : ReverseAcceleration;
                _currentVehicleSpeed = Mathf.MoveTowards(_currentVehicleSpeed, MaxReverseSpeed * -throttle, accel * deltaTime);
            }
            else
            {
                _currentVehicleSpeed = Mathf.MoveTowards(_currentVehicleSpeed, 0f, RollingDeceleration * deltaTime);
            }

            // 2. Steering (scales naturally with speed so parked vehicles don't spin in place)
            float absSpeed = Mathf.Abs(_currentVehicleSpeed);
            if (absSpeed > 0.25f && Mathf.Abs(steer) > 0.02f)
            {
                float speedFactor = Mathf.Clamp01(absSpeed / 4.5f);
                float reverseFlip = _currentVehicleSpeed >= 0f ? 1f : -1f;
                float yawDelta = steer * reverseFlip * MaxSteerDegreesPerSec * speedFactor * deltaTime;
                _currentDrivenTransform.Rotate(0f, yawDelta, 0f, Space.Self);
            }

            // 3. Forward/Reverse displacement with obstacle collision scrub
            float stepDistance = _currentVehicleSpeed * deltaTime;
            if (Mathf.Abs(stepDistance) > 0.0001f)
            {
                Vector3 moveDir = _currentDrivenTransform.forward * Mathf.Sign(stepDistance);
                float castDist = Mathf.Abs(stepDistance) + 1.15f;
                Vector3 castOrigin = _currentDrivenTransform.position + new Vector3(0f, 0.75f, 0f);

                if (Physics.SphereCast(
                        castOrigin,
                        0.58f,
                        moveDir,
                        out RaycastHit hit,
                        castDist,
                        _worldCollisionMask,
                        QueryTriggerInteraction.Ignore) &&
                    hit.normal.y < 0.55f)
                {
                    // Scrub velocity cleanly against solid walls/buildings
                    _currentVehicleSpeed *= -0.22f;
                }
                else
                {
                    Vector3 nextPos = _currentDrivenTransform.position + (_currentDrivenTransform.forward * stepDistance);
                    nextPos.y = SampleGroundHeight(nextPos);
                    _currentDrivenTransform.position = nextPos;
                }
            }

            // 4. Spin vehicle wheels visually around their horizontal axle (X axis)
            _currentSteerVisualAngle = Mathf.Lerp(_currentSteerVisualAngle, steer * 24f, 1f - Mathf.Exp(-12f * deltaTime));
            float wheelSpinDelta = stepDistance * WheelSpinDegPerMeter;
            for (int i = 0; i < _activeVehicleWheels.Count; i++)
            {
                Transform wheel = _activeVehicleWheels[i];
                if (wheel != null)
                {
                    wheel.Rotate(Vector3.right, wheelSpinDelta, Space.Self);
                }
            }
        }

        private float SampleGroundHeight(Vector3 candidatePosition)
        {
            Vector3 rayOrigin = new Vector3(candidatePosition.x, candidatePosition.y + 3.5f, candidatePosition.z);
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 10f, _worldCollisionMask, QueryTriggerInteraction.Ignore))
            {
                return hit.point.y;
            }

            return 0f;
        }

        private void CollectVehicleWheels(Transform vehicleRoot)
        {
            _activeVehicleWheels.Clear();
            CollectWheelsRecursive(vehicleRoot);
        }

        private void CollectWheelsRecursive(Transform node)
        {
            int count = node.childCount;
            for (int i = 0; i < count; i++)
            {
                Transform child = node.GetChild(i);
                if (child.name.IndexOf("Wheel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _activeVehicleWheels.Add(child);
                }

                CollectWheelsRecursive(child);
            }
        }

        private void SetPlayerVisualsVisible(bool visible)
        {
            if (_playerController == null || _playerController.PlayerGameObject == null)
            {
                return;
            }

            _cachedPlayerRenderers.Clear();
            _playerController.PlayerGameObject.GetComponentsInChildren(includeInactive: true, _cachedPlayerRenderers);
            for (int i = 0; i < _cachedPlayerRenderers.Count; i++)
            {
                if (_cachedPlayerRenderers[i] != null)
                {
                    _cachedPlayerRenderers[i].enabled = visible;
                }
            }
        }

        private void SpawnMissionBeacon(Vector3 position)
        {
            _missionBeaconGo = new GameObject("[AsodaMissionTrigger]");
            _missionBeaconGo.transform.position = position;
            Object.DontDestroyOnLoad(_missionBeaconGo);

            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycastLayer >= 0)
            {
                _missionBeaconGo.layer = ignoreRaycastLayer;
            }

            var sphereTrigger = _missionBeaconGo.AddComponent<SphereCollider>();
            sphereTrigger.isTrigger = true;
            sphereTrigger.radius = 2.6f;
            sphereTrigger.center = new Vector3(0f, 1.0f, 0f);

            _missionBeaconGo.AddComponent<MissionTriggerZone>();

            _missionBeaconMaterial = ChunkPool.CreateInstancedSurfaceMaterial(
                "Asoda_MissionBeacon_Mat",
                new Color(1.0f, 0.78f, 0.16f, 1f));

            // Glowing GTA-style ground halo cylinder
            GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "MissionGroundHalo";
            Object.Destroy(ring.GetComponent<Collider>());
            if (ignoreRaycastLayer >= 0)
            {
                ring.layer = ignoreRaycastLayer;
            }

            _missionBeaconRing = ring.transform;
            _missionBeaconRing.SetParent(_missionBeaconGo.transform, false);
            _missionBeaconRing.localPosition = new Vector3(0f, 0.08f, 0f);
            _missionBeaconRing.localScale = new Vector3(3.2f, 0.08f, 3.2f);

            var ringRenderer = ring.GetComponent<MeshRenderer>();
            ringRenderer.sharedMaterial = _missionBeaconMaterial;
            ringRenderer.shadowCastingMode = ShadowCastingMode.Off;

            // Floating diamond crystal above the ring so players spot it across Asoda Chowk
            GameObject diamond = GameObject.CreatePrimitive(PrimitiveType.Cube);
            diamond.name = "MissionFloatingDiamond";
            Object.Destroy(diamond.GetComponent<Collider>());
            if (ignoreRaycastLayer >= 0)
            {
                diamond.layer = ignoreRaycastLayer;
            }

            diamond.transform.SetParent(_missionBeaconGo.transform, false);
            diamond.transform.localPosition = new Vector3(0f, 2.2f, 0f);
            diamond.transform.localRotation = Quaternion.Euler(45f, 45f, 0f);
            diamond.transform.localScale = new Vector3(0.55f, 0.55f, 0.55f);

            var diamondRenderer = diamond.GetComponent<MeshRenderer>();
            diamondRenderer.sharedMaterial = _missionBeaconMaterial;
            diamondRenderer.shadowCastingMode = ShadowCastingMode.Off;
        }

        private void StartAsodaMission()
        {
            _missionActive = true;
            _missionWaypointIndex = 0;
            _nearbyMissionZone = null;
            CurrentInteractionPrompt = null;

            if (_missionBeaconGo != null)
            {
                _missionBeaconGo.transform.position = _missionWaypoints[0];
            }
        }

        private void TickMissionProgress()
        {
            if (!_missionActive || _missionBeaconGo == null)
            {
                return;
            }

            Transform focus = ActiveFocusTransform;
            if (focus == null)
            {
                return;
            }

            Vector3 target = _missionWaypoints[_missionWaypointIndex];
            float dx = focus.position.x - target.x;
            float dz = focus.position.z - target.z;
            if ((dx * dx) + (dz * dz) <= MissionCheckpointRadius * MissionCheckpointRadius)
            {
                _missionWaypointIndex++;
                if (_missionWaypointIndex >= _missionWaypoints.Length)
                {
                    _missionActive = false;
                    _missionWaypointIndex = 0;
                    _missionBeaconGo.transform.position = new Vector3(6.5f, 0f, -4.5f);
                    _missionBannerMessage = "MISSION COMPLETE! +100 ARMOR & VITALITY";
                    _missionBannerTimer = 4.5f;

                    if (_playerController != null)
                    {
                        _playerController.RestoreVitals(100f, 100f);
                    }
                }
                else
                {
                    _missionBeaconGo.transform.position = _missionWaypoints[_missionWaypointIndex];
                }
            }
        }

        private void AnimateMissionBeacon(float deltaTime)
        {
            if (_missionBeaconGo == null || _missionBeaconGo.transform.childCount < 2)
            {
                return;
            }

            Transform diamond = _missionBeaconGo.transform.GetChild(1);
            diamond.Rotate(0f, 95f * deltaTime, 0f, Space.World);
            float bob = 2.15f + (Mathf.Sin(Time.time * 3.2f) * 0.22f);
            diamond.localPosition = new Vector3(0f, bob, 0f);

            if (_missionBeaconRing != null)
            {
                float pulse = _missionActive ? 3.8f : (3.1f + (Mathf.Sin(Time.time * 2.6f) * 0.2f));
                _missionBeaconRing.localScale = new Vector3(pulse, 0.08f, pulse);
            }
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null)
            {
                return;
            }

            root.gameObject.layer = layer;
            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }
    }
}
