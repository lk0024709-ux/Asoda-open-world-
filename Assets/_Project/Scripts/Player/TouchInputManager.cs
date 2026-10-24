using Asoda.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
#endif

namespace Asoda.Player
{
    /// <summary>
    /// Procedural icon styles rendered into anti-aliased circular action button sprites.
    /// </summary>
    internal enum CircularIconGlyph
    {
        None = 0,
        JumpChevron = 1,
        PunchBurst = 2,
        ShootReticle = 3,
        BrakeBars = 4,
        ExitArrow = 5
    }

    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> that procedurally generates the runtime
    /// mobile touch Canvas (Virtual Joystick, Right Camera Swipe Zone, Jump, Punch, Shoot, Brake, and Vehicle Exit
    /// circular buttons) with zero pre-made prefabs. Supports Unity's New Input System and Legacy fallback.
    /// </summary>
    public sealed class TouchInputManager : IGameSystem, ITickable
    {
        public const float DefaultJoystickRadius = 72f;
        public const float DefaultSprintEdgeThreshold = 0.82f;

        private static Font _cachedRuntimeFont;

        private readonly float _joystickRadius;
        private readonly float _sprintThreshold;

        private GameObject _uiRootGo;
        private GameObject _createdEventSystemGo;
        private Canvas _canvas;
        private RectTransform _safeAreaRect;

        private GameObject _onFootButtonsCluster;
        private GameObject _drivingButtonsCluster;

        private VirtualJoystickWidget _joystickWidget;
        private TouchSwipeZoneWidget _lookSwipeWidget;
        private TouchButtonWidget _jumpButtonWidget;
        private TouchButtonWidget _punchButtonWidget;
        private TouchButtonWidget _shootButtonWidget;
        private TouchButtonWidget _exitVehicleButtonWidget;
        private TouchButtonWidget _brakeButtonWidget;

        private Sprite _ringSprite;
        private Sprite _knobSprite;
        private Sprite _jumpButtonSprite;
        private Sprite _punchButtonSprite;
        private Sprite _shootButtonSprite;
        private Sprite _exitButtonSprite;
        private Sprite _brakeButtonSprite;

        private bool _uiConstructed;
        private bool _isDrivingUiMode;
        private bool _uiInteractClickedThisFrame;

