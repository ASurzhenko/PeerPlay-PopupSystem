namespace PeerPlay.Popups
{
    /// <summary>
    /// What happens when a request arrives for a key that is already live (on screen, suspended, or
    /// waiting). Rejecting is the default because it is the answer to rapid-fire input.
    /// </summary>
    public enum PopupDuplicatePolicy
    {
        Reject = 0,
        Allow = 1
    }
}
