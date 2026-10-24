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
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> that procedurally generates a runtime
    /// mobile touch HUD (EventSystem, Canvas, bottom-left Virtual Joystick, right-side Camera Swipe Zone,
    /// and Jump button) with zero pre-made prefabs. Supports Unity's New Input System and Legacy fallback.
    /// </summary>
    public sealed class TouchInputManager : IGameSystem, ITickable
    {
        public const float DefaultJoystickRadius = 72f;
        public const float DefaultSprintEdgeThreshold = 0.82f;

        private readonly float _joystickRadius;
        private readonly float _sprintThreshold;

        private GameObject _uiRootGo;
        private GameObject _createdEventSystemGo;
        private Canvas _canvas;
        private VirtualJoystickWidget _joystickWidget;
        private TouchSwipeZoneWidget _lookSwipeWidget;
        private TouchButtonWidget _jumpButtonWidget;

        private Sprite _ringSprite;
        private Sprite _knobSprite;
        private Sprite _jumpButtonSprite;

        private bool _uiConstructed;

        /// <summary>
        /// Normalized movement stick input in [-1..1] (magnitude 0..1).
        /// </summary>
        public Vector2 MoveInput { get; private set; }

        /// <summary>
        /// Resolution-normalized per-frame swipe delta from the right-side camera touch area.
        /// Automatically resets to Vector2.zero on frames when the finger is stationary or lifted.
        /// </summary>
        public Vector2 LookInput { get; private set; }

        /// <summary>
        /// True when the virtual joystick is pushed to its outer edge (or Sprint key/button is held).
        /// </summary>
        public bool IsSprinting { get; private set; }

        /// <summary>
        /// True on the frame the Jump button (or Space / Gamepad South) is pressed.
        /// </summary>
        public bool JumpPressed { get; private set; }

        /// <summary>
        /// True while the Jump button or key is held down.
        /// </summary>
        public bool JumpHeld { get; private set; }

        public Canvas RuntimeCanvas => _canvas;

        public TouchInputManager(
            float joystickRadius = DefaultJoystickRadius,
            float sprintThreshold = DefaultSprintEdgeThreshold)
        {
            _joystickRadius = Mathf.Max(32f, joystickRadius);
            _sprintThreshold = Mathf.Clamp(sprintThreshold, 0.55f, 0.98f);
        }

        public void Initialize()
        {
            MoveInput = Vector2.zero;
            LookInput = Vector2.zero;
            IsSprinting = false;
            JumpPressed = false;
            JumpHeld = false;
            _uiConstructed = false;
        }

        /// <summary>
        /// Ensures the runtime EventSystem and Touch Canvas exist once the initial scene has loaded.
        /// </summary>
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
        }

        public void Tick(float deltaTime)
        {
            if (!_uiConstructed)
            {
                EnsureTouchUiCreated();
            }

            Vector2 move = _joystickWidget != null ? _joystickWidget.CurrentInput : Vector2.zero;
            Vector2 look = _lookSwipeWidget != null ? _lookSwipeWidget.ConsumeFrameDelta() : Vector2.zero;
            bool jumpDown = _jumpButtonWidget != null && _jumpButtonWidget.ConsumePressedThisFrame();
            bool jumpHeld = _jumpButtonWidget != null && _jumpButtonWidget.IsHeld;
            bool sprintOverride = false;

            // Merge Editor / Desktop / Gamepad fallback controls when touch controls are idle
            ReadHardwareFallbackInputs(ref move, ref look, ref jumpDown, ref jumpHeld, ref sprintOverride, deltaTime);

            if (move.sqrMagnitude > 1f)
            {
                move.Normalize();
            }

            bool sprinting = sprintOverride || move.magnitude >= _sprintThreshold;
            if (_joystickWidget != null)
            {
                _joystickWidget.SetSprintVisualState(sprinting && move.sqrMagnitude > 0.01f);
            }

            MoveInput = move;
            LookInput = look;
            IsSprinting = sprinting && move.sqrMagnitude > 0.01f;
            JumpPressed = jumpDown;
            JumpHeld = jumpHeld;
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

            _canvas = null;
            _joystickWidget = null;
            _lookSwipeWidget = null;
            _jumpButtonWidget = null;
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

            // Safe-area container for notched / punch-hole Android screens
            var safeAreaGo = new GameObject("SafeArea", typeof(RectTransform));
            var safeRect = safeAreaGo.GetComponent<RectTransform>();
            safeRect.SetParent(_uiRootGo.transform, false);
            ApplyScreenSafeArea(safeRect);

            // 1. Right-Side Camera Swipe / Look Zone (covers right 55% of the screen)
            var lookZoneGo = new GameObject("RightCameraSwipeZone", typeof(RectTransform), typeof(Image));
            var lookRect = lookZoneGo.GetComponent<RectTransform>();
            lookRect.SetParent(safeRect, false);
            lookRect.anchorMin = new Vector2(0.45f, 0f);
            lookRect.anchorMax = new Vector2(1f, 1f);
            lookRect.offsetMin = Vector2.zero;
            lookRect.offsetMax = Vector2.zero;

            var lookImg = lookZoneGo.GetComponent<Image>();
            lookImg.color = new Color(0f, 0f, 0f, 0f); // Invisible touch catcher
            lookImg.raycastTarget = true;

            _lookSwipeWidget = lookZoneGo.AddComponent<TouchSwipeZoneWidget>();
            _lookSwipeWidget.Initialize(_canvas);

            // 2. Bottom-Left Virtual Joystick Zone (dynamic recenter inside bottom-left quadrant)
            var joystickZoneGo = new GameObject("LeftJoystickZone", typeof(RectTransform), typeof(Image));
            var joystickZoneRect = joystickZoneGo.GetComponent<RectTransform>();
            joystickZoneRect.SetParent(safeRect, false);
            joystickZoneRect.anchorMin = new Vector2(0f, 0f);
            joystickZoneRect.anchorMax = new Vector2(0.45f, 0.62f);
            joystickZoneRect.pivot = Vector2.zero;
            joystickZoneRect.offsetMin = Vector2.zero;
            joystickZoneRect.offsetMax = Vector2.zero;

            var joystickZoneImg = joystickZoneGo.GetComponent<Image>();
            joystickZoneImg.color = new Color(0f, 0f, 0f, 0f);
            joystickZoneImg.raycastTarget = true;

            // Joystick Base Image
            var baseGo = new GameObject("JoystickBase", typeof(RectTransform), typeof(Image));
            var baseRect = baseGo.GetComponent<RectTransform>();
            baseRect.SetParent(joystickZoneRect, false);
            baseRect.anchorMin = new Vector2(0f, 0f);
            baseRect.anchorMax = new Vector2(0f, 0f);
            baseRect.pivot = new Vector2(0.5f, 0.5f);
            Vector2 defaultBasePos = new Vector2(210f, 200f);
            baseRect.anchoredPosition = defaultBasePos;
            float baseDiameter = _joystickRadius * 2.45f;
            baseRect.sizeDelta = new Vector2(baseDiameter, baseDiameter);

            var baseImg = baseGo.GetComponent<Image>();
            baseImg.sprite = _ringSprite;
            baseImg.color = new Color(1f, 1f, 1f, 0.52f);
            baseImg.raycastTarget = false;

            // Joystick Knob Image
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

            // 3. Bottom-Right Jump Button (layered after RightCameraSwipeZone so taps hit Jump cleanly)
            var jumpGo = new GameObject("JumpButton", typeof(RectTransform), typeof(Image));
            var jumpRect = jumpGo.GetComponent<RectTransform>();
            jumpRect.SetParent(safeRect, false);
            jumpRect.anchorMin = new Vector2(1f, 0f);
            jumpRect.anchorMax = new Vector2(1f, 0f);
            jumpRect.pivot = new Vector2(0.5f, 0.5f);
            jumpRect.anchoredPosition = new Vector2(-195f, 195f);
            jumpRect.sizeDelta = new Vector2(126f, 126f);

            var jumpImg = jumpGo.GetComponent<Image>();
            jumpImg.sprite = _jumpButtonSprite;
            jumpImg.color = new Color(1f, 0.94f, 0.82f, 0.78f);
            jumpImg.raycastTarget = true;

            _jumpButtonWidget = jumpGo.AddComponent<TouchButtonWidget>();
            _jumpButtonWidget.Initialize(jumpRect, jumpImg);
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
            Vector2 anchorMin = new Vector2(safe.xMin / w, safe.yMin / h);
            Vector2 anchorMax = new Vector2(safe.xMax / w, safe.yMax / h);

            safeRect.anchorMin = anchorMin;
            safeRect.anchorMax = anchorMax;
            safeRect.offsetMin = Vector2.zero;
            safeRect.offsetMax = Vector2.zero;
        }

        private void BuildProceduralSprites()
        {
            _ringSprite = CreateRingSprite(128, innerFillAlpha: 0.24f, drawUpChevron: false);
            _knobSprite = CreateDiscSprite(96);
            _jumpButtonSprite = CreateRingSprite(128, innerFillAlpha: 0.34f, drawUpChevron: true);
        }

        private static Sprite CreateDiscSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "ProceduralKnobTex",
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

        private static Sprite CreateRingSprite(int size, float innerFillAlpha, bool drawUpChevron)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = drawUpChevron ? "ProceduralJumpTex" : "ProceduralRingTex",
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

                    byte r = 24;
                    byte g = 26;
                    byte b = 32;
                    float alpha = 0f;

                    if (dist <= outerR + 1.2f)
                    {
                        alpha = innerFillAlpha * outerEdge;
                        if (ringMask > 0.01f)
                        {
                            r = (byte)Mathf.RoundToInt(Mathf.Lerp(r, 255f, ringMask));
                            g = (byte)Mathf.RoundToInt(Mathf.Lerp(g, 242f, ringMask));
                            b = (byte)Mathf.RoundToInt(Mathf.Lerp(b, 214f, ringMask));
                            alpha = Mathf.Max(alpha, ringMask * outerEdge * 0.95f);
                        }
                    }

                    if (drawUpChevron && dist < innerR * 0.72f)
                    {
                        // Crisp upward jump chevron icon in normalized [-1..1] space
                        float nx = dx / (size * 0.22f);
                        float ny = (dy / (size * 0.22f)) - 0.12f;
                        float chevronDist = Mathf.Abs(ny + (Mathf.Abs(nx) * 0.85f));
                        if (Mathf.Abs(nx) <= 0.85f && chevronDist < 0.22f)
                        {
                            float iconMask = Mathf.Clamp01((0.22f - chevronDist) * 8f);
                            r = (byte)Mathf.RoundToInt(Mathf.Lerp(r, 255f, iconMask));
                            g = (byte)Mathf.RoundToInt(Mathf.Lerp(g, 235f, iconMask));
                            b = (byte)Mathf.RoundToInt(Mathf.Lerp(b, 185f, iconMask));
                            alpha = Mathf.Max(alpha, iconMask * 0.95f);
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
            ref bool sprintOverride,
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

                jumpDown |= pad.buttonSouth.wasPressedThisFrame;
                jumpHeld |= pad.buttonSouth.isPressed;
                sprintOverride |= pad.leftStickButton.isPressed || pad.rightTrigger.isPressed;
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

                jumpDown |= kb.spaceKey.wasPressedThisFrame;
                jumpHeld |= kb.spaceKey.isPressed;
                sprintOverride |= kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
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

            jumpDown |= Input.GetKeyDown(KeyCode.Space);
            jumpHeld |= Input.GetKey(KeyCode.Space);
            sprintOverride |= Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
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
                    // Clamp floating joystick origin inside zone margins so base never clips off screen
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
            private static readonly Color IdleColor = new Color(1f, 0.94f, 0.82f, 0.78f);
            private static readonly Color PressedColor = new Color(1f, 0.76f, 0.28f, 0.98f);

            private RectTransform _rect;
            private Image _image;
            private int _activePointerId = int.MinValue;
            private bool _pressedThisFrame;

            public bool IsHeld { get; private set; }

            public void Initialize(RectTransform rect, Image image)
            {
                _rect = rect;
                _image = image;
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
                    _rect.localScale = new Vector3(0.92f, 0.92f, 1f);
                }

                if (_image != null)
                {
                    _image.color = PressedColor;
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
                    _image.color = IdleColor;
                }
            }
        }

        #endregion
    }
}
