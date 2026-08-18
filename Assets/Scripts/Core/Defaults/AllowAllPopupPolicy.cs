using PeerPlay.Popups.Seams;

namespace PeerPlay.Popups.Defaults
{
    /// <summary>
    /// The policy the core ships so that the queue and its tests need nothing from the sourcing layer.
    /// The real one — remote kill switch, frequency caps, cooldowns — replaces it at the composition
    /// root without a single change here.
    /// </summary>
    public sealed class AllowAllPopupPolicy : IPopupPolicy
    {
        public PopupDecision Evaluate(in PopupRequestInfo request)
        {
            return PopupDecision.Allow;
        }

        public void NotifyShown(in PopupRequestInfo request)
        {
        }
    }
}
