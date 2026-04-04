using UnityEngine;
using UnityEngine.UI;

namespace SomniumSpace.Worlds.Snacks
{
    /// <summary>
    /// VR-compatible high effect. Uses a World Space canvas that follows Camera.main
    /// every frame — this is the only reliable way to render fullscreen overlays
    /// inside a VR headset in Somnium Space (Screen Space Overlay only shows on
    /// the desktop mirror window, not in the headset).
    ///
    /// SETUP (same as before):
    ///  1. Add this component to any GameObject in your scene.
    ///  2. In GrabAndEat add:
    ///       [SerializeField] private HighEffect highEffect;
    ///     and inside OnNetworkEatReceived() after the `if (isEnabled) return;` guard:
    ///       if (SomniumBridge.PlayersContainer?.LocalPlayer != null)
    ///           highEffect?.TriggerHigh();
    ///  3. Drag the HighEffect component into the slot on GrabAndEat's Inspector.
    /// </summary>
    public class HighEffect : MonoBehaviour
    {
        // ── Duration & Timing ────────────────────────────────────────────────
        [Header("Duration & Timing")]
        [Tooltip("Total duration of the high in seconds.")]
        [SerializeField] private float highDuration = 30f;

        [Tooltip("Seconds to fade IN at the start.")]
        [SerializeField] private float fadeInDuration = 3f;

        [Tooltip("Seconds to fade OUT at the end.")]
        [SerializeField] private float fadeOutDuration = 5f;

        // ── Colour Overlay ───────────────────────────────────────────────────
        [Header("Colour Overlay")]
        [Tooltip("Max alpha of the fullscreen colour overlay (0.2-0.35 recommended).")]
        [SerializeField, Range(0f, 1f)] private float overlayMaxAlpha = 0.25f;

        [Tooltip("How fast the hue cycles (higher = faster rainbow).")]
        [SerializeField] private float hueSpeed = 0.12f;

        // ── Sway (canvas wobble) ─────────────────────────────────────────────
        [Header("Screen Sway")]
        [Tooltip("Max metres the canvas shifts side-to-side in world space.")]
        [SerializeField] private float swayAmplitude = 0.04f;

        [Tooltip("How fast the canvas sways (cycles per second).")]
        [SerializeField] private float swayFrequency = 0.4f;

        // ── Vignette ─────────────────────────────────────────────────────────
        [Header("Vignette Pulse")]
        [Tooltip("Max alpha of the vignette darkening around screen edges.")]
        [SerializeField, Range(0f, 1f)] private float vignetteMaxAlpha = 0.6f;

        [Tooltip("How fast the vignette pulses (breathes in and out).")]
        [SerializeField] private float vignettePulseSpeed = 0.8f;

        // ── Time Warp ────────────────────────────────────────────────────────
        [Header("Time Warp")]
        [Tooltip("How much to slow time during the high (1 = normal, 0.75 = 25% slower).")]
        [SerializeField, Range(0.3f, 1f)] private float timeScaleTarget = 0.75f;

        // ── Canvas Distance ──────────────────────────────────────────────────
        [Header("VR Canvas Settings")]
        [Tooltip("How far in front of the camera the overlay canvas sits (metres). " +
                 "0.1 is safe for most headsets without clipping.")]
        [SerializeField] private float canvasDistance = 0.1f;

        [Tooltip("World-space size of the canvas quad in metres. " +
                 "0.2 fills the view well at 0.1m distance.")]
        [SerializeField] private float canvasSize = 0.2f;

        // ── Runtime State ────────────────────────────────────────────────────
        private bool _isHigh;
        private float _highTimer;
        private float _effectStrength;

        private Camera _mainCamera;

        private GameObject _canvasGO;
        private Image _colourOverlay;
        private Image _vignetteOverlay;

        private float _hue;

        #region Unity Lifecycle

        private void Awake()
        {
            BuildWorldSpaceCanvas();
        }

        private void Update()
        {
            // Always keep canvas glued to camera so it is ready the instant
            // TriggerHigh() is called, even if camera loaded after Awake.
            TryCacheCamera();
            UpdateCanvasTransform(swayOffset: 0f);

            if (!_isHigh) return;

            _highTimer += Time.unscaledDeltaTime;

            // ── Effect strength curve (fade in / hold / fade out) ────────────
            float holdEnd = highDuration - fadeOutDuration;

            if (_highTimer < fadeInDuration)
                _effectStrength = _highTimer / Mathf.Max(fadeInDuration, 0.001f);
            else if (_highTimer < holdEnd)
                _effectStrength = 1f;
            else if (_highTimer < highDuration)
                _effectStrength = 1f - ((_highTimer - holdEnd) / Mathf.Max(fadeOutDuration, 0.001f));
            else
            {
                EndHigh();
                return;
            }

            ApplyColourOverlay();
            ApplyVignette();
            ApplySway();
            ApplyTimeWarp();
        }

