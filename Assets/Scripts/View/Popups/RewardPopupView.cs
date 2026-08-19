using TMPro;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    public sealed class RewardPopupView : PopupView<RewardData>
    {
        [SerializeField] private TMP_Text _title;
        [SerializeField] private TMP_Text _body;
        [SerializeField] private TMP_Text _amount;

        public override void Bind(in RewardData data)
        {
            SetText(_title, Text.Get(Entry.TitleKey));
            SetText(_body, Text.Get(Entry.BodyKey));
            SetText(_amount, $"{data.Amount} {data.CurrencyId}");
            RebuildContent();
        }

        protected override void ClearText()
        {
            SetText(_title, string.Empty);
            SetText(_body, string.Empty);
            SetText(_amount, string.Empty);
        }
    }
}
