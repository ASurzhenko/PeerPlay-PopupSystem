namespace PeerPlay.Popups
{
    /// <summary>
    /// The lifecycle. Initialization, Opening, Active, Closing and Disposal are the phases the popup
    /// itself goes through; Pending and Suspended are the two the queue needs on top of them.
    /// </summary>
    public enum PopupState
    {
        /// <summary>Not a request state: what a stale handle and an empty slot report.</summary>
        None = 0,

        /// <summary>Accepted and waiting in a band.</summary>
        Pending = 1,

        /// <summary>The view is being created and the data bound.</summary>
        Initializing = 2,

        /// <summary>The open transition is running.</summary>
        Opening = 3,

        /// <summary>Up, waiting for a close.</summary>
        Active = 4,

        /// <summary>Preempted: the view is retained and told it is suspended.</summary>
        Suspended = 5,

        /// <summary>The close transition is running.</summary>
        Closing = 6,

        /// <summary>View released, terminal delivered, request recycled.</summary>
        Disposed = 7
    }
}