        private void OnDisable()
        {
            if (_isHigh) EndHigh();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Triggers the high effect for the local player.
        /// Safe to call multiple times — restarts cleanly if already running.
        /// </summary>
        public void TriggerHigh()
        {
            if (_isHigh) EndHigh();

            TryCacheCamera();

            _highTimer = 0f;
            _effectStrength = 0f;
            _isHigh = true;

            SetOverlayVisible(true);
            Debug.Log("HighEffect: High triggered! Duration=" + highDuration + "s");
        }

        #endregion

        #region Effects

        private void ApplyColourOverlay()
        {
            if (_colourOverlay == null) return;

            _hue = (_hue + hueSpeed * Time.unscaledDeltaTime) % 1f;
            Color c = Color.HSVToRGB(_hue, 0.8f, 1f);
            c.a = overlayMaxAlpha * _effectStrength;
            _colourOverlay.color = c;
        }

        private void ApplyVignette()
        {
            if (_vignetteOverlay == null) return;

            float pulse = (Mathf.Sin(Time.unscaledTime * vignettePulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;
            Color v = Color.black;
            v.a = vignetteMaxAlpha * _effectStrength * (0.5f + 0.5f * pulse);
            _vignetteOverlay.color = v;
        }

        private void ApplySway()
        {
            // In VR, Camera.main rotation is owned by the headset tracker — writing
            // to it gets overwritten immediately. Instead we wobble the canvas quad
            // laterally in world space which achieves the same "drunk" feeling.
            float offset = Mathf.Sin(Time.unscaledTime * swayFrequency * Mathf.PI * 2f)
                           * swayAmplitude * _effectStrength;
            UpdateCanvasTransform(swayOffset: offset);
        }

        private void ApplyTimeWarp()
        {
            float targetScale = Mathf.Lerp(1f, timeScaleTarget, _effectStrength);
            Time.timeScale = Mathf.Lerp(Time.timeScale, targetScale, Time.unscaledDeltaTime * 3f);
            Time.fixedDeltaTime = 0.02f * Time.timeScale;
        }

        private void EndHigh()
        {
            _isHigh = false;
            _effectStrength = 0f;

            Time.timeScale = 1f;
            Time.fixedDeltaTime = 0.02f;

            SetOverlayVisible(false);
            Debug.Log("HighEffect: High ended, all effects restored.");
        }

        #endregion

        #region Canvas Positioning

        private void TryCacheCamera()
        {
            if (_mainCamera == null && Camera.main != null)
            {
                _mainCamera = Camera.main;
                Debug.Log("HighEffect: Camera.main cached: " + _mainCamera.name);
            }
        }

        /// <summary>
        /// Locks the world-space canvas quad directly in front of the camera every
        /// frame so it fills both eyes. swayOffset shifts it right/left for wobble.
        /// </summary>
        private void UpdateCanvasTransform(float swayOffset)
        {
            if (_canvasGO == null || _mainCamera == null) return;

            Transform cam = _mainCamera.transform;

            Vector3 pos = cam.position
                        + cam.forward * canvasDistance
                        + cam.right   * swayOffset;

            _canvasGO.transform.position = pos;
            _canvasGO.transform.rotation = cam.rotation; // always face the camera
        }

        #endregion

        #region Canvas Builder

        private void BuildWorldSpaceCanvas()
        {
            _canvasGO = new GameObject("HighEffect_WorldCanvas");
            _canvasGO.transform.SetParent(transform);

            var canvas = _canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; // ← KEY: renders in VR headset

            // Size the rect to fill the FOV at canvasDistance
            var rt = _canvasGO.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(canvasSize, canvasSize);

            // Colour overlay (rainbow hue shift)
            _colourOverlay = CreateFullscreenImage(_canvasGO.transform, "ColourOverlay");
            _colourOverlay.color = new Color(1f, 0f, 1f, 0f);

            // Vignette overlay (dark pulsing edges)
            _vignetteOverlay = CreateFullscreenImage(_canvasGO.transform, "VignetteOverlay");
            _vignetteOverlay.sprite = BuildVignetteSprite(256);
            _vignetteOverlay.color = new Color(0f, 0f, 0f, 0f);

            SetOverlayVisible(false);
        }

        private static Image CreateFullscreenImage(Transform parent, string imgName)
        {
            var go = new GameObject(imgName);
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var img = go.AddComponent<Image>();
            img.raycastTarget = false; // never block VR controller raycasts
            return img;
        }

        /// <summary>
        /// Bakes a radial vignette texture entirely in code — no external assets needed.
        /// </summary>
        private static Sprite BuildVignetteSprite(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;

            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx    = (x - half) / half;
                    float dy    = (y - half) / half;
                    float dist  = Mathf.Clamp01(dx * dx + dy * dy);
                    float alpha = Mathf.SmoothStep(0f, 1f, dist);
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, alpha));
                }
            }
            tex.Apply();

            return Sprite.Create(tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f));
        }

        private void SetOverlayVisible(bool visible)
        {
            if (_canvasGO != null)
                _canvasGO.SetActive(visible);
        }

        #endregion
    }
}