using PeerPlay.Popups.View;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>One validated config row. Immutable, because a snapshot is swapped by reference.</summary>
    public readonly struct PopupRule
    {
        public readonly string KeyId;
        public readonly string AssetId;
        public readonly bool Disabled;
        public readonly PopupPriority Priority;
        public readonly PopupSequencing Sequencing;
        public readonly PopupModality Modality;
        public readonly PopupSuspendBehaviour Suspend;
        public readonly string TransitionId;
        public readonly string ImageUrl;
        public readonly string TitleKey;
        public readonly string BodyKey;
        public readonly int CooldownSeconds;

        /// <summary>Zero means unlimited — the sentinel the policy's cap check reads.</summary>
        public readonly int MaxPerSession;

        public PopupRule(string keyId, string assetId, bool disabled, PopupPriority priority,
                         PopupSequencing sequencing, PopupModality modality, PopupSuspendBehaviour suspend,
                         string transitionId, string imageUrl, string titleKey, string bodyKey,
                         int cooldownSeconds, int maxPerSession)
        {
            KeyId = keyId;
            AssetId = assetId;
            Disabled = disabled;
            Priority = priority;
            Sequencing = sequencing;
            Modality = modality;
            Suspend = suspend;
            TransitionId = transitionId;
            ImageUrl = imageUrl;
            TitleKey = titleKey;
            BodyKey = bodyKey;
            CooldownSeconds = cooldownSeconds;
            MaxPerSession = maxPerSession;
        }
    }
}
