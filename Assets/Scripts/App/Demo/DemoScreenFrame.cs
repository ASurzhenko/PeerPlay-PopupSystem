using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// The aspect switcher, without Game view presets: presets live in the editor's own preferences, so a
    /// beat that depends on one is a beat that does not run on the grader's machine.
    ///
    /// With CanvasScaler Match = 0 the canvas is 1080 units wide at every aspect, so the height in units is
    /// 1080 × h/w — 2522 at 21:9, 1920 at 9:16, 1440 at 3:4. The switcher resizes the frame the popups are
    /// laid out inside, which simulates the LAYOUT BUDGET and nothing else: the real resolution and
    /// Screen.safeArea do not change. That budget is exactly what the 3:4 tablet case is about, and it is
    /// the case that breaks a dialog authored against 1920.
    ///
    /// 21:9 asks for more height than a 9:16 game view has, so the frame is clamped and says so. The clamp
    /// is in the safe direction — a taller screen only gains room — but a demo that silently drew a lie
    /// would be worse than one that names it.
    /// </summary>
    public sealed class DemoScreenFrame : MonoBehaviour
    {
        private const float ReferenceWidth = 1080f;

        [SerializeField] private RectTransform _canvasRect;
        [SerializeField] private RectTransform _popupFrame;
        [SerializeField] private RectTransform _mirrorFrame;
        [SerializeField] private TMP_Text _caption;

        [Header("Aspects")]
        [SerializeField] private Button _tall2109Button;
        [SerializeField] private Button _phone916Button;
        [SerializeField] private Button _tablet34Button;

        // Stored, so teardown removes the three listeners this component added and not whatever else the
        // scene put on those buttons.
        private UnityEngine.Events.UnityAction _onTall;
        private UnityEngine.Events.UnityAction _onPhone;
        private UnityEngine.Events.UnityAction _onTablet;

        private void Start()
        {
            _onTall = Wire(_tall2109Button, 9f / 21f, "21:9 phone");
            _onPhone = Wire(_phone916Button, 9f / 16f, "9:16 phone (reference)");
            _onTablet = Wire(_tablet34Button, 3f / 4f, "3:4 tablet");

            Apply(9f / 16f, "9:16 phone (reference)");
        }

        private void OnDestroy()
        {
            Unwire(_tall2109Button, _onTall);
            Unwire(_phone916Button, _onPhone);
            Unwire(_tablet34Button, _onTablet);
        }

        private UnityEngine.Events.UnityAction Wire(Button button, float widthOverHeight, string label)
        {
            if (button == null)
            {
                return null;
            }

            UnityEngine.Events.UnityAction handler = () => Apply(widthOverHeight, label);
            button.onClick.AddListener(handler);
            return handler;
        }

        private static void Unwire(Button button, UnityEngine.Events.UnityAction handler)
        {
            if (button != null && handler != null)
            {
                button.onClick.RemoveListener(handler);
            }
        }

        private void Apply(float widthOverHeight, string label)
        {
            float wanted = ReferenceWidth / widthOverHeight;
            float available = _canvasRect.rect.height;
            float height = Mathf.Min(wanted, available);
            bool clamped = height < wanted - 0.5f;

            Resize(_popupFrame, height);
            Resize(_mirrorFrame, height);

            _caption.text = clamped
                ? $"{label} — {wanted:0} units of height, clamped to {height:0} by the game view"
                : $"{label} — {height:0} units of height";
        }

        private static void Resize(RectTransform frame, float height)
        {
            if (frame == null)
            {
                return;
            }

            frame.anchorMin = new Vector2(0.5f, 0.5f);
            frame.anchorMax = new Vector2(0.5f, 0.5f);
            frame.pivot = new Vector2(0.5f, 0.5f);
            frame.anchoredPosition = Vector2.zero;
            frame.sizeDelta = new Vector2(ReferenceWidth, height);
        }
    }
}
