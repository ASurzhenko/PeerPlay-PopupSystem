using TMPro;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    public sealed class InfoPopupView : PopupView<InfoData>
    {
        [SerializeField] private TMP_Text _title;
        [SerializeField] private TMP_Text _body;
        [SerializeField] private TMP_Text _detail;

        public override void Bind(in InfoData data)
        {
            SetText(_title, Text.Get(Entry.TitleKey));
            SetText(_body, Text.Get(Entry.BodyKey));
            SetText(_detail, data.Detail);

            if (_detail != null)
            {
                _detail.gameObject.SetActive(!string.IsNullOrEmpty(data.Detail));
            }

            // Last, and once per bind rather than once per label.
            RebuildContent();
        }

        protected override void ClearText()
        {
            SetText(_title, string.Empty);
            SetText(_body, string.Empty);
            SetText(_detail, string.Empty);
        }
    }
}
