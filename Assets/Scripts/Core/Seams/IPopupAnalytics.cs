namespace PeerPlay.Popups.Seams
{
    /// <summary>The three events a LiveOps team lives on.</summary>
    public interface IPopupAnalytics
    {
        void Shown(string keyId);

        void Dismissed(string keyId, string action);

        /// <summary>Raised by game code when the popup achieved what it was for, not by the core.</summary>
        void Converted(string keyId, string action);
    }
}
