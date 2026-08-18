namespace PeerPlay.Popups
{
    /// <summary>
    /// The terminal set. Every submitted request ends in exactly one of these and the caller can observe
    /// it — a request that could end in none of them is the defect this enum exists to prevent.
    /// </summary>
    public enum PopupOutcome : byte
    {
        /// <summary>Never delivered. The default of the enum and of a default PopupResult.</summary>
        None = 0,

        /// <summary>It opened, then closed — by the user or by code. The normal path.</summary>
        Completed = 1,

        /// <summary>Caller token, handle.Cancel(), ForceCloseAll(), or the service was disposed.</summary>
        Cancelled = 2,

        /// <summary>A Replace request took its slot.</summary>
        Superseded = 3,

        /// <summary>Rejected at submit: the same key was already live.</summary>
        Duplicate = 4,

        /// <summary>The policy said no — kill switch, frequency cap, cooldown.</summary>
        Refused = 5,

        /// <summary>The view factory could not produce a view.</summary>
        LoadFailed = 6,

        /// <summary>An unexpected exception escaped a hop. Logged with the exception.</summary>
        Faulted = 7
    }
}