        public Vector2 MoveInput { get; private set; }
        public Vector2 LookInput { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool JumpPressed { get; private set; }
        public bool JumpHeld { get; private set; }
        public bool PunchPressed { get; private set; }
        public bool ShootPressed { get; private set; }
        public bool ShootHeld { get; private set; }
        public bool InteractPressed { get; private set; }
        public bool ExitVehiclePressed { get; private set; }
        public bool BrakeHeld { get; private set; }
        public bool WeaponSwitchPressed { get; private set; }

        public Canvas RuntimeCanvas => _canvas;
        public RectTransform SafeAreaRect => _safeAreaRect;
        public Sprite SharedDiscSprite => _knobSprite;
        public Sprite SharedRingSprite => _ringSprite;
        public bool IsDrivingUiMode => _isDrivingUiMode;

        public TouchInputManager(
            float joystickRadius = DefaultJoystickRadius,
            float sprintThreshold = DefaultSprintEdgeThreshold)
        {
            _joystickRadius = Mathf.Max(32f, joystickRadius);
            _sprintThreshold = Mathf.Clamp(sprintThreshold, 0.55f, 0.98f);
        }

        public static Font ResolveRuntimeFont()
        {
            if (_cachedRuntimeFont != null)
            {
                return _cachedRuntimeFont;
            }

#if UNITY_2022_2_OR_NEWER
            _cachedRuntimeFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#endif
            if (_cachedRuntimeFont == null)
            {
                try
                {
                    _cachedRuntimeFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }
                catch
                {
                    // Ignored on Unity 2022.2+ where LegacyRuntime.ttf is used
                }
            }

            if (_cachedRuntimeFont == null)
            {
                _cachedRuntimeFont = Font.CreateDynamicFontFromOSFont("Sans-Serif", 18);
            }

            return _cachedRuntimeFont;
        }

        public void Initialize()
        {
            MoveInput = Vector2.zero;
            LookInput = Vector2.zero;
            IsSprinting = false;
            JumpPressed = false;
            JumpHeld = false;
            PunchPressed = false;
            ShootPressed = false;
            ShootHeld = false;
            InteractPressed = false;
            ExitVehiclePressed = false;
            BrakeHeld = false;
            WeaponSwitchPressed = false;
            _uiInteractClickedThisFrame = false;
            _uiConstructed = false;
            _isDrivingUiMode = false;
        }

        public void EnsureTouchUiCreated()
        {
            if (_uiConstructed)
            {
                return;
            }

            _uiConstructed = true;
            EnsureEventSystem();
            BuildProceduralSprites();
            BuildRuntimeCanvas();
            SetDrivingUiMode(false);
        }

        /// <summary>
        /// Toggles the right-side circular button layout between On-Foot (Jump/Punch/Shoot) and Driving (Exit/Brake).
        /// </summary>
        public void SetDrivingUiMode(bool driving)
        {
            _isDrivingUiMode = driving;
            if (_onFootButtonsCluster != null)
            {
                _onFootButtonsCluster.SetActive(!driving);
            }

            if (_drivingButtonsCluster != null)
            {
                _drivingButtonsCluster.SetActive(driving);
            }
        }

        /// <summary>
        /// Invoked by HudManager when the player taps the Center-Bottom Interaction Prompt ("ENTER VEHICLE" / "START MISSION").
        /// </summary>
        public void TriggerUiInteraction()
        {
            _uiInteractClickedThisFrame = true;
        }

        public void Tick(float deltaTime)
        {
            if (!_uiConstructed)
            {
                EnsureTouchUiCreated();
            }

            Vector2 move = _joystickWidget != null ? _joystickWidget.CurrentInput : Vector2.zero;
            Vector2 look = _lookSwipeWidget != null ? _lookSwipeWidget.ConsumeFrameDelta() : Vector2.zero;

            bool jumpDown = !_isDrivingUiMode && _jumpButtonWidget != null && _jumpButtonWidget.ConsumePressedThisFrame();
            bool jumpHeld = !_isDrivingUiMode && _jumpButtonWidget != null && _jumpButtonWidget.IsHeld;
            bool punchDown = !_isDrivingUiMode && _punchButtonWidget != null && _punchButtonWidget.ConsumePressedThisFrame();
            bool shootDown = !_isDrivingUiMode && _shootButtonWidget != null && _shootButtonWidget.ConsumePressedThisFrame();
            bool shootHeld = !_isDrivingUiMode && _shootButtonWidget != null && _shootButtonWidget.IsHeld;

            bool exitDown = _isDrivingUiMode && _exitVehicleButtonWidget != null && _exitVehicleButtonWidget.ConsumePressedThisFrame();
            bool brakeHeld = _isDrivingUiMode && _brakeButtonWidget != null && _brakeButtonWidget.IsHeld;

            bool interactDown = _uiInteractClickedThisFrame;
            _uiInteractClickedThisFrame = false;

            bool sprintOverride = false;
            bool weaponSwitchDown = false;

            ReadHardwareFallbackInputs(
                ref move,
                ref look,
                ref jumpDown,
                ref jumpHeld,
                ref punchDown,
                ref shootDown,
                ref shootHeld,
                ref interactDown,
                ref exitDown,
                ref brakeHeld,
                ref sprintOverride,
                ref weaponSwitchDown,
                _isDrivingUiMode,
                deltaTime);

            if (move.sqrMagnitude > 1f)
            {
                move.Normalize();
            }

            bool sprinting = !_isDrivingUiMode && (sprintOverride || move.magnitude >= _sprintThreshold);
            if (_joystickWidget != null)
            {
                _joystickWidget.SetSprintVisualState(sprinting && move.sqrMagnitude > 0.01f);
            }

            MoveInput = move;
            LookInput = look;
            IsSprinting = sprinting && move.sqrMagnitude > 0.01f;
            JumpPressed = jumpDown;
            JumpHeld = jumpHeld;
            PunchPressed = punchDown;
            ShootPressed = shootDown;
            ShootHeld = shootHeld;
            InteractPressed = interactDown;
            ExitVehiclePressed = exitDown;
            BrakeHeld = brakeHeld;
            WeaponSwitchPressed = weaponSwitchDown;
        }

        public void Shutdown()
        {
            if (_uiRootGo != null)
            {
                Object.Destroy(_uiRootGo);
                _uiRootGo = null;
            }

            if (_createdEventSystemGo != null)
            {
                Object.Destroy(_createdEventSystemGo);
                _createdEventSystemGo = null;
            }

            DestroySpriteAndTexture(ref _ringSprite);
            DestroySpriteAndTexture(ref _knobSprite);
            DestroySpriteAndTexture(ref _jumpButtonSprite);
            DestroySpriteAndTexture(ref _punchButtonSprite);
            DestroySpriteAndTexture(ref _shootButtonSprite);
            DestroySpriteAndTexture(ref _exitButtonSprite);
            DestroySpriteAndTexture(ref _brakeButtonSprite);

            _canvas = null;
            _safeAreaRect = null;
            _onFootButtonsCluster = null;
            _drivingButtonsCluster = null;
            _joystickWidget = null;
            _lookSwipeWidget = null;
            _jumpButtonWidget = null;
            _punchButtonWidget = null;
            _shootButtonWidget = null;
            _exitVehicleButtonWidget = null;
            _brakeButtonWidget = null;
            _uiConstructed = false;
        }

        private void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                EnsureInputModuleOn(EventSystem.current.gameObject);
                return;
            }

