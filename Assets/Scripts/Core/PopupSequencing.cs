namespace PeerPlay.Popups
{
    /// <summary>
    /// What a request does when it reaches the head of the queue and something is already on screen.
    /// Whether an interrupted popup hides or stays visible behind the interrupter is a view-level
    /// decision, not a sequencing one — which is why there are three values here and not five.
    /// </summary>
    public enum PopupSequencing
    {
        /// <summary>Wait for the occupant to close. The default.</summary>
        Queue = 0,

        /// <summary>Suspend the occupant, open, and let it resume afterwards.</summary>
        InterruptAndResume = 1,

        /// <summary>Close the occupant for good and take its place.</summary>
        Replace = 2
    }
}
