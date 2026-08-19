using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The one popup with remote content. It opens immediately and lets the picture arrive later: a fetch
    /// inside the open transition is the "the UI must remain responsive" failure, and a content failure is
    /// not a popup failure — the placeholder and one line of copy say so while the popup keeps working.
    /// </summary>
    public sealed class OfferPopupView : PopupView<OfferData>
    {
        [SerializeField] private TMP_Text _title;
        [SerializeField] private TMP_Text _body;
        [SerializeField] private TMP_Text _price;

        public override void Bind(in OfferData data)
        {
            SetText(_title, Text.Get(Entry.TitleKey));
            SetText(_body, Text.Get(Entry.BodyKey));
            SetText(_price, data.DiscountPercent > 0
                ? $"{data.PriceLabel}  (-{data.DiscountPercent}%)"
                : data.PriceLabel);

            RebuildContent();

            // Deliberately not awaited: the popup must not wait on the network to appear. The base method
            // owns the generation guard and the lease, so a resolution after this instance went back to the
            // pool paints nothing.
            LoadContentAsync(Entry.ImageUrl).Forget();
        }

        protected override void ClearText()
        {
            SetText(_title, string.Empty);
            SetText(_body, string.Empty);
            SetText(_price, string.Empty);
        }
    }
}
