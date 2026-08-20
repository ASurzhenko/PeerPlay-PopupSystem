using System;

namespace PeerPlay.Popups.Sourcing
{
    /// <summary>
    /// The wire shape, and JsonUtility is what decides it rather than taste. An absent field is
    /// indistinguishable from a field carrying the type's default, so:
    ///
    ///   bool enabled  -> absent means false  -> every popup disabled, undetectably. The incident itself.
    ///   int  maxPerSession -> absent means 0 -> this schema's "unlimited", so the cap silently vanishes.
    ///   string id     -> absent means null   -> distinguishable from "", and the validator rejects it.
    ///
    /// Therefore every per-entry field is a string with no exceptions. Only the envelope's version stays
    /// an int, and rule 3 rejects 0, so its default is caught anyway.
    /// </summary>
    [Serializable]
    public sealed class PopupConfigDto
    {
        public int version;
        public PopupEntryDto[] popups;
    }

    [Serializable]
    public sealed class PopupEntryDto
    {
        public string id;
        public string assetId;
        public string state;
        public string priority;
        public string sequencing;
        public string modality;
        public string transition;
        public string suspend;
        public string imageUrl;
        public string titleKey;
        public string bodyKey;
        public string cooldownSeconds;
        public string maxPerSession;
    }
}
