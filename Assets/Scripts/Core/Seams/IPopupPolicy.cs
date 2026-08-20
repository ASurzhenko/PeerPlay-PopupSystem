namespace PeerPlay.Popups.Seams
{
    /// <summary>
    /// Owns the remote kill switch, frequency caps and per-family cooldowns. Consulted at submit and
    /// again every time a request is considered for the slot — so a switch flipped while the request
    /// waited in the queue still stops it from opening. A refusal is a normal outcome, never an exception.
    /// </summary>
    public interface IPopupPolicy
    {
        /// <summary>
        /// Synchronous on purpose: the implementation answers from an already-fetched config, which
        /// keeps a whole row out of the cancellation matrix and makes the policy trivially testable.
        /// </summary>
        /// <remarks>
        /// <b>Must be pure.</b> The number of calls per request is not fixed and is not part of the
        /// contract: one at submit, then one per admission attempt. A request that preempts an occupant
        /// is admitted twice — once to decide the occupant may be displaced, and again after the slot was
        /// vacated, because vacating runs foreign code (the outgoing view's SetSuspended, or a whole
        /// terminal chain that resumes calling game code inline) and a config refresh landing in that
        /// window must still be able to stop the popup. Which request wins the slot is decided from the
        /// bands after the vacate, so the second admission is not necessarily about the same request.
        ///
        /// Counting therefore belongs in <see cref="NotifyShown"/>, which is raised exactly once per
        /// request. A frequency cap incremented here would consume itself before the popup ever opened.
        /// </remarks>
        PopupDecision Evaluate(in PopupRequestInfo request);

        /// <summary>Raised when a request reaches Active — the moment a frequency cap should count.</summary>
        void NotifyShown(in PopupRequestInfo request);
    }

    public readonly struct PopupDecision
    {
        public readonly bool Allowed;

        /// <summary>Surfaced verbatim in <see cref="PopupResult.Reason"/> when the request is refused.</summary>
        public readonly string Reason;

        private PopupDecision(bool allowed, string reason)
        {
            Allowed = allowed;
            Reason = reason;
        }

        public static PopupDecision Allow => new PopupDecision(true, null);

        public static PopupDecision Refuse(string reason)
        {
            return new PopupDecision(false, reason);
        }
    }

    public readonly struct PopupRequestInfo
    {
        public readonly string KeyId;
        public readonly string Family;
        public readonly PopupPriority Priority;

        public PopupRequestInfo(string keyId, string family, PopupPriority priority)
        {
            KeyId = keyId;
            Family = family;
            Priority = priority;
        }
    }
}
