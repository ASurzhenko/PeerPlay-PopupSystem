using PeerPlay.Popups.View;

namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// Keys the demo owns. They are declared here rather than in <see cref="PopupKeys"/> because they exist
    /// to make a claim visible, not to ship: the catalog maps every one of them onto a prefab the game
    /// already has, so none of them costs an asset.
    ///
    /// The three queue keys are separate keys on purpose. <see cref="PopupDuplicatePolicy.Reject"/> is the
    /// default, so three requests under ONE key would terminate two of them as duplicates and the priority
    /// beat would have nothing to order.
    /// </summary>
    internal static class DemoKeys
    {
        internal static readonly PopupKey<InfoData> QueueLow = new PopupKey<InfoData>("queue_low");
        internal static readonly PopupKey<InfoData> QueueNormal = new PopupKey<InfoData>("queue_normal");
        internal static readonly PopupKey<InfoData> QueueCritical = new PopupKey<InfoData>("queue_critical");

        /// <summary>Non-dismissible: the half of the back-button beat that has to refuse.</summary>
        internal static readonly PopupKey<InfoData> Blocking = new PopupKey<InfoData>("info_blocking");

        /// <summary>Modeless by config — what "modal blocks the background" is contrasted against.</summary>
        internal static readonly PopupKey<InfoData> Modeless = new PopupKey<InfoData>("info_modeless");

        /// <summary>Modeless AND instant, so a cycle contains no backdrop tween. See the stress runner.</summary>
        internal static readonly PopupKey<InfoData> Stress = new PopupKey<InfoData>("stress");

        /// <summary>Carries a real maxPerSession of 1 — the frequency cap is demonstrated, not neutered.</summary>
        internal static readonly PopupKey<InfoData> Capped = new PopupKey<InfoData>("capped_daily");
    }
}
