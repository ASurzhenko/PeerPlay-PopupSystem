namespace PeerPlay.Popups.View
{
    /// <summary>
    /// One popup's catalog facts, carried to the view inside <see cref="PopupViewSetup"/>.
    ///
    /// Two kinds of field, and the split is what gives every value exactly one owner. The asset id, the
    /// image URL and the two text keys are the catalog's outright. The transition, the modality and the
    /// suspend mode are the <b>prefab's</b>, and are null here unless the remote config overrode them —
    /// so a config that later drops an override leaves the authored value in place rather than the
    /// previous config's.
    /// </summary>
    public readonly struct PopupCatalogEntry
    {
        public readonly string KeyId;
        public readonly string AssetId;
        public readonly string ImageUrl;
        public readonly string TitleKey;
        public readonly string BodyKey;

        /// <summary>Null unless the remote config named a transition for this key.</summary>
        public readonly string TransitionId;

        /// <summary>Null unless the remote config named a modality for this key.</summary>
        public readonly PopupModality? Modality;

        /// <summary>Null unless the remote config named a suspend behaviour for this key.</summary>
        public readonly PopupSuspendBehaviour? Suspend;

        public PopupCatalogEntry(string keyId, string assetId, string imageUrl, string titleKey, string bodyKey,
                                 string transitionId = null, PopupModality? modality = null,
                                 PopupSuspendBehaviour? suspend = null)
        {
            KeyId = keyId;
            AssetId = assetId;
            ImageUrl = imageUrl;
            TitleKey = titleKey;
            BodyKey = bodyKey;
            TransitionId = transitionId;
            Modality = modality;
            Suspend = suspend;
        }

        public bool IsEmpty => KeyId == null;
    }
}
