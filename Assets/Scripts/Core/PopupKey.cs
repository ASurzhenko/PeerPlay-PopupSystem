namespace PeerPlay.Popups
{
    /// <summary>
    /// A popup's identity and its payload type in one value. The type parameter is what makes a wrong
    /// payload a compile error at the call site instead of a cast at runtime, and it is why the call
    /// site needs no view type parameter — the view binding is asserted once, at registration.
    /// </summary>
    public readonly struct PopupKey<TData>
    {
        public readonly string Id;

        public PopupKey(string id)
        {
            Id = id;
        }

        public override string ToString()
        {
            return Id;
        }
    }
}
