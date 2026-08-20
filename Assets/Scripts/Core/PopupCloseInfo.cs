namespace PeerPlay.Popups
{
    public enum CloseSource : byte
    {
        None = 0,
        User = 1,
        Code = 2
    }

    /// <summary>
    /// What the view reports when its close channel resolves. One channel, two producers — the user and
    /// <see cref="Seams.IPopupView.RequestClose"/> — so the core awaits one thing and gets one answer.
    /// </summary>
    public readonly struct PopupCloseInfo
    {
        public readonly CloseSource Source;
        public readonly string Action;

        public PopupCloseInfo(CloseSource source, string action)
        {
            Source = source;
            Action = action;
        }
    }
}
