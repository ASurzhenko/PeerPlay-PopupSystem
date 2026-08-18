namespace PeerPlay.Popups
{
    /// <summary>
    /// Everything a caller can say about a request beyond its key and its data. The defaults are chosen
    /// so that <c>Show(key, data)</c> — no options at all — is the right call for the common case.
    /// </summary>
    public readonly struct ShowOptions
    {
        // Stored with an offset so that default(ShowOptions) means Normal rather than Low: the enum's
        // zero is Low, and a defaulted struct must not silently downgrade a popup's priority.
        private readonly byte _priorityOffset;

        public readonly PopupSequencing Sequencing;
        public readonly PopupDuplicatePolicy Duplicates;

        /// <summary>Groups keys for policy cooldowns. Null means the key id is its own family.</summary>
        public readonly string Family;

        public ShowOptions(PopupPriority priority = PopupPriority.Normal,
                           PopupSequencing sequencing = PopupSequencing.Queue,
                           PopupDuplicatePolicy duplicates = PopupDuplicatePolicy.Reject,
                           string family = null)
        {
            _priorityOffset = (byte)((int)priority + 1);
            Sequencing = sequencing;
            Duplicates = duplicates;
            Family = family;
        }

        public PopupPriority Priority => _priorityOffset == 0
            ? PopupPriority.Normal
            : (PopupPriority)(_priorityOffset - 1);
    }
}
