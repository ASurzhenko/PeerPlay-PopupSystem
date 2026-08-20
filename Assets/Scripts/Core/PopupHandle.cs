namespace PeerPlay.Popups
{
    /// <summary>
    /// A reference to one submitted request. It holds a monotonic id, never an index and never the
    /// request object: request objects are pooled, so a handle kept past a terminal would otherwise be
    /// able to act on whichever popup inherited the recycled object. Every operation resolves the id
    /// through the live map, which makes a stale handle a logged no-op instead of a wrong close.
    /// </summary>
    public readonly struct PopupHandle
    {
        public readonly ulong Id;

        private readonly PopupService _service;

        internal PopupHandle(PopupService service, ulong id)
        {
            _service = service;
            Id = id;
        }

        /// <summary>True while the request still exists — pending, opening, active, suspended or closing.</summary>
        public bool IsLive => _service != null && _service.IsLive(Id);

        /// <summary>The request's state, or <see cref="PopupState.None"/> if the handle is stale.</summary>
        public PopupState State => _service == null ? PopupState.None : _service.GetState(Id);

        /// <summary>
        /// Asks the popup to close, reporting <paramref name="action"/> to the caller. False if the
        /// handle is stale, or the popup is not on screen yet.
        /// </summary>
        public bool Close(string action = null)
        {
            return _service != null && _service.TryCloseFromHandle(Id, action);
        }

        /// <summary>
        /// Cancels the request wherever it is — waiting in a band, loading, or on screen. False if the
        /// handle is stale.
        /// </summary>
        public bool Cancel()
        {
            return _service != null && _service.TryCancelFromHandle(Id);
        }
    }
}
