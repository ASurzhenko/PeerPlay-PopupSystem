namespace PeerPlay.Popups.View
{
    /// <summary>
    /// The keys game code calls with. A key's id and its asset id are different strings on purpose — the
    /// id is identity, the asset id is where the prefab lives, and the remote config is what relates them.
    /// </summary>
    public static class PopupKeys
    {
        public static readonly PopupKey<InfoData> Info = new PopupKey<InfoData>("info");
        public static readonly PopupKey<ConfirmData> Confirm = new PopupKey<ConfirmData>("confirm");
        public static readonly PopupKey<RewardData> Reward = new PopupKey<RewardData>("reward");
        public static readonly PopupKey<OfferData> Offer = new PopupKey<OfferData>("offer_weekend");
    }
}