            _createdEventSystemGo = new GameObject("[TouchEventSystem]");
            Object.DontDestroyOnLoad(_createdEventSystemGo);
            _createdEventSystemGo.AddComponent<EventSystem>();
            EnsureInputModuleOn(_createdEventSystemGo);
        }

        private static void EnsureInputModuleOn(GameObject eventSystemGo)
        {
            if (eventSystemGo == null || eventSystemGo.GetComponent<BaseInputModule>() != null)
            {
                return;
            }

#if ENABLE_INPUT_SYSTEM
            eventSystemGo.AddComponent<InputSystemUIInputModule>();
#elif ENABLE_LEGACY_INPUT_MANAGER
            eventSystemGo.AddComponent<StandaloneInputModule>();
#endif
        }

        private void BuildRuntimeCanvas()
        {
            _uiRootGo = new GameObject("[MobileTouchHUD]");
            Object.DontDestroyOnLoad(_uiRootGo);

            _canvas = _uiRootGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = _uiRootGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            _uiRootGo.AddComponent<GraphicRaycaster>();

            var safeAreaGo = new GameObject("SafeArea", typeof(RectTransform));
            _safeAreaRect = safeAreaGo.GetComponent<RectTransform>();
            _safeAreaRect.SetParent(_uiRootGo.transform, false);
            ApplyScreenSafeArea(_safeAreaRect);

            // 1. Right-Side Camera Swipe / Look Zone (covers right 55% of the screen behind action buttons)
            var lookZoneGo = new GameObject("RightCameraSwipeZone", typeof(RectTransform), typeof(Image));
            var lookRect = lookZoneGo.GetComponent<RectTransform>();
            lookRect.SetParent(_safeAreaRect, false);
            lookRect.anchorMin = new Vector2(0.45f, 0f);
            lookRect.anchorMax = new Vector2(1f, 1f);
            lookRect.offsetMin = Vector2.zero;
            lookRect.offsetMax = Vector2.zero;

            var lookImg = lookZoneGo.GetComponent<Image>();
            lookImg.color = new Color(0f, 0f, 0f, 0f);
            lookImg.raycastTarget = true;

            _lookSwipeWidget = lookZoneGo.AddComponent<TouchSwipeZoneWidget>();
            _lookSwipeWidget.Initialize(_canvas);

            // 2. Bottom-Left Virtual Joystick Zone
            var joystickZoneGo = new GameObject("LeftJoystickZone", typeof(RectTransform), typeof(Image));
            var joystickZoneRect = joystickZoneGo.GetComponent<RectTransform>();
            joystickZoneRect.SetParent(_safeAreaRect, false);
            joystickZoneRect.anchorMin = new Vector2(0f, 0f);
            joystickZoneRect.anchorMax = new Vector2(0.42f, 0.58f);
            joystickZoneRect.pivot = Vector2.zero;
            joystickZoneRect.offsetMin = Vector2.zero;
            joystickZoneRect.offsetMax = Vector2.zero;

            var joystickZoneImg = joystickZoneGo.GetComponent<Image>();
            joystickZoneImg.color = new Color(0f, 0f, 0f, 0f);
            joystickZoneImg.raycastTarget = true;

            var baseGo = new GameObject("JoystickBase", typeof(RectTransform), typeof(Image));
            var baseRect = baseGo.GetComponent<RectTransform>();
            baseRect.SetParent(joystickZoneRect, false);
            baseRect.anchorMin = Vector2.zero;
            baseRect.anchorMax = Vector2.zero;
            baseRect.pivot = new Vector2(0.5f, 0.5f);
            Vector2 defaultBasePos = new Vector2(195f, 185f);
            baseRect.anchoredPosition = defaultBasePos;
            float baseDiameter = _joystickRadius * 2.45f;
            baseRect.sizeDelta = new Vector2(baseDiameter, baseDiameter);

            var baseImg = baseGo.GetComponent<Image>();
            baseImg.sprite = _ringSprite;
            baseImg.color = new Color(1f, 1f, 1f, 0.54f);
            baseImg.raycastTarget = false;

            var knobGo = new GameObject("JoystickKnob", typeof(RectTransform), typeof(Image));
            var knobRect = knobGo.GetComponent<RectTransform>();
            knobRect.SetParent(baseRect, false);
            knobRect.anchorMin = new Vector2(0.5f, 0.5f);
            knobRect.anchorMax = new Vector2(0.5f, 0.5f);
            knobRect.pivot = new Vector2(0.5f, 0.5f);
            knobRect.anchoredPosition = Vector2.zero;
            float knobDiameter = _joystickRadius * 1.05f;
            knobRect.sizeDelta = new Vector2(knobDiameter, knobDiameter);

            var knobImg = knobGo.GetComponent<Image>();
            knobImg.sprite = _knobSprite;
            knobImg.color = new Color(0.96f, 0.94f, 0.88f, 0.88f);
            knobImg.raycastTarget = false;

            _joystickWidget = joystickZoneGo.AddComponent<VirtualJoystickWidget>();
            _joystickWidget.Initialize(joystickZoneRect, baseRect, knobRect, knobImg, defaultBasePos, _joystickRadius);

            // 3. On-Foot Circular Action Cluster (Ergonomic thumb arc: Jump, Punch, Shoot)
            _onFootButtonsCluster = CreateFullStretchContainer("OnFootActionCluster", _safeAreaRect);

            _jumpButtonWidget = CreateCircularActionButton(
                parent: _onFootButtonsCluster.transform,
                name: "JumpButton",
                anchoredOffsetFromBottomRight: new Vector2(-135f, 138f),
                diameter: 114f,
                sprite: _jumpButtonSprite,
                idleColor: new Color(1.0f, 0.95f, 0.84f, 0.80f),
                pressedColor: new Color(1.0f, 0.78f, 0.30f, 0.98f),
                caption: null);

            _punchButtonWidget = CreateCircularActionButton(
                parent: _onFootButtonsCluster.transform,
                name: "PunchButton",
                anchoredOffsetFromBottomRight: new Vector2(-282f, 128f),
                diameter: 114f,
                sprite: _punchButtonSprite,
                idleColor: new Color(1.0f, 0.72f, 0.44f, 0.84f),
                pressedColor: new Color(1.0f, 0.50f, 0.22f, 0.98f),
                caption: "PUNCH");

            _shootButtonWidget = CreateCircularActionButton(
                parent: _onFootButtonsCluster.transform,
                name: "ShootButton",
                anchoredOffsetFromBottomRight: new Vector2(-156f, 286f),
                diameter: 132f,
                sprite: _shootButtonSprite,
                idleColor: new Color(1.0f, 0.46f, 0.36f, 0.88f),
                pressedColor: new Color(1.0f, 0.26f, 0.20f, 1.00f),
                caption: "FIRE");

            // 4. Driving Circular Action Cluster (Vehicle EXIT & BRAKE buttons)
            _drivingButtonsCluster = CreateFullStretchContainer("DrivingActionCluster", _safeAreaRect);

            _exitVehicleButtonWidget = CreateCircularActionButton(
                parent: _drivingButtonsCluster.transform,
                name: "ExitVehicleButton",
                anchoredOffsetFromBottomRight: new Vector2(-148f, 268f),
                diameter: 122f,
                sprite: _exitButtonSprite,
                idleColor: new Color(1.0f, 0.58f, 0.32f, 0.90f),
                pressedColor: new Color(1.0f, 0.36f, 0.18f, 1.00f),
                caption: "EXIT");

            _brakeButtonWidget = CreateCircularActionButton(
                parent: _drivingButtonsCluster.transform,
                name: "BrakeButton",
                anchoredOffsetFromBottomRight: new Vector2(-280f, 132f),
                diameter: 116f,
                sprite: _brakeButtonSprite,
                idleColor: new Color(0.95f, 0.38f, 0.34f, 0.84f),
                pressedColor: new Color(1.0f, 0.22f, 0.20f, 0.98f),
                caption: "BRAKE");
        }

