using System.Collections.Generic;
using Asoda.Core;
using Asoda.World;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Asoda.Player
{
    /// <summary>
    /// Pure C# <see cref="IGameSystem"/> and <see cref="ITickable"/> constructing and updating the GTA-style
    /// runtime HUD: Top-Left Health/Armor/Stamina bars, Top-Left Masked Circular Minimap (zero second Camera),
    /// Top-Right Weapon &amp; Ammo Slot, Center Crosshair, and Center-Bottom Interaction Prompt.
    /// </summary>
    public sealed class HudManager : IGameSystem, ITickable
    {
        private const int MaxMinimapBlipPool = 64;
        private const float MinimapWorldRadius = 85f;
        private const float MinimapViewportDiameter = 194f;
        private const float MinimapInnerPixelRadius = 86f;

        private static readonly Color HealthBarColor = new Color(0.91f, 0.28f, 0.28f, 0.96f);
        private static readonly Color ArmorBarColor = new Color(0.24f, 0.64f, 0.96f, 0.96f);
        private static readonly Color StaminaBarColor = new Color(0.96f, 0.73f, 0.20f, 0.96f);

        private static readonly Color MandirBlipColor = new Color(1.00f, 0.77f, 0.20f, 1.00f);
        private static readonly Color VehicleBlipColor = new Color(0.20f, 0.87f, 1.00f, 1.00f);
        private static readonly Color BuildingBlipColor = new Color(0.86f, 0.54f, 0.36f, 0.90f);
        private static readonly Color MissionBlipColor = new Color(0.34f, 1.00f, 0.42f, 1.00f);

        private static readonly Color CrosshairIdleColor = new Color(1.0f, 0.98f, 0.92f, 0.86f);
        private static readonly Color CrosshairHitColor = new Color(1.0f, 0.36f, 0.24f, 1.00f);

        private readonly TouchInputManager _inputManager;
        private readonly PlayerController _playerController;
        private readonly ThirdPersonCamera _thirdPersonCamera;
        private readonly WorldChunkManager _chunkManager;
        private readonly InteractionSystem _interactionSystem;
        private readonly PlayerCombatController _combatController;

        private readonly List<MinimapBlipData> _blipQueryBuffer = new List<MinimapBlipData>(96);

        private GameObject _hudRootGo;

        // Top-Left Status Bars
        private RectTransform _healthFillRect;
        private RectTransform _armorFillRect;
        private RectTransform _staminaFillRect;
        private float _lastHealthNorm = -1f;
        private float _lastArmorNorm = -1f;
        private float _lastStaminaNorm = -1f;

        // Top-Left Masked Circular Minimap (Zero Second Camera)
        private RectTransform _minimapBlipLayerRect;
        private RectTransform _playerCenterArrowRect;
        private RectTransform _northBadgeRect;
        private RectTransform[] _blipDotRects;
        private Image[] _blipDotImages;
        private int _activeBlipCount;
        private Sprite _playerArrowSprite;
        private Sprite _minimapMaskDiscSprite;
        private Sprite _minimapBezelSprite;

        // Top-Right Weapon Slot UI
        private Text _weaponNameText;
        private Text _weaponAmmoText;
        private Text _weaponSubHintText;
        private string _cachedWeaponName;
        private int _cachedClipAmmo = int.MinValue;
        private int _cachedReserveAmmo = int.MinValue;
        private bool _cachedReloadState;
        private bool _cachedWeaponEquippedState = true;

        // Center Crosshair UI
        private GameObject _crosshairRootGo;
        private RectTransform _crosshairTopTick;
        private RectTransform _crosshairBottomTick;
        private RectTransform _crosshairLeftTick;
        private RectTransform _crosshairRightTick;
        private Image[] _crosshairGraphicImages;
        private bool _lastHitColorState;

        // Center-Bottom Interaction Prompt & Top-Center Mission Banner
        private GameObject _interactionPromptGo;
        private Text _interactionPromptText;
        private string _cachedPromptText;

        private GameObject _missionBannerGo;
        private Text _missionBannerText;
        private string _cachedMissionBannerText;

        public HudManager(
            TouchInputManager inputManager,
            PlayerController playerController,
            ThirdPersonCamera thirdPersonCamera,
            WorldChunkManager chunkManager,
            InteractionSystem interactionSystem,
            PlayerCombatController combatController)
        {
            _inputManager = inputManager;
            _playerController = playerController;
            _thirdPersonCamera = thirdPersonCamera;
            _chunkManager = chunkManager;
            _interactionSystem = interactionSystem;
            _combatController = combatController;
        }

        public void Initialize()
        {
            if (_inputManager != null)
            {
                _inputManager.EnsureTouchUiCreated();
            }

            BuildHudSprites();
            BuildHudHierarchy();
        }

        public void Tick(float deltaTime)
        {
            if (_hudRootGo == null)
            {
                return;
            }

            UpdateVitalityBars();
            UpdateMaskedCircularMinimap();
            UpdateWeaponSlotUi();
            UpdateCrosshairUi();
            UpdateInteractionAndMissionPrompts();
        }

        public void Shutdown()
        {
            if (_hudRootGo != null)
            {
                Object.Destroy(_hudRootGo);
                _hudRootGo = null;
            }

            DestroySpriteAndTexture(ref _playerArrowSprite);
            DestroySpriteAndTexture(ref _minimapMaskDiscSprite);
            DestroySpriteAndTexture(ref _minimapBezelSprite);

            _blipDotRects = null;
            _blipDotImages = null;
            _crosshairGraphicImages = null;
            _blipQueryBuffer.Clear();
        }

        #region HUD Construction

        private void BuildHudSprites()
        {
            _minimapMaskDiscSprite = CreateSolidCircleSprite(128);
            _minimapBezelSprite = CreateBezelRingSprite(128);
            _playerArrowSprite = CreateCenterArrowSprite(64);
        }

        private void BuildHudHierarchy()
        {
            RectTransform parentRect = _inputManager != null ? _inputManager.SafeAreaRect : null;
            if (parentRect == null)
            {
                return;
            }

            _hudRootGo = new GameObject("GameplayHUDLayer", typeof(RectTransform));
            var hudRect = _hudRootGo.GetComponent<RectTransform>();
            hudRect.SetParent(parentRect, false);
            hudRect.anchorMin = Vector2.zero;
            hudRect.anchorMax = Vector2.one;
            hudRect.offsetMin = Vector2.zero;
            hudRect.offsetMax = Vector2.zero;

            // Place GameplayHUDLayer behind touch buttons or allow interactive cards on top cleanly
            BuildTopLeftCircularMinimap(hudRect);
            BuildTopLeftStatusBars(hudRect);
            BuildTopRightWeaponSlot(hudRect);
            BuildCenterCrosshair(hudRect);
            BuildCenterBottomInteractionPrompt(hudRect);
            BuildTopCenterMissionBanner(hudRect);
        }

        private void BuildTopLeftCircularMinimap(RectTransform parent)
        {
            Font font = TouchInputManager.ResolveRuntimeFont();

            // Outer Circular Frame at Top-Left
            var frameGo = new GameObject("CircularMinimapFrame", typeof(RectTransform), typeof(Image));
            var frameRect = frameGo.GetComponent<RectTransform>();
            frameRect.SetParent(parent, false);
            frameRect.anchorMin = new Vector2(0f, 1f);
            frameRect.anchorMax = new Vector2(0f, 1f);
            frameRect.pivot = new Vector2(0.5f, 0.5f);
            frameRect.anchoredPosition = new Vector2(130f, -130f);
            frameRect.sizeDelta = new Vector2(212f, 212f);

            var frameImg = frameGo.GetComponent<Image>();
            frameImg.sprite = _minimapBezelSprite;
            frameImg.color = new Color(0.96f, 0.78f, 0.36f, 0.92f);
            frameImg.raycastTarget = false;

            // Inner Circular Masked Viewport (Uses UGUI Mask — Zero Second Camera!)
            var maskViewportGo = new GameObject("MinimapMaskedViewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            var maskRect = maskViewportGo.GetComponent<RectTransform>();
            maskRect.SetParent(frameRect, false);
            maskRect.anchorMin = new Vector2(0.5f, 0.5f);
            maskRect.anchorMax = new Vector2(0.5f, 0.5f);
            maskRect.pivot = new Vector2(0.5f, 0.5f);
            maskRect.anchoredPosition = Vector2.zero;
            maskRect.sizeDelta = new Vector2(MinimapViewportDiameter, MinimapViewportDiameter);

            var maskBgImg = maskViewportGo.GetComponent<Image>();
            maskBgImg.sprite = _minimapMaskDiscSprite;
            maskBgImg.color = new Color(0.08f, 0.11f, 0.16f, 0.88f);
            maskBgImg.raycastTarget = false;

            var uiMask = maskViewportGo.GetComponent<Mask>();
            uiMask.showMaskGraphic = true;

            // Subtle Mid-Range Radar Ring inside the Mask
            var innerRingGo = new GameObject("RadarMidRing", typeof(RectTransform), typeof(Image));
            var innerRingRect = innerRingGo.GetComponent<RectTransform>();
            innerRingRect.SetParent(maskRect, false);
            innerRingRect.anchorMin = new Vector2(0.5f, 0.5f);
            innerRingRect.anchorMax = new Vector2(0.5f, 0.5f);
            innerRingRect.sizeDelta = new Vector2(114f, 114f);
            var innerRingImg = innerRingGo.GetComponent<Image>();
            innerRingImg.sprite = _minimapBezelSprite;
            innerRingImg.color = new Color(0.55f, 0.70f, 0.85f, 0.16f);
            innerRingImg.raycastTarget = false;

            // Blip Dot Container (clipped by UGUI Mask)
            var blipLayerGo = new GameObject("BlipDotsContainer", typeof(RectTransform));
            _minimapBlipLayerRect = blipLayerGo.GetComponent<RectTransform>();
            _minimapBlipLayerRect.SetParent(maskRect, false);
            _minimapBlipLayerRect.anchorMin = new Vector2(0.5f, 0.5f);
            _minimapBlipLayerRect.anchorMax = new Vector2(0.5f, 0.5f);
            _minimapBlipLayerRect.sizeDelta = Vector2.zero;

            _blipDotRects = new RectTransform[MaxMinimapBlipPool];
            _blipDotImages = new Image[MaxMinimapBlipPool];

            for (int i = 0; i < MaxMinimapBlipPool; i++)
            {
                var dotGo = new GameObject($"BlipDot_{i}", typeof(RectTransform), typeof(Image));
                var dotRect = dotGo.GetComponent<RectTransform>();
                dotRect.SetParent(_minimapBlipLayerRect, false);
                dotRect.anchorMin = new Vector2(0.5f, 0.5f);
                dotRect.anchorMax = new Vector2(0.5f, 0.5f);
                dotRect.pivot = new Vector2(0.5f, 0.5f);
                dotRect.sizeDelta = new Vector2(9f, 9f);

                var dotImg = dotGo.GetComponent<Image>();
                dotImg.sprite = _minimapMaskDiscSprite;
                dotImg.raycastTarget = false;

                dotGo.SetActive(false);
                _blipDotRects[i] = dotRect;
                _blipDotImages[i] = dotImg;
            }

            _activeBlipCount = 0;

            // Fixed Center Player Arrow (always rendered cleanly on top of radar dots)
            var arrowGo = new GameObject("PlayerCenterArrow", typeof(RectTransform), typeof(Image));
            _playerCenterArrowRect = arrowGo.GetComponent<RectTransform>();
            _playerCenterArrowRect.SetParent(maskRect, false);
            _playerCenterArrowRect.anchorMin = new Vector2(0.5f, 0.5f);
            _playerCenterArrowRect.anchorMax = new Vector2(0.5f, 0.5f);
            _playerCenterArrowRect.pivot = new Vector2(0.5f, 0.5f);
            _playerCenterArrowRect.anchoredPosition = Vector2.zero;
            _playerCenterArrowRect.sizeDelta = new Vector2(24f, 24f);

            var arrowImg = arrowGo.GetComponent<Image>();
            arrowImg.sprite = _playerArrowSprite;
            arrowImg.color = new Color(1.0f, 0.98f, 0.90f, 1.0f);
            arrowImg.raycastTarget = false;

            // Orbiting North ("N") Compass Indicator on the Bezel Rim
            var northGo = new GameObject("NorthCompassBadge", typeof(RectTransform), typeof(Image));
            _northBadgeRect = northGo.GetComponent<RectTransform>();
            _northBadgeRect.SetParent(frameRect, false);
            _northBadgeRect.anchorMin = new Vector2(0.5f, 0.5f);
            _northBadgeRect.anchorMax = new Vector2(0.5f, 0.5f);
            _northBadgeRect.pivot = new Vector2(0.5f, 0.5f);
            _northBadgeRect.anchoredPosition = new Vector2(0f, 98f);
            _northBadgeRect.sizeDelta = new Vector2(26f, 26f);

            var northBg = northGo.GetComponent<Image>();
            northBg.sprite = _minimapMaskDiscSprite;
            northBg.color = new Color(0.12f, 0.14f, 0.20f, 0.96f);
            northBg.raycastTarget = false;

            var northLabelGo = new GameObject("N", typeof(RectTransform), typeof(Text));
            var northLabelRect = northLabelGo.GetComponent<RectTransform>();
            northLabelRect.SetParent(_northBadgeRect, false);
            northLabelRect.anchorMin = Vector2.zero;
            northLabelRect.anchorMax = Vector2.one;
            northLabelRect.offsetMin = Vector2.zero;
            northLabelRect.offsetMax = Vector2.zero;

            var northTxt = northLabelGo.GetComponent<Text>();
            northTxt.font = font;
            northTxt.text = "N";
            northTxt.fontSize = 14;
            northTxt.fontStyle = FontStyle.Bold;
            northTxt.alignment = TextAnchor.MiddleCenter;
            northTxt.color = new Color(1.0f, 0.78f, 0.30f, 1.0f);
            northTxt.raycastTarget = false;
        }

        private void BuildTopLeftStatusBars(RectTransform parent)
        {
            Font font = TouchInputManager.ResolveRuntimeFont();

            var panelGo = new GameObject("TopLeftVitalityBars", typeof(RectTransform), typeof(Image));
            var panelRect = panelGo.GetComponent<RectTransform>();
            panelRect.SetParent(parent, false);
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(252f, -34f);
            panelRect.sizeDelta = new Vector2(256f, 108f);

            var panelBg = panelGo.GetComponent<Image>();
            panelBg.color = new Color(0.07f, 0.09f, 0.14f, 0.74f);
            panelBg.raycastTarget = false;

            _healthFillRect = CreateSingleStatusBar(panelRect, font, "HP", -16f, HealthBarColor);
            _armorFillRect = CreateSingleStatusBar(panelRect, font, "AR", -48f, ArmorBarColor);
            _staminaFillRect = CreateSingleStatusBar(panelRect, font, "ST", -80f, StaminaBarColor);
        }

        private static RectTransform CreateSingleStatusBar(
            RectTransform parent,
            Font font,
            string shortLabel,
            float yOffset,
            Color fillColor)
        {
            var labelGo = new GameObject($"Label_{shortLabel}", typeof(RectTransform), typeof(Text));
            var labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.SetParent(parent, false);
            labelRect.anchorMin = new Vector2(0f, 1f);
            labelRect.anchorMax = new Vector2(0f, 1f);
            labelRect.pivot = new Vector2(0f, 0.5f);
            labelRect.anchoredPosition = new Vector2(12f, yOffset - 6f);
            labelRect.sizeDelta = new Vector2(34f, 22f);

            var txt = labelGo.GetComponent<Text>();
            txt.font = font;
            txt.text = shortLabel;
            txt.fontSize = 14;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = new Color(0.95f, 0.93f, 0.88f, 0.92f);
            txt.raycastTarget = false;

            var trackGo = new GameObject($"Track_{shortLabel}", typeof(RectTransform), typeof(Image));
            var trackRect = trackGo.GetComponent<RectTransform>();
            trackRect.SetParent(parent, false);
            trackRect.anchorMin = new Vector2(0f, 1f);
            trackRect.anchorMax = new Vector2(0f, 1f);
            trackRect.pivot = new Vector2(0f, 0.5f);
            trackRect.anchoredPosition = new Vector2(48f, yOffset - 6f);
            trackRect.sizeDelta = new Vector2(194f, 16f);

            var trackImg = trackGo.GetComponent<Image>();
            trackImg.color = new Color(0.18f, 0.21f, 0.27f, 0.92f);
            trackImg.raycastTarget = false;

            var fillGo = new GameObject($"Fill_{shortLabel}", typeof(RectTransform), typeof(Image));
            var fillRect = fillGo.GetComponent<RectTransform>();
            fillRect.SetParent(trackRect, false);
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(2f, 2f);
            fillRect.offsetMax = new Vector2(-2f, -2f);

            var fillImg = fillGo.GetComponent<Image>();
            fillImg.color = fillColor;
            fillImg.raycastTarget = false;

            return fillRect;
        }

        private void BuildTopRightWeaponSlot(RectTransform parent)
        {
            Font font = TouchInputManager.ResolveRuntimeFont();

            var slotGo = new GameObject("TopRightWeaponSlot", typeof(RectTransform), typeof(Image));
            var slotRect = slotGo.GetComponent<RectTransform>();
            slotRect.SetParent(parent, false);
            slotRect.anchorMin = new Vector2(1f, 1f);
            slotRect.anchorMax = new Vector2(1f, 1f);
            slotRect.pivot = new Vector2(1f, 1f);
            slotRect.anchoredPosition = new Vector2(-26f, -26f);
            slotRect.sizeDelta = new Vector2(268f, 94f);

            var slotBg = slotGo.GetComponent<Image>();
            slotBg.color = new Color(0.08f, 0.10f, 0.15f, 0.82f);
            slotBg.raycastTarget = true;

            var tapRelay = slotGo.AddComponent<HudTapActionRelay>();
            tapRelay.Initialize(() =>
            {
                if (_combatController != null)
                {
                    _combatController.ToggleWeaponHolsterOrReload();
                }
            });

            // Gold Accent Bar on Right Edge
            var accentGo = new GameObject("GoldAccentBar", typeof(RectTransform), typeof(Image));
            var accentRect = accentGo.GetComponent<RectTransform>();
            accentRect.SetParent(slotRect, false);
            accentRect.anchorMin = new Vector2(1f, 0f);
            accentRect.anchorMax = new Vector2(1f, 1f);
            accentRect.pivot = new Vector2(1f, 0.5f);
            accentRect.sizeDelta = new Vector2(5f, 0f);
            var accentImg = accentGo.GetComponent<Image>();
            accentImg.color = new Color(1.0f, 0.76f, 0.26f, 0.95f);
            accentImg.raycastTarget = false;

            // Weapon Name Label
            var nameGo = new GameObject("WeaponNameLabel", typeof(RectTransform), typeof(Text));
            var nameRect = nameGo.GetComponent<RectTransform>();
            nameRect.SetParent(slotRect, false);
            nameRect.anchorMin = new Vector2(0f, 0.54f);
            nameRect.anchorMax = new Vector2(1f, 0.94f);
            nameRect.offsetMin = new Vector2(14f, 0f);
            nameRect.offsetMax = new Vector2(-16f, 0f);

            _weaponNameText = nameGo.GetComponent<Text>();
            _weaponNameText.font = font;
            _weaponNameText.fontSize = 18;
            _weaponNameText.fontStyle = FontStyle.Bold;
            _weaponNameText.alignment = TextAnchor.MiddleRight;
            _weaponNameText.color = new Color(1.0f, 0.84f, 0.42f, 1.0f);
            _weaponNameText.raycastTarget = false;

            // Ammo Count Label
            var ammoGo = new GameObject("WeaponAmmoLabel", typeof(RectTransform), typeof(Text));
            var ammoRect = ammoGo.GetComponent<RectTransform>();
            ammoRect.SetParent(slotRect, false);
            ammoRect.anchorMin = new Vector2(0f, 0.18f);
            ammoRect.anchorMax = new Vector2(1f, 0.58f);
            ammoRect.offsetMin = new Vector2(14f, 0f);
            ammoRect.offsetMax = new Vector2(-16f, 0f);

            _weaponAmmoText = ammoGo.GetComponent<Text>();
            _weaponAmmoText.font = font;
            _weaponAmmoText.fontSize = 22;
            _weaponAmmoText.fontStyle = FontStyle.Bold;
            _weaponAmmoText.alignment = TextAnchor.MiddleRight;
            _weaponAmmoText.color = new Color(0.98f, 0.97f, 0.94f, 1.0f);
            _weaponAmmoText.raycastTarget = false;

            // Sub-hint ("TAP TO RELOAD / SWITCH")
            var hintGo = new GameObject("WeaponHintLabel", typeof(RectTransform), typeof(Text));
            var hintRect = hintGo.GetComponent<RectTransform>();
            hintRect.SetParent(slotRect, false);
            hintRect.anchorMin = new Vector2(0f, 0f);
            hintRect.anchorMax = new Vector2(1f, 0.22f);
            hintRect.offsetMin = new Vector2(12f, 2f);
            hintRect.offsetMax = new Vector2(-16f, 0f);

            _weaponSubHintText = hintGo.GetComponent<Text>();
            _weaponSubHintText.font = font;
            _weaponSubHintText.text = "TAP SLOT: RELOAD / HOLSTER";
            _weaponSubHintText.fontSize = 11;
            _weaponSubHintText.alignment = TextAnchor.MiddleRight;
            _weaponSubHintText.color = new Color(0.75f, 0.78f, 0.84f, 0.78f);
            _weaponSubHintText.raycastTarget = false;
        }

        private void BuildCenterCrosshair(RectTransform parent)
        {
            _crosshairRootGo = new GameObject("CenterCrosshair", typeof(RectTransform));
            var rootRect = _crosshairRootGo.GetComponent<RectTransform>();
            rootRect.SetParent(parent, false);
            rootRect.anchorMin = new Vector2(0.5f, 0.5f);
            rootRect.anchorMax = new Vector2(0.5f, 0.5f);
            rootRect.pivot = new Vector2(0.5f, 0.5f);
            rootRect.anchoredPosition = Vector2.zero;
            rootRect.sizeDelta = new Vector2(64f, 64f);

            var centerDotImg = CreateCrosshairElement(rootRect, "CenterDot", Vector2.zero, new Vector2(4f, 4f), out _);
            var topImg = CreateCrosshairElement(rootRect, "TickTop", new Vector2(0f, 11f), new Vector2(2.5f, 9f), out _crosshairTopTick);
            var bottomImg = CreateCrosshairElement(rootRect, "TickBottom", new Vector2(0f, -11f), new Vector2(2.5f, 9f), out _crosshairBottomTick);
            var leftImg = CreateCrosshairElement(rootRect, "TickLeft", new Vector2(-11f, 0f), new Vector2(9f, 2.5f), out _crosshairLeftTick);
            var rightImg = CreateCrosshairElement(rootRect, "TickRight", new Vector2(11f, 0f), new Vector2(9f, 2.5f), out _crosshairRightTick);

            _crosshairGraphicImages = new[] { centerDotImg, topImg, bottomImg, leftImg, rightImg };
        }

        private static Image CreateCrosshairElement(
            RectTransform parent,
            string name,
            Vector2 anchoredPos,
            Vector2 size,
            out RectTransform rectOut)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            rectOut = go.GetComponent<RectTransform>();
            rectOut.SetParent(parent, false);
            rectOut.anchorMin = new Vector2(0.5f, 0.5f);
            rectOut.anchorMax = new Vector2(0.5f, 0.5f);
            rectOut.pivot = new Vector2(0.5f, 0.5f);
            rectOut.anchoredPosition = anchoredPos;
            rectOut.sizeDelta = size;

            var img = go.GetComponent<Image>();
            img.color = CrosshairIdleColor;
            img.raycastTarget = false;
            return img;
        }

        private void BuildCenterBottomInteractionPrompt(RectTransform parent)
        {
            Font font = TouchInputManager.ResolveRuntimeFont();

            _interactionPromptGo = new GameObject("CenterBottomInteractionPrompt", typeof(RectTransform), typeof(Image));
            var promptRect = _interactionPromptGo.GetComponent<RectTransform>();
            promptRect.SetParent(parent, false);
            promptRect.anchorMin = new Vector2(0.5f, 0f);
            promptRect.anchorMax = new Vector2(0.5f, 0f);
            promptRect.pivot = new Vector2(0.5f, 0.5f);
            promptRect.anchoredPosition = new Vector2(0f, 114f);
            promptRect.sizeDelta = new Vector2(336f, 64f);

            var promptBg = _interactionPromptGo.GetComponent<Image>();
            promptBg.color = new Color(0.09f, 0.11f, 0.16f, 0.90f);
            promptBg.raycastTarget = true;

            var tapRelay = _interactionPromptGo.AddComponent<HudTapActionRelay>();
            tapRelay.Initialize(() =>
            {
                if (_inputManager != null)
                {
                    _inputManager.TriggerUiInteraction();
                }
            });

            // Saffron-Gold Bottom Highlight Strip
            var stripGo = new GameObject("BottomAccentStrip", typeof(RectTransform), typeof(Image));
            var stripRect = stripGo.GetComponent<RectTransform>();
            stripRect.SetParent(promptRect, false);
            stripRect.anchorMin = new Vector2(0f, 0f);
            stripRect.anchorMax = new Vector2(1f, 0f);
            stripRect.pivot = new Vector2(0.5f, 0f);
            stripRect.sizeDelta = new Vector2(0f, 4f);
            var stripImg = stripGo.GetComponent<Image>();
            stripImg.color = new Color(1.0f, 0.76f, 0.22f, 1.0f);
            stripImg.raycastTarget = false;

            var txtGo = new GameObject("PromptLabel", typeof(RectTransform), typeof(Text));
            var txtRect = txtGo.GetComponent<RectTransform>();
            txtRect.SetParent(promptRect, false);
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = new Vector2(12f, 4f);
            txtRect.offsetMax = new Vector2(-12f, 0f);

            _interactionPromptText = txtGo.GetComponent<Text>();
            _interactionPromptText.font = font;
            _interactionPromptText.fontSize = 21;
            _interactionPromptText.fontStyle = FontStyle.Bold;
            _interactionPromptText.alignment = TextAnchor.MiddleCenter;
            _interactionPromptText.color = new Color(1.0f, 0.88f, 0.52f, 1.0f);
            _interactionPromptText.raycastTarget = false;

            _interactionPromptGo.SetActive(false);
        }

        private void BuildTopCenterMissionBanner(RectTransform parent)
        {
            Font font = TouchInputManager.ResolveRuntimeFont();

            _missionBannerGo = new GameObject("TopCenterMissionBanner", typeof(RectTransform), typeof(Image));
            var bannerRect = _missionBannerGo.GetComponent<RectTransform>();
            bannerRect.SetParent(parent, false);
            bannerRect.anchorMin = new Vector2(0.5f, 1f);
            bannerRect.anchorMax = new Vector2(0.5f, 1f);
            bannerRect.pivot = new Vector2(0.5f, 1f);
            bannerRect.anchoredPosition = new Vector2(0f, -26f);
            bannerRect.sizeDelta = new Vector2(520f, 48f);

            var bg = _missionBannerGo.GetComponent<Image>();
            bg.color = new Color(0.08f, 0.11f, 0.16f, 0.82f);
            bg.raycastTarget = false;

            var txtGo = new GameObject("MissionObjectiveText", typeof(RectTransform), typeof(Text));
            var txtRect = txtGo.GetComponent<RectTransform>();
            txtRect.SetParent(bannerRect, false);
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = new Vector2(14f, 0f);
            txtRect.offsetMax = new Vector2(-14f, 0f);

            _missionBannerText = txtGo.GetComponent<Text>();
            _missionBannerText.font = font;
            _missionBannerText.fontSize = 18;
            _missionBannerText.fontStyle = FontStyle.Bold;
            _missionBannerText.alignment = TextAnchor.MiddleCenter;
            _missionBannerText.color = new Color(1.0f, 0.84f, 0.34f, 1.0f);
            _missionBannerText.raycastTarget = false;

            _missionBannerGo.SetActive(false);
        }

        #endregion

        #region Per-Frame Zero-GC HUD Updates

        private void UpdateVitalityBars()
        {
            if (_playerController == null)
            {
                return;
            }

            float healthNorm = Mathf.Clamp01(_playerController.Health / PlayerController.MaxHealth);
            if (Mathf.Abs(healthNorm - _lastHealthNorm) > 0.002f && _healthFillRect != null)
            {
                _lastHealthNorm = healthNorm;
                _healthFillRect.anchorMax = new Vector2(healthNorm, 1f);
            }

            float armorNorm = Mathf.Clamp01(_playerController.Armor / PlayerController.MaxArmor);
            if (Mathf.Abs(armorNorm - _lastArmorNorm) > 0.002f && _armorFillRect != null)
            {
                _lastArmorNorm = armorNorm;
                _armorFillRect.anchorMax = new Vector2(armorNorm, 1f);
            }

            float staminaNorm = Mathf.Clamp01(_playerController.Stamina / PlayerController.MaxStamina);
            if (Mathf.Abs(staminaNorm - _lastStaminaNorm) > 0.002f && _staminaFillRect != null)
            {
                _lastStaminaNorm = staminaNorm;
                _staminaFillRect.anchorMax = new Vector2(staminaNorm, 1f);
            }
        }

        private void UpdateMaskedCircularMinimap()
        {
            if (_blipDotRects == null)
            {
                return;
            }

            Transform focusTransform = _interactionSystem != null
                ? _interactionSystem.ActiveFocusTransform
                : (_playerController != null ? _playerController.PlayerTransform : null);

            if (focusTransform == null)
            {
                return;
            }

            Vector3 focusPos = focusTransform.position;
            float referenceYawDeg = _thirdPersonCamera != null ? _thirdPersonCamera.CurrentYaw : focusTransform.eulerAngles.y;

            // Orbit the "N" compass badge around the circular bezel
            if (_northBadgeRect != null)
            {
                float northRad = referenceYawDeg * Mathf.Deg2Rad;
                _northBadgeRect.anchoredPosition = new Vector2(
                    -Mathf.Sin(northRad) * 98f,
                    Mathf.Cos(northRad) * 98f);
            }

            // Keep center arrow fixed at (0,0) and rotate subtly to reflect character/vehicle nose relative to camera
            if (_playerCenterArrowRect != null)
            {
                float relativeHeading = Mathf.DeltaAngle(referenceYawDeg, focusTransform.eulerAngles.y);
                _playerCenterArrowRect.localEulerAngles = new Vector3(0f, 0f, -relativeHeading);
            }

            // Collect nearby Building, Mandir, Vehicle, and Mission blips with zero allocations
            _blipQueryBuffer.Clear();
            if (_chunkManager != null)
            {
                _chunkManager.CollectNearbyMinimapBlips(focusPos, MinimapWorldRadius, _blipQueryBuffer);
            }

            if (_interactionSystem != null)
            {
                _interactionSystem.AppendDynamicMinimapBlips(focusPos, MinimapWorldRadius, _blipQueryBuffer);
            }

            float rad = referenceYawDeg * Mathf.Deg2Rad;
            float cosYaw = Mathf.Cos(rad);
            float sinYaw = Mathf.Sin(rad);
            float pixelsPerMeter = MinimapInnerPixelRadius / MinimapWorldRadius;

            int shownCount = Mathf.Min(_blipQueryBuffer.Count, MaxMinimapBlipPool);
            for (int i = 0; i < shownCount; i++)
            {
                MinimapBlipData blip = _blipQueryBuffer[i];
                float dx = blip.WorldPosition.x - focusPos.x;
                float dz = blip.WorldPosition.z - focusPos.z;

                // Rotate world offset by -referenceYaw so forward in world maps to +Y on radar
                float radarX = (dx * cosYaw) - (dz * sinYaw);
                float radarY = (dx * sinYaw) + (dz * cosYaw);

                Vector2 uiPos = new Vector2(radarX * pixelsPerMeter, radarY * pixelsPerMeter);
                if (blip.Category == MinimapBlipCategory.Mission && uiPos.sqrMagnitude > (MinimapInnerPixelRadius * MinimapInnerPixelRadius))
                {
                    uiPos = uiPos.normalized * (MinimapInnerPixelRadius - 4f);
                }

                RectTransform dotRect = _blipDotRects[i];
                Image dotImg = _blipDotImages[i];

                if (i >= _activeBlipCount)
                {
                    dotRect.gameObject.SetActive(true);
                }

                dotRect.anchoredPosition = uiPos;

                switch (blip.Category)
                {
                    case MinimapBlipCategory.Mandir:
                        dotRect.sizeDelta = new Vector2(13f, 13f);
                        dotImg.color = MandirBlipColor;
                        break;
                    case MinimapBlipCategory.Vehicle:
                        dotRect.sizeDelta = new Vector2(10f, 10f);
                        dotImg.color = VehicleBlipColor;
                        break;
                    case MinimapBlipCategory.Mission:
                        dotRect.sizeDelta = new Vector2(14f, 14f);
                        dotImg.color = MissionBlipColor;
                        break;
                    default:
                        dotRect.sizeDelta = new Vector2(7.5f, 7.5f);
                        dotImg.color = BuildingBlipColor;
                        break;
                }
            }

            for (int i = shownCount; i < _activeBlipCount; i++)
            {
                _blipDotRects[i].gameObject.SetActive(false);
            }

            _activeBlipCount = shownCount;
        }

        private void UpdateWeaponSlotUi()
        {
            if (_combatController == null || _weaponNameText == null || _weaponAmmoText == null)
            {
                return;
            }

            string currentName = _combatController.DisplayWeaponName;
            if (_cachedWeaponName != currentName)
            {
                _cachedWeaponName = currentName;
                _weaponNameText.text = currentName;
            }

            bool equipped = _combatController.IsWeaponEquipped;
            bool reloading = _combatController.IsReloading;
            int clip = _combatController.CurrentClipAmmo;
            int reserve = _combatController.ReserveAmmo;

            if (equipped != _cachedWeaponEquippedState ||
                reloading != _cachedReloadState ||
                clip != _cachedClipAmmo ||
                reserve != _cachedReserveAmmo)
            {
                _cachedWeaponEquippedState = equipped;
                _cachedReloadState = reloading;
                _cachedClipAmmo = clip;
                _cachedReserveAmmo = reserve;

                if (!equipped)
                {
                    _weaponAmmoText.text = "MELEE";
                }
                else if (reloading)
                {
                    _weaponAmmoText.text = "RELOADING...";
                }
                else
                {
                    _weaponAmmoText.text = $"{clip} / {reserve}";
                }
            }
        }

        private void UpdateCrosshairUi()
        {
            if (_crosshairRootGo == null)
            {
                return;
            }

            bool isDriving = _interactionSystem != null && _interactionSystem.IsDriving;
            if (_crosshairRootGo.activeSelf == isDriving)
            {
                _crosshairRootGo.SetActive(!isDriving);
            }

            if (isDriving || _combatController == null)
            {
                return;
            }

            float gap = 10f + (_combatController.CrosshairBloomNormalized * 14f);
            _crosshairTopTick.anchoredPosition = new Vector2(0f, gap);
            _crosshairBottomTick.anchoredPosition = new Vector2(0f, -gap);
            _crosshairLeftTick.anchoredPosition = new Vector2(-gap, 0f);
            _crosshairRightTick.anchoredPosition = new Vector2(gap, 0f);

            bool hitActive = _combatController.HasRecentHitConfirmation;
            if (hitActive != _lastHitColorState && _crosshairGraphicImages != null)
            {
                _lastHitColorState = hitActive;
                Color targetCol = hitActive ? CrosshairHitColor : CrosshairIdleColor;
                for (int i = 0; i < _crosshairGraphicImages.Length; i++)
                {
                    _crosshairGraphicImages[i].color = targetCol;
                }
            }
        }

        private void UpdateInteractionAndMissionPrompts()
        {
            if (_interactionPromptGo != null && _interactionSystem != null)
            {
                bool showPrompt = _interactionSystem.HasInteractionPrompt;
                if (_interactionPromptGo.activeSelf != showPrompt)
                {
                    _interactionPromptGo.SetActive(showPrompt);
                }

                if (showPrompt)
                {
                    string prompt = _interactionSystem.CurrentInteractionPrompt;
                    if (_cachedPromptText != prompt)
                    {
                        _cachedPromptText = prompt;
                        _interactionPromptText.text = prompt;
                    }
                }
            }

            if (_missionBannerGo != null && _interactionSystem != null)
            {
                string banner = _interactionSystem.ActiveMissionBannerText;
                bool showBanner = !string.IsNullOrEmpty(banner);
                if (_missionBannerGo.activeSelf != showBanner)
                {
                    _missionBannerGo.SetActive(showBanner);
                }

                if (showBanner && _cachedMissionBannerText != banner)
                {
                    _cachedMissionBannerText = banner;
                    _missionBannerText.text = banner;
                }
            }
        }

        #endregion

        #region Procedural Minimap Sprites & UI Tap Relay

        private static Sprite CreateSolidCircleSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "MinimapMaskCircle",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            float maxR = center - 1.2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt((dx * dx) + (dy * dy));
                    float a = Mathf.Clamp01((maxR - dist) * 0.95f);
                    pixels[(y * size) + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite CreateBezelRingSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "MinimapBezelRing",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[size * size];
            float center = (size - 1) * 0.5f;
            float outerR = center - 1.2f;
            float innerR = outerR - (size * 0.06f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt((dx * dx) + (dy * dy));
                    float outerMask = Mathf.Clamp01((outerR - dist) * 0.9f);
                    float innerMask = Mathf.Clamp01((dist - innerR) * 0.9f);
                    float a = outerMask * innerMask;
                    pixels[(y * size) + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite CreateCenterArrowSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false)
            {
                name = "MinimapPlayerArrow",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            var pixels = new Color32[size * size];
            float half = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                float ny = (y - half) / half; // -1 (bottom) .. +1 (top)
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - half) / half;
                    float absX = Mathf.Abs(nx);

                    // Crisp upward GTA-style notched arrowhead
                    float maxHalfWidthAtY = (0.72f - ny) * 0.48f;
                    float notchFloorY = -0.52f + (absX * 0.55f);

                    bool inside = ny <= 0.72f && ny >= notchFloorY && absX <= maxHalfWidthAtY;
                    pixels[(y * size) + x] = inside
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(0, 0, 0, 0);
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

        private sealed class HudTapActionRelay : MonoBehaviour, IPointerDownHandler
        {
            private System.Action _onTap;

            public void Initialize(System.Action onTap)
            {
                _onTap = onTap;
            }

            public void OnPointerDown(PointerEventData eventData)
            {
                _onTap?.Invoke();
            }
        }

        #endregion
    }
}
