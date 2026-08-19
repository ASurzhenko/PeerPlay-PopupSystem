namespace PeerPlay.Popups.View
{
    /// <summary>
    /// Whether the popup takes the backdrop with it. A view-level fact: the queue neither sets it nor
    /// reads it.
    /// </summary>
    public enum PopupModality
    {
        Modal = 0,
        Modeless = 1
    }
}
