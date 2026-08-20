namespace PeerPlay.Popups.App.Demo
{
    /// <summary>
    /// Which Addressables Play Mode Script the editor is set to, as a plain string the overlay prints.
    ///
    /// It is written by an editor-only probe because the setting lives in UnityEditor, and it is stated on
    /// screen rather than assumed: the active play-mode index is NOT stored in the project — it lives in
    /// Library/, which is gitignored — so a fresh clone always starts in "Use Asset Database (fastest)"
    /// whatever the author last selected. The demo is designed for that mode, and the overlay proves which
    /// one is actually running rather than asking the reader to trust the README.
    /// </summary>
    public static class DemoAddressablesMode
    {
        /// <summary>Empty in a player, where the question has no answer.</summary>
        public static string Name { get; set; } = string.Empty;
    }
}
