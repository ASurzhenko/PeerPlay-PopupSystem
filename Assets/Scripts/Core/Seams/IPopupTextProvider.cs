namespace PeerPlay.Popups.Seams
{
    /// <summary>
    /// Popup copy resolved through a seam instead of hardcoded. Declared here so it is part of the
    /// architecture, and consumed by the view layer: payloads carry text keys, and the view factory
    /// resolves them when it binds data to a view. The queue never calls it.
    /// </summary>
    public interface IPopupTextProvider
    {
        bool TryGet(string key, out string value);

        /// <summary>Returns the key itself when it is missing, and logs that once.</summary>
        string Get(string key);
    }
}
