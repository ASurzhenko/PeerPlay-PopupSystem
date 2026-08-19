using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// Android back / Escape, and an honest report of what it did.
    ///
    /// The discriminator is CurrentState, not CurrentKeyId. CloseTop resolves the close channel, which
    /// resumes the request's finish path inline as far as Closing and only clears the occupant at the last
    /// terminal step — after the close transition has played. So immediately after a SUCCESSFUL close the
    /// key is unchanged and the state reads Closing, while a refused close is still Active on the same key.
    /// Comparing the key before and after would print "nothing closed" for both.
    /// </summary>
    public sealed class DemoBackButton : MonoBehaviour
    {
        [SerializeField] private PopupSystemInstaller _installer;
        [SerializeField] private Button _backButton;
        [SerializeField] private DemoDirector _director;

        private void Start()
        {
            if (_backButton != null)
            {
                _backButton.onClick.AddListener(Back);
            }
        }

        private void OnDestroy()
        {
            if (_backButton != null)
            {
                _backButton.onClick.RemoveListener(Back);
            }
        }

        /// <summary>
        /// Escape on a desktop keyboard and the Android system back button arrive on the same key. The
        /// null check is not defensive noise: a phone reports no keyboard device at all, so reading
        /// Keyboard.current unguarded is a NullReferenceException on the platform this is built for.
        /// </summary>
        private void Update()
        {
            Keyboard keyboard = Keyboard.current;

            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                Back();
            }
        }

        private void Back()
        {
            IPopupService service = _installer.Service;
            string keyBefore = service.CurrentKeyId;

            if (keyBefore == null)
            {
                _director.Ledger.Note("back: nothing on screen");
                return;
            }

            service.CloseTop("back");

            PopupState after = service.CurrentState;
            bool closed = after != PopupState.Active || service.CurrentKeyId != keyBefore;

            _director.Ledger.Note(closed
                ? $"back: closed '{keyBefore}' (state {after})"
                : $"back: consumed, nothing closed — '{keyBefore}' is not dismissible");
        }
    }
}