        private static GameObject CreateFullStretchContainer(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return go;
        }

        private static TouchButtonWidget CreateCircularActionButton(
            Transform parent,
            string name,
            Vector2 anchoredOffsetFromBottomRight,
            float diameter,
            Sprite sprite,
            Color idleColor,
            Color pressedColor,
            string caption)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredOffsetFromBottomRight;
            rect.sizeDelta = new Vector2(diameter, diameter);

            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.color = idleColor;
            img.raycastTarget = true;

            if (!string.IsNullOrEmpty(caption))
            {
                var labelGo = new GameObject("Caption", typeof(RectTransform), typeof(Text));
                var labelRect = labelGo.GetComponent<RectTransform>();
                labelRect.SetParent(rect, false);
                labelRect.anchorMin = new Vector2(0f, 0f);
                labelRect.anchorMax = new Vector2(1f, 0.34f);
                labelRect.offsetMin = new Vector2(0f, 8f);
                labelRect.offsetMax = Vector2.zero;

                var txt = labelGo.GetComponent<Text>();
                txt.font = ResolveRuntimeFont();
                txt.text = caption;
                txt.fontSize = 15;
                txt.fontStyle = FontStyle.Bold;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.color = new Color(1f, 0.97f, 0.90f, 0.92f);
                txt.raycastTarget = false;
            }

