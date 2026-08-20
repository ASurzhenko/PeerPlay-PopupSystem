namespace PeerPlay.Popups.View
{
    /// <summary>
    /// One remote-config row translated into view terms. A view type, not a sourcing one: the config
    /// assembly already references this one, and declaring it there would make the reference graph
    /// View -> Sourcing -> View, which Unity refuses.
    /// </summary>
    public readonly struct PopupCatalogOverride
    {
        public readonly string KeyId;
        public readonly string AssetId;
        public readonly string TransitionId;
        public readonly PopupModality Modality;
        public readonly PopupSuspendBehaviour Suspend;
        public readonly string ImageUrl;
        public readonly string TitleKey;
        public readonly string BodyKey;

        public PopupCatalogOverride(string keyId, string assetId, string transitionId, PopupModality modality,
                                    PopupSuspendBehaviour suspend, string imageUrl, string titleKey, string bodyKey)
        {
            KeyId = keyId;
            AssetId = assetId;
            TransitionId = transitionId;
            Modality = modality;
            Suspend = suspend;
            ImageUrl = imageUrl;
            TitleKey = titleKey;
            BodyKey = bodyKey;
        }
    }
}
