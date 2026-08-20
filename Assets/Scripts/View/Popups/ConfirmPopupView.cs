using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// Two answers on one channel. The buttons are wired once, in OnPrepared's stead — in Awake, through
    /// the base — and the action strings arrive with the payload, so the caller decides what "confirm"
    /// is called without a second popup type.
    /// </summary>
    public sealed class ConfirmPopupView : PopupView<ConfirmData>
    {
        [SerializeField] private TMP_Text _title;
        [SerializeField] private TMP_Text _body;
        [SerializeField] private Button _confirmButton;
        [SerializeField] private Button _cancelButton;

        private string _confirmAction = "confirm";
        private string _cancelAction = "cancel";
        private bool _wired;

        public override void Bind(in ConfirmData data)
        {
            EnsureWired();

            _confirmAction = data.ConfirmAction;
            _cancelAction = data.CancelAction;

            SetText(_title, Text.Get(Entry.TitleKey));
            SetText(_body, string.IsNullOrEmpty(data.Detail) ? Text.Get(Entry.BodyKey) : data.Detail);
            RebuildContent();
        }

        protected override void ClearText()
        {
            SetText(_title, string.Empty);
            SetText(_body, string.Empty);
        }

        /// <remarks>
        /// Once per instance ever, never per Bind: AddListener in Bind is the pooling bug that fires N
        /// times on the Nth reuse.
        /// </remarks>
        private void EnsureWired()
        {
            if (_wired)
            {
                return;
            }

            _wired = true;

            if (_confirmButton != null)
            {
                _confirmButton.onClick.AddListener(OnConfirm);
            }

            if (_cancelButton != null)
            {
                _cancelButton.onClick.AddListener(OnCancel);
            }
        }

        private void OnConfirm()
        {
            Resolve(CloseSource.User, _confirmAction);
        }

        private void OnCancel()
        {
            Resolve(CloseSource.User, _cancelAction);
        }
    }
}