            var widget = go.AddComponent<TouchButtonWidget>();
            widget.Initialize(rect, img, idleColor, pressedColor);
            return widget;
        }

        private static void ApplyScreenSafeArea(RectTransform safeRect)
        {
            int w = Screen.width;
            int h = Screen.height;
            if (w <= 0 || h <= 0)
            {
                safeRect.anchorMin = Vector2.zero;
                safeRect.anchorMax = Vector2.one;
                safeRect.offsetMin = Vector2.zero;
                safeRect.offsetMax = Vector2.zero;
                return;
            }

            Rect safe = Screen.safeArea;
            safeRect.anchorMin = new Vector2(safe.xMin / w, safe.yMin / h);
            safeRect.anchorMax = new Vector2(safe.xMax / w, safe.yMax / h);
            safeRect.offsetMin = Vector2.zero;
            safeRect.offsetMax = Vector2.zero;
        }

        private void BuildProceduralSprites()
        {
            _ringSprite = CreateRingSprite(128, innerFillAlpha: 0.24f, CircularIconGlyph.None);
            _knobSprite = CreateDiscSprite(96);
            _jumpButtonSprite = CreateRingSprite(128, innerFillAlpha: 0.36f, CircularIconGlyph.JumpChevron);
            _punchButtonSprite = CreateRingSprite(128, innerFillAlpha: 0.38f, CircularIconGlyph.PunchBurst);
            _shootButtonSprite = CreateRingSprite(128, innerFillAlpha: 0.42f, CircularIconGlyph.ShootReticle);
            _exitButtonSprite = CreateRingSprite(128, innerFillAlpha: 0.42f, CircularIconGlyph.ExitArrow);
            _brakeButtonSprite = CreateRingSprite(128, innerFillAlpha: 0.38f, CircularIconGlyph.BrakeBars);
        }

        private static Sprite CreateDiscSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "ProceduralDiscTex",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            float maxR = center - 1.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt((dx * dx) + (dy * dy));
                    float alpha = Mathf.Clamp01((maxR - dist) * 0.85f);
                    byte a = (byte)Mathf.RoundToInt(alpha * 255f);
                    pixels[(y * size) + x] = new Color32(255, 255, 255, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite CreateRingSprite(int size, float innerFillAlpha, CircularIconGlyph glyph)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = $"ProceduralBtn_{glyph}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            float outerR = center - 1.5f;
            float ringThickness = size * 0.055f;
            float innerR = outerR - ringThickness;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt((dx * dx) + (dy * dy));

                    float outerEdge = Mathf.Clamp01((outerR - dist) * 0.85f);
                    float ringMask = Mathf.Clamp01(1f - (Mathf.Abs(dist - (outerR - (ringThickness * 0.5f))) / (ringThickness * 0.65f)));

                    byte r = 22;
                    byte g = 24;
                    byte b = 30;
                    float alpha = 0f;

                    if (dist <= outerR + 1.2f)
                    {
                        alpha = innerFillAlpha * outerEdge;
                        if (ringMask > 0.01f)
                        {
                            r = (byte)Mathf.RoundToInt(Mathf.Lerp(r, 255f, ringMask));
                            g = (byte)Mathf.RoundToInt(Mathf.Lerp(g, 245f, ringMask));
                            b = (byte)Mathf.RoundToInt(Mathf.Lerp(b, 225f, ringMask));
                            alpha = Mathf.Max(alpha, ringMask * outerEdge * 0.96f);
                        }
                    }

                    if (glyph != CircularIconGlyph.None && dist < innerR * 0.78f)
                    {
                        float iconMask = EvaluateGlyphMask(glyph, dx, dy + (glyph == CircularIconGlyph.JumpChevron ? 0f : -size * 0.05f), size);
                        if (iconMask > 0.01f)
                        {
                            r = (byte)Mathf.RoundToInt(Mathf.Lerp(r, 255f, iconMask));
                            g = (byte)Mathf.RoundToInt(Mathf.Lerp(g, 248f, iconMask));
                            b = (byte)Mathf.RoundToInt(Mathf.Lerp(b, 232f, iconMask));
                            alpha = Mathf.Max(alpha, iconMask * 0.96f);
                        }
                    }

                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
                    pixels[(y * size) + x] = new Color32(r, g, b, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static float EvaluateGlyphMask(CircularIconGlyph glyph, float dx, float dy, int size)
        {
            float scale = size * 0.22f;
            float nx = dx / scale;
            float ny = dy / scale;

            switch (glyph)
            {
                case CircularIconGlyph.JumpChevron:
                {
                    float chevron = Mathf.Abs((ny - 0.12f) + (Mathf.Abs(nx) * 0.85f));
                    return (Mathf.Abs(nx) <= 0.85f && chevron < 0.22f)
                        ? Mathf.Clamp01((0.22f - chevron) * 8f)
                        : 0f;
                }

                case CircularIconGlyph.PunchBurst:
                {
                    // Stylized compact fist block + knuckle ridge
                    float box = Mathf.Max(Mathf.Abs(nx) - 0.52f, Mathf.Abs(ny - 0.12f) - 0.38f);
                    float wrist = Mathf.Max(Mathf.Abs(nx) - 0.34f, Mathf.Abs(ny + 0.32f) - 0.18f);
                    float d = Mathf.Min(box, wrist);
                    return d < 0.08f ? Mathf.Clamp01((0.08f - d) * 10f) : 0f;
                }

                case CircularIconGlyph.ShootReticle:
                {
                    // Crisp target ring + 4 crosshair ticks + center dot
                    float r = Mathf.Sqrt((nx * nx) + ((ny - 0.10f) * (ny - 0.10f)));
                    float ring = Mathf.Clamp01(1f - (Mathf.Abs(r - 0.58f) / 0.11f));
                    float dot = Mathf.Clamp01((0.16f - r) * 10f);
                    float cy = ny - 0.10f;
                    bool onCross = (Mathf.Abs(nx) < 0.09f && Mathf.Abs(cy) > 0.26f && Mathf.Abs(cy) < 0.84f) ||
                                   (Mathf.Abs(cy) < 0.09f && Mathf.Abs(nx) > 0.26f && Mathf.Abs(nx) < 0.84f);
                    return Mathf.Max(Mathf.Max(ring, dot), onCross ? 0.95f : 0f);
                }

                case CircularIconGlyph.ExitArrow:
                {
                    // Door frame on left + outward arrow to the right
                    float cy = ny - 0.12f;
                    bool shaft = Mathf.Abs(cy) < 0.11f && nx > -0.45f && nx < 0.55f;
                    float head = Mathf.Abs(Mathf.Abs(cy) - (0.65f - nx));
                    bool arrowHead = nx >= 0.15f && nx <= 0.68f && head < 0.16f;
                    bool doorPost = (Mathf.Abs(nx + 0.62f) < 0.09f && Mathf.Abs(cy) < 0.56f);
                    return (shaft || arrowHead || doorPost) ? 0.95f : 0f;
                }

                case CircularIconGlyph.BrakeBars:
                {
                    float cy = ny - 0.12f;
                    bool leftBar = Mathf.Abs(nx + 0.32f) < 0.12f && Mathf.Abs(cy) < 0.50f;
                    bool rightBar = Mathf.Abs(nx - 0.32f) < 0.12f && Mathf.Abs(cy) < 0.50f;
                    return (leftBar || rightBar) ? 0.95f : 0f;
                }

                default:
                    return 0f;
            }
        }

        private static void DestroySpriteAndTexture(ref Sprite sprite)
        {
            if (sprite == null)
            {
                return;
            }

            Texture2D tex = sprite.texture;
            Object.Destroy(sprite);
            if (tex != null)
            {
                Object.Destroy(tex);
            }

            sprite = null;
        }

        private static void ReadHardwareFallbackInputs(
            ref Vector2 move,
            ref Vector2 look,
            ref bool jumpDown,
            ref bool jumpHeld,
            ref bool punchDown,
            ref bool shootDown,
            ref bool shootHeld,
            ref bool interactDown,
            ref bool exitDown,
            ref bool brakeHeld,
            ref bool sprintOverride,
            ref bool weaponSwitchDown,
            bool isDriving,
            float deltaTime)
        {
#if ENABLE_INPUT_SYSTEM
            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                if (move.sqrMagnitude < 0.001f)
                {
                    move = pad.leftStick.ReadValue();
                }

                if (look.sqrMagnitude < 0.0001f)
                {
                    Vector2 rightStick = pad.rightStick.ReadValue();
                    if (rightStick.sqrMagnitude > 0.01f)
                    {
                        look = rightStick * (160f * deltaTime);
                    }
                }

                if (isDriving)
                {
                    exitDown |= pad.buttonWest.wasPressedThisFrame;
                    brakeHeld |= pad.buttonSouth.isPressed || pad.leftTrigger.isPressed;
                }
                else
                {
                    jumpDown |= pad.buttonSouth.wasPressedThisFrame;
                    jumpHeld |= pad.buttonSouth.isPressed;
                    punchDown |= pad.buttonEast.wasPressedThisFrame;
                    shootDown |= pad.rightTrigger.wasPressedThisFrame;
                    shootHeld |= pad.rightTrigger.isPressed;
                    interactDown |= pad.buttonWest.wasPressedThisFrame;
                    sprintOverride |= pad.leftStickButton.isPressed;
                    weaponSwitchDown |= pad.buttonNorth.wasPressedThisFrame;
                }
            }

            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (move.sqrMagnitude < 0.001f)
                {
                    float hx = 0f;
                    float vz = 0f;
                    if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) hx += 1f;
                    if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) hx -= 1f;
                    if (kb.wKey.isPressed || kb.upArrowKey.isPressed) vz += 1f;
                    if (kb.sKey.isPressed || kb.downArrowKey.isPressed) vz -= 1f;
                    move = new Vector2(hx, vz);
                }

                if (isDriving)
                {
                    exitDown |= kb.eKey.wasPressedThisFrame || kb.fKey.wasPressedThisFrame;
                    brakeHeld |= kb.spaceKey.isPressed;
                }
                else
                {
                    jumpDown |= kb.spaceKey.wasPressedThisFrame;
                    jumpHeld |= kb.spaceKey.isPressed;
                    punchDown |= kb.vKey.wasPressedThisFrame;
                    shootDown |= kb.leftCtrlKey.wasPressedThisFrame;
                    shootHeld |= kb.leftCtrlKey.isPressed;
                    interactDown |= kb.eKey.wasPressedThisFrame || kb.fKey.wasPressedThisFrame;
                    sprintOverride |= kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                    weaponSwitchDown |= kb.qKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame;
                }
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (move.sqrMagnitude < 0.001f)
            {
                float hx = 0f;
                float vz = 0f;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) hx += 1f;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) hx -= 1f;
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) vz += 1f;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) vz -= 1f;
                move = new Vector2(hx, vz);
            }

            if (isDriving)
            {
                exitDown |= Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.F);
                brakeHeld |= Input.GetKey(KeyCode.Space);
            }
            else
            {
                jumpDown |= Input.GetKeyDown(KeyCode.Space);
                jumpHeld |= Input.GetKey(KeyCode.Space);
                punchDown |= Input.GetKeyDown(KeyCode.V);
                shootDown |= Input.GetKeyDown(KeyCode.LeftControl);
                shootHeld |= Input.GetKey(KeyCode.LeftControl);
                interactDown |= Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.F);
                sprintOverride |= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                weaponSwitchDown |= Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.Tab);
            }
