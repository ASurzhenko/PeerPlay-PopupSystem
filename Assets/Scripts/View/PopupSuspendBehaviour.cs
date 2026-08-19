namespace PeerPlay.Popups.View
{
    /// <summary>
    /// What an interrupted popup looks like while something else owns the screen. The core calls
    /// SetSuspended(bool) and never learns this flag exists — which is where "queue logic independent of
    /// UI rendering" is either true or false.
    /// </summary>
    public enum PopupSuspendBehaviour
    {
        /// <summary>Alpha to zero. The GameObject stays active so the close channel and any in-flight image survive.</summary>
        Hide = 0,

        /// <summary>Left on screen behind the interrupter, dimmed by the backdrop, but not interactive.</summary>
        StayVisible = 1
    }
}
