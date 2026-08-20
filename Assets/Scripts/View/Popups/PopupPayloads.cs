namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The payloads a caller passes. They carry the <b>dynamic</b> half only: the authored copy and the
    /// art come from the catalog entry, so no field has two candidate owners.
    /// </summary>
    public readonly struct InfoData
    {
        /// <summary>An extra line under the authored body. Null for the plain informational case.</summary>
        public readonly string Detail;

        public InfoData(string detail = null)
        {
            Detail = detail;
        }
    }

    public readonly struct ConfirmData
    {
        public readonly string Detail;
        public readonly string ConfirmAction;
        public readonly string CancelAction;

        public ConfirmData(string detail = null, string confirmAction = "confirm", string cancelAction = "cancel")
        {
            Detail = detail;
            ConfirmAction = confirmAction;
            CancelAction = cancelAction;
        }
    }

    public readonly struct RewardData
    {
        public readonly int Amount;
        public readonly string CurrencyId;

        public RewardData(int amount, string currencyId)
        {
            Amount = amount;
            CurrencyId = currencyId;
        }
    }

    public readonly struct OfferData
    {
        public readonly string OfferId;
        public readonly string PriceLabel;
        public readonly int DiscountPercent;

        public OfferData(string offerId, string priceLabel, int discountPercent)
        {
            OfferId = offerId;
            PriceLabel = priceLabel;
            DiscountPercent = discountPercent;
        }
    }
}
