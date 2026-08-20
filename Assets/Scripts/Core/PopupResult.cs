namespace PeerPlay.Popups
{
    /// <summary>What the caller of ShowAsync gets back. Cancellation is a result here, never an exception.</summary>
    public readonly struct PopupResult
    {
        public readonly PopupOutcome Outcome;
        public readonly CloseSource Source;

        /// <summary>The action the view reported, e.g. "confirm" / "cancel". Null unless it reported one.</summary>
        public readonly string Action;

        /// <summary>Set for Refused / LoadFailed / Faulted / Duplicate; null otherwise.</summary>
        public readonly string Reason;

        public PopupResult(PopupOutcome outcome, CloseSource source, string action, string reason)
        {
            Outcome = outcome;
            Source = source;
            Action = action;
            Reason = reason;
        }

        public override string ToString()
        {
            return $"{Outcome} (source={Source}, action={Action ?? "-"}, reason={Reason ?? "-"})";
        }
    }

    /// <summary>
    /// The payload of <see cref="IPopupService.RequestCompleted"/>: every terminal, for every request,
    /// including the fire-and-forget ones nobody awaits.
    /// </summary>
    public readonly struct PopupCompletion
    {
        public readonly ulong Id;
        public readonly string KeyId;
        public readonly PopupResult Result;

        public PopupCompletion(ulong id, string keyId, in PopupResult result)
        {
            Id = id;
            KeyId = keyId;
            Result = result;
        }
    }
}