#endif
        }

        #region Event-Driven UGUI Touch Widgets (Zero Update() Calls)

        private sealed class VirtualJoystickWidget : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
        {
            private static readonly Color IdleKnobColor = new Color(0.96f, 0.94f, 0.88f, 0.88f);
            private static readonly Color SprintKnobColor = new Color(1.0f, 0.72f, 0.24f, 0.96f);

            private RectTransform _zoneRect;
            private RectTransform _baseRect;
            private RectTransform _knobRect;
            private Image _knobImage;
            private Vector2 _defaultBasePosition;
            private float _maxRadius;
            private int _activePointerId = int.MinValue;
            private bool _lastSprintVisual;

            public Vector2 CurrentInput { get; private set; }

            public void Initialize(
                RectTransform zoneRect,
                RectTransform baseRect,
                RectTransform knobRect,
                Image knobImage,
                Vector2 defaultBasePos,
                float maxRadius)
            {
                _zoneRect = zoneRect;
                _baseRect = baseRect;
                _knobRect = knobRect;
                _knobImage = knobImage;
                _defaultBasePosition = defaultBasePos;
                _maxRadius = maxRadius;
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                if (_activePointerId != int.MinValue)
                {
                    return;
                }

                _activePointerId = eventData.pointerId;

                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        _zoneRect,
                        eventData.position,
                        eventData.pressEventCamera,
                        out Vector2 localZonePos))
                {
                    Rect r = _zoneRect.rect;
                    float pad = _maxRadius * 1.25f;
                    float minX = Mathf.Min(pad, r.width * 0.5f);
                    float maxX = Mathf.Max(r.width - pad, r.width * 0.5f);
                    float minY = Mathf.Min(pad, r.height * 0.5f);
                    float maxY = Mathf.Max(r.height - pad, r.height * 0.5f);

                    _baseRect.anchoredPosition = new Vector2(
                        Mathf.Clamp(localZonePos.x, minX, maxX),
                        Mathf.Clamp(localZonePos.y, minY, maxY));
                }

                UpdateKnobFromPointer(eventData);
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (eventData.pointerId != _activePointerId)
                {
                    return;
                }

                UpdateKnobFromPointer(eventData);
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                if (eventData.pointerId != _activePointerId)
                {
                    return;
                }

                ResetStick();
            }

            public void SetSprintVisualState(bool sprinting)
            {
                if (_lastSprintVisual == sprinting || _knobImage == null)
                {
                    return;
                }

                _lastSprintVisual = sprinting;
                _knobImage.color = sprinting ? SprintKnobColor : IdleKnobColor;
            }

            private void OnDisable()
            {
                ResetStick();
            }

            private void UpdateKnobFromPointer(PointerEventData eventData)
            {
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        _baseRect,
                        eventData.position,
                        eventData.pressEventCamera,
                        out Vector2 offset))
                {
                    return;
                }

                Vector2 clamped = Vector2.ClampMagnitude(offset, _maxRadius);
                _knobRect.anchoredPosition = clamped;
                CurrentInput = clamped / _maxRadius;
            }

            private void ResetStick()
            {
                _activePointerId = int.MinValue;
                CurrentInput = Vector2.zero;
                if (_knobRect != null)
                {
                    _knobRect.anchoredPosition = Vector2.zero;
                }

                if (_baseRect != null)
                {
                    _baseRect.anchoredPosition = _defaultBasePosition;
                }

                SetSprintVisualState(false);
            }
        }

        private sealed class TouchSwipeZoneWidget : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
        {
            private Canvas _canvas;
            private int _activePointerId = int.MinValue;
            private Vector2 _accumulatedDelta;

            public void Initialize(Canvas canvas)
            {
                _canvas = canvas;
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                if (_activePointerId == int.MinValue)
                {
                    _activePointerId = eventData.pointerId;
                }
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (eventData.pointerId != _activePointerId)
                {
                    return;
                }

                float scale = (_canvas != null && _canvas.scaleFactor > 0.01f) ? _canvas.scaleFactor : 1f;
                _accumulatedDelta += eventData.delta / scale;
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                if (eventData.pointerId == _activePointerId)
                {
                    _activePointerId = int.MinValue;
                }
            }

            public Vector2 ConsumeFrameDelta()
            {
                Vector2 delta = _accumulatedDelta;
                _accumulatedDelta = Vector2.zero;
                return delta;
            }

            private void OnDisable()
            {
                _activePointerId = int.MinValue;
                _accumulatedDelta = Vector2.zero;
            }
        }

        private sealed class TouchButtonWidget : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
        {
            private RectTransform _rect;
            private Image _image;
            private Color _idleColor;
            private Color _pressedColor;
            private int _activePointerId = int.MinValue;
            private bool _pressedThisFrame;

            public bool IsHeld { get; private set; }

            public void Initialize(RectTransform rect, Image image, Color idleColor, Color pressedColor)
            {
                _rect = rect;
                _image = image;
                _idleColor = idleColor;
                _pressedColor = pressedColor;
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                if (_activePointerId != int.MinValue)
                {
                    return;
                }

                _activePointerId = eventData.pointerId;
                _pressedThisFrame = true;
                IsHeld = true;

                if (_rect != null)
                {
                    _rect.localScale = new Vector3(0.91f, 0.91f, 1f);
                }

                if (_image != null)
                {
                    _image.color = _pressedColor;
                }
            }

            public void OnPointerUp(PointerEventData eventData)
            {
                if (eventData.pointerId != _activePointerId)
                {
                    return;
                }

                ResetButton();
            }

            public bool ConsumePressedThisFrame()
            {
                bool v = _pressedThisFrame;
                _pressedThisFrame = false;
                return v;
            }

            private void OnDisable()
            {
                ResetButton();
            }

            private void ResetButton()
            {
                _activePointerId = int.MinValue;
                _pressedThisFrame = false;
                IsHeld = false;

                if (_rect != null)
                {
                    _rect.localScale = Vector3.one;
                }

                if (_image != null)
                {
                    _image.color = _idleColor;
                }
            }
        }

        #endregion
    }
}
