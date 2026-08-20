namespace PeerPlay.Popups
{
    /// <summary>
    /// One band per value. The queue scans from <see cref="Critical"/> down, and within a band the
    /// order is the order of submission.
    /// </summary>
    public enum PopupPriority
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3
    }
}
